using IProviderFactory = Ashlar.Infrastructure.Execution.IProviderFactory;
using Ashlar.Abstractions.Security.Egress;
using Ashlar.Core.Application.Execution.Ports;
using Ashlar.Infrastructure.Execution;
using Ashlar.Infrastructure.Execution.LoadPolicy;
using FluentAssertions;
using Moq;
using Xunit;
using System.Reflection;
using Ashlar.Agents.TestKit;
using Ashlar.Core.Application.Ephemeral.Ports;
using Ashlar.Infrastructure.Execution.Ollama;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

[Trait("Category", "Certification")]
[Collection("EnvironmentVariables")]
public sealed class EgressProviderFallbackTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Real_Ollama_chat_preserves_refusal_instead_of_an_unreachable_result(bool wrapped)
    {
        var refusal = Refusal();
        var sends = 0;
        using var client = new HttpClient(StubHttpMessageHandler.FromSync((request, _) =>
        {
            if (request.Method == HttpMethod.Get)
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                { Content = new StringContent("""{"models":[{"name":"m:latest","size":1}]}""") };
            sends++;
            throw wrapped ? new HttpRequestException("private-canary", refusal) : refusal;
        }));
        var provider = new OllamaProvider(client, "https://remote.example");
        (await provider.RefreshModelsAsync()).IsSuccess.Should().BeTrue();
        var error = await Record.ExceptionAsync(() => provider.ExecuteChatAsync("m:latest", "system", "user", null));
        error.Should().BeSameAs(refusal);
        sends.Should().Be(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Real_Ollama_health_refusal_survives_provider_availability_conversion(bool wrapped)
    {
        const string baseUrl = "https://remote.example";
        var refusal = Refusal();
        var lifecycle = new Mock<IEphemeralModelLifecycle>();
        lifecycle.Setup(x => x.GetBaseUrlAsync(It.IsAny<CancellationToken>())).ThrowsAsync(refusal);
        var inner = new ProviderFactory(NullLogger<ProviderFactory>.Instance, lifecycle.Object);
        await inner.OllamaWarmup;
        lifecycle.Setup(x => x.GetBaseUrlAsync(It.IsAny<CancellationToken>())).ReturnsAsync(baseUrl);
        var calls = 0;
        using var client = new HttpClient(StubHttpMessageHandler.FromSync((_, _) =>
        {
            calls++;
            throw wrapped ? new HttpRequestException("private-canary", refusal) : refusal;
        }));
        typeof(ProviderFactory).GetField("_ollamaProvider", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(inner, new OllamaProvider(client, baseUrl));
        typeof(ProviderFactory).GetField("_ollamaProviderBaseUrl", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(inner, baseUrl);
        var policy = new Mock<ILoadPolicy>();
        policy.Setup(x => x.ResolveProvider(inner)).Returns(() => inner.IsProviderAvailable("ollama") ? "ollama" : null);
        var factory = new AdaptiveProviderFactory(inner, policy.Object);
        var error = await Record.ExceptionAsync(() => factory.ExecuteLLMAsync("ignored", "system", "user", new object()));
        error.Should().BeSameAs(refusal);
        calls.Should().Be(1);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Refused_attempt_keeps_first_refusal_unless_an_alternative_succeeds(bool vision, bool succeeds)
    {
        var first = Refusal();
        var later = Refusal();
        var inner = new Mock<IProviderFactory>();
        inner.Setup(x => x.IsProviderAvailable(It.IsAny<string>())).Returns(true);
        Task<string> Execute(string provider) => provider == "openai" && succeeds
            ? Task.FromResult("allowed") : Task.FromException<string>(provider == "ollama" ? first : later);
        inner.Setup(x => x.ExecuteLLMAsync(It.IsAny<string>(), "system", "user", It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .Returns((string p, string s, string u, object c, CancellationToken t) => Execute(p));
        inner.Setup(x => x.ExecuteVisionAsync(It.IsAny<string>(), "system", "user", It.IsAny<byte[]>(), It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .Returns((string p, string s, string u, byte[] b, object c, CancellationToken t) => Execute(p));
        var policy = new Mock<ILoadPolicy>();
        policy.Setup(x => x.ResolveProvider(inner.Object)).Returns("ollama");
        var factory = new AdaptiveProviderFactory(inner.Object, policy.Object);
        string? response = null;
        var error = await Record.ExceptionAsync(async () => response = vision
            ? await factory.ExecuteVisionAsync("ignored", "system", "user", Array.Empty<byte>(), new object())
            : await factory.ExecuteLLMAsync("ignored", "system", "user", new object()));
        if (succeeds) { error.Should().BeNull(); response.Should().Be("allowed"); }
        else error.Should().BeSameAs(first);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Refused_availability_probe_is_retained_without_blocking_an_available_provider(bool alternative)
    {
        var refusal = Refusal();
        var inner = new Mock<IProviderFactory>();
        inner.Setup(x => x.IsProviderAvailable(It.IsAny<string>())).Returns((string p) => alternative && p == "openai");
        inner.Setup(x => x.IsProviderAvailable("ollama")).Throws(new HttpRequestException("wrapped", refusal));
        inner.Setup(x => x.ExecuteLLMAsync("openai", "system", "user", It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("allowed");
        var previous = Environment.GetEnvironmentVariable("ASHLAR_LOAD_PREFERENCE");
        try
        {
            Environment.SetEnvironmentVariable("ASHLAR_LOAD_PREFERENCE", "edge");
            var factory = new AdaptiveProviderFactory(inner.Object, new PreferenceLoadPolicy());
            string? response = null;
            var error = await Record.ExceptionAsync(async () => response = await factory.ExecuteLLMAsync("ignored", "system", "user", new object()));
            inner.Verify(x => x.IsProviderAvailable("ollama"), Times.Once);
            if (alternative) { error.Should().BeNull(); response.Should().Be("allowed"); }
            else error.Should().BeSameAs(refusal);
        }
        finally { Environment.SetEnvironmentVariable("ASHLAR_LOAD_PREFERENCE", previous); }
    }

    [Fact]
    public async Task Refusal_state_does_not_escape_into_a_concurrent_invocation()
    {
        var refusal = Refusal();
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiting = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var inner = new Mock<IProviderFactory>();
        inner.Setup(x => x.IsProviderAvailable(It.IsAny<string>())).Returns(true);
        inner.Setup(x => x.ExecuteLLMAsync(It.IsAny<string>(), "system", It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .Returns((string p, string s, string u, object c, CancellationToken t) =>
            {
                if (u == "refused" && p == "ollama") return Task.FromException<string>(refusal);
                if (u == "refused" && p == "openai") { waiting.TrySetResult(true); return release.Task; }
                return Task.FromException<string>(new IOException("ordinary failure"));
            });
        var policy = new Mock<ILoadPolicy>();
        policy.Setup(x => x.ResolveProvider(inner.Object)).Returns("ollama");
        var factory = new AdaptiveProviderFactory(inner.Object, policy.Object);
        var first = Record.ExceptionAsync(() => factory.ExecuteLLMAsync("ignored", "system", "refused", new object()));
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = await Record.ExceptionAsync(() => factory.ExecuteLLMAsync("ignored", "system", "ordinary", new object()));
        release.TrySetException(new IOException("later failure"));
        (await first).Should().BeSameAs(refusal);
        second.Should().BeOfType<ModelUnavailableException>();
    }

    [Fact]
    public async Task Multi_frame_vision_preserves_the_typed_refusal()
    {
        var refusal = Refusal();
        var inner = new Mock<IProviderFactory>();
        inner.Setup(x => x.ExecuteVisionMultiFrameAsync("ollama", "system", "user", It.IsAny<IReadOnlyList<byte[]>>(), It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("wrapped", refusal));
        var policy = new Mock<ILoadPolicy>();
        policy.Setup(x => x.ResolveProvider(inner.Object)).Returns("ollama");
        var factory = new AdaptiveProviderFactory(inner.Object, policy.Object);
        var error = await Record.ExceptionAsync(() => factory.ExecuteVisionMultiFrameAsync("ignored", "system", "user", Array.Empty<byte[]>(), new object()));
        error.Should().BeSameAs(refusal);
    }

    private static EgressRefusedException Refusal() => new(new EgressGuard("full", "enforce").Evaluate(
        new EgressRequest(EgressFamilies.Http, "provider-refusal", new Uri("https://remote.example"))));
}
