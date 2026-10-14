using System.CommandLine;
using System.Text.Json;
using Ashlar.BackgroundAgents.Forge;
using Ashlar.BackgroundAgents.Objectives;
using Ashlar.BackgroundAgents.RuntimeStudio;
using Ashlar.Core.Application.Paths;
using Ashlar.CLI.Runtime;

namespace Ashlar.CLI.Commands;

/// <summary>
/// Runtime Studio helper commands (local agent-set tuning).
/// </summary>
public sealed class RuntimeStudioCommand : Command
{
    /// <summary>Creates a new RuntimeStudioCommand instance.</summary>
    public RuntimeStudioCommand() : base("runtime-studio", "Runtime Studio: local agent-set helpers")
    {
        var jsonOpt = new Option<bool>("--format-json", () => false, "Emit JSON output");

        var specOpt = new Option<string>(
            "--spec",
            () => Path.Combine(".ashlar", "workflow", "workflow_lab.runtime.json"),
            "Workflow lab runtime spec JSON (repo-relative or absolute).");

        var agentSetOpt = new Option<string>(
            "--agent-set",
            DefaultAgentSetRelativePath,
            "Background agent-set JSON to update (repo-relative or absolute). Default: the gitignored " +
            ".ashlar/runtime-studio/agent_set.local.json when it exists (written by optimize_agent_cluster.sh), " +
            "otherwise the tracked apps/runtime-studio/config/agent_set.local.json.");

        var dryRunOpt = new Option<bool>("--dry-run", () => false, "Print planned ModelName changes without writing.");

        var applyTuneCmd = new Command(
            "apply-tune",
            "Apply the last workflow optimize winner to Runtime Studio Ollama ModelName fields (see .ashlar/runtime/workflow_optimize_last.json).");
        applyTuneCmd.AddOption(jsonOpt);
        applyTuneCmd.AddOption(specOpt);
        applyTuneCmd.AddOption(agentSetOpt);
        applyTuneCmd.AddOption(dryRunOpt);
        applyTuneCmd.SetHandler(
            (bool json, string spec, string agentSet, bool dryRun) =>
            {
                EgressCliInvocation.Exit(ExecuteApplyTune(json, spec, agentSet, dryRun));
            },
            jsonOpt,
            specOpt,
            agentSetOpt,
            dryRunOpt);

        var withMetricsOpt = new Option<bool>(
            "--with-metrics",
            () => false,
            "Append runtime-studio backlog metrics (same collector as `runtime-studio metrics`).");

        var statusCmd = new Command(
            "status",
            "Show last workflow optimize tune snapshot and current Ollama ModelName entries in the agent-set config.");
        statusCmd.AddOption(jsonOpt);
        statusCmd.AddOption(agentSetOpt);
        statusCmd.AddOption(withMetricsOpt);
        statusCmd.SetHandler(
            (bool json, string agentSet, bool withMetrics) =>
            {
                EgressCliInvocation.Exit(ExecuteStatus(json, agentSet, withMetrics));
            },
            jsonOpt,
            agentSetOpt,
            withMetricsOpt);

        var metricsCmd = new Command(
            "metrics",
            "Print runtime-studio backlog metrics (aligned with GET /api/runtime-studio/metrics).");
        metricsCmd.AddOption(jsonOpt);
        metricsCmd.SetHandler(
            (bool json) => { EgressCliInvocation.Exit(ExecuteMetrics(json)); },
            jsonOpt);

        var strictDoctorOpt = new Option<bool>(
            "--strict",
            () => false,
            "Treat missing objectives/forge directories as errors (not just warnings).");

        var doctorCmd = new Command(
            "doctor",
            "Validate agent-set JSON and runtime-studio paths (exit 1 on hard failures).");
        doctorCmd.AddOption(jsonOpt);
        doctorCmd.AddOption(agentSetOpt);
        doctorCmd.AddOption(strictDoctorOpt);
        doctorCmd.SetHandler(
            (bool json, string agentSet, bool strict) => { EgressCliInvocation.Exit(ExecuteDoctor(json, agentSet, strict)); },
            jsonOpt,
            agentSetOpt,
            strictDoctorOpt);

        AddCommand(applyTuneCmd);
        AddCommand(statusCmd);
        AddCommand(metricsCmd);
        AddCommand(doctorCmd);
    }

    /// <summary>Tracked agent-set definitions (roles, schedules, policies); never the tune output target by default.</summary>
    internal static readonly string TrackedAgentSetRelativePath =
        Path.Combine("apps", "runtime-studio", "config", "agent_set.local.json");

    /// <summary>Gitignored, hardware-tuned copy written by <c>optimize_agent_cluster.sh</c> / <c>setup --tune</c>.</summary>
    internal static readonly string LocalAgentSetRelativePath =
        Path.Combine(".ashlar", "runtime-studio", "agent_set.local.json");

    /// <summary>
    /// Local tuned copy first, tracked definitions as the fallback (mirrors <c>run_agent_set_local.sh</c>).
    /// </summary>
    internal static string DefaultAgentSetRelativePath()
    {
        var repoRoot = RepoPathResolver.FindRepoRoot();
        return File.Exists(Path.Combine(repoRoot, LocalAgentSetRelativePath))
            ? LocalAgentSetRelativePath
            : TrackedAgentSetRelativePath;
    }

    private static int ExecuteDoctor(bool formatJson, string agentSet, bool strict)
    {
        var repoRoot = RepoPathResolver.FindRepoRoot();
        var agentSetPath = ResolveAgentSetPath(repoRoot, agentSet);
        var issues = new List<string>();
        var warnings = new List<string>();

        if (!File.Exists(agentSetPath))
            issues.Add($"agent_set_not_found:{agentSetPath}");
        else
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(agentSetPath));
                var root = doc.RootElement;
                if (!root.TryGetProperty("BackgroundAgents", out var bg) || bg.ValueKind != JsonValueKind.Object)
                    issues.Add("agent_set_missing_background_agents");
                else if (!bg.TryGetProperty("Agents", out var agents) || agents.ValueKind != JsonValueKind.Array)
                    issues.Add("agent_set_missing_agents_array");
                else if (agents.GetArrayLength() == 0)
                    warnings.Add("agent_set_agents_empty");
            }
            catch (JsonException)
            {
                issues.Add("agent_set_invalid_json");
            }
        }

        var paths = RuntimeStudioPathResolver.Resolve(repoRoot);
        void CheckDir(string label, string path)
        {
            if (Directory.Exists(path))
                return;
            var msg = $"{label}_missing:{path}";
            if (strict)
                issues.Add(msg);
            else
                warnings.Add(msg);
        }

        CheckDir("objectives_root", paths.ObjectivesRoot);
        CheckDir("forge_root", paths.ForgeRoot);
        var obsDir = Path.GetDirectoryName(paths.ObservationsPath);
        if (!string.IsNullOrEmpty(obsDir) && !Directory.Exists(obsDir))
        {
            var msg = $"observations_parent_missing:{obsDir}";
            if (strict)
                issues.Add(msg);
            else
                warnings.Add(msg);
        }

        var ok = issues.Count == 0;
        if (formatJson)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                ok,
                repoRoot,
                agentSetPath,
                objectivesRoot = paths.ObjectivesRoot,
                forgeRoot = paths.ForgeRoot,
                observationsPath = paths.ObservationsPath,
                issues,
                warnings
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        else
        {
            Console.WriteLine(ok ? "Runtime Studio doctor: OK" : "Runtime Studio doctor: FAILED");
            Console.WriteLine($"Repository: {repoRoot}");
            Console.WriteLine($"Agent set:  {agentSetPath}");
            foreach (var w in warnings)
                Console.WriteLine($"  warning: {w}");
            foreach (var e in issues)
                Console.WriteLine($"  error:   {e}");
        }

        return ok ? 0 : 1;
    }

    private static int ExecuteMetrics(bool formatJson)
    {
        var repoRoot = RepoPathResolver.FindRepoRoot();
        var (paths, disk) = CollectDiskMetricsWithPaths(repoRoot);

        if (formatJson)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                ok = true,
                objectivesRoot = paths.ObjectivesRoot,
                forgeRoot = paths.ForgeRoot,
                observationsPath = paths.ObservationsPath,
                objectivesByStatus = disk.ObjectivesByStatus,
                objectiveSla = disk.ObjectiveSla,
                proposalsByStatus = disk.ProposalsByStatus,
                observationsFileBytes = disk.ObservationsFileBytes,
                observationsTailLineCount = disk.ObservationsTailLineCount,
                observationsLastTimestamp = disk.ObservationsLastTimestamp
            }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }

        Console.WriteLine($"Objectives root:    {paths.ObjectivesRoot}");
        Console.WriteLine($"Forge root:         {paths.ForgeRoot}");
        Console.WriteLine($"Observations path: {paths.ObservationsPath}");
        Console.WriteLine();
        Console.WriteLine($"Pending: {disk.ObjectiveSla.PendingCount}  InProgress: {disk.ObjectiveSla.InProgressCount}  Blocked: {disk.ObjectiveSla.BlockedCount}");
        if (disk.ObjectiveSla.OldestPendingAgeHours is { } oph)
            Console.WriteLine($"Oldest pending age (h): {oph:F1}");
        if (disk.ObjectiveSla.OldestInProgressAgeHours is { } oih)
            Console.WriteLine($"Oldest in-progress age (h): {oih:F1}");
        Console.WriteLine($"Observations file bytes: {disk.ObservationsFileBytes?.ToString() ?? "(missing)"}");
        if (disk.ObservationsTailLineCount is { } tlc)
            Console.WriteLine($"Observations tail lines (sample): {tlc}");
        if (disk.ObservationsLastTimestamp is { } olt)
            Console.WriteLine($"Observations last event (UTC): {olt:u}");
        return 0;
    }

    private static string ResolveAgentSetPath(string repoRoot, string agentSetRelativeOrAbsolute)
    {
        return Path.IsPathRooted(agentSetRelativeOrAbsolute)
            ? Path.GetFullPath(agentSetRelativeOrAbsolute)
            : Path.GetFullPath(Path.Combine(repoRoot, agentSetRelativeOrAbsolute));
    }

    private static int ExecuteStatus(bool json, string agentSet, bool withMetrics)
    {
        var repoRoot = RepoPathResolver.FindRepoRoot();
        var agentSetPath = ResolveAgentSetPath(repoRoot, agentSet);
        var last = WorkflowOptimizeLastStore.TryRead(repoRoot);
        var ollamaAgents = RuntimeStudioAgentSetReader.TryListOllamaAgents(agentSetPath);

        if (!File.Exists(agentSetPath))
        {
            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    ok = false,
                    repoRoot,
                    agentSetPath,
                    error = "Agent set file not found."
                }));
            }
            else
            {
                Console.Error.WriteLine($"Agent set not found: {agentSetPath}");
            }

            return 1;
        }

        var disk = withMetrics ? CollectDiskMetrics(repoRoot) : null;

        if (json)
        {
            if (withMetrics)
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    ok = true,
                    repoRoot,
                    lastTune = last,
                    agentSetPath,
                    ollamaAgents = ollamaAgents.Select(a => new { id = a.Id, modelName = a.ModelName }).ToArray(),
                    runtimeStudioMetrics = disk
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
            else
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    ok = true,
                    repoRoot,
                    lastTune = last,
                    agentSetPath,
                    ollamaAgents = ollamaAgents.Select(a => new { id = a.Id, modelName = a.ModelName }).ToArray()
                }, new JsonSerializerOptions { WriteIndented = true }));
            }

            return 0;
        }

        Console.WriteLine($"Repository: {repoRoot}");
        Console.WriteLine($"Agent set:  {agentSetPath}");
        Console.WriteLine();

        if (last is null)
        {
            Console.WriteLine("Last workflow optimize: (no .ashlar/runtime/workflow_optimize_last.json yet)");
        }
        else
        {
            Console.WriteLine($"Last workflow optimize: {last.WrittenAtUtc:u}  ok={last.Ok}");
            Console.WriteLine($"  session:      {last.OptimizeRunId}");
            if (!string.IsNullOrWhiteSpace(last.WinnerCandidateId))
                Console.WriteLine($"  winner:       {last.WinnerCandidateId}");
            if (!string.IsNullOrWhiteSpace(last.ModelProfileId))
                Console.WriteLine($"  modelProfile: {last.ModelProfileId}");
            if (!string.IsNullOrWhiteSpace(last.CompositionId))
                Console.WriteLine($"  composition:  {last.CompositionId}");
            if (last.OllamaModels.Count > 0)
                Console.WriteLine($"  ollama models (winner): {string.Join(", ", last.OllamaModels)}");
        }

        Console.WriteLine();
        if (ollamaAgents.Count == 0)
        {
            Console.WriteLine("Ollama agents in agent set: (none with ModelProvider=ollama and ModelName set)");
        }
        else
        {
            Console.WriteLine("Ollama agents in agent set:");
            foreach (var row in ollamaAgents)
                Console.WriteLine($"  {row.Id}: {row.ModelName}");
        }

        if (withMetrics && disk is not null)
        {
            Console.WriteLine();
            Console.WriteLine("--- Backlog metrics ---");
            Console.WriteLine($"Pending: {disk.ObjectiveSla.PendingCount}  InProgress: {disk.ObjectiveSla.InProgressCount}  Blocked: {disk.ObjectiveSla.BlockedCount}");
            if (disk.ObjectiveSla.OldestPendingAgeHours is { } oph)
                Console.WriteLine($"Oldest pending age (h): {oph:F1}");
            if (disk.ObjectiveSla.OldestInProgressAgeHours is { } oih)
                Console.WriteLine($"Oldest in-progress age (h): {oih:F1}");
            Console.WriteLine($"Observations file bytes: {disk.ObservationsFileBytes?.ToString() ?? "(missing)"}");
            if (disk.ObservationsTailLineCount is { } tlc2)
                Console.WriteLine($"Observations tail lines (sample): {tlc2}");
            if (disk.ObservationsLastTimestamp is { } olt2)
                Console.WriteLine($"Observations last event (UTC): {olt2:u}");
        }

        return 0;
    }

    private static RuntimeStudioDiskMetrics CollectDiskMetrics(string repoRoot) =>
        CollectDiskMetricsWithPaths(repoRoot).disk;

    private static (RuntimeStudioPathResolver.ResolvedPaths paths, RuntimeStudioDiskMetrics disk) CollectDiskMetricsWithPaths(
        string repoRoot)
    {
        var paths = RuntimeStudioPathResolver.Resolve(repoRoot);
        var objectives = new ObjectiveStore(paths.ObjectivesRoot);
        var proposals = new ChangeProposalStore(paths.ForgeRoot);
        var disk = RuntimeStudioMetricsCollector.Collect(objectives, proposals, paths.ObservationsPath);
        return (paths, disk);
    }

    private static int ExecuteApplyTune(bool json, string spec, string agentSet, bool dryRun)
    {
        var repoRoot = RepoPathResolver.FindRepoRoot();
        var last = WorkflowOptimizeLastStore.TryRead(repoRoot);
        if (last is null)
        {
            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    ok = false,
                    summary = "Missing .ashlar/runtime/workflow_optimize_last.json; run workflow optimize first."
                }));
            }
            else
            {
                Console.Error.WriteLine(
                    "Missing .ashlar/runtime/workflow_optimize_last.json; run `ashlar workflow optimize` or apps/runtime-studio/scripts/optimize_agent_cluster.sh first.");
            }

            return 1;
        }

        var result = RuntimeStudioTuneApplier.Apply(repoRoot, spec, agentSet, last, dryRun);
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                ok = result.Ok,
                summary = result.Summary,
                updated = result.UpdatedAgentIds
            }));
        }
        else
        {
            Console.WriteLine(result.Summary);
        }

        return result.Ok ? 0 : 1;
    }
}
