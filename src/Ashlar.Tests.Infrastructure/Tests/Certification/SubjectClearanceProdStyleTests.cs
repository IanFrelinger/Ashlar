using Ashlar.Abstractions.Security;
using Ashlar.Hosting;
using Ashlar.Manifest;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

[Collection("EnvironmentVariables")]
[Trait("Category", "Certification")]
public sealed class SubjectClearanceProdStyleTests
{
    private const string Policy = """
        apiVersion: ashlar/v1
        kind: Policy
        sandbox:
          root: .
        selfExtend:
          mode: sealed
        never: [modify_gate, widen_sandbox, access_signing_keys, truncate_ledger, grant_capability]
        """;

    [Theory]
    [InlineData("Secret", "Public", "Public")]
    [InlineData("Secret", "SystemHigh", "Secret")]
    [InlineData("SystemHigh", "TopSecret", "TopSecret")]
    [InlineData("SystemHigh", "SystemHigh", "SystemHigh")]
    [InlineData("Secret//C:A,B", "TopSecret//C:B,C", "Secret//C:B")]
    public void Host_limits_intersect_the_policy_and_never_widen_it(string ceiling, string limit, string expected)
    {
        var authority = new SubjectClearanceAuthority(new Dictionary<string, string?> { ["agent:a"] = ceiling },
            new Dictionary<string, string?> { ["agent:a"] = limit, ["agent:unknown"] = "SystemHigh" });
        authority.Resolve("agent:a").ToString().Should().Be(expected);
        authority.Resolve("agent:unknown").Should().Be(SecurityLabel.Public);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("secret")]
    [InlineData("Secret ")]
    [InlineData("NoSuchLevel")]
    public void Malformed_clearances_refuse_both_trusted_configuration_sources(string? label)
    {
        var bad = new Dictionary<string, string?> { ["agent:a"] = label };
        var good = new Dictionary<string, string?> { ["agent:a"] = "SystemHigh" };
        Action badPolicy = () => new SubjectClearanceAuthority(bad);
        Action badHost = () => new SubjectClearanceAuthority(good, bad);
        badPolicy.Should().Throw<ArgumentException>();
        badHost.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Only_an_explicit_grant_reads_unlabelled_data_and_identity_is_exact()
    {
        var authority = new SubjectClearanceAuthority(new Dictionary<string, string?>
            { ["agent:top"] = "TopSecret", ["agent:operator"] = "SystemHigh" });
        ReferenceMonitor.CanRead(authority.Resolve("agent:operator"), SecurityLabel.SystemHigh).Allowed.Should().BeTrue();
        foreach (var id in new[] { null, "agent:top", "agent:unknown", "AGENT:OPERATOR", " agent:operator" })
            ReferenceMonitor.CanRead(authority.Resolve(id), SecurityLabel.SystemHigh).Allowed.Should().BeFalse();
        ReferenceMonitor.CanRead(authority.Resolve(null), SecurityLabel.Public).Allowed.Should().BeTrue();
    }

    [Fact]
    public void Caller_mutation_cannot_change_the_authority_snapshot()
    {
        var policy = new Dictionary<string, string?> { ["agent:a"] = "Secret" };
        var host = new Dictionary<string, string?> { ["agent:a"] = "Internal" };
        var authority = new SubjectClearanceAuthority(policy, host);
        policy["agent:a"] = host["agent:a"] = "SystemHigh";
        authority.Resolve("agent:a").ToString().Should().Be("Internal");
    }

    [Theory]
    [InlineData("agentClearances: null")]
    [InlineData("agentClearances: {agent: bogus}")]
    [InlineData("agentClearances: {agent: null}")]
    public void Policy_loader_refuses_invalid_clearance_maps(string mapping)
    {
        PolicyLoader.TryLoad(Policy + "\n" + mapping, out var policy, out _).Should().BeFalse();
        policy.Should().BeNull();
    }

    [Fact]
    public void AddAshlar_loads_operator_policy_and_snapshots_host_narrowing()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, Policy + "\nagentClearances:\n  agent:a: Secret\n  agent:operator: SystemHigh\n");
            var limits = new Dictionary<string, string?> { ["agent:a"] = "Internal" };
            var services = new ServiceCollection();
            services.AddAshlar(o => { o.AgentClearancePolicyPath = path; o.AgentClearanceLimits = limits; });
            limits["agent:a"] = "SystemHigh";
            File.WriteAllText(path, "invalid after composition");
            using var provider = services.BuildServiceProvider();
            var authority = provider.GetRequiredService<SubjectClearanceAuthority>();
            authority.Resolve("agent:a").ToString().Should().Be("Internal");
            authority.Resolve("agent:operator").Should().Be(SecurityLabel.SystemHigh);
            authority.Resolve("unknown").Should().Be(SecurityLabel.Public);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void AddAshlar_without_policy_cannot_grant_clearance_through_host_limits()
    {
        var services = new ServiceCollection();
        services.AddAshlar(o => o.AgentClearanceLimits["agent:a"] = "SystemHigh");
        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<SubjectClearanceAuthority>().Resolve("agent:a").Should().Be(SecurityLabel.Public);
    }

    [Fact]
    public void AddAshlar_refuses_invalid_policy_before_composition()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, Policy + "\nagentClearances: {agent: unknown}");
            var services = new ServiceCollection();
            Action compose = () => services.AddAshlar(o => o.AgentClearancePolicyPath = path);
            compose.Should().Throw<InvalidOperationException>().WithMessage("Agent clearance policy failed validation.");
        }
        finally { File.Delete(path); }
    }
}
