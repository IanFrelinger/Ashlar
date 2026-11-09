using Ashlar.Tests.Infrastructure.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Ashlar.Core.Application.Common.Ports;
using Ashlar.Core.Application.Validation.Models;
using Ashlar.Core.Application.Validation.Ports;
using Ashlar.Infrastructure.Validation.Adapters;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Validation;

/// <summary>Tests for cached validation service adapter gap coverage.</summary>
[Collection("ProcessCwd")]
public sealed class CachedValidationServiceAdapterGapCoverageTests : IDisposable
{
    private readonly string _previousDirectory = Directory.GetCurrentDirectory();
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("ashlar-validation-cache-");

    public CachedValidationServiceAdapterGapCoverageTests()
    {
        // Cache behavior needs a stable, bounded input tree, not the live test runner's outputs.
        Directory.SetCurrentDirectory(_directory.FullName);
    }

    public void Dispose()
    {
        Directory.SetCurrentDirectory(_previousDirectory);
        _directory.Delete(recursive: true);
    }

    [Fact]
    public void Constructor_throws_for_null_dependencies()
    {
        var inner = new Mock<IValidationService>().Object;
        var cache = new Mock<ICacheStrategy>().Object;
        var logger = NullLogger<CachedValidationServiceAdapter>.Instance;

        var act = () => new CachedValidationServiceAdapter(null!, cache, logger);
        act.Should().Throw<ArgumentNullException>().WithParameterName("inner");

        act = () => new CachedValidationServiceAdapter(inner, null!, logger);
        act.Should().Throw<ArgumentNullException>().WithParameterName("cache");

        act = () => new CachedValidationServiceAdapter(inner, cache, null!);
        act.Should().Throw<ArgumentNullException>().WithParameterName("logger");
    }

    [Fact]
    public async Task ValidateAsync_returns_cached_result_without_calling_inner()
    {
        var cached = new ValidationResult
        {
            Passed = true,
            Message = "cached",
            TestsRun = 3,
            TestsPassed = 3,
            TestsFailed = 0,
        };
        var inner = new Mock<IValidationService>();
        var cache = new Mock<ICacheStrategy>();
        cache.Setup(c => c.GetAsync<ValidationResult>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(cached);

        var sut = new CachedValidationServiceAdapter(
            inner.Object,
            cache.Object,
            NullLogger<CachedValidationServiceAdapter>.Instance);

        var result = await sut.ValidateAsync("filter", progress: null, CancellationToken.None);

        result.Should().BeSameAs(cached);
        inner.Verify(
            s => s.ValidateAsync(It.IsAny<string?>(), It.IsAny<IProgress<Ashlar.Core.Application.Common.Models.ProgressReport>?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ValidateAsync_reports_progress_on_cache_hit()
    {
        var cached = new ValidationResult
        {
            Passed = true,
            Message = "cached",
            TestsRun = 0,
            TestsPassed = 0,
            TestsFailed = 0,
        };
        var cache = new Mock<ICacheStrategy>();
        cache.Setup(c => c.GetAsync<ValidationResult>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(cached);

        var sut = new CachedValidationServiceAdapter(
            new Mock<IValidationService>().Object,
            cache.Object,
            NullLogger<CachedValidationServiceAdapter>.Instance);

        var reports = new List<Ashlar.Core.Application.Common.Models.ProgressReport>();
        await sut.ValidateAsync(null, new SyncProgress<Ashlar.Core.Application.Common.Models.ProgressReport>(reports.Add), CancellationToken.None);

        reports.Should().ContainSingle(r => r.Message.Contains("cached", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ValidateAsync_stores_result_on_cache_miss()
    {
        var innerResult = new ValidationResult
        {
            Passed = true,
            Message = "fresh",
            TestsRun = 1,
            TestsPassed = 1,
            TestsFailed = 0,
        };
        var inner = new Mock<IValidationService>();
        inner.Setup(s => s.ValidateAsync(It.IsAny<string?>(), It.IsAny<IProgress<Ashlar.Core.Application.Common.Models.ProgressReport>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(innerResult);

        var cache = new Mock<ICacheStrategy>();
        cache.Setup(c => c.GetAsync<ValidationResult>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ValidationResult?)null);

        var sut = new CachedValidationServiceAdapter(
            inner.Object,
            cache.Object,
            NullLogger<CachedValidationServiceAdapter>.Instance);

        var result = await sut.ValidateAsync("filter", progress: null, CancellationToken.None);

        result.Should().BeSameAs(innerResult);
        cache.Verify(
            c => c.SetAsync(It.IsAny<string>(), innerResult, TimeSpan.FromMinutes(15), It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
