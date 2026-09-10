using FluentAssertions;
using Ashlar.Infrastructure.Testing;
using Ashlar.Infrastructure.Testing.Agents;
using Ashlar.Infrastructure.Testing.CodeAnalysis;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests;

/// <summary>Tests for platform compatibility checker gap coverage.</summary>
[Collection("CompatibilityTestHooks")]
public class PlatformCompatibilityCheckerGapCoverageTests : IDisposable
{
    /// <summary>Platform compatibility checker gap coverage tests.</summary>
    public PlatformCompatibilityCheckerGapCoverageTests() => CompatibilityTestHooks.Reset();

    /// <summary>Dispose.</summary>
    public void Dispose() => CompatibilityTestHooks.Reset();

    [Fact]
    public void CodeAnalysisChecker_reports_compatible_when_all_dependencies_present()
    {
        var result = PlatformCompatibilityChecker.CheckCompatibility();

        result.Platform.Should().BeOneOf("Windows", "Linux", "macOS", "Unknown");
        result.IsCompatible.Should().BeTrue();
        result.Issues.Should().BeEmpty();
    }

    [Fact]
    public void CodeAnalysisChecker_reports_missing_roslyn_decompiler_and_reflection()
    {
        CompatibilityTestHooks.TypeResolver = _ => null;
        CompatibilityTestHooks.ReflectionProbe = () => false;

        var result = PlatformCompatibilityChecker.CheckCompatibility();

        result.IsCompatible.Should().BeFalse();
        result.Issues.Should().Contain("Microsoft.CodeAnalysis.CSharp (Roslyn) is not available");
        result.Issues.Should().Contain("ICSharpCode.Decompiler is not available");
        result.Issues.Should().Contain("System.Reflection capabilities are limited");
    }

    [Fact]
    public void CodeAnalysisChecker_records_exception_messages_from_dependency_probes()
    {
        CompatibilityTestHooks.TypeResolver = name =>
            throw new InvalidOperationException($"failed:{name.Split(',')[0]}");
        CompatibilityTestHooks.ReflectionProbe = () => throw new InvalidOperationException("reflection failed");

        var result = PlatformCompatibilityChecker.CheckCompatibility();

        result.IsCompatible.Should().BeFalse();
        result.Issues.Should().Contain(i => i.StartsWith("Roslyn check failed:", StringComparison.Ordinal));
        result.Issues.Should().Contain(i => i.StartsWith("Decompiler check failed:", StringComparison.Ordinal));
        result.Issues.Should().Contain(i => i.StartsWith("Reflection check failed:", StringComparison.Ordinal));
    }

    [Fact]
    public void CodeAnalysisChecker_reports_unknown_platform_when_override_set()
    {
        CompatibilityTestHooks.PlatformNameProvider = () => "Unknown";

        var result = PlatformCompatibilityChecker.CheckCompatibility();

        result.Platform.Should().Be("Unknown");
    }

    [Fact]
    public void AgentChecker_reports_compatible_when_logging_present()
    {
        var result = AgentPlatformCompatibilityChecker.CheckCompatibility();

        result.Platform.Should().BeOneOf("Windows", "Linux", "macOS", "Unknown");
        result.IsCompatible.Should().BeTrue();
    }

    [Fact]
    public void AgentChecker_reports_missing_logging_as_incompatible()
    {
        CompatibilityTestHooks.TypeResolver = _ => null;

        var result = AgentPlatformCompatibilityChecker.CheckCompatibility();

        result.IsCompatible.Should().BeFalse();
        result.Issues.Should().Contain("Microsoft.Extensions.Logging is not available");
    }

    [Fact]
    public void AgentChecker_records_optional_playwright_absence_without_failing()
    {
        CompatibilityTestHooks.TypeResolver = name =>
            name.Contains("Microsoft.Extensions.Logging", StringComparison.Ordinal)
                ? typeof(object)
                : null;

        var result = AgentPlatformCompatibilityChecker.CheckCompatibility();

        result.IsCompatible.Should().BeTrue();
        result.Issues.Should().Contain("Microsoft.Playwright is not available (optional)");
    }

    [Fact]
    public void AgentChecker_records_playwright_probe_exception_as_optional_issue()
    {
        CompatibilityTestHooks.TypeResolver = name =>
        {
            if (name.Contains("Microsoft.Extensions.Logging", StringComparison.Ordinal))
                /// <summary>Typeof.</summary>
                return typeof(object);
            /// <summary>Invalid operation exception.</summary>
            /// <param name="failed"">Failed".</param>
            throw new InvalidOperationException("playwright probe failed");
        };

        var result = AgentPlatformCompatibilityChecker.CheckCompatibility();

        result.IsCompatible.Should().BeTrue();
        result.Issues.Should().Contain(i => i.StartsWith("Playwright check failed (optional):", StringComparison.Ordinal));
    }

    [Fact]
    public void AgentChecker_records_logging_probe_exception()
    {
        CompatibilityTestHooks.TypeResolver = _ => throw new InvalidOperationException("logging probe failed");

        var result = AgentPlatformCompatibilityChecker.CheckCompatibility();

        result.IsCompatible.Should().BeFalse();
        result.Issues.Should().Contain("Logging check failed: logging probe failed");
    }
}
