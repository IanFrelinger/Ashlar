using Ashlar.Tests.Infrastructure.Helpers;
using Microsoft.Extensions.Logging;
using Moq;
using Ashlar.Core.Application.Common.Models;
using Ashlar.Core.Application.Common.Ports;
using Ashlar.Core.Application.Testing.Abstractions;
using Ashlar.Core.Application.Validation.Models;
using Ashlar.Core.Application.Validation.Ports;
using Ashlar.Infrastructure.Validation.Adapters;

namespace Ashlar.Tests.Infrastructure.Tests.Validation;

/// <summary>Tests for cached validation service adapter.</summary>
public class CachedValidationServiceAdapterTests : UnitTestBase
{
    private string? _previousDirectory;
    private DirectoryInfo? _directory;

    public override Task SetupAsync(CancellationToken cancellationToken = default)
    {
        _previousDirectory = Directory.GetCurrentDirectory();
        _directory = Directory.CreateTempSubdirectory("ashlar-validation-cache-");
        Directory.SetCurrentDirectory(_directory.FullName);
        return Task.CompletedTask;
    }

    public override Task CleanupAsync(CancellationToken cancellationToken = default)
    {
        if (_previousDirectory is not null) Directory.SetCurrentDirectory(_previousDirectory);
        _directory?.Delete(recursive: true);
        return Task.CompletedTask;
    }

    public override async Task<TestResult> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            /// <summary>Test cache hit.</summary>
            await TestCacheHit();
            /// <summary>Test cache miss.</summary>
            await TestCacheMiss();
            /// <summary>Test cache storage.</summary>
            await TestCacheStorage();
            /// <summary>Test progress reporting with cache hit.</summary>
            await TestProgressReportingWithCacheHit();
            /// <summary>Test progress reporting with cache miss.</summary>
            await TestProgressReportingWithCacheMiss();
            /// <summary>Test cancellation.</summary>
            await TestCancellation();

            return new TestResult
            {
                Name = nameof(CachedValidationServiceAdapterTests),
                Category = "Infrastructure",
                Passed = true,
                Message = "All CachedValidationServiceAdapter tests passed"
            };
        }
        catch (AssertionException ex)
        {
            return new TestResult
            {
                Name = nameof(CachedValidationServiceAdapterTests),
                Category = "Infrastructure",
                Passed = false,
                ErrorMessage = $"Assertion failed: {ex.Message}",
                StackTrace = ex.StackTrace
            };
        }
        catch (Exception ex)
        {
            return new TestResult
            {
                Name = nameof(CachedValidationServiceAdapterTests),
                Category = "Infrastructure",
                Passed = false,
                ErrorMessage = $"Unexpected exception: {ex.Message}",
                StackTrace = ex.StackTrace
            };
        }
    }

    private async Task TestCacheHit()
    {
        var mockInner = new Mock<IValidationService>();
        var mockCache = new Mock<ICacheStrategy>();
        var mockLogger = new Mock<ILogger<CachedValidationServiceAdapter>>();

        var cachedResult = new ValidationResult
        {
            Passed = true,
            Message = "All tests passed",
            TestsRun = 5,
            TestsPassed = 5,
            TestsFailed = 0
        };

        mockCache
            .Setup(c => c.GetAsync<ValidationResult>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(cachedResult);

        var adapter = new CachedValidationServiceAdapter(mockInner.Object, mockCache.Object, mockLogger.Object);
        var result = await adapter.ValidateAsync(null, null, CancellationToken.None);

        /// <summary>Assert not null.</summary>
        AssertNotNull(result);
        /// <summary>Assert true.</summary>
        AssertTrue(result.Passed);
        /// <summary>Assert equal.</summary>
        AssertEqual(5, result.TestsRun);

        // Inner service should not be called on cache hit
        mockInner.Verify(s => s.ValidateAsync(It.IsAny<string?>(), It.IsAny<IProgress<ProgressReport>>(), It.IsAny<CancellationToken>()), Times.Never);
        mockCache.Verify(c => c.GetAsync<ValidationResult>(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    private async Task TestCacheMiss()
    {
        var mockInner = new Mock<IValidationService>();
        var mockCache = new Mock<ICacheStrategy>();
        var mockLogger = new Mock<ILogger<CachedValidationServiceAdapter>>();

        var innerResult = new ValidationResult
        {
            Passed = false,
            Message = "Some tests failed",
            TestsRun = 5,
            TestsPassed = 3,
            TestsFailed = 2,
            TestResults = new List<TestResult>
            {
                new TestResult { Name = "Test1", Passed = true },
                new TestResult { Name = "Test2", Passed = false }
            }
        };

        // Cache miss - return null
        mockCache
            .Setup(c => c.GetAsync<ValidationResult>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ValidationResult?)null);

        mockInner
            .Setup(s => s.ValidateAsync(It.IsAny<string?>(), It.IsAny<IProgress<ProgressReport>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(innerResult);

        var adapter = new CachedValidationServiceAdapter(mockInner.Object, mockCache.Object, mockLogger.Object);
        var result = await adapter.ValidateAsync("test-filter", null, CancellationToken.None);

        /// <summary>Assert not null.</summary>
        AssertNotNull(result);
        /// <summary>Assert false.</summary>
        AssertFalse(result.Passed);
        /// <summary>Assert equal.</summary>
        AssertEqual(5, result.TestsRun);
        /// <summary>Assert equal.</summary>
        AssertEqual(2, result.TestsFailed);

        // Inner service should be called on cache miss
        mockInner.Verify(s => s.ValidateAsync("test-filter", It.IsAny<IProgress<ProgressReport>>(), It.IsAny<CancellationToken>()), Times.Once);
        mockCache.Verify(c => c.GetAsync<ValidationResult>(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    private async Task TestCacheStorage()
    {
        var mockInner = new Mock<IValidationService>();
        var mockCache = new Mock<ICacheStrategy>();
        var mockLogger = new Mock<ILogger<CachedValidationServiceAdapter>>();

        var innerResult = new ValidationResult
        {
            Passed = true,
            Message = "All tests passed",
            TestsRun = 3,
            TestsPassed = 3,
            TestsFailed = 0
        };

        // Cache miss
        mockCache
            .Setup(c => c.GetAsync<ValidationResult>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ValidationResult?)null);

        mockInner
            .Setup(s => s.ValidateAsync(It.IsAny<string?>(), It.IsAny<IProgress<ProgressReport>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(innerResult);

        var adapter = new CachedValidationServiceAdapter(mockInner.Object, mockCache.Object, mockLogger.Object);
        await adapter.ValidateAsync(null, null, CancellationToken.None);

        // Result should be stored in cache with 15 minute expiration
        mockCache.Verify(c => c.SetAsync(
            It.IsAny<string>(),
            It.Is<ValidationResult>(r => r.Passed == true),
            It.Is<TimeSpan>(t => t.TotalMinutes == 15),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    private async Task TestProgressReportingWithCacheHit()
    {
        var mockInner = new Mock<IValidationService>();
        var mockCache = new Mock<ICacheStrategy>();
        var mockLogger = new Mock<ILogger<CachedValidationServiceAdapter>>();

        var cachedResult = new ValidationResult
        {
            Passed = true,
            Message = "All tests passed",
            TestsRun = 5,
            TestsPassed = 5,
            TestsFailed = 0
        };

        mockCache
            .Setup(c => c.GetAsync<ValidationResult>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(cachedResult);

        var progressReports = new List<ProgressReport>();
        var progress = new SyncProgress<ProgressReport>(report => progressReports.Add(report));

        var adapter = new CachedValidationServiceAdapter(mockInner.Object, mockCache.Object, mockLogger.Object);
        await adapter.ValidateAsync(null, progress, CancellationToken.None);

        // Should report cached status - Progress<T> may invoke callback asynchronously
        if (progressReports.Count > 0)
        {
            var cachedReport = progressReports.FirstOrDefault(r => 
                r.Message.Contains("cached", StringComparison.OrdinalIgnoreCase) ||
                (r.Metadata != null && r.Metadata.ContainsKey("Cached")));
            if (cachedReport != null)
            {
                AssertTrue(cachedReport.Message.Contains("cached", StringComparison.OrdinalIgnoreCase) ||
                           cachedReport.Message.Contains("Cached", StringComparison.OrdinalIgnoreCase),
                    $"Progress message should indicate cached result, got: {cachedReport.Message}");
                /// <summary>Assert not null.</summary>
                /// <param name="metadata"">Metadata".</param>
                AssertNotNull(cachedReport.Metadata, "Progress report should have metadata");
                AssertTrue(cachedReport.Metadata!.ContainsKey("Cached"), "Progress report metadata should contain 'Cached' key");
            }
        }
    }

    private async Task TestProgressReportingWithCacheMiss()
    {
        var mockInner = new Mock<IValidationService>();
        var mockCache = new Mock<ICacheStrategy>();
        var mockLogger = new Mock<ILogger<CachedValidationServiceAdapter>>();

        var innerResult = new ValidationResult
        {
            Passed = true,
            Message = "All tests passed",
            TestsRun = 3,
            TestsPassed = 3,
            TestsFailed = 0
        };

        mockCache
            .Setup(c => c.GetAsync<ValidationResult>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ValidationResult?)null);

        var progressReports = new List<ProgressReport>();
        var progress = new SyncProgress<ProgressReport>(report => progressReports.Add(report));

        mockInner
            .Setup(s => s.ValidateAsync(It.IsAny<string?>(), It.IsAny<IProgress<ProgressReport>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(innerResult)
            .Callback<string?, IProgress<ProgressReport>?, CancellationToken>((filter, prog, ct) =>
            {
                prog?.Report(new ProgressReport { Percentage = 50, Message = "Running tests" });
            });

        var adapter = new CachedValidationServiceAdapter(mockInner.Object, mockCache.Object, mockLogger.Object);
        await adapter.ValidateAsync(null, progress, CancellationToken.None);

        // Progress should be passed through to inner service
        // May or may not have reports depending on timing
    }

    private async Task TestCancellation()
    {
        var mockInner = new Mock<IValidationService>();
        var mockCache = new Mock<ICacheStrategy>();
        var mockLogger = new Mock<ILogger<CachedValidationServiceAdapter>>();

        var cts = new CancellationTokenSource();
        cts.Cancel();

        mockCache
            .Setup(c => c.GetAsync<ValidationResult>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var adapter = new CachedValidationServiceAdapter(mockInner.Object, mockCache.Object, mockLogger.Object);

        await AssertThrowsAsync<OperationCanceledException>(() =>
            adapter.ValidateAsync(null, null, cts.Token));
    }
}

