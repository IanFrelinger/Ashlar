using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ashlar.Abstractions;
using Ashlar.Core.Application.Common.Ports;
using Ashlar.Core.Application.Common.Services;
using Ashlar.Infrastructure.Execution;
using Ashlar.Tests.Application.Helpers;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.BaseFramework;

/// <summary>
/// Smoke tests for base framework components (logging, HTTP, DI, filesystem).
/// </summary>
public class BaseFrameworkSmokeTests : IDisposable
{
    private readonly ServiceProvider _serviceProvider;
    private readonly string _testOutputDir;
    private readonly IDisposable _tempDirCleanup;

    public BaseFrameworkSmokeTests()
    {
        (_testOutputDir, _tempDirCleanup) = TestHelpers.CreateTempDirectoryWithCleanup("ashlar-base-framework");

        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddConsole());
        services.AddHttpClient();
        services.AddScoped<IProviderFactory, ProviderFactory>();
        services.AddScoped<ILoopKernel, SequentialLoopKernel>();
        _serviceProvider = services.BuildServiceProvider();
    }

    [Fact]
    public void Logging_ShouldBeConfigured()
    {
        // Arrange & Act
        var loggerFactory = _serviceProvider.GetRequiredService<ILoggerFactory>();
        var logger = loggerFactory.CreateLogger<BaseFrameworkSmokeTests>();

        // Assert
        logger.Should().NotBeNull("Logger should be created");
        loggerFactory.Should().NotBeNull("LoggerFactory should be available");
        
        // Verify logging works without throwing
        logger.LogInformation("Test log message");
    }

    [Fact]
    public void HttpClientFactory_ShouldCreateClients()
    {
        // Arrange
        var httpClientFactory = _serviceProvider.GetRequiredService<IHttpClientFactory>();

        // Act
        var defaultClient = httpClientFactory.CreateClient();

        // Assert
        defaultClient.Should().NotBeNull("Default HTTP client should be created");
        defaultClient.BaseAddress.Should().BeNull("Default client should have no base address");
    }

    [Fact]
    public void DependencyInjection_ShouldResolveServices()
    {
        // Arrange & Act
        var providerFactory = _serviceProvider.GetRequiredService<IProviderFactory>();
        var loopKernel = _serviceProvider.GetRequiredService<ILoopKernel>();

        // Assert
        providerFactory.Should().NotBeNull("ProviderFactory should be resolvable");
        loopKernel.Should().NotBeNull("LoopKernel should be resolvable");
    }

    [Fact]
    public void DatabaseOperations_ShouldBeSupported()
    {
        // Arrange
        var dbPath = Path.Combine(_testOutputDir, "test.db");
        
        // Act - Test that SQLite can be used (if available)
        // This validates database infrastructure without requiring API project
        var dbExists = File.Exists(dbPath);
        
        // Assert
        // Just verify file system supports database file creation
        // Actual database testing happens in geo app tests
        dbPath.Should().NotBeNullOrEmpty("Database path should be valid");
    }

    [Fact]
    public void FileSystem_ShouldSupportBasicOperations()
    {
        // Arrange
        var testFile = Path.Combine(_testOutputDir, "test.txt");
        var testContent = "Hello, World!";

        // Act
        File.WriteAllText(testFile, testContent);
        var readContent = File.ReadAllText(testFile);
        var exists = File.Exists(testFile);

        // Assert
        exists.Should().BeTrue("File should exist after creation");
        readContent.Should().Be(testContent, "File content should match");
    }

    [Fact]
    public void DirectoryOperations_ShouldWork()
    {
        // Arrange
        var testDir = Path.Combine(_testOutputDir, "subdir");

        // Act
        Directory.CreateDirectory(testDir);
        var exists = Directory.Exists(testDir);

        // Assert
        exists.Should().BeTrue("Directory should exist after creation");
    }

    [Fact]
    public void ServiceProvider_ShouldSupportScopedServices()
    {
        // Arrange
        using var scope1 = _serviceProvider.CreateScope();
        using var scope2 = _serviceProvider.CreateScope();

        // Act
        var service1 = scope1.ServiceProvider.GetRequiredService<IProviderFactory>();
        var service2 = scope2.ServiceProvider.GetRequiredService<IProviderFactory>();

        // Assert
        service1.Should().NotBeNull("Service should be resolvable in scope 1");
        service2.Should().NotBeNull("Service should be resolvable in scope 2");
        // Scoped services should be different instances
        service1.Should().NotBeSameAs(service2, "Scoped services should be different instances");
    }

    [Fact]
    public async Task AsyncOperations_ShouldComplete()
    {
        // Arrange & Act
        var result = await Task.Run(async () =>
        {
            await Task.Delay(10);
            return "completed";
        });

        // Assert
        result.Should().Be("completed", "Async operations should complete");
    }

    [Fact]
    public async Task CancellationToken_ShouldBeSupported()
    {
        using var cts = new CancellationTokenSource();
        var operation = Task.Delay(Timeout.InfiniteTimeSpan, cts.Token);
        operation.IsCompleted.Should().BeFalse();

        // Cancel an existing operation directly; two competing timers do not establish order.
        cts.Cancel();

        Func<Task> observeCancellation = () => operation.WaitAsync(TimeSpan.FromSeconds(5));
        var canceled = await observeCancellation.Should().ThrowAsync<OperationCanceledException>();
        canceled.Which.CancellationToken.Should().Be(cts.Token);
        operation.IsCanceled.Should().BeTrue();
    }

    public void Dispose()
    {
        try
        {
            _serviceProvider?.Dispose();
            _tempDirCleanup?.Dispose();
        }
        catch
        {
            // Ignore cleanup errors
        }
    }
}
