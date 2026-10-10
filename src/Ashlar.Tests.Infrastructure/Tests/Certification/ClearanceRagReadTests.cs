using Ashlar.Abstractions.Security;
using Ashlar.Abstractions.Security.Egress;
using Ashlar.AI.Pipeline.Embeddings;
using Ashlar.AI.Pipeline.Governance;
using Ashlar.AI.Pipeline.Rag;
using Ashlar.BackgroundAgents.RAG;
using Ashlar.Hosting.Meai;
using FluentAssertions;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

[Trait("Category", "Certification")]
public sealed class ClearanceRagReadTests
{
    private const string Text = "private incident response playbook";

    public static TheoryData<string, string?, string?, string, string> Cases
    {
        get
        {
            var cases = new TheoryData<string, string?, string?, string, string>();
            foreach (var kind in new[] { "memory", "sqlite", "vector-data" })
            {
                cases.Add(kind, "SystemHigh", "SystemHigh", "agent:reader", "public,secret,top,unlabelled,unknown");
                cases.Add(kind, "TopSecret", "SystemHigh", "agent:reader", "public,secret,top");
                cases.Add(kind, "Secret", "SystemHigh", "agent:reader", "public,secret");
                cases.Add(kind, "Public", "SystemHigh", "agent:reader", "public");
                cases.Add(kind, "SystemHigh", "Secret", "agent:reader", "public,secret");
                cases.Add(kind, "SystemHigh", null, "agent:reader", "public");
                cases.Add(kind, "SystemHigh", "bogus", "agent:reader", "public");
                cases.Add(kind, "SystemHigh", "SystemHigh", "agent:unknown", "public");
                cases.Add(kind, null, "SystemHigh", "agent:reader", "public");
            }
            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Every_store_bounds_the_query_by_trusted_clearance(
        string kind, string? ceiling, string? requested, string identity, string expected)
    {
        var authority = ceiling is null ? null : new SubjectClearanceAuthority(
            new Dictionary<string, string?> { ["agent:reader"] = ceiling });
        await using var fixture = new Fixture(kind, authority);
        await fixture.Seed();
        using var subject = EgressSubject.Enter(identity, new HighWaterMark(SecurityLabel.SystemHigh));
        var hits = await fixture.Rag.SearchAsync(Text, 10, 0, requested);
        hits.Select(h => h.Id).Should().BeEquivalentTo(expected.Split(','));
    }

    [Theory]
    [InlineData("memory")]
    [InlineData("sqlite")]
    [InlineData("vector-data")]
    public async Task A_host_limit_and_an_enclosing_subject_both_restrict_the_read(string kind)
    {
        var authority = new SubjectClearanceAuthority(
            new Dictionary<string, string?> { ["agent:reader"] = "SystemHigh", ["agent:parent"] = "Public" },
            new Dictionary<string, string?> { ["agent:reader"] = "Secret" });
        await using var fixture = new Fixture(kind, authority);
        await fixture.Seed();
        using (EgressSubject.Enter("agent:reader", new HighWaterMark()))
            (await fixture.Rag.SearchAsync(Text, 10, 0, "SystemHigh")).Select(h => h.Id)
                .Should().BeEquivalentTo(new[] { "public", "secret" });
        using var parent = EgressSubject.Enter("agent:parent", new HighWaterMark());
        using var child = EgressSubject.Enter("agent:reader", new HighWaterMark());
        (await fixture.Rag.SearchAsync(Text, 10, 0, "SystemHigh")).Select(h => h.Id)
            .Should().BeEquivalentTo(new[] { "public" });
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteVectorStore? _sqlite;
        private readonly string? _path;
        public IRAGService Rag { get; }

        public Fixture(string kind, SubjectClearanceAuthority? authority)
        {
            if (kind == "vector-data")
            {
                var collection = new InProcessVectorStore().GetCollection<string, ChunkRecord>("clearance-reads");
                Rag = new MeaiVectorDataRagAdapter(new VectorDataRagService(collection,
                    new TokenHashEmbeddingGenerator(), new InMemoryChatInvocationAuditor(), authority));
            }
            else
            {
                Ashlar.BackgroundAgents.RAG.IVectorStore store;
                if (kind == "sqlite")
                {
                    _path = Path.Combine(Path.GetTempPath(), "clearance-" + Guid.NewGuid().ToString("N") + ".db");
                    store = _sqlite = new SqliteVectorStore(_path, clearances: authority);
                }
                else store = new InMemoryVectorStore(clearances: authority);
                Rag = new RAGService(store, new TokenEmbeddingGenerator());
            }
        }

        public async Task Seed()
        {
            foreach (var (id, label) in new (string, string?)[]
                { ("public", "Public"), ("secret", "Secret"), ("top", "TopSecret"), ("unlabelled", null), ("unknown", "Bogus") })
                await Rag.IndexAsync(id, Text, label);
            (await Rag.GetDocumentCountAsync()).Should().Be(5);
        }

        public async ValueTask DisposeAsync()
        {
            if (_sqlite is not null) await _sqlite.DisposeAsync();
            if (_path is not null && File.Exists(_path)) File.Delete(_path);
        }
    }
}
