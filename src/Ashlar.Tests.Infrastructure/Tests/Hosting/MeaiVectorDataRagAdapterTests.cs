using Ashlar.AI.Pipeline.Embeddings;
using Ashlar.AI.Pipeline.Governance;
using Ashlar.AI.Pipeline.Rag;
using Ashlar.Hosting.Meai;
using FluentAssertions;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Hosting;

/// <summary>
/// MeaiVectorDataRagAdapter is the IRAGService the shipped hosts resolve (phase 13b overrides the
/// legacy one unconditionally), so it is what <c>rag search</c> and <c>rag index</c> actually run.
/// It used to substitute a tier for a missing one in both directions, and both substitutions were
/// open: an omitted search clearance became TopSecret (the whole corpus, audited as if cleared),
/// and an omitted index label became Public (served to everyone).
/// </summary>
public sealed class MeaiVectorDataRagAdapterTests
{
    private const string Text = "shared incident playbook guidance";

    [Fact]
    public async Task Search_without_a_clearance_is_the_floor_and_is_audited_as_omitted_not_as_TopSecret()
    {
        var (adapter, auditor) = Create();
        await adapter.IndexAsync("pub", Text, "Public");
        await adapter.IndexAsync("sec", Text, "Secret");

        var hits = await adapter.SearchAsync("incident playbook", 10, 0.0, null);

        hits.Select(h => h.Id).Should().BeEquivalentTo(new[] { "pub" });
        var decisions = auditor.Records.Last(r => r.TargetKey == "rag:search").PolicyDecisions;
        decisions.Should().Contain("caller_tier=Public").And.Contain("caller_tier_basis=omitted");
        decisions.Should().NotContain("caller_tier=TopSecret");

        // POSITIVE CONTROL: the Secret chunk is reachable with a clearance, so the empty answer
        // above is the floor and not a chunk that failed to index.
        (await adapter.SearchAsync("incident playbook", 10, 0.0, "Secret")).Select(h => h.Id)
            .Should().BeEquivalentTo(new[] { "pub", "sec" });
    }

    [Fact]
    public async Task Index_without_a_label_is_unmarked_and_served_only_to_the_top_clearance()
    {
        var (adapter, _) = Create();
        await adapter.IndexAsync("/kb/notes.md", Text, sensitivityLevelName: null);

        (await adapter.SearchAsync("incident playbook", 10, 0.0, null)).Should().BeEmpty();
        (await adapter.SearchAsync("incident playbook", 10, 0.0, "Public")).Should().BeEmpty();
        (await adapter.SearchAsync("incident playbook", 10, 0.0, "Secret")).Should().BeEmpty();

        var top = await adapter.SearchAsync("incident playbook", 10, 0.0, "TopSecret");
        top.Should().ContainSingle().Which.SensitivityLevelName.Should().BeNull(
            "IRAGService reports an unmarked document as null, and the adapter invents no label for it");
    }

    [Fact]
    public async Task Index_over_an_existing_id_at_a_lower_label_is_refused()
    {
        var (adapter, _) = Create();
        await adapter.IndexAsync("/kb/plan.md", Text, "Secret");

        var downgrade = async () => await adapter.IndexAsync("/kb/plan.md", Text, "Public");

        await downgrade.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Refusing to re-index*");
        (await adapter.SearchAsync("incident playbook", 10, 0.0, "Secret"))
            .Should().ContainSingle().Which.SensitivityLevelName.Should().Be("Secret");
    }

    private static (MeaiVectorDataRagAdapter Adapter, InMemoryChatInvocationAuditor Auditor) Create()
    {
        var auditor = new InMemoryChatInvocationAuditor();
        var collection = new InProcessVectorStore().GetCollection<string, ChunkRecord>(VectorDataRagService.DefaultCollectionName);
        var rag = new VectorDataRagService(collection, new TokenHashEmbeddingGenerator(), auditor);
        return (new MeaiVectorDataRagAdapter(rag), auditor);
    }
}
