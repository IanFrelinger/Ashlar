using Ashlar.BackgroundAgents.RAG;
using Ashlar.CLI.Commands.BackgroundAgent;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Ashlar.Tests.CLI.Tests.Commands;

/// <summary>
/// <c>rag index</c> no longer stores an unlabelled file as Public, and <c>rag search</c> no longer
/// searches an omitted clearance as TopSecret. The index side REFUSES without a label (rather than
/// defaulting to the top, which would make the knowledge base silently unsearchable); the search
/// side passes the omission through to the store, which applies the floor.
/// </summary>
public sealed class RAGCommandFailClosedTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Index_without_a_sensitivity_is_refused_and_indexes_nothing(string? sensitivity)
    {
        var indexer = new RecordingIndexer();
        var command = new RAGCommand(new RecordingRag(), indexer, NullLogger<RAGCommand>.Instance);

        var (exit, stdout) = await CaptureAsync(() => command.IndexAsync(new[] { "docs/" }, sensitivity, formatJson: true));

        exit.Should().Be(1);
        indexer.Calls.Should().BeEmpty("nothing may be indexed without a deliberate label");
        stdout.Should().Contain("--sensitivity is required");
    }

    [Fact]
    public async Task Index_with_an_unknown_sensitivity_is_refused_and_indexes_nothing()
    {
        var indexer = new RecordingIndexer();
        var command = new RAGCommand(new RecordingRag(), indexer, NullLogger<RAGCommand>.Instance);

        var (exit, stdout) = await CaptureAsync(() => command.IndexAsync(new[] { "docs/" }, "Secrte", formatJson: false));

        exit.Should().Be(1);
        indexer.Calls.Should().BeEmpty();
        stdout.Should().Contain("'Secrte' is not a known sensitivity level");
    }

    // POSITIVE CONTROL for both refusals: a known label indexes, and reaches the indexer as given.
    [Fact]
    public async Task Index_with_a_known_sensitivity_indexes_with_that_label()
    {
        var indexer = new RecordingIndexer();
        var command = new RAGCommand(new RecordingRag(), indexer, NullLogger<RAGCommand>.Instance);

        var (exit, _) = await CaptureAsync(() => command.IndexAsync(new[] { "docs/" }, "Secret", formatJson: true));

        exit.Should().Be(0);
        indexer.Calls.Should().ContainSingle().Which.Should().Be("Secret");
    }

    [Fact]
    public async Task Search_without_a_clearance_passes_the_omission_through_and_reports_the_floor()
    {
        var rag = new RecordingRag();
        var command = new RAGCommand(rag, new RecordingIndexer(), NullLogger<RAGCommand>.Instance);

        var (exit, stdout) = await CaptureAsync(() => command.SearchAsync("incident playbook", 5, 0.0, null, formatJson: true));

        exit.Should().Be(0);
        rag.Clearances.Should().ContainSingle().Which.Should().BeNull(
            "the CLI must not substitute a clearance for an omitted one -- it used to reach the store as TopSecret");
        stdout.Should().Contain("\"clearance\": \"Public\"");
    }

    [Fact]
    public async Task Search_with_an_unknown_clearance_is_refused_and_searches_nothing()
    {
        var rag = new RecordingRag();
        var command = new RAGCommand(rag, new RecordingIndexer(), NullLogger<RAGCommand>.Instance);

        var (exit, stdout) = await CaptureAsync(() => command.SearchAsync("incident playbook", 5, 0.0, "Secrte", formatJson: false));

        exit.Should().Be(1);
        rag.Clearances.Should().BeEmpty();
        stdout.Should().Contain("'Secrte' is not a known sensitivity level");
    }

    // End to end through a real store: an omitted clearance does not reach the Secret document.
    [Fact]
    public async Task Search_without_a_clearance_does_not_return_a_Secret_document()
    {
        var rag = new RAGService(new InMemoryVectorStore(), new TokenEmbeddingGenerator());
        await rag.IndexAsync("pub", "shared incident playbook", "Public");
        await rag.IndexAsync("sec", "shared incident playbook", "Secret");
        var command = new RAGCommand(rag, new KnowledgeBaseIndexer(rag), NullLogger<RAGCommand>.Instance);

        var (omitted, omittedOut) = await CaptureAsync(() => command.SearchAsync("incident playbook", 5, 0.0, null, formatJson: true));
        var (cleared, clearedOut) = await CaptureAsync(() => command.SearchAsync("incident playbook", 5, 0.0, "Secret", formatJson: true));

        omitted.Should().Be(0);
        omittedOut.Should().Contain("\"pub\"").And.NotContain("\"sec\"");
        cleared.Should().Be(0);
        clearedOut.Should().Contain("\"sec\"", "positive control: the Secret document is there for a Secret clearance");
    }

    private static async Task<(int Exit, string Stdout)> CaptureAsync(Func<Task<int>> run)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            var exit = await run();
            return (exit, stdout.ToString() + stderr.ToString());
        }
        finally
        {
            Console.SetOut(ConsoleCapture.Out);
            Console.SetError(ConsoleCapture.Error);
        }
    }

    private sealed class RecordingIndexer : IKnowledgeBaseIndexer
    {
        public List<string?> Calls { get; } = new();

        public Task<int> IndexDocumentsAsync(IEnumerable<string> paths, string? defaultSensitivityLevelName, CancellationToken cancellationToken = default)
        {
            Calls.Add(defaultSensitivityLevelName);
            return Task.FromResult(1);
        }
    }

    private sealed class RecordingRag : IRAGService
    {
        public List<string?> Clearances { get; } = new();

        public Task<IReadOnlyList<VectorSearchResult>> SearchAsync(string query, int maxResults, double minScore, string? maxSensitivityLevelName, CancellationToken cancellationToken = default)
        {
            Clearances.Add(maxSensitivityLevelName);
            return Task.FromResult<IReadOnlyList<VectorSearchResult>>(Array.Empty<VectorSearchResult>());
        }

        public Task IndexAsync(string id, string text, string? sensitivityLevelName, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RemoveAsync(string id, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ClearAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<int> GetDocumentCountAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
    }
}
