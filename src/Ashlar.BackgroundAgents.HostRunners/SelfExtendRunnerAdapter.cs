using Microsoft.Extensions.Logging;
using Ashlar.Abstractions;
using Ashlar.BackgroundAgents.Agents;
using Ashlar.BackgroundAgents.Configuration;
using Ashlar.BackgroundAgents.DataSensitivity;
using Ashlar.BackgroundAgents.Extending;
using Ashlar.BackgroundAgents.Forge;
using Ashlar.BackgroundAgents.Objectives;
using Ashlar.BackgroundAgents.Observations;
using Ashlar.BackgroundAgents.Registry;
using Ashlar.BackgroundAgents.Security;
using Ashlar.Core.Application.Certification.Ports;
using Ashlar.Runtime;

namespace Ashlar.BackgroundAgents.HostRunners;

/// <summary>
/// Host implementation of ISelfExtendRunner: builds a toolbox (repo.fs.write, repo.fs.search_replace),
/// policy (path allowlist, max write size), and a tool-calling agent backed by IModel, then runs one ThinkAsync cycle and executes approved tool calls.
/// </summary>
public sealed class SelfExtendRunnerAdapter : ISelfExtendRunner
{
    /// <summary>
    /// Maximum number of most-recent observations surfaced to the planner each cycle.
    /// Sized to keep the snapshot under typical model context budgets while still
    /// giving the planner several test/build/PR signals to react to. If callers find
    /// they need a longer tail, expose this via configuration rather than raising it
    /// blindly — large observation tails dilute the model's attention on the objective.
    /// </summary>
    private const int RecentObservationsLimit = 10;

    private readonly IModel _model;
    private readonly ILogger<SelfExtendRunnerAdapter> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IObservationStore? _observations;
    private readonly IObjectiveStore? _objectives;
    private readonly IChangeProposalStore? _proposals;
    private readonly IAggressivenessModeStore? _modeStore;
    private readonly ICertificationRecordStore _certificationStore;
    // Lazy on purpose — see the constructor remarks. Resolving this eagerly closes a
    // dependency cycle that hangs host startup.
    private readonly Lazy<IBackgroundAgentRegistry>? _agentRegistry;
    private readonly IDataSensitivityRegistry? _sensitivityRegistry;

    /// <summary>
    /// Creates the adapter.
    /// </summary>
    /// <param name="agentRegistry">
    /// The agent registry, deferred. MUST stay <see cref="Lazy{T}"/> — taking
    /// <c>IBackgroundAgentRegistry</c> directly reintroduces a dependency cycle that
    /// hangs host startup:
    /// <code>
    /// IBackgroundAgentRegistry factory  -> sp.GetService&lt;ISelfExtendRunner&gt;()
    /// ISelfExtendRunner factory         -> sp.GetRequiredService&lt;SelfExtendRunnerAdapter&gt;()
    /// SelfExtendRunnerAdapter ctor      -> IBackgroundAgentRegistry   (back to the top)
    /// </code>
    /// Microsoft.Extensions.DependencyInjection cannot catch this. Its circular-dependency
    /// check inspects constructor-injected graphs at validation time, and here the loop is
    /// laundered through two factory lambdas, which the validator cannot see through. The
    /// graph therefore passes ValidateOnBuild and then recurses at RESOLUTION time — the
    /// observed failure was ~298 repetitions of that pair on a single thread, with
    /// WebApplicationFactory blocked forever on host startup and no exception ever thrown.
    /// Making the parameter optional does not help either: it is registered, so DI
    /// dutifully re-enters the factory rather than passing null.
    ///
    /// The registry is only read when a self-extend run actually executes, long after the
    /// container is built, so deferring costs nothing.
    /// </param>
    public SelfExtendRunnerAdapter(
        IModel model,
        ILogger<SelfExtendRunnerAdapter> logger,
        ILoggerFactory loggerFactory,
        IObservationStore? observations = null,
        IObjectiveStore? objectives = null,
        IChangeProposalStore? proposals = null,
        IAggressivenessModeStore? modeStore = null,
        ICertificationRecordStore? certificationStore = null,
        Lazy<IBackgroundAgentRegistry>? agentRegistry = null,
        IDataSensitivityRegistry? sensitivityRegistry = null,
        IEnumerable<IToolSource>? toolSources = null,
        IExtensionCompileCheck? compileCheck = null,
        IPostApplyVerification? verification = null)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _observations = observations;
        _objectives = objectives;
        _proposals = proposals;
        _modeStore = modeStore;
        _certificationStore = certificationStore ?? FailClosedCertificationRecordStore.Instance;
        _agentRegistry = agentRegistry;
        _sensitivityRegistry = sensitivityRegistry;
        _toolSources = toolSources;
        _compileCheck = compileCheck;
        _verification = verification;
    }

    private readonly IEnumerable<IToolSource>? _toolSources;
    private readonly IExtensionCompileCheck? _compileCheck;
    private readonly IPostApplyVerification? _verification;

    /// <inheritdoc />
    public async Task<SelfExtendRunResult> RunAsync(string repoRoot, CancellationToken cancellationToken = default)
        => await RunAsync(repoRoot, objective: null, agentName: null, modelProvider: null, modelName: null, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Runs a self-extend cycle with an explicit objective for the tool-calling agent.
    /// </summary>
    public async Task<SelfExtendRunResult> RunAsync(string repoRoot, string? objective, CancellationToken cancellationToken = default)
        => await RunAsync(repoRoot, objective, agentName: null, modelProvider: null, modelName: null, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Runs a self-extend cycle with an explicit objective and agent role name for multi-agent workflows.
    /// </summary>
    public async Task<SelfExtendRunResult> RunAsync(string repoRoot, string? objective, string? agentName, CancellationToken cancellationToken = default)
        => await RunAsync(repoRoot, objective, agentName, modelProvider: null, modelName: null, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Runs a self-extend cycle with an explicit objective, agent role name, and model routing.
    /// Implements <see cref="ISelfExtendRunner.RunAsync(string, string?, string?, string?, string?, CancellationToken)"/>.
    /// </summary>
    public async Task<SelfExtendRunResult> RunAsync(
        string repoRoot,
        string? objective,
        string? agentName,
        string? modelProvider,
        string? modelName,
        CancellationToken cancellationToken = default)
        => await RunAsync(repoRoot, objective, agentName, modelProvider, modelName, agentId: null, cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// Runs a self-extend cycle with registry agent id for policy enforcement (exfiltration, cert admission).
    /// </summary>
    public async Task<SelfExtendRunResult> RunAsync(
        string repoRoot,
        string? objective,
        string? agentName,
        string? modelProvider,
        string? modelName,
        string? agentId,
        CancellationToken cancellationToken = default)
    {
        if (!BackgroundAgentAdapterValidation.TryResolveDirectory(repoRoot, "RepoRoot", out var errorMessage))
        {
            return new SelfExtendRunResult(false, 0, 0, errorMessage!);
        }

        var resolvedAgentName = string.IsNullOrWhiteSpace(agentName) ? "self-extend" : agentName!.Trim();
        var resolvedAgentId = string.IsNullOrWhiteSpace(agentId) ? resolvedAgentName : agentId!.Trim();

        // Claim a backlog item for this cycle when an objective store is wired and
        // the caller hasn't pinned an explicit objective string. Without this the
        // planner would keep re-using the static objective from agent_set.local.json
        // every cycle, which is exactly the rut Stream B is meant to fix.
        ObjectiveDocument? claimed = null;
        var effectiveObjective = objective;
        if (_objectives is not null && string.IsNullOrWhiteSpace(objective))
        {
            try
            {
                claimed = _objectives.ClaimNext(resolvedAgentName);
                if (claimed is not null)
                {
                    effectiveObjective = $"[{claimed.Id}] {claimed.Title}\n\n{claimed.Body}";
                    _logger.LogInformation(
                        "Self-extend cycle ({Agent}) claimed objective '{Id}' (priority {Priority}, attempt #{Attempt}).",
                        resolvedAgentName, claimed.Id, claimed.Priority, claimed.Attempts);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Objective claim failed; proceeding without an objective override.");
            }
        }

        try
        {
            // M1 enforcement: inside an ashlar project, writes are MEDIATED — the project-
            // local forge store holds them and only a gate admission applies them. This
            // overrides both the host's proposal store (admission must read the same queue
            // `ashlar gates` does) and the host's aggressiveness mode (mediation is the
            // project's law, not the host's mood). Outside ashlar projects: unchanged.
            var isAshlarProject = AshlarProjectMediation.IsAshlarProject(repoRoot!);
            var effectiveProposals = _proposals;
            var effectiveModeStore = _modeStore;
            Ashlar.BackgroundAgents.Forge.ChangeProposalStore? projectForge = null;
            IReadOnlySet<string>? forgeIdsBefore = null;
            if (isAshlarProject)
            {
                projectForge = AshlarProjectMediation.ProjectStore(repoRoot!);
                effectiveProposals = projectForge;
                effectiveModeStore = new FixedAggressivenessModeStore(
                    Ashlar.Abstractions.BackgroundAgentAggressivenessMode.Passive);
                forgeIdsBefore = AshlarProjectMediation.SnapshotIds(projectForge);
            }

            var (tools, policies, budget) = RepoFsToolboxFactory.CreateWithBuildTest(
                observations: _observations,
                source: resolvedAgentId,
                objectiveId: claimed?.Id,
                proposals: effectiveProposals,
                modeStore: effectiveModeStore,
                certificationStore: _certificationStore,
                // .Value here, not in the constructor: by the time a self-extend run
                // executes, the registry is fully built and resolving it is a no-op.
                agentRegistry: _agentRegistry?.Value,
                sensitivityRegistry: _sensitivityRegistry,
                // Sources are snapshotted per cycle: discovery (e.g. remote MCP servers)
                // completes after DI build, so the tool set is only known here.
                extraTools: _toolSources?.SelectMany(s => s.GetTools()));
            budget.Reset();
            // Register objective lifecycle tools only when a store is wired — the
            // tools cannot operate without one and must not be advertised to the LLM
            // in their absence (would result in confusing "tool failed: store missing"
            // observations).
            if (_objectives is not null)
            {
                tools.Register(new ObjectiveCompleteTool(_objectives));
                tools.Register(new ObjectiveBlockTool(_objectives));
            }

            var agent = new ToolCallingAgent(
                resolvedAgentName,
                _model,
                _loggerFactory.CreateLogger<ToolCallingAgent>(),
                effectiveObjective,
                modelProvider,
                modelName);
            var scratchpadPath = ResolveScratchpadPath(repoRoot!, resolvedAgentName);
            var recent = LoadRecentObservations();
            var snapshot = BuildSnapshot(
                repoRoot!,
                resolvedAgentName,
                resolvedAgentId,
                effectiveObjective,
                scratchpadPath,
                recent,
                claimed);

            // Multi-turn ReAct: agent loops up to MaxIterations, executing tool calls inline
            // through the policy engine and feeding observations back into the conversation.
            // The previous single-turn AgentHost path was unable to chain list → read → write
            // because tool results never reached the LLM after the first round.
            var memory = tools.MemoryFor(agent);
            var cycle = await agent
                .RunCycleAsync(snapshot, tools, policies, onRejected: null, memory, cancellationToken)
                .ConfigureAwait(false);

            var writePaths = cycle.MergedDelta is null
                ? Array.Empty<string>()
                : ExtractWritePaths(cycle.MergedDelta.Log);
            PlannerScratchpad.Append(scratchpadPath, new ScratchpadEntry(
                DateTimeOffset.UtcNow,
                resolvedAgentName,
                cycle.Iterations,
                cycle.ToolCallsExecuted,
                cycle.ToolCallsDenied,
                cycle.StoppedReason,
                cycle.FinalRationale,
                writePaths));

            // Release-on-no-action: if we claimed an objective but the agent didn't
            // call objective.complete or objective.block, the objective is still in
            // the InProgress folder. Move it back to Pending so another cycle (or
            // operator) can pick it up. The Attempts counter is preserved so the
            // store's auto-block still fires after MaxAttempts.
            if (claimed is not null && _objectives is not null)
            {
                ReleaseClaimIfStillInProgress(claimed.Id, cycle);
            }

            var summary = $"{cycle.ToolCallsExecuted} tool call(s) executed, {cycle.ToolCallsDenied} denied " +
                          $"(iter={cycle.Iterations}, stopped={cycle.StoppedReason}).";
            _logger.LogInformation("Self-extend cycle ({Agent}): {Summary}", resolvedAgentName, summary);

            // The producer wiring: inside an ashlar project, the cycle's outcome is recorded
            // through the admission gate — the same store `ashlar gates` reads. With M1
            // mediation, the cycle's writes are the NEW forge proposals (parked, not on
            // disk); the gate decides their application. No-op elsewhere.
            IReadOnlyList<string> newForgeIds = [];
            if (projectForge is not null && forgeIdsBefore is not null)
            {
                newForgeIds = AshlarProjectMediation.SnapshotIds(projectForge)
                    .Except(forgeIdsBefore, StringComparer.Ordinal)
                    .OrderBy(id => id, StringComparer.Ordinal)
                    .ToList();
            }
            var gateOutcome = await SelfExtendAdmissionBridge
                .TryRecordAsync(repoRoot!, resolvedAgentName, effectiveObjective, writePaths,
                    cycle.ToolCallsExecuted, cycle.ToolCallsDenied, _logger, cancellationToken,
                    newForgeIds, compileCheck: _compileCheck, verification: _verification)
                .ConfigureAwait(false);

            return new SelfExtendRunResult(
                cycle.ToolCallsDenied == 0,
                cycle.ToolCallsExecuted,
                cycle.ToolCallsDenied,
                gateOutcome is null ? summary : $"{summary} {gateOutcome}",
                cycle.Iterations,
                cycle.StoppedReason,
                gateOutcome);
        }
        catch (Exception ex)
        {
            // Best-effort release on hard failure too — we don't want a crashed
            // cycle to permanently park an objective in InProgress.
            if (claimed is not null && _objectives is not null)
            {
                try { ReleaseClaimIfStillInProgress(claimed.Id, cycle: null); } catch { /* swallow */ }
            }
            return new SelfExtendRunResult(false, 0, 0, BackgroundAgentAdapterFailure.LogAndMessage(_logger, ex, $"Run failed: {ex.Message}"));
        }
    }

    /// <summary>
    /// Builds the snapshot passed to the tool-calling agent. In addition to the standard
    /// RepoRoot/OutputRoot, this includes:
    /// - <c>AgentName</c>, <c>Objective</c>: surfaced so the LLM can ground its plan even when
    ///   the system prompt is heavily token-truncated.
    /// - <c>RepoOverview</c>: top-level directory + key files (README, AGENTS, etc.) so the
    ///   model has bootstrap context without having to call <c>repo.fs.list .</c> first. With
    ///   the previous bare snapshot the planner was being asked to write blind.
    /// </summary>
    private static WorldSnapshot BuildSnapshot(
        string repoRoot,
        string agentName,
        string agentId,
        string? objective,
        string? scratchpadPath = null,
        IReadOnlyList<RuntimeObservation>? recentObservations = null,
        ObjectiveDocument? claimed = null)
    {
        var data = new Dictionary<string, object?>
        {
            ["RepoRoot"] = repoRoot,
            ["OutputRoot"] = Path.Combine(repoRoot, "out"),
            ["AgentName"] = agentName,
            ["agentId"] = agentId,
            ["selfExtendAdmission"] = true
        };

        if (!string.IsNullOrWhiteSpace(objective))
            data["Objective"] = objective.Trim();

        // Surface the claimed objective as structured metadata so the LLM can
        // call objective.complete / objective.block with the right id without
        // having to parse the free-form Objective string.
        if (claimed is not null)
        {
            data["ObjectiveId"] = claimed.Id;
            data["ObjectiveMeta"] = new
            {
                id = claimed.Id,
                title = claimed.Title,
                priority = claimed.Priority,
                attempts = claimed.Attempts,
                tags = claimed.Tags
            };
        }

        if (!string.IsNullOrWhiteSpace(scratchpadPath))
        {
            var tail = PlannerScratchpad.LoadTail(scratchpadPath);
            if (!string.IsNullOrWhiteSpace(tail))
                data["RecentNotes"] = tail;
        }

        // Surface recent facts other agents have published as a compact, model-friendly
        // projection. We deliberately project to anonymous primitives (string ts, kind,
        // source, summary, severity) rather than the full record so the snapshot dictionary
        // stays JSON-friendly when serialised into the system prompt.
        if (recentObservations is { Count: > 0 })
        {
            data["RecentObservations"] = recentObservations
                .Select(o => new
                {
                    ts = o.ts.ToString("u", System.Globalization.CultureInfo.InvariantCulture),
                    source = o.source,
                    kind = o.kind.ToString(),
                    severity = o.severity.ToString(),
                    summary = o.summary
                })
                .ToArray();
        }

        try
        {
            var rootFull = Path.GetFullPath(repoRoot);
            var skipDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "bin", "obj", ".git", ".vs", "node_modules", ".nuget", ".idea", ".cache", "out"
            };

            var topDirs = Directory
                .EnumerateDirectories(rootFull)
                .Select(Path.GetFileName)
                .Where(n => !string.IsNullOrEmpty(n) && !skipDirs.Contains(n!))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .Take(40)
                .ToArray();

            var keyFileCandidates = new[]
            {
                "README.md", "AGENTS.md", "Directory.Build.props", "Directory.Packages.props",
                "global.json", "Ashlar.sln", "package.json"
            };
            var topFiles = keyFileCandidates
                .Where(f => File.Exists(Path.Combine(rootFull, f)))
                .ToArray();

            data["RepoOverview"] = new
            {
                root = rootFull,
                topDirectories = topDirs,
                topFiles
            };
        }
        catch
        {
            // Bootstrap context is best-effort; falling back to bare RepoRoot is fine.
        }

        return new WorldSnapshot(0, data);
    }

    /// <summary>
    /// Default scratchpad location: <c>&lt;repoRoot&gt;/.ashlar/runtime-studio/&lt;agent&gt;-notes.md</c>.
    /// The directory is created lazily by <see cref="PlannerScratchpad.Append"/>; the agent name
    /// is sanitised so that values like "runtime-planner" or "self-extend" produce predictable
    /// filenames without filesystem-illegal characters.
    /// </summary>
    /// <summary>
    /// Pulls the tail of the observation log so the planner sees what the tester /
    /// optimizer / extender saw in their most recent runs. Best-effort: a missing or
    /// unreadable store yields an empty list, never an exception.
    /// </summary>
    private IReadOnlyList<RuntimeObservation> LoadRecentObservations()
    {
        if (_observations is null) return Array.Empty<RuntimeObservation>();
        try
        {
            // Pull a slightly larger window then take the tail — ReadSince streams in
            // append order, and the planner cares about the *most recent* events.
            var window = _observations
                .ReadSince(since: DateTimeOffset.UtcNow - TimeSpan.FromHours(24))
                .ToList();
            if (window.Count <= RecentObservationsLimit)
                return window;
            return window.GetRange(window.Count - RecentObservationsLimit, RecentObservationsLimit);
        }
        catch
        {
            return Array.Empty<RuntimeObservation>();
        }
    }

    /// <summary>
    /// If the agent claimed an objective but didn't explicitly complete or block it
    /// during the cycle, move it back to <c>Pending</c> so the next cycle (or
    /// operator) can pick it up. Attempts counter is preserved so the store's
    /// auto-block heuristic still fires after <see cref="ObjectiveStore.MaxAttemptsBeforeBlock"/>
    /// stuck cycles.
    /// </summary>
    private void ReleaseClaimIfStillInProgress(string objectiveId, Ashlar.BackgroundAgents.Agents.AgentCycleResult? cycle)
    {
        try
        {
            var current = _objectives!.Find(objectiveId);
            if (current is null || current.Status != ObjectiveStatus.InProgress)
                return; // Agent must have called objective.complete / objective.block — nothing to do.

            // Re-block defensively if the cycle errored hard (cycle == null) since
            // a hard crash strongly suggests the objective is fragile and shouldn't
            // be re-claimed immediately. Otherwise just release back to Pending.
            if (cycle is null)
            {
                _objectives.Block(objectiveId, "cycle_crashed");
                _logger.LogWarning("Objective '{Id}' auto-blocked after cycle crash.", objectiveId);
                return;
            }

            // We can't directly call Unblock (objective is InProgress, not Blocked),
            // and the store doesn't expose a "release" verb. The cleanest way is to
            // serialize a Pending copy on top of the in-progress file via a fresh
            // Add. Use the same id so the file move is a no-op rename across folders.
            // (ObjectiveStore.Add overwrites a same-id file in pending/.)
            var released = current with
            {
                Status = ObjectiveStatus.Pending,
                Owner = null,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            _objectives.Add(released);

            // Best-effort: clean up the leftover file in the in_progress folder
            // since Add only writes to pending/.
            var inProgressPath = System.IO.Path.Combine(_objectives.Location, "in_progress", objectiveId + ".md");
            if (File.Exists(inProgressPath)) File.Delete(inProgressPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Best-effort objective release failed for '{Id}'", objectiveId);
        }
    }

    private static string ResolveScratchpadPath(string repoRoot, string agentName)
    {
        var safe = string.Concat(agentName.Select(c =>
            char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '-'));
        return Path.Combine(repoRoot, ".ashlar", "runtime-studio", $"{safe}-notes.md");
    }

    /// <summary>
    /// The write paths a cycle claims: what the signed admission record lists as its diff and
    /// what the scratchpad records. Internal so the tool edge can pin that a REFUSED write is not
    /// harvested — a refusal used to share the <c>write:</c> prefix, which made every refused
    /// governance write a signed claim that it landed.
    /// </summary>
    internal static IReadOnlyList<string> ExtractWritePaths(IReadOnlyList<string> log)
    {
        // Both repo.fs.write and repo.fs.search_replace tools log entries shaped like
        // "write:relative/path bytes=NNN" / "s&r:relative/path …". Pull the first
        // whitespace-bounded token after the prefix so the scratchpad records what
        // actually changed on disk. A refused write logs "write-refused:" and is not matched.
        var result = new List<string>();
        foreach (var line in log)
        {
            string? prefix = null;
            if (line.StartsWith("write:", StringComparison.Ordinal)) prefix = "write:";
            else if (line.StartsWith("s&r:", StringComparison.Ordinal)) prefix = "s&r:";
            if (prefix is null) continue;
            var rest = line[prefix.Length..];
            var space = rest.IndexOf(' ');
            var path = space < 0 ? rest : rest[..space];
            if (!string.IsNullOrWhiteSpace(path))
                result.Add(path);
        }
        return result;
    }
}
