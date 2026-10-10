using Ashlar.Abstractions.Security;
using Ashlar.Hosting;
using Ashlar.Tests.Infrastructure.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

[Collection("EnvironmentVariables")]
[Trait("Category", "Certification")]
public sealed class SubjectClearanceConfigurationTests
{
    private static string Policy(string level) => """
        apiVersion: ashlar/v1
        kind: Policy
        sandbox:
          root: .
        selfExtend:
          mode: sealed
        never: [modify_gate, widen_sandbox, access_signing_keys, truncate_ledger, grant_capability]
        agentClearances:
        """ + "\n  agent:reader: " + level + "\n";

    [Fact]
    public void Environment_policy_is_latched_and_host_limits_still_apply()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, Policy("SystemHigh"));
            using var environment = new EnvironmentVariableScope("ASHLAR_CLEARANCE_POLICY", path);
            var services = new ServiceCollection();
            services.AddAshlar(o => o.AgentClearanceLimits["agent:reader"] = "Secret");
            File.WriteAllText(path, Policy("Public"));
            using var provider = services.BuildServiceProvider();
            provider.GetRequiredService<SubjectClearanceAuthority>().Resolve("agent:reader").ToString().Should().Be("Secret");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Explicit_host_path_takes_precedence_over_the_environment()
    {
        var environmentPath = Path.GetTempFileName();
        var hostPath = Path.GetTempFileName();
        try
        {
            File.WriteAllText(environmentPath, Policy("SystemHigh"));
            File.WriteAllText(hostPath, Policy("Public"));
            using var environment = new EnvironmentVariableScope("ASHLAR_CLEARANCE_POLICY", environmentPath);
            var services = new ServiceCollection();
            services.AddAshlar(o => o.AgentClearancePolicyPath = hostPath);
            using var provider = services.BuildServiceProvider();
            provider.GetRequiredService<SubjectClearanceAuthority>().Resolve("agent:reader").Should().Be(SecurityLabel.Public);
        }
        finally { File.Delete(environmentPath); File.Delete(hostPath); }
    }

    [Fact]
    public void A_configured_missing_file_refuses_composition()
    {
        using var environment = new EnvironmentVariableScope("ASHLAR_CLEARANCE_POLICY",
            Path.Combine(Path.GetTempPath(), "missing-policy-" + Guid.NewGuid().ToString("N") + ".yaml"));
        Action compose = () => new ServiceCollection().AddAshlar();
        compose.Should().Throw<FileNotFoundException>();
    }
}
