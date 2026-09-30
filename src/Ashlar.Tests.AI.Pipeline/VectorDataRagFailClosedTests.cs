using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Ashlar.AI.Pipeline;
using Ashlar.AI.Pipeline.Clients;
using Ashlar.AI.Pipeline.Governance;
using Ashlar.AI.Pipeline.Rag;
using Xunit;

namespace Ashlar.Tests.AI.Pipeline;

/// <summary>
/// VectorDataRagService is the store the shipped hosts search (AshlarKernelRegistrar phase 13b
/// points IRAGService at it through MeaiVectorDataRagAdapter), so its defaults ARE the product's
/// defaults: an omitted clearance searches at the floor, an unlabelled chunk is served only to the
/// top clearance, and re-indexing can never lower a chunk's tier.
/// </summary>
public sealed class VectorDataRagFailClosedTests
{
    private const string Text = "shared incident playbook guidance";

    [Fact]
    public async Task An_omitted_clearance_searches_at_the_floor_and_the_audit_says_so()
    {
        var auditor = new InMemoryChatInvocationAuditor();
        await using var provider = BuildProvider(auditor);
        var rag = provider.GetRequiredService<VectorDataRagService>();
        await rag.IndexAsync("pub", Text, trustTier: "Public");
        await rag.IndexAsync("sec", Text, trustTier: "Secret");

        var hits = await rag.SearchAsync("incident playbook", callerMaxTrustTier: null, top: 10);

        hits.Select(h => h.Record.Key).Should().BeEquivalentTo(new[] { "pub" });
        LastSearchDecisions(auditor).Should().Contain("caller_tier=Public")
            .And.Contain("caller_tier_basis=omitted")
            .And.NotContain(d => d.Contains("TopSecret"), "the audit must name the clearance applied, not a substitute");
    }

    [Fact]
    public async Task An_unrecognised_clearance_searches_at_the_floor_and_is_audited_as_unrecognised()
    {
        var auditor = new InMemoryChatInvocationAuditor();
        await using var provider = BuildProvider(auditor);
        var rag = provider.GetRequiredService<VectorDataRagService>();
        await rag.IndexAsync("pub", Text, trustTier: "Public");
        await rag.IndexAsync("sec", Text, trustTier: "Secret");

        var hits = await rag.SearchAsync("incident playbook", callerMaxTrustTier: "Unclassified", top: 10);

        hits.Select(h => h.Record.Key).Should().BeEquivalentTo(new[] { "pub" });
        LastSearchDecisions(auditor).Should().Contain("caller_tier=Public")
            .And.Contain("caller_tier_basis=unrecognised");
    }

    // POSITIVE CONTROL for the two above: an explicit clearance still reaches what it is cleared
    // for, so "only Public came back" cannot be satisfied by a search that returns only Public.
    [Fact]
    public async Task An_explicit_clearance_reaches_what_it_is_cleared_for_and_is_audited_as_explicit()
    {
        var auditor = new InMemoryChatInvocationAuditor();
        await using var provider = BuildProvider(auditor);
        var rag = provider.GetRequiredService<VectorDataRagService>();
        await rag.IndexAsync("pub", Text, trustTier: "Public");
        await rag.IndexAsync("sec", Text, trustTier: "Secret");

        var hits = await rag.SearchAsync("incident playbook", callerMaxTrustTier: "secret", top: 10);

        hits.Select(h => h.Record.Key).Should().BeEquivalentTo(new[] { "pub", "sec" });
        LastSearchDecisions(auditor).Should().Contain("caller_tier=Secret")
            .And.Contain("caller_tier_basis=explicit");
    }

    [Fact]
    public async Task An_unlabelled_chunk_is_served_only_to_the_top_clearance()
    {
        await using var provider = BuildProvider();
        var rag = provider.GetRequiredService<VectorDataRagService>();
        await rag.IndexAsync("unlabelled", Text);

        (await rag.SearchAsync("incident playbook", "Public", top: 10)).Should().BeEmpty();
        (await rag.SearchAsync("incident playbook", "Secret", top: 10)).Should().BeEmpty();

        var top = await rag.SearchAsync("incident playbook", "TopSecret", top: 10);
        top.Should().ContainSingle().Which.Record.TrustTier.Should().BeEmpty("no tier is invented for an unlabelled chunk");
    }

    // A ChunkRecord built without a tier -- written straight into the collection, as a connector
    // migration or a host-side import would -- is unlabelled, not Public. ChunkRecord.TrustTier
    // used to default to "Public", which published any record whose writer forgot the field.
    [Fact]
    public async Task A_record_written_without_a_tier_is_unlabelled_not_Public()
    {
        await using var provider = BuildProvider();
        var rag = provider.GetRequiredService<VectorDataRagService>();
        var collection = provider.GetRequiredService<Microsoft.Extensions.VectorData.VectorStoreCollection<string, ChunkRecord>>();
        var generator = provider.GetRequiredService<Microsoft.Extensions.AI.IEmbeddingGenerator<string, Microsoft.Extensions.AI.Embedding<float>>>();
        var embedding = (await generator.GenerateAsync(new[] { Text })).First().Vector;

        await collection.UpsertAsync(new ChunkRecord { Key = "imported", Text = Text, Embedding = embedding });

        (await rag.SearchAsync("incident playbook", "Secret", top: 10)).Should().BeEmpty();
        (await rag.SearchAsync("incident playbook", "TopSecret", top: 10))
            .Should().ContainSingle().Which.Record.Key.Should().Be("imported");
    }

    [Fact]
    public async Task Reindexing_a_key_at_a_lower_tier_is_refused_and_the_stored_tier_survives()
    {
        var auditor = new InMemoryChatInvocationAuditor();
        await using var provider = BuildProvider(auditor);
        var rag = provider.GetRequiredService<VectorDataRagService>();
        await rag.IndexAsync("/kb/plan.md", Text, trustTier: "Secret");

        var downgrade = async () => await rag.IndexAsync("/kb/plan.md", Text, trustTier: "Public");

        (await downgrade.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*Refusing to re-index*'/kb/plan.md'*'Public'*'Secret'*");
        (await rag.SearchAsync("incident playbook", "Public", top: 10)).Should().BeEmpty("the downgrade must not have been written");
        (await rag.SearchAsync("incident playbook", "Secret", top: 10))
            .Should().ContainSingle().Which.Record.TrustTier.Should().Be("Secret");
        auditor.Records.Should().Contain(r =>
            r.TargetKey == "rag:index" && r.Outcome == "denied" && r.PolicyDecisions.Contains("event=downgrade_refused"));
    }

    [Fact]
    public async Task Reindexing_an_unlabelled_key_with_a_real_label_is_a_downgrade_too()
    {
        await using var provider = BuildProvider();
        var rag = provider.GetRequiredService<VectorDataRagService>();
        await rag.IndexAsync("/kb/unlabelled.md", Text);

        var relabel = async () => await rag.IndexAsync("/kb/unlabelled.md", Text, trustTier: "Internal");

        await relabel.Should().ThrowAsync<InvalidOperationException>().WithMessage("*(unlabelled)*");
    }

    // POSITIVE CONTROL for the refusals: re-indexing at the same or a HIGHER tier is accepted, so
    // the refusal cannot be satisfied by refusing every re-index.
    [Fact]
    public async Task Reindexing_at_the_same_or_a_higher_tier_is_accepted()
    {
        await using var provider = BuildProvider();
        var rag = provider.GetRequiredService<VectorDataRagService>();
        await rag.IndexAsync("/kb/plan.md", Text, trustTier: "Internal");

        await rag.IndexAsync("/kb/plan.md", Text, trustTier: "internal");
        await rag.IndexAsync("/kb/plan.md", Text, trustTier: "Secret");

        (await rag.SearchAsync("incident playbook", "Internal", top: 10)).Should().BeEmpty();
        (await rag.SearchAsync("incident playbook", "Secret", top: 10))
            .Should().ContainSingle().Which.Record.TrustTier.Should().Be("Secret");
    }

    private static IReadOnlyList<string> LastSearchDecisions(InMemoryChatInvocationAuditor auditor) =>
        auditor.Records.Last(r => r.TargetKey == "rag:search").PolicyDecisions;

    private static ServiceProvider BuildProvider(InMemoryChatInvocationAuditor? auditor = null)
    {
        var services = new ServiceCollection();
        if (auditor is not null)
        {
            services.AddSingleton<IChatInvocationAuditor>(auditor);
        }

        services.AddAshlarMeaiPipeline(
            ollamaInnerFactory: _ => new FakeChatClient(),
            onnxInnerFactory: _ => new FakeChatClient(),
            registerDefaultRouter: false);
        return services.BuildServiceProvider();
    }
}
