using System.ComponentModel;
using System.Text.Json;
using Ashlar.Abstractions.Security.Egress;
using Ashlar.CLI.Commands.Runtime;
using Ashlar.CLI.Runtime;
using Ashlar.Infrastructure.HostProcess;
using Ashlar.Infrastructure.Preflight;
using Ashlar.Tests.Infrastructure.Helpers;
using Xunit;

namespace Ashlar.Tests.CLI.Tests.Commands;

[Collection("MeshIntegration")]
public sealed class VisualDependencyPreflightTests
{
    [Fact]
    public async Task Selected_visual_dependencies_are_read_only_bounded_and_do_not_report_raw_output()
    {
        var commands = new List<string>();
        var report = await VisualDependencyPreflight.RunAsync(".", true, true, CancellationToken.None,
            runProbe: (command, timeout, _) => {
                Assert.Equal(TimedProcess.DaemonProbeTimeout, timeout);
                commands.Add(command);
                return Task.FromResult(new TimedProcessResult(0, "private output", "private error", false));
            });
        Assert.True(report.RequiredAvailable);
        Assert.Equal(3, commands.Count);
        Assert.Equal(new[] { VisualDependencyPreflight.DockerCli, VisualDependencyPreflight.DockerDaemon, VisualDependencyPreflight.OllamaCli }, report.Checks.Select(check => check.Id));
        Assert.All(commands, command => {
            Assert.DoesNotContain("install", command, StringComparison.Ordinal);
            Assert.DoesNotContain("docker run", command, StringComparison.Ordinal);
        });
        Assert.DoesNotContain("private", JsonSerializer.Serialize(report), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Nonvisual_or_disabled_operation_does_not_launch_probes(bool runVisualQa, bool enabled)
    {
        var report = await VisualDependencyPreflight.RunAsync(".", runVisualQa, true, CancellationToken.None, enabled,
            runProbe: (_, _, _) => throw new InvalidOperationException("Unexpected probe."));
        Assert.Equal(enabled, report.Ran);
        if (!enabled) Assert.False(report.RequiredAvailable);
        if (!runVisualQa) Assert.Empty(report.Checks);
    }

    [Fact]
    public async Task Missing_cli_and_unavailable_daemon_keep_dependency_specific_results()
    {
        var report = await VisualDependencyPreflight.RunAsync(".", true, true, CancellationToken.None,
            runProbe: (_, _, _) => Task.FromResult(new TimedProcessResult(1, "", "", false)));
        Assert.Equal(new[] { DependencyAvailability.Missing, DependencyAvailability.Unavailable, DependencyAvailability.Missing },
            report.Checks.Select(check => check.Availability));
        Assert.False(report.RequiredAvailable);
        var optional = await VisualDependencyPreflight.RunAsync(".", true, false, CancellationToken.None,
            runProbe: (_, _, _) => Task.FromResult(new TimedProcessResult(1, "", "", false)));
        Assert.True(optional.RequiredAvailable);
        Assert.All(optional.Checks, check => Assert.False(check.Available));
    }

    [Fact]
    public async Task Timed_out_or_unlaunchable_probe_is_not_reported_as_missing_installed_dependency()
    {
        var timedOut = await VisualDependencyPreflight.RunAsync(".", true, true, CancellationToken.None,
            runProbe: (_, _, _) => Task.FromResult(new TimedProcessResult(124, "", "", true)));
        Assert.All(timedOut.Checks, check => Assert.Equal(DependencyAvailability.TimedOut, check.Availability));
        var unavailable = await VisualDependencyPreflight.RunAsync(".", true, true, CancellationToken.None,
            runProbe: (_, _, _) => throw new Win32Exception(2));
        Assert.All(unavailable.Checks, check => Assert.Equal(DependencyAvailability.Unavailable, check.Availability));
    }

    [Fact]
    public async Task Egress_refusal_remains_mandatory_even_for_optional_dependencies_or_after_skipping_preflight()
    {
        using var mode = new EnvironmentVariableScope("ASHLAR_EGRESS_MODE", "enforce");
        using var profile = new EnvironmentVariableScope("ASHLAR_DEPLOYMENT_PROFILE", "secure-workstation");
        using var state = new EgressProcessStateScope(reset: true);
        var refusal = await Assert.ThrowsAsync<EgressRefusedException>(() =>
            VisualDependencyPreflight.RunAsync(".", true, false, CancellationToken.None));
        Assert.Equal("EG-PROC-08", refusal.Site);
        var skipped = await VisualDependencyPreflight.RunAsync(".", true, true, CancellationToken.None, enabled: false);
        Assert.False(skipped.RequiredAvailable);
        // Skipping optional readiness does not alter the real operation's process guard.
        await Assert.ThrowsAsync<EgressRefusedException>(() => TimedProcess.RunShellAsync("exit 0", TimeSpan.FromSeconds(1)));
    }

    [Theory]
    [InlineData(false, true, true, true)]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, false, true)]
    [InlineData(true, true, true, false)]
    public async Task Runtime_only_checks_visual_capability_when_selected_and_enabled(
        bool runTests, bool runPreflight, bool allowDegrade, bool visual)
    {
        using var mode = new EnvironmentVariableScope("ASHLAR_EGRESS_MODE", "enforce");
        using var profile = new EnvironmentVariableScope("ASHLAR_DEPLOYMENT_PROFILE", "secure-workstation");
        using var state = new EgressProcessStateScope(reset: true);
        var context = Context(visual);
        // With an enforcing profile any accidentally launched probe would throw before process start.
        var result = await ExecuteHandler.ApplyVisualCapabilityFallbackAsync(context, ".", "filter", runTests,
            runPreflight, allowDegrade, CancellationToken.None);
        Assert.Same(context, result);
    }

    [Theory]
    [InlineData(false, "balanced")]
    [InlineData(true, "functional")]
    public async Task Runtime_visual_flag_alone_does_not_select_an_inactive_aesthetic_path(bool aesthetic, string focus)
    {
        using var mode = new EnvironmentVariableScope("ASHLAR_EGRESS_MODE", "enforce");
        using var profile = new EnvironmentVariableScope("ASHLAR_DEPLOYMENT_PROFILE", "secure-workstation");
        using var state = new EgressProcessStateScope(reset: true);
        var original = Context(true);
        var context = original with { Plan = original.Plan with { RunAestheticQa = aesthetic, Focus = focus } };
        var result = await ExecuteHandler.ApplyVisualCapabilityFallbackAsync(context, ".", "filter", true,
            true, true, CancellationToken.None);
        Assert.Same(context, result);
    }

    [Fact]
    public async Task Runtime_caller_can_degrade_without_changing_probe_outcomes()
    {
        var context = Context(true);
        var missing = await new DependencyPreflight([]).RunAsync("visual-qa", [new("visual:docker-cli")]);
        var degraded = ExecuteHandler.ApplyVisualCapabilityFallback(context, "filter", missing);
        Assert.Equal("degrade", degraded.Plan.VisualQaFallbackPolicy);
        Assert.Equal("strict", context.Plan.VisualQaFallbackPolicy);
        Assert.False(missing.RequiredAvailable);
        Assert.Contains(degraded.Plan.Reasons, reason => reason.Contains("visual:docker-cli", StringComparison.Ordinal));
        var ready = await new DependencyPreflight([new("visual:docker-cli", _ => Task.FromResult(new DependencyObservation(DependencyAvailability.Available, "ready")))])
            .RunAsync("visual-qa", [new("visual:docker-cli")]);
        Assert.Same(context, ExecuteHandler.ApplyVisualCapabilityFallback(context, "filter", ready));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public void Runtime_json_does_not_present_skipped_preflight_as_a_pass(bool ran, bool passed)
    {
        using var output = new StringWriter();
        Console.SetOut(output);
        try
        {
            RuntimeOutputWriter.WriteResult(new RuntimeExecuteResult(true, "execution completed",
                PreflightRan: ran, PreflightOk: passed), json: true);
        }
        finally { Console.SetOut(ConsoleCapture.Out); }
        using var payload = JsonDocument.Parse(output.ToString());
        var gates = payload.RootElement.GetProperty("gates");
        Assert.Equal(ran, gates.GetProperty("preflightRan").GetBoolean());
        if (ran) Assert.Equal(passed, gates.GetProperty("preflightOk").GetBoolean());
        else Assert.Equal(JsonValueKind.Null, gates.GetProperty("preflightOk").ValueKind);
    }

    private static RuntimePlanContext Context(bool visual)
    {
        var plan = new AdaptiveRuntimeExecutionPlan { RunVisualQa = visual, RunAestheticQa = visual, Focus = "balanced", VisualQaFallbackPolicy = "strict" };
        return new("goal", AdaptiveRuntimeManifest.Default(), plan,
            AdaptiveRuntimePlanResolver.BuildRuntimeSpec(plan, "filter"), "prod", null);
    }
}
