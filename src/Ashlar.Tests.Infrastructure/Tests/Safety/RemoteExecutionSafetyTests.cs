using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Ashlar.Infrastructure.Execution;
using Ashlar.Infrastructure.Execution.LoadPolicy;
using Ashlar.Infrastructure.Testing.ExecutionPlatform;
using Xunit;

using Ashlar.Core.Application.Execution.Ports;

namespace Ashlar.Tests.Infrastructure.Tests.Safety;

/// <summary>
/// Safety tests for remote execution and provider failover.
/// Ensures explicit failure when dependencies are unavailable — no silent fallback.
/// </summary>
[Trait("Category", "Safety")]
[Trait("Category", "Unit")]
[Collection("EnvironmentVariables")]
public sealed class RemoteExecutionSafetyTests
{
    [Fact]
    public async Task RemoteExecutionPlatform_WhenApiUnreachable_ReturnsFailedResult_NotSilent()
    {
        using var client = new HttpClient
        {
            BaseAddress = new Uri("http://localhost:0"),
            Timeout = TimeSpan.FromSeconds(1),
        };
        var platform = new RemoteExecutionPlatform(client, NullLogger<RemoteExecutionPlatform>.Instance);

        var result = await platform.RunContainerAsync("test:latest", new[] { "echo", "hi" });

        result.Success.Should().BeFalse("unreachable API must return explicit failure");
        result.StandardError.Should().NotBeNullOrEmpty("error must be communicated");
    }

    [Fact]
    public async Task RemoteExecutionPlatform_WhenApiUnreachable_IsAvailableAsyncReturnsFalse()
    {
        using var client = new HttpClient
        {
            BaseAddress = new Uri("http://localhost:0"),
            Timeout = TimeSpan.FromSeconds(1),
        };
        var platform = new RemoteExecutionPlatform(client, NullLogger<RemoteExecutionPlatform>.Instance);

        var available = await platform.IsAvailableAsync();

        available.Should().BeFalse("unreachable API must report unavailable");
    }

    [Fact]
    public async Task AdaptiveProviderFactory_WhenNoProviderAvailable_ThrowsModelUnavailableException()
    {
        var failingInner = new FailingProviderFactory();
        var loadPolicy = new PreferEdgeLoadPolicy();
        var factory = new AdaptiveProviderFactory(failingInner, loadPolicy, null);

        var act = () => factory.ExecuteLLMAsync("ollama", "sys", "user", new { }, default);

        await act.Should().ThrowAsync<ModelUnavailableException>()
            .WithMessage("*No model available*");
    }

    [Fact]
    public async Task AdaptiveProviderFactory_FailoverMidExecution_ThrowsWhenAllProvidersFail()
    {
        var callCount = 0;
        var failAfterFirst = new FailAfterFirstCallProvider(() => callCount++);
        var loadPolicy = new PreferEdgeLoadPolicy();
        var factory = new AdaptiveProviderFactory(failAfterFirst, loadPolicy, null);

        var first = await factory.ExecuteLLMAsync("ollama", "sys", "first", new { });
        first.Should().NotBeNullOrEmpty();

        var act = () => factory.ExecuteLLMAsync("ollama", "sys", "second", new { });
        await act.Should().ThrowAsync<ModelUnavailableException>()
            .WithMessage("*No model available*");
    }

    [Fact]
    public async Task AdaptiveProviderFactory_NeverFallsBackToMock_WhenMockNotExplicitlyEnabled()
    {
        var prev = Environment.GetEnvironmentVariable("ASHLAR_ALLOW_MOCK");
        try
        {
            Environment.SetEnvironmentVariable("ASHLAR_ALLOW_MOCK", null);
            var failingInner = new FailingProviderFactory();
            var loadPolicy = new PreferEdgeLoadPolicy();
            var factory = new AdaptiveProviderFactory(failingInner, loadPolicy, null);

            var ex = await Assert.ThrowsAsync<ModelUnavailableException>(
                () => factory.ExecuteLLMAsync("ollama", "sys", "user", new { }, default));

            ex.Message.Contains("mock", StringComparison.OrdinalIgnoreCase).Should().BeFalse("must not fall back to mock");
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASHLAR_ALLOW_MOCK", prev);
        }
    }

    private sealed class FailAfterFirstCallProvider : Ashlar.Infrastructure.Execution.IProviderFactory
    {
        private readonly Func<int> _getCallCount;

        public FailAfterFirstCallProvider(Func<int> getCallCount) => _getCallCount = getCallCount;

        public bool IsProviderAvailable(string provider) => true;

        public Task<string> ExecuteLLMAsync(string provider, string systemPrompt, string userPrompt, object config, CancellationToken cancellationToken = default)
        {
            if (_getCallCount() > 0)
                throw new ModelUnavailableException("Provider failed on second call");
            return Task.FromResult("ok");
        }

        public Task<string> ExecuteVisionAsync(string provider, string systemPrompt, string userPrompt, byte[] imageBytes, object config, CancellationToken cancellationToken = default) =>
            throw new ModelUnavailableException("No vision");

        public Task<string> ExecuteVisionMultiFrameAsync(string provider, string systemPrompt, string userPrompt, IReadOnlyList<byte[]> frameBytes, object config, CancellationToken cancellationToken = default) =>
            throw new ModelUnavailableException("No vision");

        public Task<string> ExecuteVideoAsync(string systemPrompt, string userPrompt, IReadOnlyList<byte[]> frameBytes, object config, CancellationToken cancellationToken = default) =>
            throw new ModelUnavailableException("No video");

        public Task EnsureOllamaReachableAsync(bool requireVisionModel, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class FailingProviderFactory : Ashlar.Infrastructure.Execution.IProviderFactory
    {
        public bool IsProviderAvailable(string provider) => false;

        public Task<string> ExecuteLLMAsync(string provider, string systemPrompt, string userPrompt, object config, CancellationToken cancellationToken = default) =>
            throw new ModelUnavailableException("No model available.");

        public Task<string> ExecuteVisionAsync(string provider, string systemPrompt, string userPrompt, byte[] imageBytes, object config, CancellationToken cancellationToken = default) =>
            throw new ModelUnavailableException("No vision model available.");

        public Task<string> ExecuteVisionMultiFrameAsync(string provider, string systemPrompt, string userPrompt, IReadOnlyList<byte[]> frameBytes, object config, CancellationToken cancellationToken = default) =>
            throw new ModelUnavailableException("No vision model available.");

        public Task<string> ExecuteVideoAsync(string systemPrompt, string userPrompt, IReadOnlyList<byte[]> frameBytes, object config, CancellationToken cancellationToken = default) =>
            throw new ModelUnavailableException("No video model available.");

        public Task EnsureOllamaReachableAsync(bool requireVisionModel, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class PreferEdgeLoadPolicy : ILoadPolicy
    {
        public LoadPreference GetPreference() => LoadPreference.Edge;
        public string? ResolveProvider(Ashlar.Infrastructure.Execution.IProviderFactory factory) => "ollama";
    }
}
