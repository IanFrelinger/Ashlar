using FluentAssertions;
using Microsoft.Extensions.Logging;
using Ashlar.Core.Domain;
using Ashlar.Infrastructure.Execution;
using Ashlar.Tests.Infrastructure.Helpers;
using Xunit;

using Ashlar.Core.Application.Execution.Ports;

namespace Ashlar.Tests.Infrastructure.Tests.Execution;

/// <summary>
/// Edge-case tests for <see cref="ProviderFactory"/> covering provider selection,
/// mock behavior, environment variable handling, and concurrent execution safety.
/// </summary>
[Trait("Category", "E2E")]
[Collection("EnvironmentVariables")]
public sealed class ProviderFactoryEdgeCaseTests
{
    private ProviderFactory CreateFactory()
    {
        var logger = new LoggerFactory().CreateLogger<ProviderFactory>();
        return new ProviderFactory(logger);
    }

    [Fact(Timeout = TestTimeouts.Quick)]
    public async Task MockProvider_WhenNotAllowed_IsUnavailable()
    {
        await Task.CompletedTask;
        var prev = Environment.GetEnvironmentVariable("ASHLAR_ALLOW_MOCK");
        try
        {
            Environment.SetEnvironmentVariable("ASHLAR_ALLOW_MOCK", null);
            var factory = CreateFactory();

            factory.IsProviderAvailable("mock").Should().BeFalse();
            factory.IsProviderAvailable("offline").Should().BeFalse();
            factory.IsProviderAvailable("mock-json").Should().BeFalse();
            factory.IsProviderAvailable("echo").Should().BeFalse();
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASHLAR_ALLOW_MOCK", prev);
        }
    }

    [Fact(Timeout = TestTimeouts.Quick)]
    public async Task MockProvider_WhenAllowed_IsAvailable()
    {
        await Task.CompletedTask;
        var prev = Environment.GetEnvironmentVariable("ASHLAR_ALLOW_MOCK");
        try
        {
            Environment.SetEnvironmentVariable("ASHLAR_ALLOW_MOCK", "1");
            var factory = CreateFactory();

            factory.IsProviderAvailable("mock").Should().BeTrue();
            factory.IsProviderAvailable("offline").Should().BeTrue();
            factory.IsProviderAvailable("mock-json").Should().BeTrue();
            factory.IsProviderAvailable("echo").Should().BeTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASHLAR_ALLOW_MOCK", prev);
        }
    }

    [Fact(Timeout = TestTimeouts.Quick)]
    public async Task UnknownProvider_IsNotAvailable()
    {
        await Task.CompletedTask;
        var factory = CreateFactory();

        factory.IsProviderAvailable("unknown").Should().BeFalse();
        factory.IsProviderAvailable("").Should().BeFalse();
        factory.IsProviderAvailable("   ").Should().BeFalse();
    }

    [Fact(Timeout = TestTimeouts.Quick)]
    public async Task ProviderAvailability_IsCaseInsensitive()
    {
        await Task.CompletedTask;
        var prev = Environment.GetEnvironmentVariable("ASHLAR_ALLOW_MOCK");
        try
        {
            Environment.SetEnvironmentVariable("ASHLAR_ALLOW_MOCK", "1");
            var factory = CreateFactory();

            factory.IsProviderAvailable("MOCK").Should().BeTrue();
            factory.IsProviderAvailable("Mock").Should().BeTrue();
            factory.IsProviderAvailable("ECHO").Should().BeTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASHLAR_ALLOW_MOCK", prev);
        }
    }

    [Fact(Timeout = TestTimeouts.Quick)]
    public async Task MockProvider_DisabledByDefault_ThrowsOnExecution()
    {
        var prevMock = Environment.GetEnvironmentVariable("ASHLAR_ALLOW_MOCK");
        try
        {
            Environment.SetEnvironmentVariable("ASHLAR_ALLOW_MOCK", null);
            var factory = CreateFactory();

            var act = () => factory.ExecuteLLMAsync("mock", "system", "user", new { }, CancellationToken.None);
            await act.Should().ThrowAsync<ModelUnavailableException>()
                .WithMessage("*ASHLAR_ALLOW_MOCK*");
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASHLAR_ALLOW_MOCK", prevMock);
        }
    }

    [Fact(Timeout = TestTimeouts.Quick)]
    public async Task MockProvider_Enabled_ReturnsResponse()
    {
        var prevMock = Environment.GetEnvironmentVariable("ASHLAR_ALLOW_MOCK");
        try
        {
            Environment.SetEnvironmentVariable("ASHLAR_ALLOW_MOCK", "1");
            var factory = CreateFactory();

            var response = await factory.ExecuteLLMAsync("mock", "system", "say hello", new { }, CancellationToken.None);
            response.Should().NotBeNullOrEmpty();
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASHLAR_ALLOW_MOCK", prevMock);
        }
    }

    [Fact(Timeout = TestTimeouts.Quick)]
    public async Task UnknownProvider_ThrowsInvalidOperation()
    {
        var prevMock = Environment.GetEnvironmentVariable("ASHLAR_ALLOW_MOCK");
        try
        {
            Environment.SetEnvironmentVariable("ASHLAR_ALLOW_MOCK", "1");
            var factory = CreateFactory();

            var act = () => factory.ExecuteLLMAsync("nonexistent", "s", "u", new { }, CancellationToken.None);
            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*Unknown*unsupported*");
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASHLAR_ALLOW_MOCK", prevMock);
        }
    }

    [Fact(Timeout = TestTimeouts.Quick)]
    public async Task OpenAiProvider_WithoutApiKey_ThrowsInvalidOperation()
    {
        var prevKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", null);
            var factory = CreateFactory();

            var act = () => factory.ExecuteLLMAsync("openai", "s", "u", new { }, CancellationToken.None);
            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*OPENAI_API_KEY*");
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", prevKey);
        }
    }

    [Fact(Timeout = TestTimeouts.Quick)]
    public async Task AzureProvider_WithoutEndpoint_ThrowsInvalidOperation()
    {
        var prevEndpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT");
        var prevKey = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY");
        var prevDeploy = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT");
        try
        {
            Environment.SetEnvironmentVariable("AZURE_OPENAI_ENDPOINT", null);
            Environment.SetEnvironmentVariable("AZURE_OPENAI_API_KEY", null);
            Environment.SetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT", null);
            var factory = CreateFactory();

            var act = () => factory.ExecuteLLMAsync("azure", "s", "u", new { }, CancellationToken.None);
            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*Azure*env*");
        }
        finally
        {
            Environment.SetEnvironmentVariable("AZURE_OPENAI_ENDPOINT", prevEndpoint);
            Environment.SetEnvironmentVariable("AZURE_OPENAI_API_KEY", prevKey);
            Environment.SetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT", prevDeploy);
        }
    }

    [Fact(Timeout = TestTimeouts.E2E)]
    public async Task ConcurrentMockExecutions_AreThreadSafe()
    {
        var prevMock = Environment.GetEnvironmentVariable("ASHLAR_ALLOW_MOCK");
        try
        {
            Environment.SetEnvironmentVariable("ASHLAR_ALLOW_MOCK", "1");
            var factory = CreateFactory();

            var tasks = Enumerable.Range(0, 50).Select(i =>
                factory.ExecuteLLMAsync("mock", "system", $"prompt {i}", new { }, CancellationToken.None));

            var results = await Task.WhenAll(tasks);
            results.Should().AllSatisfy(r => r.Should().NotBeNullOrEmpty());
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASHLAR_ALLOW_MOCK", prevMock);
        }
    }

    [Fact(Timeout = TestTimeouts.Quick)]
    public async Task VisionProvider_MockDisabled_Throws()
    {
        var prevMock = Environment.GetEnvironmentVariable("ASHLAR_ALLOW_MOCK");
        try
        {
            Environment.SetEnvironmentVariable("ASHLAR_ALLOW_MOCK", null);
            var factory = CreateFactory();

            var act = () => factory.ExecuteVisionAsync("mock", "s", "u", new byte[] { 1, 2 }, new { }, CancellationToken.None);
            await act.Should().ThrowAsync<ModelUnavailableException>();
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASHLAR_ALLOW_MOCK", prevMock);
        }
    }
}
