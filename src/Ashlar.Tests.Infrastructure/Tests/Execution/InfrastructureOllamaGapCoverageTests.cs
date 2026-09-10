using Ashlar.Agents.TestKit;
using System.Net;
using System.Text;
using FluentAssertions;
using Ashlar.Infrastructure.Execution.Ollama;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Execution;

/// <summary>Tests for infrastructure ollama gap coverage.</summary>
public class InfrastructureOllamaGapCoverageTests
{
    [Fact]
    public async Task OllamaProvider_execute_chat_succeeds_with_text_and_vision_payload()
    {
        var handler = new StubHttpMessageHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/tags")
            {
                /// <summary>Json.</summary>
                /// <param name=""llava:7b"">"llava:7b".</param>
                /// <param name="}"""">}""".</param>
                return Json("""{ "models": [ { "name": "llava:7b", "size": 100 } ] }""");
            }

            request.RequestUri.AbsolutePath.Should().Be("/api/chat");
            /// <summary>Json.</summary>
            /// <param name="}"""">}""".</param>
            return Json("""{ "message": { "content": "hello from ollama" } }""");
        });

        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:11434/") };
        var sut = new OllamaProvider(client, "http://localhost:11434");
        await sut.InitializeAsync(CancellationToken.None);

        var text = await sut.ExecuteChatAsync("llava", "system", "user", null);
        text.IsSuccess.Should().BeTrue();
        text.Value.Should().Be("hello from ollama");

        var vision = await sut.ExecuteChatAsync(
            "llava",
            "describe",
            "image",
            new[] { new byte[] { 1, 2, 3 }, Array.Empty<byte>() });
        vision.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task OllamaProvider_validate_model_handles_empty_and_prefix_resolution()
    {
        using var client = new HttpClient(new StubHttpMessageHandler(_ => Json("""
        {
          "models": [
            { "name": "llama3.2:3b", "size": 1 },
            { "name": "mixtral:8x7b", "size": 2 }
          ]
        }
        """))) { BaseAddress = new Uri("http://localhost:11434/") };

        var sut = new OllamaProvider(client);
        await sut.InitializeAsync(CancellationToken.None);

        sut.ValidateModel("").Error!.Code.Should().Be("OLLAMA_MODEL_REQUIRED");
        sut.ValidateModel("llama3.2").Value!.Name.Should().Be("llama3.2:3b");
        sut.ValidateModel("mixtral").Value!.Name.Should().Be("mixtral:8x7b");
    }

    [Fact]
    public async Task OllamaProvider_refresh_handles_http_and_json_errors()
    {
        using var httpClient = new HttpClient(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)))
        {
            BaseAddress = new Uri("http://localhost:11434/"),
        };
        var httpError = new OllamaProvider(httpClient);
        (await httpError.RefreshModelsAsync()).Error!.Code.Should().Be("OLLAMA_TAGS_HTTP_ERROR");

        using var jsonClient = new HttpClient(new StubHttpMessageHandler(_ => Json("not-json"))) { BaseAddress = new Uri("http://localhost:11434/") };
        var jsonError = new OllamaProvider(jsonClient);
        (await jsonError.RefreshModelsAsync()).Error!.Code.Should().Be("OLLAMA_TAGS_INVALID_JSON");
    }

    [Fact]
    public async Task OllamaProvider_refresh_handles_network_errors()
    {
        using var client = new HttpClient(new StubHttpMessageHandler(_ => throw new HttpRequestException("down"))) { BaseAddress = new Uri("http://localhost:11434/") };
        var sut = new OllamaProvider(client);
        (await sut.RefreshModelsAsync()).Error!.Code.Should().Be("OLLAMA_UNREACHABLE");
        sut.IsAvailable.Should().BeFalse();
    }

    [Fact]
    public async Task OllamaProvider_chat_handles_http_invalid_response_and_cancel()
    {
        using var client = new HttpClient(new StubHttpMessageHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/tags")
                /// <summary>Json.</summary>
                /// <param name=""m:latest"">"m:latest".</param>
                /// <param name="}"""">}""".</param>
                return Json("""{ "models": [ { "name": "m:latest", "size": 1 } ] }""");
            if (request.RequestUri.AbsolutePath == "/api/chat")
                /// <summary>Http response message.</summary>
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            /// <summary>Json.</summary>
            return Json("{}");
        })) { BaseAddress = new Uri("http://localhost:11434/") };

        var sut = new OllamaProvider(client);
        await sut.InitializeAsync(CancellationToken.None);
        (await sut.ExecuteChatAsync("m", "s", "u", null)).Error!.Code.Should().Be("OLLAMA_CHAT_MODEL_NOT_FOUND");

        using var badJsonClient = new HttpClient(new StubHttpMessageHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/tags")
                /// <summary>Json.</summary>
                /// <param name=""m:latest"">"m:latest".</param>
                /// <param name="}"""">}""".</param>
                return Json("""{ "models": [ { "name": "m:latest", "size": 1 } ] }""");
            /// <summary>Json.</summary>
            /// <param name="}"""">}""".</param>
            return Json("""{ "unexpected": true }""");
        })) { BaseAddress = new Uri("http://localhost:11434/") };

        var badJson = new OllamaProvider(badJsonClient);
        await badJson.InitializeAsync(CancellationToken.None);
        (await badJson.ExecuteChatAsync("m", "s", "u", null)).Error!.Code.Should().Be("OLLAMA_CHAT_INVALID_RESPONSE");

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        using var cancelClient = new HttpClient(new StubHttpMessageHandler(_ => throw new TaskCanceledException())) { BaseAddress = new Uri("http://localhost:11434/") };
        var cancelled = new OllamaProvider(cancelClient);
        (await cancelled.RefreshModelsAsync(cts.Token)).Error!.Code.Should().Be("OLLAMA_TAGS_CANCELLED");
    }

    [Fact]
    public async Task OllamaProvider_initialize_tolerates_failed_initial_refresh()
    {
        using var client = new HttpClient(new StubHttpMessageHandler(_ => throw new HttpRequestException("offline")))
        {
            BaseAddress = new Uri("http://localhost:11434/"),
        };

        var sut = new OllamaProvider(client, logger: null);
        await sut.InitializeAsync(CancellationToken.None);
        sut.IsAvailable.Should().BeFalse();
    }

    /// <summary>Json.</summary>
    /// <param name="json">Json.</param>
    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    /// <summary>Tests for fake handler.</summary>
}
