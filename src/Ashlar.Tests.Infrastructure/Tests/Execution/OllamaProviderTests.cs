using Ashlar.Agents.TestKit;
using System.Net;
using System.Text;
using FluentAssertions;
using Ashlar.Infrastructure.Execution.Ollama;
using Xunit;
using Ashlar.Tests.Infrastructure.Helpers;

namespace Ashlar.Tests.Infrastructure.Tests.Execution;

/// <summary>Tests for ollama provider.</summary>
public sealed class OllamaProviderTests
{
    [Fact]
    public async Task InitializeAsync_LoadsManifest_FromTagsEndpoint()
    {
        var handler = new StubHttpMessageHandler(_ => JsonResponse("""
        {
          "models": [
            { "name": "llama3.2:3b", "size": 1234, "modified_at": "2026-01-05T12:00:00Z" },
            { "name": "llava:7b", "size": 9876, "modified_at": "2026-01-06T12:00:00Z" }
          ]
        }
        """));

        using var httpClient = new HttpClient(handler);
        var sut = new OllamaProvider(httpClient, "http://localhost:11434");
        await sut.InitializeAsync(CancellationToken.None);

        sut.IsAvailable.Should().BeTrue();
        sut.Manifest.Select(model => model.Name).Should().Contain(new[] { "llama3.2:3b", "llava:7b" });
        sut.Manifest.Single(model => model.Name == "llama3.2:3b").Size.Should().Be(1234);
        handler.Requests.Should().ContainSingle();
        handler.Requests[0].RequestUri!.AbsolutePath.Should().Be("/api/tags");
    }

    [Fact]
    public async Task RefreshModelsAsync_UpdatesCachedManifest()
    {
        var responses = new Queue<HttpResponseMessage>(new[]
        {
            JsonResponse("""{ "models": [ { "name": "llama3.2:3b", "size": 1234 } ] }"""),
            JsonResponse("""{ "models": [ { "name": "qwen2.5:7b", "size": 4321 } ] }""")
        });

        var handler = new StubHttpMessageHandler(_ => responses.Dequeue());
        using var httpClient = new HttpClient(handler);
        var sut = new OllamaProvider(httpClient, "http://localhost:11434");
        await sut.InitializeAsync(CancellationToken.None);

        var refreshResult = await sut.RefreshModelsAsync();

        refreshResult.IsSuccess.Should().BeTrue();
        sut.Manifest.Select(model => model.Name).Should().ContainSingle().Which.Should().Be("qwen2.5:7b");
        handler.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task ValidateModel_ResolvesBareFamilyName_ToLatestTag()
    {
        var handler = new StubHttpMessageHandler(_ => JsonResponse("""
        {
          "models": [
            { "name": "llama3.1:latest", "size": 1234 }
          ]
        }
        """));
        using var httpClient = new HttpClient(handler);
        var sut = new OllamaProvider(httpClient, "http://localhost:11434");
        await sut.InitializeAsync(CancellationToken.None);

        var validation = sut.ValidateModel("llama3.1");

        validation.IsSuccess.Should().BeTrue();
        validation.Value.Should().NotBeNull();
        validation.Value!.Name.Should().Be("llama3.1:latest");
    }

    [Fact]
    public async Task ValidateModel_WhenMissing_ReturnsStructuredResultError()
    {
        var handler = new StubHttpMessageHandler(_ => JsonResponse("""
        {
          "models": [
            { "name": "llama3.2:3b", "size": 1234 }
          ]
        }
        """));
        using var httpClient = new HttpClient(handler);
        var sut = new OllamaProvider(httpClient, "http://localhost:11434");
        await sut.InitializeAsync(CancellationToken.None);

        var validation = sut.ValidateModel("mistral:latest");

        validation.IsSuccess.Should().BeFalse();
        validation.Error.Should().NotBeNull();
        validation.Error!.Code.Should().Be("OLLAMA_MODEL_NOT_FOUND");
        validation.Error.Metadata.Should().NotBeNull();
        validation.Error.Metadata!["requestedModel"].Should().Be("mistral:latest");
    }

    [Fact]
    public async Task CheckHealthAsync_WhenUnreachable_MarksProviderUnavailable()
    {
        var requestCount = 0;
        var handler = new StubHttpMessageHandler(_ =>
        {
            requestCount++;
            if (requestCount == 1)
            {
                /// <summary>Json response.</summary>
                /// <param name=""llama3.2:3b"">"llama3.2:3b".</param>
                /// <param name="}"""">}""".</param>
                return JsonResponse("""{ "models": [ { "name": "llama3.2:3b", "size": 1234 } ] }""");
            }

            /// <summary>Http request exception.</summary>
            /// <param name="refused"">Refused".</param>
            throw new HttpRequestException("Connection refused");
        });

        using var httpClient = new HttpClient(handler);
        var sut = new OllamaProvider(httpClient, "http://localhost:11434");
        await sut.InitializeAsync(CancellationToken.None);

        var health = await sut.CheckHealthAsync();

        health.IsSuccess.Should().BeFalse();
        health.Error.Should().NotBeNull();
        health.Error!.Code.Should().Be("OLLAMA_UNREACHABLE");
        sut.IsAvailable.Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteChatAsync_WhenModelMissing_DoesNotCallChatEndpoint()
    {
        var handler = new StubHttpMessageHandler(request =>
        {
            request.RequestUri!.AbsolutePath.Should().Be("/api/tags");
            return JsonResponse("""
            {
              "models": [
                { "name": "llama3.2:3b", "size": 1234 }
              ]
            }
            """);
        });

        using var httpClient = new HttpClient(handler);
        var sut = new OllamaProvider(httpClient, "http://localhost:11434");
        await sut.InitializeAsync(CancellationToken.None);

        var result = await sut.ExecuteChatAsync("gemma3:4b", "system", "user", null);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().NotBeNull();
        result.Error!.Code.Should().Be("OLLAMA_MODEL_NOT_FOUND");
        handler.Requests.Should().ContainSingle(request => request.RequestUri!.AbsolutePath == "/api/tags");
        handler.Requests.Should().NotContain(request => request.RequestUri!.AbsolutePath == "/api/chat");
    }

    [Fact]
    public async Task Constructor_PerformsNoNetworkIO()
    {
        // #567: the constructor used to block a thread-pool thread on /api/tags. It must now do
        // no I/O at all; the manifest arrives only when InitializeAsync is awaited.
        var handler = new StubHttpMessageHandler(_ => JsonResponse("""
        {
          "models": [
            { "name": "llama3.2:3b", "size": 1234 }
          ]
        }
        """));

        using var httpClient = new HttpClient(handler);
        var sut = new OllamaProvider(httpClient, "http://localhost:11434");

        handler.Requests.Should().BeEmpty();
        sut.IsAvailable.Should().BeFalse();
        sut.LastRefreshUtc.Should().BeNull();

        var initialization = await sut.InitializeAsync(CancellationToken.None);

        initialization.IsSuccess.Should().BeTrue();
        handler.Requests.Should().ContainSingle();
        handler.Requests[0].RequestUri!.AbsolutePath.Should().Be("/api/tags");
        sut.IsAvailable.Should().BeTrue();
        sut.LastRefreshUtc.Should().NotBeNull();
    }

    [Fact(Timeout = TestTimeouts.Quick)]
    public async Task InitializeAsync_HonoursCancellation()
    {
        // The stub only answers once the caller's token fires, so the request hangs exactly the
        // way an accepted-but-silent Ollama socket does. RefreshModelsAsync documents cancellation
        // as a failed Result (OLLAMA_TAGS_CANCELLED), not an exception; InitializeAsync inherits that.
        var handler = new StubHttpMessageHandler(async (_, ct) =>
        {
            var pending = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = ct.Register(() => pending.TrySetCanceled(ct));
            return await pending.Task;
        });

        using var httpClient = new HttpClient(handler);
        var sut = new OllamaProvider(httpClient, "http://localhost:11434");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var initialization = await sut.InitializeAsync(cts.Token);

        initialization.IsSuccess.Should().BeFalse();
        initialization.Error.Should().NotBeNull();
        initialization.Error!.Code.Should().Be("OLLAMA_TAGS_CANCELLED");
        sut.IsAvailable.Should().BeFalse();
        handler.Requests.Should().ContainSingle(request => request.RequestUri!.AbsolutePath == "/api/tags");
    }

    private static HttpResponseMessage JsonResponse(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    /// <summary>Tests for fake http message handler.</summary>
}
