using System.Text.Json;
using FluentAssertions;
using Moq;
using Ashlar.Abstractions;
using Ashlar.BackgroundAgents.RAG;
using Xunit;

namespace Ashlar.Tests.BackgroundAgents.RAG;

/// <summary>Tests for rag tool.</summary>
public class RAGToolTests
{
    [Fact]
    public void Id_IsRagSearch()
    {
        var tool = new RAGTool(Mock.Of<IRAGService>());
        tool.Id.Should().Be("rag_search");
    }

    [Fact]
    public void Schema_DescribesQuery()
    {
        var tool = new RAGTool(Mock.Of<IRAGService>());
        tool.Schema.Id.Should().Be("rag_search");
        tool.Schema.Description.Should().Contain("RAG");
    }

    [Fact]
    public async Task InvokeAsync_CallsRAGService_AndReturnsPayload()
    {
        var service = new Mock<IRAGService>();
        service.Setup(s => s.SearchAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<double>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new VectorSearchResult("doc1", "found text", 0.9, null) });
        var tool = new RAGTool(service.Object);
        var args = JsonSerializer.SerializeToElement(new { query = "test query" });
        var call = new ToolCall("rag_search", args);
        var snapshot = new WorldSnapshot(0, new Dictionary<string, object?>());

        var result = await tool.InvokeAsync(call, snapshot, default);

        result.Delta.Log.Should().Contain(l => l.Contains("RAG search"));
        result.Payload.Should().NotBeNull();
        service.Verify(s => s.SearchAsync("test query", 5, 0.7, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    // This tool is the caller that had to change with the stores. The query text arrives in the
    // model's own JSON arguments and is never validated, minScore likewise -- a model-supplied 0
    // overrides the safe default of 0.7 -- and the stores now REFUSE a query whose embedding has
    // zero magnitude instead of scoring it as 0.0 against everything. InvokeAsync had no catch,
    // so that refusal would have escaped into the agent loop as an unhandled exception.
    //
    // Two things are asserted, and the second is the point. The loop survives; and the outcome
    // is reported as a refusal rather than as a result. The old behaviour logged
    // `RAG search: query='', results=5` -- five arbitrary documents entering the agent's context
    // in exactly the shape of a successful retrieval -- and an empty payload would read to the
    // model as "the knowledge base has nothing on this", which is a different and unearned
    // claim.
    [Fact]
    public async Task InvokeAsync_WhenTheStoreRefusesAnUnrankableQuery_ReportsARefusal_NotAnEmptyResult()
    {
        var service = new Mock<IRAGService>();
        service.Setup(s => s.SearchAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<double>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ArgumentException(
                "The query embedding has zero magnitude, so it cannot be ranked against anything.",
                "embedding"));
        var tool = new RAGTool(service.Object);
        var args = JsonSerializer.SerializeToElement(new { query = "!!!", minScore = 0.0 });
        var call = new ToolCall("rag_search", args);
        var snapshot = new WorldSnapshot(0, new Dictionary<string, object?>());

        var act = async () => await tool.InvokeAsync(call, snapshot, default);
        var result = await act.Should().NotThrowAsync("the agent loop must not die on a malformed query");

        result.Subject.Delta.Log.Should().ContainSingle()
            .Which.Should().Contain("REFUSED").And.Contain("zero magnitude");
        JsonSerializer.Serialize(result.Subject.Payload)
            .Should().Contain(
                "Refused",
                "the payload must say it was refused, not hand the model an empty result list");
    }

    // Positive control for the test above: a fault that is NOT a refusal is a genuine error and
    // must not be swallowed into a tidy tool result. Without this, widening the catch to
    // Exception would pass the refusal test while hiding every real failure behind the same
    // reassuring shape.
    [Fact]
    public async Task InvokeAsync_DoesNotSwallowAnUnrelatedFailure()
    {
        var service = new Mock<IRAGService>();
        service.Setup(s => s.SearchAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<double>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("the vector store is unreachable"));
        var tool = new RAGTool(service.Object);
        var call = new ToolCall("rag_search", JsonSerializer.SerializeToElement(new { query = "real query" }));
        var snapshot = new WorldSnapshot(0, new Dictionary<string, object?>());

        var act = async () => await tool.InvokeAsync(call, snapshot, default);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
