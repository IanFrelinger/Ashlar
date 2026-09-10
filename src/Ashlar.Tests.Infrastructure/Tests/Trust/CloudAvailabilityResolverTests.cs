using FluentAssertions;
using Microsoft.Extensions.Logging;
using Ashlar.Infrastructure.Trust;
using Ashlar.Tests.Infrastructure.Helpers;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Trust;

/// <summary>Tests for cloud availability resolver.</summary>
[Collection("EnvironmentVariables")]
public sealed class CloudAvailabilityResolverTests
{
    [Fact]
    public async Task IsAirGappedAsync_WhenAshlarAirgapEnvIs1_ReturnsTrue()
    {
        using var airgap = new EnvironmentVariableScope("ASHLAR_AIRGAP", "1");

        var logger = new LoggerFactory().CreateLogger<CloudAvailabilityResolver>();
        var resolver = new CloudAvailabilityResolver(logger);

        var result = await resolver.IsAirGappedAsync();

        result.Should().BeTrue();
    }

    [Fact]
    public async Task IsAirGappedAsync_WhenAshlarAirgapEnvIsTrue_ReturnsTrue()
    {
        using var airgap = new EnvironmentVariableScope("ASHLAR_AIRGAP", "true");

        var logger = new LoggerFactory().CreateLogger<CloudAvailabilityResolver>();
        var resolver = new CloudAvailabilityResolver(logger);

        var result = await resolver.IsAirGappedAsync();

        result.Should().BeTrue();
    }

    [Fact]
    public async Task IsAirGappedAsync_WhenAshlarAirgapEnvIs0_ReturnsFalse()
    {
        using var airgap = new EnvironmentVariableScope("ASHLAR_AIRGAP", "0");

        var logger = new LoggerFactory().CreateLogger<CloudAvailabilityResolver>();
        var resolver = new CloudAvailabilityResolver(logger);

        var result = await resolver.IsAirGappedAsync();

        result.Should().BeFalse();
    }

    [Fact]
    public async Task IsAirGappedAsync_WithConfigFileContainingAirGappedTrue_ReturnsTrue()
    {
        using var airgap = EnvironmentVariableScope.Unset("ASHLAR_AIRGAP");
        var configPath = Path.Combine(Path.GetTempPath(), $"ashlar-cloud-resolver-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(configPath, """{"airGapped": true}""");
            var logger = new LoggerFactory().CreateLogger<CloudAvailabilityResolver>();
            var resolver = new CloudAvailabilityResolver(logger, configPath);

            var result = await resolver.IsAirGappedAsync(refresh: true);

            result.Should().BeTrue();
        }
        finally
        {
            if (File.Exists(configPath))
                File.Delete(configPath);
        }
    }
}
