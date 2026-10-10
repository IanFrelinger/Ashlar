using Ashlar.Abstractions;
using Ashlar.Abstractions.Security;
using Ashlar.Abstractions.Security.Egress;
using Ashlar.BackgroundAgents.DataSensitivity;
using Ashlar.BackgroundAgents.RAG;
using FluentAssertions;
using Moq;
using System.Text.Json;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

[Trait("Category", "Certification")]
public sealed class CanonicalRagRegistryBoundaryTests
{
    [Theory]
    [InlineData("memory", "SystemHigh")]
    [InlineData("sqlite", "SystemHigh")]
    [InlineData("memory", "Secret")]
    [InlineData("sqlite", "Secret")]
    [InlineData("memory", "top-secret")]
    [InlineData("sqlite", "top-secret")]
    public async Task A_registry_cannot_relabel_data_downward_before_a_read(string kind, string label)
    {
        var registry = Registry(label, DataSensitivityLevels.Public);
        await using var fixture = new Store(kind, registry);
        await fixture.Rag.IndexAsync("control", "incident playbook", "Public");
        await fixture.Rag.IndexAsync("protected", "incident playbook", label);
        using (EgressSubject.Enter("public", new HighWaterMark()))
            (await fixture.Rag.SearchAsync("incident playbook", 10, 0, "SystemHigh"))
                .Select(h => h.Id).Should().BeEquivalentTo(new[] { "control" });
        using (EgressSubject.Enter("trusted", new HighWaterMark()))
            (await fixture.Rag.SearchAsync("incident playbook", 10, 0, "SystemHigh"))
                .Select(h => h.Id).Should().BeEquivalentTo(new[] { "control", "protected" });
    }

    [Theory]
    [InlineData("memory")]
    [InlineData("sqlite")]
    public async Task Canonical_Public_query_limits_cannot_be_remapped_upward(string kind)
    {
        var registry = Registry("Public", DataSensitivityLevels.TopSecret);
        await using var fixture = new Store(kind, registry);
        await fixture.Rag.IndexAsync("secret", "incident playbook", "Secret");
        using var subject = EgressSubject.Enter("trusted", new HighWaterMark());
        (await fixture.Rag.SearchAsync("incident playbook", 10, 0, "Public")).Should().BeEmpty();
        (await fixture.Rag.SearchAsync("incident playbook", 10, 0, "Secret"))
            .Select(h => h.Id).Should().BeEquivalentTo(new[] { "secret" });
        var tool = new RAGTool(fixture.Rag, sensitivityRegistry: registry);
        var result = await tool.InvokeAsync(new ToolCall("rag_search", JsonSerializer.SerializeToElement(new
            { query = "incident playbook", minScore = 0, maxSensitivityLevelName = "Public" })),
            new WorldSnapshot(0, new Dictionary<string, object?> { ["maxDataSensitivity"] = "SystemHigh" }), default);
        JsonSerializer.SerializeToElement(result.Payload).GetArrayLength().Should().Be(0);
    }

    private static IDataSensitivityRegistry Registry(string name, IDataSensitivityLevel replacement)
    {
        var registry = new Mock<IDataSensitivityRegistry>();
        registry.Setup(r => r.GetByName(It.IsAny<string?>()))
            .Returns((string? value) => value == name ? replacement : DataSensitivityLevels.FromName(value));
        registry.Setup(r => r.GetAll()).Returns(DataSensitivityLevels.All);
        return registry.Object;
    }

    private sealed class Store : IAsyncDisposable
    {
        private readonly SqliteVectorStore? _sqlite;
        private readonly string? _path;
        public IRAGService Rag { get; }
        public Store(string kind, IDataSensitivityRegistry registry)
        {
            var authority = new SubjectClearanceAuthority(new Dictionary<string, string?>
                { ["trusted"] = "SystemHigh", ["public"] = "Public" });
            IVectorStore store;
            if (kind == "sqlite")
            {
                _path = Path.Combine(Path.GetTempPath(), "canonical-rag-" + Guid.NewGuid().ToString("N") + ".db");
                store = _sqlite = new SqliteVectorStore(_path, registry, authority);
            }
            else store = new InMemoryVectorStore(registry, authority);
            Rag = new RAGService(store, new TokenEmbeddingGenerator());
        }
        public async ValueTask DisposeAsync()
        {
            if (_sqlite is not null) await _sqlite.DisposeAsync();
            if (_path is not null) File.Delete(_path);
        }
    }
}
