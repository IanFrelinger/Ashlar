using FluentAssertions;
using Ashlar.CLI.Formatting;
using Ashlar.Orchestration.Agents;
using Ashlar.Orchestration.Coordination;
using Xunit;

namespace Ashlar.Tests.CLI.Tests.Formatting;

/// <summary>
/// The defect: <c>ashlar run</c> printed "Orchestration completed successfully" on the line above
/// "Progress: 0/0 agents completed (0 %)" and exited 0. Both came from the same result object and
/// nothing reconciled them — <c>Success</c> means "the integrated output was structurally valid",
/// which is true of the empty output an unworked run produces.
///
/// <para>The second, quieter half: when a request decomposes into a domain this build has no
/// specialized agent for, <c>GenericAgent</c> returns a result explicitly flagged as a placeholder
/// and logs "performed no work". The flag had been added so the case would be detectable. Nothing
/// detected it. These pin that both shapes are now failures.</para>
/// </summary>
public sealed class OrchestrationWorkReportTests
{
    private static OrchestrationResult Result(
        bool success,
        ProgressSummary? progress = null,
        IReadOnlyDictionary<string, object>? integrated = null) =>
        new()
        {
            Success = success,
            ProgressSummary = progress,
            IntegratedOutput = integrated is null
                ? null
                : new IntegratedOutput
                {
                    IntegratedResults = integrated,
                    AgentOutputs = integrated,
                    IntegratedAt = DateTimeOffset.UtcNow,
                },
        };

    private static PlaceholderAgentResult Placeholder(string domain) =>
        new("fallback-1", domain, "do the thing", true, "no work performed");

    [Fact]
    public void No_agent_completed_is_not_success()
    {
        var result = Result(true, new ProgressSummary { TotalAgents = 0, Completed = 0 });

        OrchestrationWorkReport.DidWork(result).Should().BeFalse();
        OrchestrationWorkReport.IsSilentSuccess(result).Should().BeTrue(
            "this is the exact reported shape: Success=true beside 0/0 agents completed");
    }

    [Fact]
    public void A_run_whose_every_result_is_a_placeholder_is_not_success()
    {
        var result = Result(
            true,
            new ProgressSummary { TotalAgents = 1, Completed = 1, ProgressPercentage = 1.0 },
            new Dictionary<string, object> { ["Gameplay"] = Placeholder("Gameplay") });

        OrchestrationWorkReport.DidWork(result).Should().BeFalse(
            "an agent that completed by returning \"no work performed\" did no work");
        OrchestrationWorkReport.PlaceholderDomains(result).Should().Equal("Gameplay");
    }

    [Fact]
    public void A_run_with_some_real_output_is_success()
    {
        var result = Result(
            true,
            new ProgressSummary { TotalAgents = 2, Completed = 2, ProgressPercentage = 1.0 },
            new Dictionary<string, object>
            {
                ["Gameplay"] = Placeholder("Gameplay"),
                ["Data"] = new { Output = "a real answer" },
            });

        OrchestrationWorkReport.DidWork(result).Should().BeTrue(
            "partial placeholders are not a reason to refuse a run that also produced something");
    }

    [Fact]
    public void An_unmeasured_run_is_left_alone()
    {
        // Deliberately conservative: no progress summary and no integrated output is not the
        // reported defect, and refusing it would turn a reporting fix into a regression.
        OrchestrationWorkReport.DidWork(Result(true)).Should().BeTrue();
    }

    [Fact]
    public void The_report_names_the_domains_and_the_fix()
    {
        var result = Result(
            true,
            new ProgressSummary { TotalAgents = 1, Completed = 1 },
            new Dictionary<string, object> { ["Gameplay"] = Placeholder("Gameplay") });

        var report = string.Join("\n", OrchestrationWorkReport.NoWorkReport(result));

        report.Should().Contain("NO WORK WAS PERFORMED");
        report.Should().Contain("Gameplay", "the reader is told which domain had no agent");
        report.Should().Contain("provider: mock", "and that the scaffold's offline default does not do their work");
        report.Should().Contain("--verbose");
    }

    [Fact]
    public void A_failed_run_is_not_reclassified_as_silent_success()
    {
        var result = Result(false, new ProgressSummary { TotalAgents = 1, Completed = 0 });

        OrchestrationWorkReport.IsSilentSuccess(result).Should().BeFalse(
            "silent success is specifically the claim of success over nothing; an honest failure is not that");
    }
}
