using Ashlar.Agents.TestKit;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Ashlar.Core.Application.Trust.Models;
using Ashlar.Infrastructure.Trust;
using System.Reflection;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Trust;

/// <summary>Tests for infrastructure trust gap coverage.</summary>
[Collection("EnvironmentVariables")]
public class InfrastructureTrustGapCoverageTests
{
    [Fact]
    public async Task CloudAvailabilityResolver_reads_env_var_and_probes_network()
    {
        var key = "ASHLAR_AIRGAP";
        var previous = Environment.GetEnvironmentVariable(key);
        try
        {
            Environment.SetEnvironmentVariable(key, "true");
            var envResolver = new CloudAvailabilityResolver(NullLogger<CloudAvailabilityResolver>.Instance);
            (await envResolver.IsAirGappedAsync()).Should().BeTrue();
            (await envResolver.IsAirGappedAsync(refresh: true)).Should().BeTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable(key, previous);
        }

        var handler = StubHttpMessageHandler.FromSync((_, _) => throw new HttpRequestException("blocked"));
        var probeResolver = new CloudAvailabilityResolver(
            NullLogger<CloudAvailabilityResolver>.Instance,
            configPath: "/does/not/exist",
            enableNetworkProbe: true,
            httpClient: new HttpClient(handler));
        (await probeResolver.IsAirGappedAsync()).Should().BeTrue();
    }

    [Fact]
    public async Task CloudAvailabilityResolver_tolerates_invalid_config_file()
    {
        var path = Path.Combine(Path.GetTempPath(), "ashlar-airgap-bad-" + Guid.NewGuid() + ".json");
        await File.WriteAllTextAsync(path, "{not json");
        try
        {
            var resolver = new CloudAvailabilityResolver(NullLogger<CloudAvailabilityResolver>.Instance, path);
            (await resolver.IsAirGappedAsync()).Should().BeFalse();
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task AccessBoundary_persists_and_loads_json_config()
    {
        var path = Path.Combine(Path.GetTempPath(), "boundary-" + Guid.NewGuid() + ".json");
        try
        {
            var boundary = new AccessBoundary(path);
            boundary.SetPause(true);
            boundary.SetCategoryAllowed("shell", false);
            boundary.SetSourceAllowed("git", false);
            boundary.SetProjectOverride("/proj", new Dictionary<string, bool> { ["git"] = true });

            /// <summary>Wait for persisted config async.</summary>
            await WaitForPersistedConfigAsync(boundary);

            var reloaded = new AccessBoundary(path);
            reloaded.IsObservationPaused.Should().BeTrue();
            reloaded.IsCategoryAllowed("shell").Should().BeFalse();
            reloaded.IsSourceAllowedForProject("git", "/proj").Should().BeTrue();
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void AccessBoundary_apply_policy_pack_and_reset()
    {
        var boundary = new AccessBoundary();
        var pack = new TrustPolicyPack(
            "strict",
            "2.0",
            "Strict",
            "desc",
            PauseObservationByDefault: true,
            new Dictionary<string, bool> { ["shell"] = false },
            new Dictionary<string, bool> { ["git"] = false },
            new[] { new TrustPolicyPackProjectRule("/proj", new Dictionary<string, bool> { ["git"] = true }) });

        boundary.ApplyPolicyPack(pack);
        boundary.IsObservationPaused.Should().BeTrue();
        boundary.GetActivePolicyPack()!.Id.Should().Be("strict");
        boundary.GetCategoryAllowlistSnapshot()["shell"].Should().BeFalse();

        boundary.Reset();
        boundary.IsObservationPaused.Should().BeFalse();
        boundary.GetActivePolicyPack().Should().BeNull();
    }

    [Fact]
    public void AccessBoundary_rejects_blank_category_source_and_invalid_pack()
    {
        var boundary = new AccessBoundary();
        boundary.IsCategoryAllowed("").Should().BeFalse();
        boundary.IsSourceAllowed("  ").Should().BeFalse();
        boundary.SetCategoryAllowed("", true);
        boundary.SetSourceAllowed("", true);

        var act = () => boundary.ApplyPolicyPack(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task CloudAvailabilityResolver_reads_config_file_and_caches()
    {
        var path = Path.Combine(Path.GetTempPath(), "ashlar-airgap-" + Guid.NewGuid() + ".json");
        await File.WriteAllTextAsync(path, """{"airGapped": true}""");
        var key = "ASHLAR_AIRGAP";
        var previous = Environment.GetEnvironmentVariable(key);
        try
        {
            Environment.SetEnvironmentVariable(key, null);
            var resolver = new CloudAvailabilityResolver(NullLogger<CloudAvailabilityResolver>.Instance, path);
            (await resolver.IsAirGappedAsync()).Should().BeTrue();
            (await resolver.IsAirGappedAsync()).Should().BeTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable(key, previous);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static async Task WaitForPersistedConfigAsync(AccessBoundary boundary)
    {
        var save = typeof(AccessBoundary).GetMethod("SaveToFileAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        save.Should().NotBeNull();
        await ((Task)save!.Invoke(boundary, null)!).ConfigureAwait(false);
    }

    /// <summary>Tests for fake trust handler.</summary>
}
