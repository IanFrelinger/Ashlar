using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Ashlar.CLI.Commands.BackgroundAgent;

namespace Ashlar.CLI;

/// <summary>Program.</summary>
static partial class Program
{
    private static Command BuildBackgroundAgentCommand(Option<bool> jsonOpt)
    {
        // ashlar background-agent
        var backgroundAgentCmd = new Command("background-agent", "Configure and manage background agents");
        var listBgCmd = new Command("list", "List configured background agents")
        {
            new Option<string?>("--status", "Filter by status (Running, Stopped, Error, NotRegistered)"),
            new Option<string?>("--role", "Filter by role"),
            new Option<string?>("--sensitivity", "Filter by max sensitivity level")
        };
        listBgCmd.SetHandler(
            /// <summary>Async.</summary>
            /// <param name="formatJson">Format json.</param>
            /// <param name="status">Status.</param>
            /// <param name="role">Role.</param>
            /// <param name="sensitivity">Sensitivity.</param>
            async (bool formatJson, string? status, string? role, string? sensitivity) =>
            {
                var cmd = ServiceProvider.GetRequiredService<BackgroundAgentCommand>();
                var exitCode = await cmd.ListAsync(formatJson, status, role, sensitivity);
                Environment.Exit(exitCode);
            },
            jsonOpt,
            listBgCmd.Options[0] as Option<string?> ?? throw new InvalidOperationException(),
            listBgCmd.Options[1] as Option<string?> ?? throw new InvalidOperationException(),
            listBgCmd.Options[2] as Option<string?> ?? throw new InvalidOperationException());
        backgroundAgentCmd.AddCommand(listBgCmd);

        var showBgIdOpt = new Option<string>("--id", "Agent ID") { IsRequired = true };
        var showBgCmd = new Command("show", "Show details for a background agent") { showBgIdOpt };
        showBgCmd.SetHandler(
            /// <summary>Async.</summary>
            /// <param name="id">Id.</param>
            /// <param name="formatJson">Format json.</param>
            async (string id, bool formatJson) =>
            {
                var cmd = ServiceProvider.GetRequiredService<BackgroundAgentCommand>();
                var exitCode = await cmd.ShowAsync(id, formatJson);
                Environment.Exit(exitCode);
            },
            showBgIdOpt,
            jsonOpt);
        backgroundAgentCmd.AddCommand(showBgCmd);

        var startBgIdOpt = new Option<string>("--id", "Agent ID") { IsRequired = true };
        var startBgCmd = new Command("start", "Start a background agent") { startBgIdOpt };
        startBgCmd.SetHandler(
            /// <summary>Async.</summary>
            /// <param name="id">Id.</param>
            /// <param name="formatJson">Format json.</param>
            async (string id, bool formatJson) =>
            {
                var cmd = ServiceProvider.GetRequiredService<BackgroundAgentCommand>();
                var exitCode = await cmd.StartAsync(id, formatJson);
                Environment.Exit(exitCode);
            },
            startBgIdOpt,
            jsonOpt);
        backgroundAgentCmd.AddCommand(startBgCmd);

        var stopBgIdOpt = new Option<string>("--id", "Agent ID") { IsRequired = true };
        var stopBgCmd = new Command("stop", "Stop a background agent") { stopBgIdOpt };
        stopBgCmd.SetHandler(
            /// <summary>Async.</summary>
            /// <param name="id">Id.</param>
            /// <param name="formatJson">Format json.</param>
            async (string id, bool formatJson) =>
            {
                var cmd = ServiceProvider.GetRequiredService<BackgroundAgentCommand>();
                var exitCode = await cmd.StopAsync(id, formatJson);
                Environment.Exit(exitCode);
            },
            stopBgIdOpt,
            jsonOpt);
        backgroundAgentCmd.AddCommand(stopBgCmd);

        var restartBgIdOpt = new Option<string>("--id", "Agent ID") { IsRequired = true };
        var restartBgCmd = new Command("restart", "Restart a background agent") { restartBgIdOpt };
        restartBgCmd.SetHandler(
            /// <summary>Async.</summary>
            /// <param name="id">Id.</param>
            /// <param name="formatJson">Format json.</param>
            async (string id, bool formatJson) =>
            {
                var cmd = ServiceProvider.GetRequiredService<BackgroundAgentCommand>();
                var exitCode = await cmd.RestartAsync(id, formatJson);
                Environment.Exit(exitCode);
            },
            restartBgIdOpt,
            jsonOpt);
        backgroundAgentCmd.AddCommand(restartBgCmd);

        // ashlar background-agent autoscale
        var autoscaleCmd = new Command("autoscale", "Demand-based autoscaling for role agents");
        var autoscaleApplyRoleOpt = new Option<string>("--role", "Role to scale (e.g., extender, tester)") { IsRequired = true };
        var autoscaleApplyDemandOpt = new Option<int>("--demand", "Current demand score for this role") { IsRequired = true };
        var autoscaleApplyMinOpt = new Option<int>("--min-agents", () => 0, "Minimum desired agents for the role");
        var autoscaleApplyMaxOpt = new Option<int>("--max-agents", () => 5, "Maximum desired agents for the role");
        var autoscaleApplyUnitsOpt = new Option<int>("--units-per-agent", () => 1, "Demand units handled per agent (higher value = fewer agents)");
        var autoscaleApplyIdleOpt = new Option<int>("--idle-seconds", () => 0, "Idle threshold before stopping surplus autoscaled agents");
        var autoscaleApplyCmd = new Command("apply", "Apply one autoscale decision")
        {
            autoscaleApplyRoleOpt,
            autoscaleApplyDemandOpt,
            autoscaleApplyMinOpt,
            autoscaleApplyMaxOpt,
            autoscaleApplyUnitsOpt,
            autoscaleApplyIdleOpt
        };
        autoscaleApplyCmd.SetHandler(
            /// <summary>Async.</summary>
            /// <param name="role">Role.</param>
            /// <param name="demand">Demand.</param>
            /// <param name="minAgents">Min agents.</param>
            /// <param name="maxAgents">Max agents.</param>
            /// <param name="unitsPerAgent">Units per agent.</param>
            /// <param name="idleSeconds">Idle seconds.</param>
            /// <param name="formatJson">Format json.</param>
            async (string role, int demand, int minAgents, int maxAgents, int unitsPerAgent, int idleSeconds, bool formatJson) =>
            {
                var cmd = ServiceProvider.GetRequiredService<BackgroundAgentCommand>();
                Environment.Exit(await cmd.AutoScaleAsync(role, demand, minAgents, maxAgents, unitsPerAgent, idleSeconds, formatJson));
            },
            autoscaleApplyRoleOpt,
            autoscaleApplyDemandOpt,
            autoscaleApplyMinOpt,
            autoscaleApplyMaxOpt,
            autoscaleApplyUnitsOpt,
            autoscaleApplyIdleOpt,
            jsonOpt);
        autoscaleCmd.AddCommand(autoscaleApplyCmd);
        backgroundAgentCmd.AddCommand(autoscaleCmd);

        // ashlar background-agent execute
        var executeBgIdOpt = new Option<string>("--id", "Agent ID") { IsRequired = true };
        var executeBgAsyncOpt = new Option<bool>("--async", "Run execution asynchronously (don't wait)");
        var executeBgCmd = new Command("execute", "Manually run one execution of a background agent") { executeBgIdOpt, executeBgAsyncOpt };
        executeBgCmd.SetHandler(
            /// <summary>Async.</summary>
            /// <param name="id">Id.</param>
            /// <param name="runAsync">Run async.</param>
            /// <param name="formatJson">Format json.</param>
            async (string id, bool runAsync, bool formatJson) =>
            {
                var cmd = ServiceProvider.GetRequiredService<Ashlar.CLI.Commands.BackgroundAgent.ExecuteBackgroundAgentCommand>();
                Environment.Exit(await cmd.ExecuteAsync(id, runAsync, formatJson));
            },
            executeBgIdOpt, executeBgAsyncOpt, jsonOpt);
        backgroundAgentCmd.AddCommand(executeBgCmd);

        // ashlar background-agent logs
        var logsBgIdOpt = new Option<string>("--id", "Agent ID") { IsRequired = true };
        var logsBgCmd = new Command("logs", "Show agent execution logs")
        {
            logsBgIdOpt,
            new Option<int>("--tail", () => 100, "Show last N lines"),
            new Option<string?>("--level", "Filter by level (Debug, Info, Warning, Error)"),
            new Option<string?>("--since", "Show logs since duration (e.g. 1h, 30m)")
        };
        logsBgCmd.SetHandler(
            /// <summary>Async.</summary>
            /// <param name="id">Id.</param>
            /// <param name="tail">Tail.</param>
            /// <param name="level">Level.</param>
            /// <param name="since">Since.</param>
            /// <param name="formatJson">Format json.</param>
            async (string id, int tail, string? level, string? since, bool formatJson) =>
            {
                var cmd = ServiceProvider.GetRequiredService<Ashlar.CLI.Commands.BackgroundAgent.LogsBackgroundAgentCommand>();
                TimeSpan? sinceTs = ParseSince(since);
                Environment.Exit(await cmd.ExecuteAsync(id, tail, level, sinceTs, formatJson));
            },
            logsBgIdOpt,
            logsBgCmd.Options[1] as Option<int> ?? throw new InvalidOperationException(),
            logsBgCmd.Options[2] as Option<string?> ?? throw new InvalidOperationException(),
            logsBgCmd.Options[3] as Option<string?> ?? throw new InvalidOperationException(),
            jsonOpt);
        backgroundAgentCmd.AddCommand(logsBgCmd);

        // ashlar background-agent metrics
        var metricsBgIdOpt = new Option<string>("--id", "Agent ID") { IsRequired = true };
        var metricsBgCmd = new Command("metrics", "Show agent performance metrics") { metricsBgIdOpt };
        metricsBgCmd.SetHandler(
            /// <summary>Async.</summary>
            /// <param name="id">Id.</param>
            /// <param name="formatJson">Format json.</param>
            async (string id, bool formatJson) =>
            {
                var cmd = ServiceProvider.GetRequiredService<Ashlar.CLI.Commands.BackgroundAgent.MetricsBackgroundAgentCommand>();
                Environment.Exit(await cmd.ExecuteAsync(id, formatJson));
            },
            metricsBgIdOpt, jsonOpt);
        backgroundAgentCmd.AddCommand(metricsBgCmd);

        // ashlar background-agent stats
        var statsAgentOpt = new Option<string?>("--agent", () => null, "Filter to a specific agent id");
        var statsRoleOpt = new Option<string?>("--role", () => null, "Filter to a specific role (planner, optimizer, tester, extender, ...)");
        var statsSinceOpt = new Option<double?>("--since-hours", () => null, "Only events newer than now-N hours");
        var statsBgCmd = new Command("stats", "Aggregate the cycle event log (cycles.jsonl) into per-agent throughput / denial / error stats")
        {
            statsAgentOpt,
            statsRoleOpt,
            statsSinceOpt
        };
        statsBgCmd.SetHandler(
            /// <summary>Async.</summary>
            /// <param name="agent">Agent.</param>
            /// <param name="role">Role.</param>
            /// <param name="since">Since.</param>
            /// <param name="formatJson">Format json.</param>
            async (string? agent, string? role, double? since, bool formatJson) =>
            {
                var cmd = ServiceProvider.GetRequiredService<Ashlar.CLI.Commands.BackgroundAgent.StatsBackgroundAgentCommand>();
                Environment.Exit(await cmd.ExecuteAsync(agent, role, since, formatJson));
            },
            statsAgentOpt, statsRoleOpt, statsSinceOpt, jsonOpt);
        backgroundAgentCmd.AddCommand(statsBgCmd);

        // ashlar background-agent report — the "what did the node do overnight" trust report (A4).
        // Joins cycle activity (cycles.jsonl) with admission outcomes (<project>/.ashlar/gates) over
        // a window, so an operator can leave the node unattended and see what it proposed and what
        // the gate decided. Read-only and offline.
        var reportSinceOpt = new Option<double?>("--since-hours", () => null, "Window in hours (default 24)");
        var reportProjectOpt = new Option<string?>("--project", () => null, "Project root whose .ashlar/gates to read (default: current directory)");
        var reportBgCmd = new Command("report", "Overnight report: cycle activity joined with admission-gate outcomes over a window")
        {
            reportSinceOpt, reportProjectOpt
        };
        reportBgCmd.SetHandler(
            async (double? since, string? project, bool formatJson) =>
            {
                var cmd = ServiceProvider.GetRequiredService<Ashlar.CLI.Commands.BackgroundAgent.ReportBackgroundAgentCommand>();
                Environment.Exit(await cmd.ExecuteAsync(since, project, formatJson));
            },
            reportSinceOpt, reportProjectOpt, jsonOpt);
        backgroundAgentCmd.AddCommand(reportBgCmd);

        // ashlar background-agent observations — read the structured observations log.
        // Companion to `stats`: where stats summarises agent execution, observations
        // surfaces the *facts* agents collectively published (build/test outcomes,
        // analysis violations, agent actions). Useful for quickly answering
        // "what does the daemon currently know about the codebase?".
        var obsSourceOpt = new Option<string?>("--source", () => null, "Filter to a specific source (typically the agent id)");
        var obsKindOpt = new Option<string?>("--kind", () => null, "Filter by kind (Build, Test, Analysis, AgentAction, UserSignal)");
        var obsSinceOpt = new Option<double?>("--since-hours", () => null, "Only observations newer than now-N hours");
        var obsTailOpt = new Option<int?>("--tail", () => null, "Show only the most recent N rows after filtering");
        var obsSummaryOpt = new Option<bool>("--summary", () => false, "Group counts by source/kind/severity instead of listing rows");
        var observationsBgCmd = new Command("observations", "Inspect the structured observations log (observations.jsonl)")
        {
            obsSourceOpt, obsKindOpt, obsSinceOpt, obsTailOpt, obsSummaryOpt
        };
        observationsBgCmd.SetHandler(
            /// <summary>Async.</summary>
            /// <param name="source">Source.</param>
            /// <param name="kind">Kind.</param>
            /// <param name="since">Since.</param>
            /// <param name="tail">Tail.</param>
            /// <param name="summary">Summary.</param>
            /// <param name="formatJson">Format json.</param>
            async (string? source, string? kind, double? since, int? tail, bool summary, bool formatJson) =>
            {
                var cmd = ServiceProvider.GetRequiredService<Ashlar.CLI.Commands.BackgroundAgent.ObservationsBackgroundAgentCommand>();
                Environment.Exit(await cmd.ExecuteAsync(source, kind, since, tail, summary, formatJson));
            },
            obsSourceOpt, obsKindOpt, obsSinceOpt, obsTailOpt, obsSummaryOpt, jsonOpt);
        backgroundAgentCmd.AddCommand(observationsBgCmd);

        // ashlar background-agent objectives — operator front door for the structured backlog.
        // Mirrors the same store the planner reads from, so adds here are picked up by the
        // next planner cycle automatically. Subcommands: list / show / add / complete /
        // block / unblock / stats. Kept under background-agent (not a top-level group)
        // because the backlog is meaningful only in the context of a daemon that consumes it.
        var objectivesBgCmd = new Command("objectives", "Manage the planner's objective backlog");

        var objListStatusOpt = new Option<string?>("--status", () => null, "Filter by status (Pending, InProgress, Done, Blocked)");
        var objListTagOpt = new Option<string?>("--tag", () => null, "Filter by tag (case-insensitive exact match)");
        var objListCmd = new Command("list", "List backlog items, sorted by status then priority")
        {
            objListStatusOpt, objListTagOpt
        };
        objListCmd.SetHandler(async (string? status, string? tag, bool formatJson) =>
        {
            var cmd = ServiceProvider.GetRequiredService<Ashlar.CLI.Commands.BackgroundAgent.ObjectivesBackgroundAgentCommand>();
            Environment.Exit(await cmd.ListAsync(status, tag, formatJson));
        }, objListStatusOpt, objListTagOpt, jsonOpt);
        objectivesBgCmd.AddCommand(objListCmd);

        var objShowIdArg = new Argument<string>("id", "Objective id");
        var objShowCmd = new Command("show", "Show one objective's full body and metadata") { objShowIdArg };
        objShowCmd.SetHandler(async (string id, bool formatJson) =>
        {
            var cmd = ServiceProvider.GetRequiredService<Ashlar.CLI.Commands.BackgroundAgent.ObjectivesBackgroundAgentCommand>();
            Environment.Exit(await cmd.ShowAsync(id, formatJson));
        }, objShowIdArg, jsonOpt);
        objectivesBgCmd.AddCommand(objShowCmd);

        var objAddIdOpt = new Option<string>("--id", "Stable slug used as the filename") { IsRequired = true };
        var objAddTitleOpt = new Option<string>("--title", "One-line human-readable title") { IsRequired = true };
        var objAddPriorityOpt = new Option<int>("--priority", () => 100, "Lower number = higher priority (1 highest)");
        var objAddTagsOpt = new Option<string[]>("--tag", () => Array.Empty<string>(), "Tag (repeatable)") { AllowMultipleArgumentsPerToken = true };
        var objAddBodyOpt = new Option<string?>("--body", () => null, "Inline markdown body (mutually exclusive with --body-file)");
        var objAddBodyFileOpt = new Option<string?>("--body-file", () => null, "Read markdown body from this file");
        var objAddCmd = new Command("add", "Add a new pending objective")
        {
            objAddIdOpt, objAddTitleOpt, objAddPriorityOpt, objAddTagsOpt, objAddBodyOpt, objAddBodyFileOpt
        };
        objAddCmd.SetHandler(async (string id, string title, int priority, string[] tags, string? body, string? bodyFile, bool formatJson) =>
        {
            var cmd = ServiceProvider.GetRequiredService<Ashlar.CLI.Commands.BackgroundAgent.ObjectivesBackgroundAgentCommand>();
            Environment.Exit(await cmd.AddAsync(id, title, priority, tags, body, bodyFile, formatJson));
        }, objAddIdOpt, objAddTitleOpt, objAddPriorityOpt, objAddTagsOpt, objAddBodyOpt, objAddBodyFileOpt, jsonOpt);
        objectivesBgCmd.AddCommand(objAddCmd);

        var objCompleteIdArg = new Argument<string>("id", "Objective id (must currently be InProgress)");
        var objCompleteNoteOpt = new Option<string?>("--note", () => null, "Optional note appended to the body");
        var objCompleteCmd = new Command("complete", "Mark an in-progress objective as Done")
        {
            objCompleteIdArg, objCompleteNoteOpt
        };
        objCompleteCmd.SetHandler(async (string id, string? note, bool formatJson) =>
        {
            var cmd = ServiceProvider.GetRequiredService<Ashlar.CLI.Commands.BackgroundAgent.ObjectivesBackgroundAgentCommand>();
            Environment.Exit(await cmd.CompleteAsync(id, note, formatJson));
        }, objCompleteIdArg, objCompleteNoteOpt, jsonOpt);
        objectivesBgCmd.AddCommand(objCompleteCmd);

        var objBlockIdArg = new Argument<string>("id", "Objective id");
        var objBlockReasonOpt = new Option<string>("--reason", "Why the objective is blocked") { IsRequired = true };
        var objBlockCmd = new Command("block", "Mark an objective as Blocked")
        {
            objBlockIdArg, objBlockReasonOpt
        };
        objBlockCmd.SetHandler(async (string id, string reason, bool formatJson) =>
        {
            var cmd = ServiceProvider.GetRequiredService<Ashlar.CLI.Commands.BackgroundAgent.ObjectivesBackgroundAgentCommand>();
            Environment.Exit(await cmd.BlockAsync(id, reason, formatJson));
        }, objBlockIdArg, objBlockReasonOpt, jsonOpt);
        objectivesBgCmd.AddCommand(objBlockCmd);

        var objUnblockIdArg = new Argument<string>("id", "Objective id");
        var objUnblockCmd = new Command("unblock", "Move a blocked objective back to Pending") { objUnblockIdArg };
        objUnblockCmd.SetHandler(async (string id, bool formatJson) =>
        {
            var cmd = ServiceProvider.GetRequiredService<Ashlar.CLI.Commands.BackgroundAgent.ObjectivesBackgroundAgentCommand>();
            Environment.Exit(await cmd.UnblockAsync(id, formatJson));
        }, objUnblockIdArg, jsonOpt);
        objectivesBgCmd.AddCommand(objUnblockCmd);

        var objReportIdOpt = new Option<string?>("--id", () => null, "Limit report to one objective");
        var objReportStatusOpt = new Option<string?>("--status", () => null, "Filter by status (Pending, InProgress, Done, Blocked)");
        var objReportSinceOpt = new Option<double?>("--since-hours", () => null, "Only count observations newer than now-N hours");
        var objReportCmd = new Command("report", "Cross-correlate objectives with observations (per-objective build/test/error counts)")
        {
            objReportIdOpt, objReportStatusOpt, objReportSinceOpt
        };
        objReportCmd.SetHandler(async (string? id, string? status, double? since, bool formatJson) =>
        {
            var cmd = ServiceProvider.GetRequiredService<Ashlar.CLI.Commands.BackgroundAgent.ObjectivesBackgroundAgentCommand>();
            Environment.Exit(await cmd.ReportAsync(id, status, since, formatJson));
        }, objReportIdOpt, objReportStatusOpt, objReportSinceOpt, jsonOpt);
        objectivesBgCmd.AddCommand(objReportCmd);

        var objStatsCmd = new Command("stats", "Per-status counts and per-tag breakdown of the backlog");
        objStatsCmd.SetHandler(async (bool formatJson) =>
        {
            var cmd = ServiceProvider.GetRequiredService<Ashlar.CLI.Commands.BackgroundAgent.ObjectivesBackgroundAgentCommand>();
            Environment.Exit(await cmd.StatsAsync(formatJson));
        }, jsonOpt);
        objectivesBgCmd.AddCommand(objStatsCmd);

        backgroundAgentCmd.AddCommand(objectivesBgCmd);

        // ashlar background-agent proposals — operator front door for the forge change
        // proposal queue. Mirrors the objectives CLI shape so operators only learn
        // one mental model. Subcommands: list / show / approve / reject / apply / stats.
        var proposalsBgCmd = new Command("proposals", "Manage forge-mediated change proposals");

        var propListStatusOpt = new Option<string?>("--status", () => null, "Filter by status (Proposed, Approved, Rejected, Applied, Stale)");
        var propListTargetOpt = new Option<string?>("--target-prefix", () => null, "Filter by target path prefix");
        var propListCmd = new Command("list", "List proposals, sorted by status then most-recent update")
        {
            propListStatusOpt, propListTargetOpt
        };
        propListCmd.SetHandler(async (string? status, string? targetPrefix, bool formatJson) =>
        {
            var cmd = ServiceProvider.GetRequiredService<Ashlar.CLI.Commands.BackgroundAgent.ProposalsBackgroundAgentCommand>();
            Environment.Exit(await cmd.ListAsync(status, targetPrefix, formatJson));
        }, propListStatusOpt, propListTargetOpt, jsonOpt);
        proposalsBgCmd.AddCommand(propListCmd);

        var propShowIdArg = new Argument<string>("id", "Proposal id");
        var propShowDiffOpt = new Option<bool>("--show-diff", () => false, "Include the proposed file content in the output");
        var propShowCmd = new Command("show", "Show one proposal's metadata (and optionally its proposed content)")
        {
            propShowIdArg, propShowDiffOpt
        };
        propShowCmd.SetHandler(async (string id, bool showDiff, bool formatJson) =>
        {
            var cmd = ServiceProvider.GetRequiredService<Ashlar.CLI.Commands.BackgroundAgent.ProposalsBackgroundAgentCommand>();
            Environment.Exit(await cmd.ShowAsync(id, showDiff, formatJson));
        }, propShowIdArg, propShowDiffOpt, jsonOpt);
        proposalsBgCmd.AddCommand(propShowCmd);

        var propApproveIdArg = new Argument<string>("id", "Proposal id (must currently be Proposed)");
        var propApproveByOpt = new Option<string?>("--approver", () => null, "Operator approving the change");
        var propApproveNoteOpt = new Option<string?>("--note", () => null, "Optional approval note");
        var propApproveCmd = new Command("approve", "Approve a Proposed change so it can be applied")
        {
            propApproveIdArg, propApproveByOpt, propApproveNoteOpt
        };
        propApproveCmd.SetHandler(async (string id, string? approver, string? note, bool formatJson) =>
        {
            var cmd = ServiceProvider.GetRequiredService<Ashlar.CLI.Commands.BackgroundAgent.ProposalsBackgroundAgentCommand>();
            Environment.Exit(await cmd.ApproveAsync(id, approver, note, formatJson));
        }, propApproveIdArg, propApproveByOpt, propApproveNoteOpt, jsonOpt);
        proposalsBgCmd.AddCommand(propApproveCmd);

        var propRejectIdArg = new Argument<string>("id", "Proposal id (must currently be Proposed)");
        var propRejectByOpt = new Option<string?>("--reviewer", () => null, "Operator rejecting the change");
        var propRejectNoteOpt = new Option<string?>("--note", () => null, "Why the change was rejected");
        var propRejectCmd = new Command("reject", "Reject a Proposed change")
        {
            propRejectIdArg, propRejectByOpt, propRejectNoteOpt
        };
        propRejectCmd.SetHandler(async (string id, string? reviewer, string? note, bool formatJson) =>
        {
            var cmd = ServiceProvider.GetRequiredService<Ashlar.CLI.Commands.BackgroundAgent.ProposalsBackgroundAgentCommand>();
            Environment.Exit(await cmd.RejectAsync(id, reviewer, note, formatJson));
        }, propRejectIdArg, propRejectByOpt, propRejectNoteOpt, jsonOpt);
        proposalsBgCmd.AddCommand(propRejectCmd);

        var propApplyIdArg = new Argument<string>("id", "Approved proposal id");
        var propApplyRootOpt = new Option<string>("--repo-root", () => Directory.GetCurrentDirectory(), "Repo root the target_path is resolved against");
        var propApplyForceOpt = new Option<bool>("--force", () => false, "Apply even if the file's sha256 has drifted from the proposal's BaseSha256");
        var propApplyVerifyBuildOpt = new Option<bool>("--verify-build", () => false, "After apply, run dotnet build -c Release from --repo-root (exit 4 if build fails; tree is still written)");
        var propApplyVerifyTestOpt = new Option<bool>("--verify-test", () => false, "After apply, run build then dotnet test (TRX, --no-build); implies build; exit 5 if tests fail (exit 4 if build fails)");
        var propApplyCmd = new Command("apply", "Write an Approved proposal to disk and mark it Applied")
        {
            propApplyIdArg, propApplyRootOpt, propApplyForceOpt, propApplyVerifyBuildOpt, propApplyVerifyTestOpt
        };
        propApplyCmd.SetHandler(async (string id, string root, bool force, bool verifyBuild, bool verifyTest, bool formatJson) =>
        {
            var cmd = ServiceProvider.GetRequiredService<Ashlar.CLI.Commands.BackgroundAgent.ProposalsBackgroundAgentCommand>();
            Environment.Exit(await cmd.ApplyAsync(id, root, force, formatJson, verifyBuild, verifyTest));
        }, propApplyIdArg, propApplyRootOpt, propApplyForceOpt, propApplyVerifyBuildOpt, propApplyVerifyTestOpt, jsonOpt);
        proposalsBgCmd.AddCommand(propApplyCmd);

        var propBuildRootOpt = new Option<string>("--repo-root", () => Directory.GetCurrentDirectory(), "Directory passed to dotnet build -c Release");
        var propBuildCmd = new Command("build", "Run dotnet build -c Release (forge-aligned operator check)")
        {
            propBuildRootOpt
        };
        propBuildCmd.SetHandler(async (string root, bool formatJson) =>
        {
            var cmd = ServiceProvider.GetRequiredService<Ashlar.CLI.Commands.BackgroundAgent.ProposalsBackgroundAgentCommand>();
            Environment.Exit(await cmd.BuildAsync(root, formatJson));
        }, propBuildRootOpt, jsonOpt);
        proposalsBgCmd.AddCommand(propBuildCmd);

        var propTestRootOpt = new Option<string>("--repo-root", () => Directory.GetCurrentDirectory(), "Directory for dotnet build then dotnet test (TRX, --no-build)");
        var propTestCmd = new Command("test", "Run dotnet build -c Release then dotnet test (forge-aligned operator check)")
        {
            propTestRootOpt
        };
        propTestCmd.SetHandler(async (string root, bool formatJson) =>
        {
            var cmd = ServiceProvider.GetRequiredService<Ashlar.CLI.Commands.BackgroundAgent.ProposalsBackgroundAgentCommand>();
            Environment.Exit(await cmd.TestAsync(root, formatJson));
        }, propTestRootOpt, jsonOpt);
        proposalsBgCmd.AddCommand(propTestCmd);

        var propJanProposedOpt = new Option<double?>("--proposed-ttl-hours", () => null, "Override Proposed TTL (default 72h)");
        var propJanApprovedOpt = new Option<double?>("--approved-ttl-hours", () => null, "Override Approved TTL (default 24h)");
        var propJanitorCmd = new Command("janitor", "Run the janitor sweep once: stale anything past its TTL")
        {
            propJanProposedOpt, propJanApprovedOpt
        };
        propJanitorCmd.SetHandler(async (double? proposedTtl, double? approvedTtl, bool formatJson) =>
        {
            var cmd = ServiceProvider.GetRequiredService<Ashlar.CLI.Commands.BackgroundAgent.ProposalsBackgroundAgentCommand>();
            Environment.Exit(await cmd.JanitorAsync(proposedTtl, approvedTtl, formatJson));
        }, propJanProposedOpt, propJanApprovedOpt, jsonOpt);
        proposalsBgCmd.AddCommand(propJanitorCmd);

        var propStatsCmd = new Command("stats", "Per-status counts of the proposal queue");
        propStatsCmd.SetHandler(async (bool formatJson) =>
        {
            var cmd = ServiceProvider.GetRequiredService<Ashlar.CLI.Commands.BackgroundAgent.ProposalsBackgroundAgentCommand>();
            Environment.Exit(await cmd.StatsAsync(formatJson));
        }, jsonOpt);
        proposalsBgCmd.AddCommand(propStatsCmd);

        backgroundAgentCmd.AddCommand(proposalsBgCmd);

        // ashlar background-agent mode
        var modeCmd = new Command("mode", "Get or set aggressiveness mode (passive, semi-active, active, ambient)");
        var modeGetCmd = new Command("get", "Get current mode");
        modeGetCmd.SetHandler(
            /// <summary>Async.</summary>
            /// <param name="formatJson">Format json.</param>
            async (bool formatJson) =>
            {
                var cmd = ServiceProvider.GetRequiredService<Ashlar.CLI.Commands.BackgroundAgent.ModeBackgroundAgentCommand>();
                Environment.Exit(await cmd.GetAsync(formatJson));
            },
            jsonOpt);
        modeCmd.AddCommand(modeGetCmd);
        var modeSetValueOpt = new Option<string>("--value", "Mode: passive, semi-active, active, ambient") { IsRequired = true };
        var modeSetCmd = new Command("set", "Set mode (switchable at runtime without restart)") { modeSetValueOpt };
        modeSetCmd.SetHandler(
            /// <summary>Async.</summary>
            /// <param name="value">Value.</param>
            /// <param name="formatJson">Format json.</param>
            async (string value, bool formatJson) =>
            {
                var cmd = ServiceProvider.GetRequiredService<Ashlar.CLI.Commands.BackgroundAgent.ModeBackgroundAgentCommand>();
                Environment.Exit(await cmd.SetAsync(value, formatJson));
            },
            modeSetValueOpt, jsonOpt);
        modeCmd.AddCommand(modeSetCmd);
        backgroundAgentCmd.AddCommand(modeCmd);

        // ashlar background-agent disarm — the emergency stop. Forces mode → Passive so every
        // extender halts on its next cycle (no restart; the mode file is re-read each cycle). A named
        // front door for the "stop it NOW" moment, distinct from `mode set --value passive` only in
        // intent and its loud confirmation.
        var disarmReasonOpt = new Option<string?>("--reason", () => null, "Optional reason, logged for the operator trail");
        var disarmCmd = new Command("disarm", "Emergency stop: disarm all background agents now (mode → Passive)")
        {
            disarmReasonOpt
        };
        disarmCmd.SetHandler(
            async (string? reason, bool formatJson) =>
            {
                var cmd = ServiceProvider.GetRequiredService<Ashlar.CLI.Commands.BackgroundAgent.ModeBackgroundAgentCommand>();
                Environment.Exit(await cmd.DisarmAsync(reason, formatJson));
            },
            disarmReasonOpt, jsonOpt);
        backgroundAgentCmd.AddCommand(disarmCmd);

        // ashlar background-agent sensitivity
        var sensitivityCmd = new Command("sensitivity", "Manage data sensitivity levels");
        var sensListCmd = new Command("list", "List sensitivity levels");
        sensListCmd.SetHandler(async (bool formatJson) =>
        {
            var cmd = ServiceProvider.GetRequiredService<Ashlar.CLI.Commands.BackgroundAgent.SensitivityCommand>();
            Environment.Exit(await cmd.ListAsync(formatJson));
        }, jsonOpt);
        sensitivityCmd.AddCommand(sensListCmd);
        var sensShowNameOpt = new Option<string>("--name", "Sensitivity level name") { IsRequired = true };
        var sensShowCmd = new Command("show", "Show a sensitivity level") { sensShowNameOpt };
        sensShowCmd.SetHandler(async (string name, bool formatJson) =>
        {
            var cmd = ServiceProvider.GetRequiredService<Ashlar.CLI.Commands.BackgroundAgent.SensitivityCommand>();
            Environment.Exit(await cmd.ShowAsync(name, formatJson));
        }, sensShowNameOpt, jsonOpt);
        sensitivityCmd.AddCommand(sensShowCmd);
        var sensAddNameOpt = new Option<string>("--name", "Level name") { IsRequired = true };
        var sensAddValueOpt = new Option<int>("--value", () => 0, "Sensitivity value (ordering)");
        var sensAddDescOpt = new Option<string>("--description", "Description");
        var sensAddCmd = new Command("add", "Add a custom sensitivity level")
        {
            sensAddNameOpt, sensAddValueOpt,
            new Option<bool>("--allows-external-llm", () => false),
            new Option<bool>("--allows-web-search", () => false),
            new Option<bool>("--requires-local-only", () => false),
            new Option<bool>("--allows-network-exports", () => false),
            sensAddDescOpt
        };
        sensAddCmd.SetHandler(
            /// <summary>Async.</summary>
            /// <param name="name">Name.</param>
            /// <param name="value">Value.</param>
            /// <param name="allowsExternalLLM">Allows external llm.</param>
            /// <param name="allowsWebSearch">Allows web search.</param>
            /// <param name="requiresLocalOnly">Requires local only.</param>
            /// <param name="allowsNetworkExports">Allows network exports.</param>
            /// <param name="description">Description.</param>
            /// <param name="formatJson">Format json.</param>
            async (string name, int value, bool allowsExternalLLM, bool allowsWebSearch, bool requiresLocalOnly, bool allowsNetworkExports, string? description, bool formatJson) =>
            {
                var cmd = ServiceProvider.GetRequiredService<Ashlar.CLI.Commands.BackgroundAgent.SensitivityCommand>();
                Environment.Exit(await cmd.AddAsync(name, value, allowsExternalLLM, allowsWebSearch, requiresLocalOnly, allowsNetworkExports, description ?? "", formatJson));
            },
            sensAddNameOpt, sensAddValueOpt,
            sensAddCmd.Options[2] as Option<bool> ?? throw new InvalidOperationException(),
            sensAddCmd.Options[3] as Option<bool> ?? throw new InvalidOperationException(),
            sensAddCmd.Options[4] as Option<bool> ?? throw new InvalidOperationException(),
            sensAddCmd.Options[5] as Option<bool> ?? throw new InvalidOperationException(),
            sensAddDescOpt, jsonOpt);
        sensitivityCmd.AddCommand(sensAddCmd);
        var sensUpdateNameOpt = new Option<string>("--name", "Level name") { IsRequired = true };
        var sensUpdateCmd = new Command("update", "Update a custom sensitivity level")
        {
            sensUpdateNameOpt, new Option<int>("--value", () => 0, "Sensitivity value"),
            new Option<bool>("--allows-external-llm", () => false),
            new Option<bool>("--allows-web-search", () => false),
            new Option<bool>("--requires-local-only", () => false),
            new Option<bool>("--allows-network-exports", () => false),
            new Option<string>("--description", "Description")
        };
        sensUpdateCmd.SetHandler(
            /// <summary>Async.</summary>
            /// <param name="name">Name.</param>
            /// <param name="value">Value.</param>
            /// <param name="allowsExternalLLM">Allows external llm.</param>
            /// <param name="allowsWebSearch">Allows web search.</param>
            /// <param name="requiresLocalOnly">Requires local only.</param>
            /// <param name="allowsNetworkExports">Allows network exports.</param>
            /// <param name="description">Description.</param>
            /// <param name="formatJson">Format json.</param>
            async (string name, int value, bool allowsExternalLLM, bool allowsWebSearch, bool requiresLocalOnly, bool allowsNetworkExports, string? description, bool formatJson) =>
            {
                var cmd = ServiceProvider.GetRequiredService<Ashlar.CLI.Commands.BackgroundAgent.SensitivityCommand>();
                Environment.Exit(await cmd.UpdateAsync(name, value, allowsExternalLLM, allowsWebSearch, requiresLocalOnly, allowsNetworkExports, description ?? "", formatJson));
            },
            sensUpdateNameOpt, sensUpdateCmd.Options[1] as Option<int> ?? throw new InvalidOperationException(),
            sensUpdateCmd.Options[2] as Option<bool> ?? throw new InvalidOperationException(),
            sensUpdateCmd.Options[3] as Option<bool> ?? throw new InvalidOperationException(),
            sensUpdateCmd.Options[4] as Option<bool> ?? throw new InvalidOperationException(),
            sensUpdateCmd.Options[5] as Option<bool> ?? throw new InvalidOperationException(),
            sensUpdateCmd.Options[6] as Option<string> ?? throw new InvalidOperationException(),
            jsonOpt);
        sensitivityCmd.AddCommand(sensUpdateCmd);
        var sensRemoveNameOpt = new Option<string>("--name", "Level name") { IsRequired = true };
        var sensRemoveCmd = new Command("remove", "Remove a custom sensitivity level") { sensRemoveNameOpt };
        sensRemoveCmd.SetHandler(async (string name, bool formatJson) =>
        {
            var cmd = ServiceProvider.GetRequiredService<Ashlar.CLI.Commands.BackgroundAgent.SensitivityCommand>();
            Environment.Exit(await cmd.RemoveAsync(name, formatJson));
        }, sensRemoveNameOpt, jsonOpt);
        sensitivityCmd.AddCommand(sensRemoveCmd);
        backgroundAgentCmd.AddCommand(sensitivityCmd);

        // ashlar background-agent rag
        var ragCmd = new Command("rag", "RAG (Retrieval Augmented Generation) operations");
        var ragIndexPathsOpt = new Option<string[]>("--paths", "Paths to index (files or directories)") { IsRequired = true, AllowMultipleArgumentsPerToken = true };
        var ragIndexSensOpt = new Option<string?>("--sensitivity", "Default sensitivity level for indexed documents");
        var ragIndexCmd = new Command("index", "Index paths into RAG store") { ragIndexPathsOpt, ragIndexSensOpt };
        ragIndexCmd.SetHandler(
            /// <summary>Async.</summary>
            /// <param name="paths">Paths.</param>
            /// <param name="sensitivity">Sensitivity.</param>
            /// <param name="formatJson">Format json.</param>
            async (string[] paths, string? sensitivity, bool formatJson) =>
            {
                var cmd = ServiceProvider.GetRequiredService<Ashlar.CLI.Commands.BackgroundAgent.RAGCommand>();
                Environment.Exit(await cmd.IndexAsync(paths ?? Array.Empty<string>(), sensitivity, formatJson));
            },
            ragIndexPathsOpt, ragIndexSensOpt, jsonOpt);
        ragCmd.AddCommand(ragIndexCmd);
        var ragSearchQueryOpt = new Option<string>("--query", "Search query") { IsRequired = true };
        var ragSearchCmd = new Command("search", "Search RAG store")
        {
            ragSearchQueryOpt,
            new Option<int>("--max-results", () => 5, "Max results"),
            new Option<double>("--min-score", () => 0.0, "Min similarity score"),
            new Option<string?>("--max-sensitivity", "Max sensitivity level for results")
        };
        ragSearchCmd.SetHandler(
            /// <summary>Async.</summary>
            /// <param name="query">Query.</param>
            /// <param name="maxResults">Max results.</param>
            /// <param name="minScore">Min score.</param>
            /// <param name="maxSensitivity">Max sensitivity.</param>
            /// <param name="formatJson">Format json.</param>
            async (string query, int maxResults, double minScore, string? maxSensitivity, bool formatJson) =>
            {
                var cmd = ServiceProvider.GetRequiredService<Ashlar.CLI.Commands.BackgroundAgent.RAGCommand>();
                Environment.Exit(await cmd.SearchAsync(query, maxResults, minScore, maxSensitivity, formatJson));
            },
            ragSearchQueryOpt,
            ragSearchCmd.Options[1] as Option<int> ?? throw new InvalidOperationException(),
            ragSearchCmd.Options[2] as Option<double> ?? throw new InvalidOperationException(),
            ragSearchCmd.Options[3] as Option<string?> ?? throw new InvalidOperationException(),
            jsonOpt);
        ragCmd.AddCommand(ragSearchCmd);
        var ragStatsCmd = new Command("stats", "Show RAG store statistics");
        ragStatsCmd.SetHandler(async (bool formatJson) =>
        {
            var cmd = ServiceProvider.GetRequiredService<Ashlar.CLI.Commands.BackgroundAgent.RAGCommand>();
            Environment.Exit(await cmd.StatsAsync(formatJson));
        }, jsonOpt);
        ragCmd.AddCommand(ragStatsCmd);
        var ragClearCmd = new Command("clear", "Clear RAG store");
        ragClearCmd.SetHandler(async (bool formatJson) =>
        {
            var cmd = ServiceProvider.GetRequiredService<Ashlar.CLI.Commands.BackgroundAgent.RAGCommand>();
            Environment.Exit(await cmd.ClearAsync(formatJson));
        }, jsonOpt);
        ragCmd.AddCommand(ragClearCmd);
        backgroundAgentCmd.AddCommand(ragCmd);

        // ashlar background-agent websearch
        var webSearchCmd = new Command("websearch", "Web search configuration and test");
        var webSearchConfigureCmd = new Command("configure", "Show web search configuration");
        webSearchConfigureCmd.SetHandler(async (bool formatJson) =>
        {
            var cmd = ServiceProvider.GetRequiredService<Ashlar.CLI.Commands.BackgroundAgent.WebSearchCommand>();
            Environment.Exit(await cmd.ConfigureAsync(formatJson));
        }, jsonOpt);
        webSearchCmd.AddCommand(webSearchConfigureCmd);
        var webSearchTestCmd = new Command("test", "Run a test search")
        {
            new Option<string>("--query", () => "Ashlar framework", "Search query"),
            new Option<int>("--max-results", () => 5, "Max results")
        };
        webSearchTestCmd.SetHandler(
            /// <summary>Async.</summary>
            /// <param name="query">Query.</param>
            /// <param name="maxResults">Max results.</param>
            /// <param name="formatJson">Format json.</param>
            async (string query, int maxResults, bool formatJson) =>
            {
                var cmd = ServiceProvider.GetRequiredService<Ashlar.CLI.Commands.BackgroundAgent.WebSearchCommand>();
                Environment.Exit(await cmd.TestAsync(query, maxResults, formatJson));
            },
            webSearchTestCmd.Options[0] as Option<string> ?? throw new InvalidOperationException(),
            webSearchTestCmd.Options[1] as Option<int> ?? throw new InvalidOperationException(),
            jsonOpt);
        webSearchCmd.AddCommand(webSearchTestCmd);
        backgroundAgentCmd.AddCommand(webSearchCmd);

        // ashlar background-agent daemon
        var daemonConfigOpt = new Option<FileInfo?>("--config", "Optional JSON config file with BackgroundAgents:Agents entries.");
        var daemonDurationOpt = new Option<string?>("--duration", "Optional run duration (e.g. 30s, 5m, 1h). Omit to run until Ctrl+C.");
        var daemonPatternStoreOpt = new Option<string?>("--pattern-store-path", "Optional pattern store path override.");
        var daemonDisableObservationOpt = new Option<bool>("--disable-observation", "Disable observation pipeline while running daemon mode.");
        var daemonCmd = new Command("daemon", "Run background agents in long-lived daemon mode")
        {
            daemonConfigOpt,
            daemonDurationOpt,
            daemonPatternStoreOpt,
            daemonDisableObservationOpt
        };
        // The InvocationContext form, not the typed-parameter one, for ONE reason: it is the only
        // shape that can hand the daemon a real cancellation token. The typed form called RunAsync
        // with no token, so the daemon's own "did the operator stop me?" guard
        // (cancellationToken.ThrowIfCancellationRequested) was dead code against
        // CancellationToken.None, and an ordinary SIGTERM of a `--duration` run was reported as
        // ok:false / status:faulted with exit 1 and a "faulted" heartbeat.
        daemonCmd.SetHandler(async (System.CommandLine.Invocation.InvocationContext ctx) =>
        {
            var cmd = ServiceProvider.GetRequiredService<Ashlar.CLI.Commands.BackgroundAgent.BackgroundAgentDaemonCommand>();
            // ctx.ExitCode, NOT Environment.Exit. GetCancellationToken() is what ARMS
            // System.CommandLine's CancelOnProcessTermination middleware, and the handler it then
            // registers on AppDomain.ProcessExit blocks until this invocation completes. Calling
            // Environment.Exit from inside the invocation therefore deadlocks against a handler
            // waiting for the invocation to finish — measured as three daemon smoke tests timing
            // out at 2m30s each. Nothing needed Environment.Exit here: Program.Main returns what
            // InvokeAsync returns, which is this.
            ctx.ExitCode = await cmd.RunAsync(
                ctx.ParseResult.GetValueForOption(daemonConfigOpt)?.FullName,
                ctx.ParseResult.GetValueForOption(daemonDurationOpt),
                ctx.ParseResult.GetValueForOption(daemonPatternStoreOpt),
                ctx.ParseResult.GetValueForOption(daemonDisableObservationOpt),
                ctx.ParseResult.GetValueForOption(jsonOpt),
                ctx.GetCancellationToken());
        });
        backgroundAgentCmd.AddCommand(daemonCmd);

        // ashlar background-agent dashboard — localhost read-only operator UI
        var dashboardPortOpt = new Option<int>("--port", () => 5055, "HTTP port (127.0.0.1 only).");
        var dashboardOpenOpt = new Option<bool>("--open", () => false, "Open the default browser to the dashboard URL.");
        var dashboardAuthOpt = new Option<string?>("--auth-token", "Optional shared secret; also read from ASHLAR_DASHBOARD_AUTH_TOKEN. When set, require ?token= or Bearer header.");
        var dashboardCmd = new Command("dashboard", "Read-only Runtime Studio operator dashboard (objectives, forge, observations)")
        {
            dashboardPortOpt,
            dashboardOpenOpt,
            dashboardAuthOpt
        };
        dashboardCmd.SetHandler(
            /// <summary>Async.</summary>
            /// <param name="port">Port.</param>
            /// <param name="open">Open.</param>
            /// <param name="authToken">Auth token.</param>
            async (int port, bool open, string? authToken) =>
            {
                var cmd = ServiceProvider.GetRequiredService<Ashlar.CLI.Commands.BackgroundAgent.OperatorDashboardBackgroundAgentCommand>();
                Environment.Exit(await cmd.RunAsync(port, open, authToken, CancellationToken.None));
            },
            dashboardPortOpt,
            dashboardOpenOpt,
            dashboardAuthOpt);
        backgroundAgentCmd.AddCommand(dashboardCmd);

        return backgroundAgentCmd;
    }
}
