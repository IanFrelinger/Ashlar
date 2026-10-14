using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Ashlar.CLI.Commands;
using Ashlar.CLI.Formatting;

namespace Ashlar.CLI;

/// <summary>Program.</summary>
static partial class Program
{
    private static Command BuildMetricsCommand(Option<bool> jsonOpt, Option<bool> verboseOpt)
    {
        // ashlar metrics (resolves lazily)
        var metricsCmd = new Command("metrics", "View orchestration metrics and performance data");
        
        // ashlar metrics report
        var metricsReportCmd = new Command("report", "Show performance report");
        metricsReportCmd.SetHandler(
            /// <summary>Async.</summary>
            /// <param name="json">Json.</param>
            /// <param name="verbose">Verbose.</param>
            async (bool json, bool verbose) =>
            {
                var cmd = ServiceProvider.GetRequiredService<MetricsCommand>();
                var exitCode = await cmd.ShowReportAsync(json, verbose);
                EgressCliInvocation.Exit(exitCode);
            },
            jsonOpt,
            verboseOpt);
        metricsCmd.AddCommand(metricsReportCmd);

        // ashlar metrics agent
        var metricsAgentCmd = new Command("agent", "Show metrics for a specific agent");
        metricsAgentCmd.AddArgument(new Argument<string>("id", "Agent ID"));
        metricsAgentCmd.SetHandler(
            /// <summary>Async.</summary>
            /// <param name="id">Id.</param>
            /// <param name="json">Json.</param>
            /// <param name="verbose">Verbose.</param>
            async (string id, bool json, bool verbose) =>
            {
                var cmd = ServiceProvider.GetRequiredService<MetricsCommand>();
                var exitCode = await cmd.ShowAgentAsync(id, json, verbose);
                EgressCliInvocation.Exit(exitCode);
            },
            metricsAgentCmd.Arguments[0] as Argument<string> ?? throw new InvalidOperationException(),
            jsonOpt,
            verboseOpt);
        metricsCmd.AddCommand(metricsAgentCmd);

        // ashlar metrics traces
        var metricsTracesCmd = new Command("traces", "Show trace spans");
        metricsTracesCmd.AddOption(new Option<string?>("--correlation-id", "Filter by correlation ID"));
        metricsTracesCmd.AddOption(new Option<string?>("--operation", "Filter by operation name"));
        metricsTracesCmd.SetHandler(
            /// <summary>Async.</summary>
            /// <param name="correlationId">Correlation id.</param>
            /// <param name="operation">Operation.</param>
            /// <param name="json">Json.</param>
            /// <param name="verbose">Verbose.</param>
            async (string? correlationId, string? operation, bool json, bool verbose) =>
            {
                var cmd = ServiceProvider.GetRequiredService<MetricsCommand>();
                var exitCode = await cmd.ShowTracesAsync(correlationId, operation, json, verbose);
                EgressCliInvocation.Exit(exitCode);
            },
            metricsTracesCmd.Options[0] as Option<string?> ?? throw new InvalidOperationException(),
            metricsTracesCmd.Options[1] as Option<string?> ?? throw new InvalidOperationException(),
            jsonOpt,
            verboseOpt);
        metricsCmd.AddCommand(metricsTracesCmd);

        // ashlar metrics clear
        var metricsClearCmd = new Command("clear", "Clear all collected metrics");
        metricsClearCmd.SetHandler(
            /// <summary>Async.</summary>
            /// <param name="json">Json.</param>
            /// <param name="verbose">Verbose.</param>
            async (bool json, bool verbose) =>
            {
                var cmd = ServiceProvider.GetRequiredService<MetricsCommand>();
                var exitCode = await cmd.ClearAsync(json, verbose);
                EgressCliInvocation.Exit(exitCode);
            },
            jsonOpt,
            verboseOpt);
        metricsCmd.AddCommand(metricsClearCmd);

        // ashlar metrics self-improvement (P3.4: holdout pass rate)
        var metricsSelfImprovementCmd = new Command("self-improvement", "Show self-improvement metrics (holdout pass rate, etc.)");
        metricsSelfImprovementCmd.SetHandler(
            /// <summary>Async.</summary>
            /// <param name="json">Json.</param>
            async (bool json) =>
            {
                var store = ServiceProvider.GetRequiredService<Ashlar.Core.Application.SelfImprovement.Ports.ISelfImprovementMetricsStore>();
                var report = await store.GetLastAsync();
                if (report == null)
                {
                    if (json) Console.WriteLine("{\"ok\":true,\"message\":\"No self-improvement run recorded\"}");
                    else Console.WriteLine("No self-improvement run recorded. Run 'ashlar improve --self' first.");
                    return;
                }
                if (json)
                {
                    var j = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
                    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
                    {
                        ok = true,
                        runAt = report.RunAt,
                        fixesPromoted = report.FixesPromoted,
                        holdoutPassed = report.HoldoutPassed,
                        holdoutTotal = report.HoldoutTotal,
                        holdoutPassRate = report.HoldoutPassRate
                    }, j));
                }
                else
                {
                    Console.WriteLine($"Last run: {report.RunAt:O}");
                    Console.WriteLine($"  Fixes promoted: {report.FixesPromoted}");
                    if (report.HoldoutTotal.HasValue)
                    {
                        Console.WriteLine($"  Holdout: {report.HoldoutPassed}/{report.HoldoutTotal} passed");
                        if (report.HoldoutPassRate.HasValue)
                            Console.WriteLine($"  Holdout pass rate: {report.HoldoutPassRate:P1}");
                    }
                }
            },
            jsonOpt);
        metricsCmd.AddCommand(metricsSelfImprovementCmd);

        return metricsCmd;
    }
}
