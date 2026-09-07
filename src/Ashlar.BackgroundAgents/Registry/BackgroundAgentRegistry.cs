using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Ashlar.Abstractions;
using Ashlar.BackgroundAgents.Configuration;
using Ashlar.BackgroundAgents.DataSensitivity;
using Ashlar.BackgroundAgents.Extending;
using Ashlar.BackgroundAgents.Logging;
using Ashlar.BackgroundAgents.Observations;
using Ashlar.BackgroundAgents.Optimization;
using Ashlar.BackgroundAgents.Scheduling;
using Ashlar.BackgroundAgents.Security;
using Ashlar.BackgroundAgents.Telemetry;
using Ashlar.BackgroundAgents.Testing;
using Ashlar.Core.Application.SelfImprovement.Ports;
using Ashlar.Core.Application.Trust.Ports;

namespace Ashlar.BackgroundAgents.Registry;

/// <summary>
/// Default implementation of <see cref="IBackgroundAgentRegistry"/>. Uses a
/// <see cref="ConcurrentDictionary{TKey,TValue}"/> keyed by agent ID, so all
/// registration and lookup operations are lock-free and safe for concurrent
/// callers without external synchronization.
///
/// <para><b>Dependency philosophy:</b> Most dependencies are optional (nullable)
/// because the registry must function in minimal deployments (e.g. the
/// <see cref="AshlarDeploymentProfile.System"/> profile) where background-agent
/// infrastructure is not registered. Each execution path null-checks and
/// gracefully degrades — e.g. no <c>logStore</c> simply means no execution
/// history is persisted.</para>
///
/// <para><b>Dog-fooding runners:</b> The <c>codeAnalysisRunner</c>,
/// <c>testRunRunner</c>, <c>selfExtendRunner</c>, and <c>selfImprovementLoop</c>
/// dependencies let background agents execute Ashlar's own analysis/test/extend
/// pipelines against the host codebase. This is a deliberate dog-food design:
/// the background agents use the same infrastructure they help build.</para>
/// </summary>
public sealed class BackgroundAgentRegistry : IBackgroundAgentRegistry
{
    /// <summary>
    /// Default grace period to wait for in-flight cycles to drain on shutdown.
    /// Sized to comfortably accommodate a typical ReAct cycle (~90s deadline) without
    /// blocking the host indefinitely; can be tuned via the constructor overload.
    /// </summary>
    public static readonly TimeSpan DefaultShutdownGracePeriod = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, BackgroundAgentInstance> _agents = new();
    private readonly ConcurrentDictionary<string, Task> _inFlight = new();
    private readonly IAgentScheduler _scheduler;
    private readonly ILogger<BackgroundAgentRegistry>? _logger;
    private readonly IBackgroundAgentLogStore? _logStore;
    private readonly ICodeAnalysisRunner? _codeAnalysisRunner;
    private readonly ITestRunRunner? _testRunRunner;
    private readonly ISelfExtendRunner? _selfExtendRunner;
    private readonly ISelfImprovementLoop? _selfImprovementLoop;
    private readonly IAggressivenessModeStore? _modeStore;
    private readonly IApprovalGate? _approvalGate;
    private readonly IDataSensitivityRegistry? _sensitivityRegistry;
    private readonly IDataDecisionAuditLog? _auditLog;
    private readonly CycleEventStore? _cycleEvents;
    private readonly Observations.IObservationStore? _observations;
    private readonly TimeSpan _shutdownGracePeriod;

    // SX-AUDIT invariant D. The ceiling is resolved once per registry (environment may only
    // lower it); the ledgers live here, NOT on the agent instance, so re-registration — which
    // an agent can do to itself — cannot reset them. See ExtensionCeiling.
    private readonly ExtensionCeiling _extensionCeiling;
    private readonly ConcurrentDictionary<string, ExtensionLedger> _extensionLedgers = new();

    /// <summary>
    /// Initializes the registry. The host (typically <c>BackgroundAgentService</c>)
    /// constructs agent instances and calls <see cref="RegisterAsync"/> before
    /// starting the scheduler. Only <paramref name="scheduler"/> is required;
    /// all other parameters degrade gracefully when <c>null</c>.
    /// </summary>
    /// <param name="scheduler">Scheduler for agent execution loops.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="logStore">Optional log store for agent execution logs.</param>
    /// <param name="codeAnalysisRunner">Optional runner for optimizer agents (dog-food: run analysis on codebase).</param>
    /// <param name="testRunRunner">Optional runner for tester agents (dog-food: run framework tests).</param>
    /// <param name="selfExtendRunner">Optional runner for extender agents (dog-food: LLM-driven code/doc changes within policy).</param>
    /// <param name="selfImprovementLoop">Optional self-improvement loop for self-improver agents (dog-food: test failures → fix → validate → promote).</param>
    /// <param name="modeStore">Optional aggressiveness mode store. When provided, Passive mode skips extender execution.</param>
    /// <param name="approvalGate">Optional approval gate for SemiActive mode. When provided and mode is SemiActive, execution requires approval.</param>
    /// <param name="auditLog">Optional audit log. When provided and mode is Ambient, actions are logged here (no user notification).</param>
    /// <param name="cycleEvents">Optional cycle event store for per-cycle telemetry.</param>
    /// <param name="observations">Optional observation store the planner reads.</param>
    /// <param name="shutdownGracePeriod">Grace period for in-flight cycles on shutdown.</param>
    /// <param name="extensionCeiling">
    /// SX-AUDIT invariant D ceilings for extender agents. Null resolves
    /// <see cref="ExtensionCeiling.Resolve()"/> — the defaults, lowered by the environment. A host
    /// may pass a stricter ceiling; there is no way to pass a looser one than the defaults short
    /// of constructing the record, which is a code change.
    /// </param>
    public BackgroundAgentRegistry(
        IAgentScheduler scheduler,
        ILogger<BackgroundAgentRegistry>? logger = null,
        IBackgroundAgentLogStore? logStore = null,
        ICodeAnalysisRunner? codeAnalysisRunner = null,
        ITestRunRunner? testRunRunner = null,
        ISelfExtendRunner? selfExtendRunner = null,
        ISelfImprovementLoop? selfImprovementLoop = null,
        IAggressivenessModeStore? modeStore = null,
        IApprovalGate? approvalGate = null,
        IDataSensitivityRegistry? sensitivityRegistry = null,
        IDataDecisionAuditLog? auditLog = null,
        CycleEventStore? cycleEvents = null,
        Observations.IObservationStore? observations = null,
        TimeSpan? shutdownGracePeriod = null,
        ExtensionCeiling? extensionCeiling = null)
    {
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _logger = logger;
        _logStore = logStore;
        _codeAnalysisRunner = codeAnalysisRunner;
        _testRunRunner = testRunRunner;
        _selfExtendRunner = selfExtendRunner;
        _selfImprovementLoop = selfImprovementLoop;
        _modeStore = modeStore;
        _approvalGate = approvalGate;
        _sensitivityRegistry = sensitivityRegistry;
        _auditLog = auditLog;
        _cycleEvents = cycleEvents;
        _observations = observations;
        _shutdownGracePeriod = shutdownGracePeriod ?? DefaultShutdownGracePeriod;
        _extensionCeiling = extensionCeiling ?? ExtensionCeiling.Resolve();
    }

    /// <summary>The invariant-D ceiling this registry enforces (before per-agent narrowing).</summary>
    public ExtensionCeiling ExtensionCeiling => _extensionCeiling;

    /// <summary>
    /// The invariant-D ledger for an extender: unattended cycles since the last human arm and
    /// cycles in the trailing hour. Intended for operators and digests, which should read
    /// <see cref="ExtensionLedger.UnattendedCycles"/> / <see cref="ExtensionLedger.CyclesInWindow"/>
    /// and call nothing.
    /// </summary>
    /// <remarks>
    /// This returns the LIVE ledger, not a snapshot, so it is a mutation surface as much as a
    /// reading one: a caller holding the registry can spend an agent's budget through
    /// <see cref="ExtensionLedger.TryBeginCycle"/> without running anything, or clear its
    /// unattended count through <see cref="ExtensionLedger.Rearm"/> — bypassing
    /// <see cref="RearmExtension"/> and the audit entry that makes a re-arm attributable. Narrowing
    /// this to a read-only projection is issue #481; it is a breaking change to a shipped public
    /// API, so it waits for the next major. Until then: never register this accessor, or anything
    /// that returns its result, as an agent tool.
    /// </remarks>
    public ExtensionLedger GetExtensionLedger(string agentId) =>
        _extensionLedgers.GetOrAdd(agentId, _ => new ExtensionLedger());

    /// <summary>
    /// A human re-arms an extender that has reached <see cref="Extending.ExtensionCeiling.MaxUnattendedCycles"/>.
    /// This is an OPERATOR surface — it must never be registered as an agent tool, or an agent
    /// could re-arm itself and the ceiling would bound nothing. Re-registration deliberately
    /// does not re-arm for the same reason. The hourly rate ceiling is not affected.
    /// </summary>
    /// <returns>The unattended-cycle count that was cleared.</returns>
    public int RearmExtension(string agentId)
    {
        if (string.IsNullOrWhiteSpace(agentId))
            throw new ArgumentException("Agent id is required.", nameof(agentId));

        var cleared = GetExtensionLedger(agentId).Rearm();
        _logStore?.Append(agentId, "Info", $"Extension re-armed by operator ({cleared} unattended cycle(s) cleared).");
        _logger?.LogInformation("Background agent {AgentId}: extension re-armed by operator ({Cleared} unattended cycles cleared)", agentId, cleared);
        return cleared;
    }

    /// <summary>
    /// How many <c>ParentId</c> hops separate an agent from a human-authored root. Roots are
    /// depth 0. A <c>ParentId</c> that resolves to no registered agent still counts as a hop
    /// (an unknown parent is not a reason to grant more authority). Bounded so a cyclic
    /// parent graph cannot spin.
    /// </summary>
    internal int LineageDepth(BackgroundAgentConfig config)
    {
        var depth = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal) { config.Id };
        var parentId = config.ParentId;
        while (!string.IsNullOrWhiteSpace(parentId) && depth < 64)
        {
            depth++;
            if (!seen.Add(parentId) || !_agents.TryGetValue(parentId, out var parent))
                break;
            parentId = parent.Config.ParentId;
        }

        return depth;
    }

    /// <summary>
    /// Register a background agent.
    /// </summary>
    public Task RegisterAsync(
        IAgent agent,
        BackgroundAgentConfig config,
        AgentRegistrationOrigin origin = AgentRegistrationOrigin.Machine,
        CancellationToken cancellationToken = default)
    {
        if (agent == null)
            throw new ArgumentNullException(nameof(agent));
        if (config == null)
            throw new ArgumentNullException(nameof(config));

        AgentPolicyNarrowingValidator.ValidateOrThrow(
            config,
            origin,
            parentId => _agents.TryGetValue(parentId, out var parent) ? parent : null,
            _sensitivityRegistry ?? new DataSensitivityRegistry());

        // Create agent instance
        var instance = new BackgroundAgentInstance
        {
            Agent = agent,
            Config = config,
            State = BackgroundAgentState.Idle
        };

        _agents[config.Id] = instance;
        WarnIfModelConfigUnused(config);
        _logger?.LogInformation("Registered background agent: {AgentId}", config.Id);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Roles that consume a real LLM via the self-extend path. Anything else with
    /// <c>ModelProvider</c> set to a non-deterministic backend is almost certainly
    /// dead config left over from copy/paste — log a warning so it gets cleaned up
    /// instead of silently misleading operators into thinking the role uses the LLM.
    /// </summary>
    private static readonly HashSet<string> LlmConsumingRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        "extender"
    };

    private void WarnIfModelConfigUnused(BackgroundAgentConfig config)
    {
        if (LlmConsumingRoles.Contains(config.Role))
            return;
        var hasProvider = !string.IsNullOrWhiteSpace(config.ModelProvider) &&
                          !string.Equals(config.ModelProvider, Ashlar.Core.Domain.AshlarDefaults.DeterministicProviderName, StringComparison.OrdinalIgnoreCase);
        var hasName = !string.IsNullOrWhiteSpace(config.ModelName);
        if (!hasProvider && !hasName)
            return;
        _logger?.LogWarning(
            "Background agent {AgentId} has role '{Role}' which does not consume an LLM, " +
            "but ModelProvider='{Provider}'/ModelName='{Name}' is configured and will be IGNORED. " +
            "Drop these fields from the agent's config to avoid confusion.",
            config.Id, config.Role, config.ModelProvider, config.ModelName ?? "<null>");
    }

    /// <summary>
    /// Get an agent instance by ID.
    /// </summary>
    public BackgroundAgentInstance? GetAgent(string agentId)
    {
        return _agents.TryGetValue(agentId, out var instance) ? instance : null;
    }

    /// <summary>
    /// Get all registered agents.
    /// </summary>
    public IReadOnlyList<BackgroundAgentInstance> GetAll()
    {
        return _agents.Values.ToList();
    }

    /// <summary>
    /// Start all registered agents.
    /// </summary>
    public Task StartAllAsync(CancellationToken cancellationToken = default)
    {
        var tasks = _agents.Values
            .Where(a => a.Config.Enabled)
            .Select(a => StartAsync(a.Config.Id, cancellationToken));
        return Task.WhenAll(tasks);
    }

    /// <summary>
    /// Stop all registered agents.
    ///
    /// <para>Shutdown ordering:
    /// <list type="number">
    ///   <item>Cancel each scheduler so no new cycles are dispatched.</item>
    ///   <item>Wait for in-flight cycles to drain, bounded by
    ///         <see cref="DefaultShutdownGracePeriod"/> (or the value supplied to
    ///         the constructor) and/or the supplied <paramref name="cancellationToken"/>.
    ///         Whichever fires first wins.</item>
    ///   <item>If cycles are still running after the grace window, log a warning
    ///         and return — the host's process exit will tear down the AppDomain.</item>
    /// </list></para>
    /// </summary>
    public async Task StopAllAsync(CancellationToken cancellationToken = default)
    {
        // Stop scheduling first so no new cycles can start while we drain.
        foreach (var instance in _agents.Values)
        {
            await StopAsync(instance.Config.Id, cancellationToken).ConfigureAwait(false);
        }

        await DrainInFlightAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task DrainInFlightAsync(CancellationToken cancellationToken)
    {
        var inFlight = _inFlight.Values.ToArray();
        if (inFlight.Length == 0)
            return;

        _logger?.LogInformation(
            "Waiting up to {GraceSeconds:F0}s for {Count} in-flight agent cycle(s) to drain",
            _shutdownGracePeriod.TotalSeconds,
            inFlight.Length);

        try
        {
            using var graceCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            graceCts.CancelAfter(_shutdownGracePeriod);
            // WaitAsync rethrows if the token cancels first; we swallow because timeout/host-cancel is expected.
            await Task.WhenAll(inFlight).WaitAsync(graceCts.Token).ConfigureAwait(false);
            _logger?.LogInformation("All in-flight agent cycles drained cleanly");
        }
        catch (OperationCanceledException)
        {
            var stillRunning = _inFlight.Count;
            if (stillRunning > 0)
            {
                _logger?.LogWarning(
                    "Shutdown grace period elapsed with {Count} in-flight cycle(s) still running; abandoning",
                    stillRunning);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Error while draining in-flight agent cycles");
        }
    }

    /// <summary>
    /// Start a specific agent.
    /// </summary>
    public Task StartAsync(string agentId, CancellationToken cancellationToken = default)
    {
        if (!_agents.TryGetValue(agentId, out var instance))
        {
            throw new InvalidOperationException($"Agent {agentId} not found");
        }

        if (instance.State == BackgroundAgentState.Running)
        {
            _logger?.LogWarning("Agent {AgentId} is already running", agentId);
            return Task.CompletedTask;
        }

        instance.State = BackgroundAgentState.Starting;
        instance.LastStartedAt = DateTimeOffset.UtcNow;

        _scheduler.StartAsync(instance, TrackedExecuteAgentAsync, cancellationToken);

        instance.State = BackgroundAgentState.Running;
        _logger?.LogInformation("Started background agent: {AgentId}", agentId);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Stop a specific agent.
    /// </summary>
    public Task StopAsync(string agentId, CancellationToken cancellationToken = default)
    {
        if (!_agents.TryGetValue(agentId, out var instance))
        {
            throw new InvalidOperationException($"Agent {agentId} not found");
        }

        if (instance.State == BackgroundAgentState.Stopped || instance.State == BackgroundAgentState.Idle)
        {
            return Task.CompletedTask;
        }

        instance.State = BackgroundAgentState.Stopping;

        _scheduler.Stop(agentId);

        instance.State = BackgroundAgentState.Stopped;
        _logger?.LogInformation("Stopped background agent: {AgentId}", agentId);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task ExecuteOnceAsync(string agentId, CancellationToken cancellationToken = default)
    {
        if (!_agents.TryGetValue(agentId, out var instance))
            throw new InvalidOperationException($"Agent {agentId} not found");
        await TrackedExecuteAgentAsync(instance, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Wraps <see cref="ExecuteAgentAsync"/> so the in-flight task is registered in
    /// <see cref="_inFlight"/> for the duration of the cycle. This lets
    /// <see cref="StopAllAsync"/> await graceful drain instead of returning while
    /// LLM calls are still in progress.
    ///
    /// <para>The scheduler may invoke this concurrently with prior cycles in degenerate
    /// cases (timer overlap), so we key by agent ID + Interlocked counter to avoid
    /// dictionary collisions.</para>
    /// </summary>
    private Task TrackedExecuteAgentAsync(BackgroundAgentInstance instance, CancellationToken cancellationToken)
    {
        var key = $"{instance.Config.Id}#{Interlocked.Increment(ref _cycleSeq)}";
        var task = RemoveWhenDoneAsync(key, ExecuteAgentAsync(instance, cancellationToken));
        _inFlight[key] = task;
        return task;
    }

    private async Task RemoveWhenDoneAsync(string key, Task inner)
    {
        try
        {
            await inner.ConfigureAwait(false);
        }
        finally
        {
            _inFlight.TryRemove(key, out _);
        }
    }

    private long _cycleSeq;

    private async Task ExecuteAgentAsync(BackgroundAgentInstance instance, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var agentId = instance.Config.Id;
        // Telemetry — populated in role branches, emitted in finally regardless of exit path.
        var cycleStart = DateTimeOffset.UtcNow;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var telemExecuted = 0;
        var telemDenied = 0;
        int? telemIterations = null;
        string? telemRationale = null;
        string? telemStoppedReason = null;
        var telemSuccess = true;
        string? telemError = null;
        try
        {
            instance.ExecutionCount++;
            _logStore?.Append(agentId, "Info", "Executing background agent.");
            // Info-level so daemon operators see scheduled cycles firing without enabling Debug logs.
            _logger?.LogInformation(
                "Executing background agent: {AgentId} (role={Role}, cycle #{Cycle})",
                agentId, instance.Config.Role, instance.ExecutionCount);

            // Dog-food: optimizer agents run code analysis when Path/AnalysisPath is set and runner is registered
            if (string.Equals(instance.Config.Role, "optimizer", StringComparison.OrdinalIgnoreCase) &&
                _codeAnalysisRunner != null &&
                TryGetParameter(instance.Config, new[] { "Path", "AnalysisPath" }, out var analysisPath))
            {
                var result = await _codeAnalysisRunner.RunAsync(analysisPath, cancellationToken).ConfigureAwait(false);
                _logStore?.Append(agentId, result.Success ? "Info" : "Warning",
                    $"Code analysis: {result.Summary} (violations: {result.ViolationCount})");
                _logger?.LogInformation("Background agent {AgentId} code analysis: {Summary}", agentId, result.Summary);
                // Publish observation so the planner can react to violations next cycle.
                // Severity is escalated when the analyser reports any violations, which is a
                // stronger signal than analyser exit-success (a clean run on a broken codebase
                // would still be Success=true with violations).
                _observations?.Append(new RuntimeObservation(
                    ts: DateTimeOffset.UtcNow,
                    source: agentId,
                    kind: ObservationKind.Analysis,
                    summary: $"{result.Summary} (violations: {result.ViolationCount})",
                    severity: result.ViolationCount > 0
                        ? ObservationSeverity.Warn
                        : ObservationSeverity.Info,
                    facts: new Dictionary<string, string>
                    {
                        ["path"] = analysisPath,
                        ["violations"] = result.ViolationCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["success"] = result.Success ? "true" : "false"
                    },
                    agentCycle: instance.ExecutionCount));
                instance.LastCompletedAt = DateTimeOffset.UtcNow;
                instance.SuccessCount++;
                _logStore?.Append(agentId, "Info", "Execution completed successfully.");
                telemSuccess = result.Success;
                telemRationale = result.Summary;
                telemStoppedReason = "code_analysis";
                return;
            }

            // Dog-food: tester agents run tests when test runner is registered (optional Filter from Parameters)
            if (string.Equals(instance.Config.Role, "tester", StringComparison.OrdinalIgnoreCase) &&
                _testRunRunner != null)
            {
                var filter = TryGetParameter(instance.Config, new[] { "Filter" }, out var f) ? f : null;
                var result = await _testRunRunner.RunAsync(filter, cancellationToken).ConfigureAwait(false);
                _logStore?.Append(agentId, result.Success ? "Info" : "Warning",
                    $"Tests: {result.Summary} (total: {result.TotalTests}, failed: {result.FailedTests})");
                _logger?.LogInformation("Background agent {AgentId} test run: {Summary}", agentId, result.Summary);
                // Publish observation so the planner sees test failures in its next snapshot.
                // Failed > 0 is always Error severity even if Success=true, so the planner can
                // react to a single regression without waiting for a fully red run.
                _observations?.Append(new RuntimeObservation(
                    ts: DateTimeOffset.UtcNow,
                    source: agentId,
                    kind: ObservationKind.Test,
                    summary: $"{result.Summary} (total: {result.TotalTests}, failed: {result.FailedTests})",
                    severity: result.FailedTests > 0
                        ? ObservationSeverity.Error
                        : (result.Success ? ObservationSeverity.Info : ObservationSeverity.Warn),
                    facts: new Dictionary<string, string>
                    {
                        ["filter"] = filter ?? string.Empty,
                        ["total"] = result.TotalTests.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["failed"] = result.FailedTests.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["success"] = result.Success ? "true" : "false"
                    },
                    agentCycle: instance.ExecutionCount));
                instance.LastCompletedAt = DateTimeOffset.UtcNow;
                instance.SuccessCount++;
                _logStore?.Append(agentId, "Info", "Execution completed successfully.");
                telemSuccess = result.Success;
                telemRationale = result.Summary;
                telemStoppedReason = "test_run";
                return;
            }

            // Dog-food: extender agents run self-extend cycle (LLM + tools) when runner is registered
            if (string.Equals(instance.Config.Role, "extender", StringComparison.OrdinalIgnoreCase) &&
                _selfExtendRunner != null &&
                TryGetParameter(instance.Config, new[] { "RepoRoot", "Path" }, out var repoRoot))
            {
                var mode = _modeStore?.GetMode() ?? BackgroundAgentAggressivenessMode.Passive;
                if (mode == BackgroundAgentAggressivenessMode.Passive)
                {
                    _logStore?.Append(agentId, "Info", "Passive mode: skipping extender execution (observe only).");
                    _logger?.LogDebug("Background agent {AgentId} in Passive mode: extender skipped", agentId);
                    instance.LastCompletedAt = DateTimeOffset.UtcNow;
                    instance.SuccessCount++;
                    return;
                }

                if (mode == BackgroundAgentAggressivenessMode.SemiActive)
                {
                    var approvalResult = _approvalGate != null
                        ? await _approvalGate.RequestApprovalAsync(
                            $"Extender agent {agentId} requests approval to run self-extend cycle.",
                            TimeSpan.FromSeconds(30),
                            cancellationToken).ConfigureAwait(false)
                        : ApprovalResult.Denied;
                    if (approvalResult != ApprovalResult.Approved)
                    {
                        var reason = approvalResult == ApprovalResult.TimedOut ? "timeout" : "denied";
                        _logStore?.Append(agentId, "Info", $"SemiActive mode: execution skipped ({reason}).");
                        _logger?.LogDebug("Background agent {AgentId} in SemiActive mode: extender skipped ({Reason})", agentId, reason);
                        instance.LastCompletedAt = DateTimeOffset.UtcNow;
                        instance.SuccessCount++;
                        return;
                    }
                }

                // SX-AUDIT invariant D: an Active (or approved) extender still may not extend
                // without bound. Lineage depth, unattended cycles since a human armed it, and
                // cycles in the trailing hour are each a refusal on their own; the refusal is
                // recorded where the Passive skip is (log store, observation, telemetry) so a
                // held extender is visible, not silent.
                var ceiling = _extensionCeiling.NarrowedBy(instance.Config);
                var ledger = GetExtensionLedger(agentId);
                // Decide AND reserve in one lock: the scheduler may overlap cycles for one agent
                // (see the concurrency note above), and a separate check-then-record let every
                // racing cycle read the same unspent budget and pass. TryBeginCycle counts the
                // cycle only when it admits, so a refusal still consumes nothing.
                var refusal = ledger.TryBeginCycle(ceiling, LineageDepth(instance.Config));
                if (refusal is not null)
                {
                    _logStore?.Append(agentId, "Warning", $"Extension refused (invariant D): {refusal}.");
                    _logger?.LogWarning("Background agent {AgentId}: extension refused (invariant D): {Reason}", agentId, refusal);
                    _observations?.Append(new RuntimeObservation(
                        ts: DateTimeOffset.UtcNow,
                        source: agentId,
                        kind: ObservationKind.AgentAction,
                        summary: $"Extension refused (invariant D): {refusal}",
                        severity: ObservationSeverity.Warn,
                        facts: new Dictionary<string, string>
                        {
                            ["repo_root"] = repoRoot,
                            ["stopped_reason"] = "extension_ceiling",
                            ["unattended_cycles"] = ledger.UnattendedCycles.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            ["cycles_in_hour"] = ledger.CyclesInWindow.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            ["mode"] = mode.ToString()
                        },
                        agentCycle: instance.ExecutionCount));
                    instance.LastCompletedAt = DateTimeOffset.UtcNow;
                    telemSuccess = false;
                    telemRationale = refusal;
                    telemStoppedReason = "extension_ceiling";
                    return;
                }

                // The cycle was counted by TryBeginCycle above, atomically with the decision that
                // admitted it. A Passive skip or an approval denial returns before that call, so
                // it still consumes none of the agent's extension budget.

                // Surface the agent's configured Objective, AgentName, ModelProvider, and ModelName
                // so the LLM has goal context and the model chain routes to the configured backend.
                // Without provider routing the HotSwappableModel falls through to a deterministic
                // echo and the planner appears to "do nothing" because the LLM is never called.
                var objective = TryGetParameter(instance.Config, new[] { "Objective", "Goal" }, out var obj) ? obj : null;
                var agentName = TryGetParameter(instance.Config, new[] { "AgentName" }, out var an) ? an : agentId;
                var modelProvider = string.IsNullOrWhiteSpace(instance.Config.ModelProvider) ? null : instance.Config.ModelProvider;
                var modelName = string.IsNullOrWhiteSpace(instance.Config.ModelName) ? null : instance.Config.ModelName;
                var result = await _selfExtendRunner
                    .RunAsync(repoRoot, objective, agentName, modelProvider, modelName, agentId, cancellationToken)
                    .ConfigureAwait(false);

                if (mode == BackgroundAgentAggressivenessMode.Ambient)
                {
                    _auditLog?.LogAmbientAction(agentId, result.Summary, result.ToolCallsExecuted);
                    _logStore?.Append(agentId, "Info", $"Ambient: executed silently ({result.ToolCallsExecuted} tool calls).");
                    _logger?.LogDebug("Background agent {AgentId} in Ambient mode: executed silently", agentId);
                }
                else
                {
                    _logStore?.Append(agentId, result.Success ? "Info" : "Warning",
                        $"Self-extend: {result.Summary} (executed: {result.ToolCallsExecuted}, denied: {result.ToolCallsDenied})");
                    _logger?.LogInformation("Background agent {AgentId} self-extend: {Summary}", agentId, result.Summary);
                }

                // Publish an AgentAction observation so the planner sees what the extender
                // did this cycle (tool counts + stopped reason). This is intentionally
                // emitted even in Ambient mode — observations are an internal substrate,
                // not user-facing notifications.
                _observations?.Append(new RuntimeObservation(
                    ts: DateTimeOffset.UtcNow,
                    source: agentId,
                    kind: ObservationKind.AgentAction,
                    summary: $"Self-extend: {result.Summary} (executed: {result.ToolCallsExecuted}, denied: {result.ToolCallsDenied})",
                    severity: result.Success ? ObservationSeverity.Info : ObservationSeverity.Warn,
                    facts: new Dictionary<string, string>
                    {
                        ["repo_root"] = repoRoot,
                        ["executed"] = result.ToolCallsExecuted.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["denied"] = result.ToolCallsDenied.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["iterations"] = (result.Iterations > 0 ? result.Iterations : 0).ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["stopped_reason"] = result.StoppedReason ?? "self_extend",
                        ["mode"] = mode.ToString()
                    },
                    agentCycle: instance.ExecutionCount));
                instance.LastCompletedAt = DateTimeOffset.UtcNow;
                instance.SuccessCount++;
                _logStore?.Append(agentId, "Info", "Execution completed successfully.");
                telemSuccess = result.Success;
                telemExecuted = result.ToolCallsExecuted;
                telemDenied = result.ToolCallsDenied;
                telemIterations = result.Iterations > 0 ? result.Iterations : null;
                telemRationale = result.Summary;
                telemStoppedReason = result.StoppedReason ?? "self_extend";
                return;
            }

            // Self-improver agents run the self-improvement loop (test failures → fix → validate → promote)
            if (string.Equals(instance.Config.Role, "self-improver", StringComparison.OrdinalIgnoreCase) &&
                _selfImprovementLoop != null)
            {
                var mode = _modeStore?.GetMode() ?? BackgroundAgentAggressivenessMode.SemiActive;
                if (mode == BackgroundAgentAggressivenessMode.Passive)
                {
                    _logStore?.Append(agentId, "Info", "Passive mode: skipping self-improver execution.");
                    _logger?.LogDebug("Background agent {AgentId} in Passive mode: self-improver skipped", agentId);
                    instance.LastCompletedAt = DateTimeOffset.UtcNow;
                    instance.SuccessCount++;
                    return;
                }

                if (mode == BackgroundAgentAggressivenessMode.SemiActive)
                {
                    var approvalResult = _approvalGate != null
                        ? await _approvalGate.RequestApprovalAsync(
                            $"Self-improver agent {agentId} requests approval to run improvement cycle.",
                            TimeSpan.FromSeconds(30),
                            cancellationToken).ConfigureAwait(false)
                        : ApprovalResult.Denied;
                    if (approvalResult != ApprovalResult.Approved)
                    {
                        var reason = approvalResult == ApprovalResult.TimedOut ? "timeout" : "denied";
                        _logStore?.Append(agentId, "Info", $"SemiActive mode: self-improver skipped ({reason}).");
                        _logger?.LogDebug("Background agent {AgentId} in SemiActive mode: self-improver skipped ({Reason})", agentId, reason);
                        instance.LastCompletedAt = DateTimeOffset.UtcNow;
                        instance.SuccessCount++;
                        return;
                    }
                }

                await _selfImprovementLoop.RunOnceAsync(cancellationToken).ConfigureAwait(false);
                var report = await _selfImprovementLoop.GetLastRunReportAsync(cancellationToken).ConfigureAwait(false);
                var summary = report != null
                    ? $"Self-improvement: {report.FailuresProcessed} processed, {report.FixesPromoted} promoted, {report.FixesRejected} rejected"
                    : "Self-improvement: completed (no report available)";
                _logStore?.Append(agentId, "Info", summary);
                _logger?.LogInformation("Background agent {AgentId} self-improvement: {Summary}", agentId, summary);
                instance.LastCompletedAt = DateTimeOffset.UtcNow;
                instance.SuccessCount++;
                _logStore?.Append(agentId, "Info", "Execution completed successfully.");
                telemRationale = summary;
                telemStoppedReason = "self_improvement";
                return;
            }

            // An agent whose ROLE has a dedicated execution lane, but whose lane did not engage,
            // must never fall through to the generic success below. That fall-through is how an
            // extender in a host that never registered ISelfExtendRunner logged "Execution
            // completed successfully" every cycle while doing nothing at all — and, because the
            // extender body is where the invariant-D ceiling lives, how ExtensionCeiling (the
            // headline blast-radius control) could never fire: the code that consults it was
            // skipped, silently, by the same condition that skipped the work.
            //
            // The lane's preconditions are named individually, because "extender did not run" is
            // useless and "no ISelfExtendRunner is registered in this host" is actionable.
            if (UnmetLanePrecondition(instance.Config) is { } unmet)
            {
                var message = $"Role '{instance.Config.Role}' did NOT run: {unmet}";
                _logStore?.Append(agentId, "Error", message);
                _logger?.LogError(
                    "Background agent {AgentId} did not execute its role: {Reason}", agentId, unmet);
                _observations?.Append(new RuntimeObservation(
                    ts: DateTimeOffset.UtcNow,
                    source: agentId,
                    kind: ObservationKind.AgentAction,
                    summary: message,
                    severity: ObservationSeverity.Error,
                    facts: new Dictionary<string, string>
                    {
                        ["role"] = instance.Config.Role ?? string.Empty,
                        ["executed"] = "0",
                        ["stopped_reason"] = "lane_preconditions_unmet"
                    },
                    agentCycle: instance.ExecutionCount));
                instance.LastCompletedAt = DateTimeOffset.UtcNow;
                instance.FailureCount++;
                telemSuccess = false;
                telemExecuted = 0;
                telemRationale = message;
                telemStoppedReason = "lane_preconditions_unmet";
                telemError = unmet;
                return;
            }

            // Default: simple success (full agent ThinkAsync + toolbox can be wired later)
            instance.LastCompletedAt = DateTimeOffset.UtcNow;
            instance.SuccessCount++;
            _logStore?.Append(agentId, "Info", "Execution completed successfully.");
            _logger?.LogDebug("Background agent {AgentId} executed successfully", agentId);
            return;
        }
        catch (OperationCanceledException)
        {
            telemSuccess = false;
            telemStoppedReason = "cancelled";
            throw;
        }
        catch (Exception ex)
        {
            instance.FailureCount++;
            instance.LastError = ex.Message;
            _logStore?.Append(agentId, "Error", $"Execution failed: {ex.Message}");
            _logger?.LogError(ex, "Background agent {AgentId} execution failed", agentId);
            telemSuccess = false;
            telemError = ex.Message;
            telemStoppedReason ??= "error";
            return;
        }
        finally
        {
            sw.Stop();
            if (_cycleEvents is not null)
            {
                var modelProviderTel = string.IsNullOrWhiteSpace(instance.Config.ModelProvider) ? null : instance.Config.ModelProvider;
                var modelNameTel = string.IsNullOrWhiteSpace(instance.Config.ModelName) ? null : instance.Config.ModelName;
                _cycleEvents.Append(new CycleEvent(
                    cycleStart,
                    agentId,
                    instance.ExecutionCount,
                    instance.Config.Role,
                    sw.ElapsedMilliseconds,
                    telemIterations,
                    telemExecuted,
                    telemDenied,
                    modelNameTel,
                    modelProviderTel,
                    telemRationale,
                    telemStoppedReason,
                    telemSuccess,
                    telemError));
            }
        }
    }

    /// <summary>
    /// Why a role with a dedicated execution lane did not reach it, or null when the role has no
    /// dedicated lane (a purely observational agent, which the generic success branch describes
    /// correctly).
    ///
    /// <para>Each condition mirrors, exactly, one clause of its lane's guard above. If a guard
    /// changes, this must change with it — the pairing is the point: a lane that can be skipped
    /// silently is a lane whose absence gets reported as success.</para>
    /// </summary>
    private string? UnmetLanePrecondition(BackgroundAgentConfig config)
    {
        var role = config.Role ?? string.Empty;

        if (string.Equals(role, "optimizer", StringComparison.OrdinalIgnoreCase))
        {
            if (_codeAnalysisRunner is null)
            {
                return "no ICodeAnalysisRunner is registered in this host, so there is nothing to run the "
                     + "analysis. Register one before AddBackgroundAgents(), or change this agent's role.";
            }
            if (!TryGetParameter(config, ["Path", "AnalysisPath"], out _))
            {
                return "the agent declares no Path (or AnalysisPath) parameter, so there is no code to "
                     + "analyse. Add Path to the agent's Parameters.";
            }
            return null;
        }

        if (string.Equals(role, "tester", StringComparison.OrdinalIgnoreCase))
        {
            return _testRunRunner is null
                ? "no ITestRunRunner is registered in this host, so no tests can be run. Register one "
                + "before AddBackgroundAgents(), or change this agent's role."
                : null;
        }

        if (string.Equals(role, "extender", StringComparison.OrdinalIgnoreCase))
        {
            if (_selfExtendRunner is null)
            {
                return "no ISelfExtendRunner is registered in this host, so no self-extend cycle can run — "
                     + "and because the ExtensionCeiling is enforced inside that cycle, no blast-radius "
                     + "limit was applied either. AddAshlar()/AddBackgroundAgents() do not register a "
                     + "runner: register your own ISelfExtendRunner before AddBackgroundAgents(), or use "
                     + "`ashlar background-agent` from the CLI, which wires one. Until then this agent "
                     + "does nothing, and says so rather than reporting success.";
            }
            if (!TryGetParameter(config, ["RepoRoot", "Path"], out _))
            {
                return "the agent declares no RepoRoot (or Path) parameter, so the cycle has no tree to "
                     + "work in. Add RepoRoot to the agent's Parameters.";
            }
            return null;
        }

        if (string.Equals(role, "self-improver", StringComparison.OrdinalIgnoreCase))
        {
            return _selfImprovementLoop is null
                ? "no ISelfImprovementLoop is registered in this host, so no improvement cycle can run. "
                + "Register one before AddBackgroundAgents(), or change this agent's role."
                : null;
        }

        return null;
    }

    private static bool TryGetParameter(BackgroundAgentConfig config, string[] keys, out string value)
    {
        value = null!;
        if (config.Parameters == null || config.Parameters.Count == 0)
            return false;
        foreach (var key in keys)
        {
            if (config.Parameters.TryGetValue(key, out var obj) && obj is string s && !string.IsNullOrWhiteSpace(s))
            {
                value = s;
                return true;
            }
        }
        return false;
    }
}
