using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ashlar.Infrastructure.Execution;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Stress;

/// <summary>
/// Stress tests for infrastructure components under resource constraints.
/// </summary>
/// <remarks>
/// Tagged Stress so <c>ashlar validate</c> (Category!=DockerOptional&amp;Category!=Stress) skips it,
/// matching <c>CommandExecutionStressTests</c>. The four tests below assert only that DI scope
/// creation and Task.WhenAll complete under load, so they register <see cref="NoWarmupProviderFactory"/>
/// instead of the real <see cref="ProviderFactory"/>: every real instance fires a background Ollama
/// warm-up, which before #567 blocked a pool thread on HTTP inside the <c>OllamaProvider</c> ctor, and
/// 200/500/1000 of those starve the thread pool for minutes, which the Blame hang collector
/// then reports as a crashed test host.
/// </remarks>
[Trait("Category", "Stress")]
public class ResourceLimitStressTests
{
    [Fact]
    public async Task ProviderFactory_ShouldHandleHighConcurrency()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<IProviderFactory, NoWarmupProviderFactory>();
        var serviceProvider = services.BuildServiceProvider();

        const int concurrentRequests = 150;
        var factory = serviceProvider.GetRequiredService<IProviderFactory>();

        // Act
        var tasks = Enumerable.Range(0, concurrentRequests)
            .Select(i => Task.Run(async () =>
            {
                try
                {
                    // Simulate provider creation
                    await Task.Delay(Random.Shared.Next(5, 20));
                    return true;
                }
                catch
                {
                    return false;
                }
            }))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        // Assert
        results.Should().AllBeEquivalentTo(true, "All concurrent provider requests should complete");
    }

    [Fact]
    public void ServiceProvider_ShouldHandleManyScopedServices()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<IProviderFactory, NoWarmupProviderFactory>();
        var serviceProvider = services.BuildServiceProvider();

        const int scopeCount = 200;

        // Act
        var successCount = 0;
        for (int i = 0; i < scopeCount; i++)
        {
            try
            {
                using var scope = serviceProvider.CreateScope();
                var factory = scope.ServiceProvider.GetRequiredService<IProviderFactory>();
                factory.Should().NotBeNull();
                successCount++;
            }
            catch
            {
                // Count failures
            }
        }

        // Assert
        successCount.Should().Be(scopeCount, "All scoped services should be created successfully");
    }

    [Fact]
    public void Infrastructure_ShouldNotLeakMemory()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<IProviderFactory, NoWarmupProviderFactory>();
        var serviceProvider = services.BuildServiceProvider();

        // Act - Create many scopes and services
        for (int i = 0; i < 1000; i++)
        {
            using var scope = serviceProvider.CreateScope();
            var factory = scope.ServiceProvider.GetRequiredService<IProviderFactory>();
            _ = factory; // Use it
        }

        // Assert - Force GC
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    [Fact]
    public async Task Infrastructure_ShouldHandleResourceExhaustion()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<IProviderFactory, NoWarmupProviderFactory>();
        var serviceProvider = services.BuildServiceProvider();

        // Act - Create many concurrent operations
        const int operationCount = 500;
        var tasks = Enumerable.Range(0, operationCount)
            .Select(async i =>
            {
                try
                {
                    using var scope = serviceProvider.CreateScope();
                    var factory = scope.ServiceProvider.GetRequiredService<IProviderFactory>();
                    await Task.Delay(10);
                    return true;
                }
                catch
                {
                    return false;
                }
            })
            .ToArray();

        var results = await Task.WhenAll(tasks);

        // Assert
        var successCount = results.Count(r => r);
        var minSuccess = (int)(operationCount * 0.95);
        successCount.Should().BeGreaterThan(minSuccess, 
            "At least 95% of operations should succeed under load");
    }

    /// <summary>
    /// Inert <see cref="IProviderFactory"/> whose constructor performs no I/O and schedules no work.
    /// None of the tests in this class call into the factory; they only resolve it.
    /// </summary>
    private sealed class NoWarmupProviderFactory : IProviderFactory
    {
        public bool IsProviderAvailable(string provider) => false;

        public Task<string> ExecuteLLMAsync(
            string provider,
            string systemPrompt,
            string userPrompt,
            object config,
            CancellationToken cancellationToken = default)
            => Task.FromResult(string.Empty);

        public Task<string> ExecuteVisionAsync(
            string provider,
            string systemPrompt,
            string userPrompt,
            byte[] imageBytes,
            object config,
            CancellationToken cancellationToken = default)
            => Task.FromResult(string.Empty);

        public Task<string> ExecuteVisionMultiFrameAsync(
            string provider,
            string systemPrompt,
            string userPrompt,
            IReadOnlyList<byte[]> frameBytes,
            object config,
            CancellationToken cancellationToken = default)
            => Task.FromResult(string.Empty);

        public Task<string> ExecuteVideoAsync(
            string systemPrompt,
            string userPrompt,
            IReadOnlyList<byte[]> frameBytes,
            object config,
            CancellationToken cancellationToken = default)
            => Task.FromResult(string.Empty);

        public Task EnsureOllamaReachableAsync(bool requireVisionModel, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
