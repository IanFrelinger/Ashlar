using FluentAssertions;
using Ashlar.BackgroundAgents.DataSensitivity;
using Ashlar.BackgroundAgents.RAG;
using Xunit;

namespace Ashlar.Tests.BackgroundAgents.RAG;

/// <summary>
/// The two legacy stores fail closed, and identically. Before this, both applied NO sensitivity
/// filter unless a registry was supplied AND the clearance resolved, and both returned every
/// unmarked document to every caller -- so the common construction, <c>new InMemoryVectorStore()</c>
/// searched with a null clearance, served everything.
///
/// Every store here is built WITHOUT a registry unless the test says otherwise, because that is the
/// construction that used to switch filtering off.
/// </summary>
public sealed class LegacyVectorStoreFailClosedTests
{
    private const string Text = "shared incident playbook guidance";

    public static TheoryData<string> Stores => new() { "memory", "sqlite" };

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task An_omitted_clearance_is_the_floor_not_everything(string kind)
    {
        await using var store = await Seed(kind, null, ("pub", "Public"), ("sec", "Secret"));

        (await Ids(store.Value, null)).Should().BeEquivalentTo(new[] { "pub" });
        (await Ids(store.Value, "   ")).Should().BeEquivalentTo(new[] { "pub" });

        // POSITIVE CONTROL: the Secret document is there, and reachable at its clearance.
        (await Ids(store.Value, "Secret")).Should().BeEquivalentTo(new[] { "pub", "sec" });
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task An_unrecognised_clearance_is_the_floor_not_everything(string kind)
    {
        await using var store = await Seed(kind, null, ("pub", "Public"), ("sec", "Secret"));

        (await Ids(store.Value, "Unclassified")).Should().BeEquivalentTo(new[] { "pub" });
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task An_unmarked_document_is_served_only_to_the_top_clearance(string kind)
    {
        await using var store = await Seed(kind, null, ("pub", "Public"), ("unmarked", null));

        (await Ids(store.Value, null)).Should().BeEquivalentTo(new[] { "pub" });
        (await Ids(store.Value, "Secret")).Should().BeEquivalentTo(new[] { "pub" });

        var top = await store.Value.SearchAsync(await Embed(), 10, 0.0, "TopSecret", default);
        top.Select(r => r.Id).Should().BeEquivalentTo(new[] { "pub", "unmarked" });
        top.Single(r => r.Id == "unmarked").SensitivityLevelName.Should().BeNull("no label is invented for an unmarked document");
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task A_document_with_an_unknown_label_is_served_only_to_the_top_clearance(string kind)
    {
        await using var store = await Seed(kind, null, ("pub", "Public"), ("odd", "Bogus"));

        (await Ids(store.Value, "Secret")).Should().BeEquivalentTo(new[] { "pub" });
        (await Ids(store.Value, "TopSecret")).Should().BeEquivalentTo(new[] { "pub", "odd" });
    }

    // With a custom level registered ABOVE TopSecret, "most restrictive" is that level, not
    // TopSecret: an unmarked document is out of reach of a TopSecret caller too.
    [Theory]
    [MemberData(nameof(Stores))]
    public async Task The_most_restrictive_level_is_the_registrys_highest_not_a_fixed_name(string kind)
    {
        var registry = new DataSensitivityRegistry();
        registry.Register(new ConfigurableSensitivityLevel("Codeword", "Codeword", 10, false, false, true, false, "above TopSecret"));
        await using var store = await Seed(kind, registry, ("unmarked", null));

        (await Ids(store.Value, "TopSecret")).Should().BeEmpty();
        (await Ids(store.Value, "Codeword")).Should().BeEquivalentTo(new[] { "unmarked" });
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task Reindexing_an_id_at_a_lower_label_is_refused_and_the_stored_label_survives(string kind)
    {
        await using var store = await Seed(kind, null, ("/kb/plan.md", "Secret"));

        var downgrade = async () => await store.Value.IndexAsync("/kb/plan.md", Text, await Embed(), "Public", default);

        (await downgrade.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*Refusing to re-index*'/kb/plan.md'*'Public'*'Secret'*");
        (await Ids(store.Value, "Public")).Should().BeEmpty("the downgrade must not have been written");
        (await store.Value.SearchAsync(await Embed(), 10, 0.0, "Secret", default))
            .Should().ContainSingle().Which.SensitivityLevelName.Should().Be("Secret");
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task Reindexing_an_unmarked_id_with_a_real_label_is_a_downgrade_too(string kind)
    {
        await using var store = await Seed(kind, null, ("/kb/unmarked.md", null));

        var relabel = async () => await store.Value.IndexAsync("/kb/unmarked.md", Text, await Embed(), "Internal", default);

        await relabel.Should().ThrowAsync<InvalidOperationException>().WithMessage("*(unmarked)*");
    }

    // POSITIVE CONTROL for the two refusals: the same or a HIGHER label is accepted.
    [Theory]
    [MemberData(nameof(Stores))]
    public async Task Reindexing_at_the_same_or_a_higher_label_is_accepted(string kind)
    {
        await using var store = await Seed(kind, null, ("/kb/plan.md", "Internal"));

        await store.Value.IndexAsync("/kb/plan.md", Text, await Embed(), "internal", default);
        await store.Value.IndexAsync("/kb/plan.md", Text, await Embed(), "Secret", default);

        (await Ids(store.Value, "Internal")).Should().BeEmpty();
        (await store.Value.SearchAsync(await Embed(), 10, 0.0, "Secret", default))
            .Should().ContainSingle().Which.SensitivityLevelName.Should().Be("Secret");
    }

    private static async Task<float[]> Embed() => await new TokenEmbeddingGenerator(32).GenerateAsync(Text, default);

    private static async Task<List<string>> Ids(IVectorStore store, string? clearance) =>
        (await store.SearchAsync(await Embed(), 10, 0.0, clearance, default)).Select(r => r.Id).ToList();

    private static async Task<StoreHandle> Seed(string kind, IDataSensitivityRegistry? registry, params (string Id, string? Label)[] docs)
    {
        IVectorStore store = kind switch
        {
            "memory" => new InMemoryVectorStore(registry),
            "sqlite" => new SqliteVectorStore(
                Path.Combine(Path.GetTempPath(), $"rag_failclosed_{Guid.NewGuid():N}.db"), registry),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };

        foreach (var (id, label) in docs)
            await store.IndexAsync(id, Text, await Embed(), label, default);

        // Arrange-step guard: every document went in, so an empty search below is the filter
        // talking and not a failed write.
        (await store.GetDocumentCountAsync(default)).Should().Be(docs.Length);
        return new StoreHandle(store);
    }

    private sealed class StoreHandle(IVectorStore value) : IAsyncDisposable
    {
        public IVectorStore Value { get; } = value;

        public ValueTask DisposeAsync() => Value is IAsyncDisposable d ? d.DisposeAsync() : ValueTask.CompletedTask;
    }
}
