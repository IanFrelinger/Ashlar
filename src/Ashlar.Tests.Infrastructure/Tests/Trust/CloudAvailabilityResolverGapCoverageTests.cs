using Ashlar.Agents.TestKit;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Ashlar.Infrastructure.Trust;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Trust;

/// <summary>Tests for cloud availability resolver gap coverage.</summary>
[Collection("EnvironmentVariables")]
public sealed class CloudAvailabilityResolverGapCoverageTests : IDisposable
{
    private readonly string? _savedAirgap;
    private readonly string? _savedConfigPath;

    public CloudAvailabilityResolverGapCoverageTests()
    {
        _savedAirgap = Environment.GetEnvironmentVariable("ASHLAR_AIRGAP");
        _savedConfigPath = Environment.GetEnvironmentVariable("ASHLAR_CONFIG_PATH");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("ASHLAR_AIRGAP", _savedAirgap);
        Environment.SetEnvironmentVariable("ASHLAR_CONFIG_PATH", _savedConfigPath);
    }

    [Fact]
    public void Constructor_throws_for_null_logger()
    {
        var act = () => new CloudAvailabilityResolver(null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("logger");
    }

    [Theory]
    [InlineData("false")]
    [InlineData("0")]
    [InlineData("no")]
    public async Task IsAirGappedAsync_treats_non_truthy_env_as_not_air_gapped(string envValue)
    {
        Environment.SetEnvironmentVariable("ASHLAR_AIRGAP", envValue);

        var resolver = new CloudAvailabilityResolver(
            NullLogger<CloudAvailabilityResolver>.Instance,
            configPath: "/does/not/exist");

        (await resolver.IsAirGappedAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task IsAirGappedAsync_accepts_numeric_one_env_flag()
    {
        Environment.SetEnvironmentVariable("ASHLAR_AIRGAP", "1");

        var resolver = new CloudAvailabilityResolver(
            NullLogger<CloudAvailabilityResolver>.Instance,
            configPath: "/does/not/exist");

        (await resolver.IsAirGappedAsync()).Should().BeTrue();
    }

    [Fact]
    public async Task IsAirGappedAsync_uses_cache_until_refresh_requested()
    {
        var path = Path.Combine(Path.GetTempPath(), "ashlar-airgap-cache-" + Guid.NewGuid() + ".json");
        await File.WriteAllTextAsync(path, """{"airGapped": true}""");
        Environment.SetEnvironmentVariable("ASHLAR_AIRGAP", null);

        try
        {
            var resolver = new CloudAvailabilityResolver(NullLogger<CloudAvailabilityResolver>.Instance, path);
            (await resolver.IsAirGappedAsync()).Should().BeTrue();

            await File.WriteAllTextAsync(path, """{"airGapped": false}""");

            (await resolver.IsAirGappedAsync()).Should().BeTrue();
            (await resolver.IsAirGappedAsync(refresh: true)).Should().BeFalse();
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public async Task IsAirGappedAsync_reads_string_true_from_config()
    {
        var path = Path.Combine(Path.GetTempPath(), "ashlar-airgap-string-" + Guid.NewGuid() + ".json");
        await File.WriteAllTextAsync(path, """{"airGapped": "true"}""");
        Environment.SetEnvironmentVariable("ASHLAR_AIRGAP", null);

        try
        {
            var resolver = new CloudAvailabilityResolver(NullLogger<CloudAvailabilityResolver>.Instance, path);
            (await resolver.IsAirGappedAsync()).Should().BeTrue();
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public async Task IsAirGappedAsync_probe_success_means_not_air_gapped()
    {
        Environment.SetEnvironmentVariable("ASHLAR_AIRGAP", null);

        var handler = StubHttpMessageHandler.FromSync((_, _) => new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        var resolver = new CloudAvailabilityResolver(
            NullLogger<CloudAvailabilityResolver>.Instance,
            configPath: "/does/not/exist",
            enableNetworkProbe: true,
            httpClient: new HttpClient(handler));

        (await resolver.IsAirGappedAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task IsAirGappedAsync_defaults_to_not_air_gapped_without_env_config_or_probe()
    {
        Environment.SetEnvironmentVariable("ASHLAR_AIRGAP", null);

        var resolver = new CloudAvailabilityResolver(
            NullLogger<CloudAvailabilityResolver>.Instance,
            configPath: "/does/not/exist",
            enableNetworkProbe: false);

        (await resolver.IsAirGappedAsync()).Should().BeFalse();
    }

    /// <summary>Tests for fake trust handler.</summary>
}
