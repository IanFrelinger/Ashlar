using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ashlar.BackgroundAgents.Objectives;
using Ashlar.Core.Application.Autonomy;
using Ashlar.Core.Application.Certification.Models;
using Ashlar.Core.Application.Execution.Ports;
using Ashlar.Core.Domain.Execution;
using Ashlar.Infrastructure.Autonomy;
using Ashlar.Infrastructure.Certification.HotSwap;

namespace Ashlar.BackgroundAgents.Autonomy;

/// <summary>
/// Where the loop runs and what it may do — the operator's dial for the LOOP itself
/// (cadence, batch size, repair policy, compile references). Everything about the
/// iteration — enablement, sessions, in-session build/execution, admission hold — is
/// <see cref="AshlarAutonomyOptions"/> (<c>Ashlar:Autonomy</c>), read by the loop and enforced by
/// the harness; there is deliberately no second copy of those switches here, because a
/// setting that is logged but not enforced is worse than none.
/// </summary>
[Experimental(AutonomyExperimental.DiagnosticId, UrlFormat = AutonomyExperimental.UrlFormat)]
public sealed class AutonomyLoopSettings
{
    /// <summary>Seconds between sweeps of the objective store. 0 disables the loop.</summary>
    public int IntervalSeconds { get; set; }

    /// <summary>
    /// Container image proposal sessions run in. Null falls back to
    /// <see cref="AshlarAutonomyOptions.SessionImage"/>; used only when
    /// <see cref="AshlarAutonomyOptions.UseSandboxSessions"/> is true.
    /// </summary>
    public string? SessionImage { get; set; }

    /// <summary>Objectives attempted per sweep. Keeps one sweep bounded.</summary>
    public int MaxObjectivesPerSweep { get; set; } = 1;

    /// <summary>
    /// Reference assemblies handed to the candidate compile. Defaults to the brick contract
    /// assemblies (<c>DomainBrick</c>, <c>BrickInput</c>) — the minimum any candidate needs to
    /// compile at all; add whatever else the objectives' candidates delegate to.
    /// </summary>
    public IReadOnlyList<string> CompilationReferences { get; set; } = DefaultCompilationReferences();

    /// <summary>The brick contract assemblies every candidate compiles against.</summary>
    public static IReadOnlyList<string> DefaultCompilationReferences() =>
        new[] { typeof(DomainBrick).Assembly.Location, typeof(BrickInput).Assembly.Location };

    /// <summary>
    /// The repair channel's dial: how much of a rejection the proposer may see, and how many
    /// repair rounds an objective gets before it is held for a human. Model-independent by
    /// design — a large hosted model and a small local one need different settings on the
    /// same loop. Default: locations and the proposer's own output, never the witness;
    /// two attempts.
    /// </summary>
    public RepairFeedbackPolicy Repair { get; set; } = RepairFeedbackPolicy.Default();
}

/// <summary>
/// The standing loop: sweeps the objective store and, for each eligible objective, drives
/// one iteration through <see cref="AutonomousIterationHarness"/>.
///
/// <para>This is the piece that turns the loop from a mechanism into a running system.
/// Every part it uses already existed and was proven in flight — intake gating, tier
/// classification, attested sessions, in-session build and execution, the swap host,
/// cadence, budgets, pause. What was missing was anything that CALLED them on a schedule
/// against a real backlog.</para>
///
/// <para><b>Fail-closed at intake, by design.</b> An objective runs only if a
/// human-authored witness sits beside it and a candidate exists to certify. No witness
/// means no run — never a run without acceptance criteria, because a certificate minted
/// without them would be a claim about nothing. Proposal and iteration failures alike
/// record an attempt and move on: one bad objective must not wedge the sweep.</para>
///
/// <para><b>Host options are read here, enforced by the harness.</b> The standing loop
/// runs only under <see cref="AshlarAutonomyOptions.Enabled"/>; sessions are opened only when
/// <see cref="AshlarAutonomyOptions.UseSandboxSessions"/> is set (image from the loop settings,
/// else the host options); the admission hold the log reports is
/// <see cref="AshlarAutonomyOptions.HoldAdmission"/> — the value the composed harness enforces,
/// not a second dial. A loop constructed by hand without options (the first-flight spike,
/// tests) can be swept directly through <see cref="SweepAsync"/>; the timer never starts
/// for it, because a background loop with no host switch to turn it off is not fail-closed.</para>
/// </summary>
[Experimental(AutonomyExperimental.DiagnosticId, UrlFormat = AutonomyExperimental.UrlFormat)]
public sealed class AutonomyLoopService : BackgroundService
{
    /// <summary>
    /// Seconds a session's keepalive outlives the iteration ceiling. The keepalive command is
    /// what keeps the container alive between execs; if it were EQUAL to the ceiling the
    /// container could exit while the last exec of a long iteration was still in flight, and
    /// the failure would read as a session fault rather than as the budget verdict it is.
    /// </summary>
    internal const int SessionKeepaliveMarginSeconds = 60;

    private static readonly AshlarAutonomyOptions OptionDefaults = new();

    private readonly IObjectiveStore _objectives;
    private readonly AutonomousIterationHarness _harness;
    private readonly IProposalSource? _proposals;
    private readonly AutonomyLoopSettings _settings;
    private readonly AshlarAutonomyOptions? _autonomy;
    private readonly ILogger<AutonomyLoopService> _logger;

    /// <summary>Creates the loop service.</summary>
    /// <param name="objectives">The objective store the loop sweeps.</param>
    /// <param name="harness">The iteration harness (composed by <c>AddAshlarAutonomy</c>).</param>
    /// <param name="settings">Loop-level settings: cadence, batch size, repair policy, references.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="proposals">Optional live proposer; null replays recorded proposals beside each objective.</param>
    /// <param name="autonomyOptions">
    /// Host options (<c>Ashlar:Autonomy</c>). Null only for hand-composed loops driven through
    /// <see cref="SweepAsync"/>; the timer-driven loop refuses to start without them.
    /// </param>
    public AutonomyLoopService(
        IObjectiveStore objectives,
        AutonomousIterationHarness harness,
        AutonomyLoopSettings settings,
        ILogger<AutonomyLoopService> logger,
        IProposalSource? proposals = null,
        IOptions<AshlarAutonomyOptions>? autonomyOptions = null)
    {
        _objectives = objectives ?? throw new ArgumentNullException(nameof(objectives));
        _harness = harness ?? throw new ArgumentNullException(nameof(harness));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _proposals = proposals;
        _autonomy = autonomyOptions?.Value;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_autonomy is null)
        {
            // Fail-closed: the standing loop has exactly one master switch, Ashlar:Autonomy:Enabled,
            // and a loop composed without host options has no such switch. Sweeping by hand
            // (SweepAsync) stays available for one-shot drivers.
            _logger.LogWarning(
                "Autonomy loop not started: no {Section} options were supplied, so there is no host "
                + "switch governing it. Compose it with AddAshlarAutonomy(configuration) or drive SweepAsync directly.",
                AshlarAutonomyOptions.SectionName);
            return;
        }

        if (!_autonomy.Enabled)
        {
            _logger.LogInformation(
                "Autonomy loop not started: {Section}:Enabled=false (IntervalSeconds={Interval})",
                AshlarAutonomyOptions.SectionName, _settings.IntervalSeconds);
            return;
        }

        if (_settings.IntervalSeconds <= 0)
        {
            _logger.LogInformation(
                "Autonomy loop disabled (IntervalSeconds={Interval})", _settings.IntervalSeconds);
            return;
        }

        // The hold reported here is the one the harness ENFORCES (AddAshlarAutonomy passes the
        // same option into it) — never a loop-level copy that could disagree.
        _logger.LogInformation(
            "Autonomy loop starting: every {Interval}s, holdAdmission={Hold} (enforced by the harness), "
            + "sessions={Sessions}, buildInSession={Build}, executeInSession={Execute}",
            _settings.IntervalSeconds, _autonomy.HoldAdmission, _autonomy.UseSandboxSessions,
            _autonomy.BuildCandidateInSession, _autonomy.ExecuteCandidateInSession);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_settings.IntervalSeconds));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await SweepAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A sweep failure is never fatal: the loop's whole value is that it
                    // keeps running and keeps reporting.
                    _logger.LogWarning(ex, "Autonomy sweep failed; the cadence continues");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Host shutdown.
        }
    }

    /// <summary>Runs one sweep. Exposed so tests drive it directly instead of on a timer.</summary>
    /// <returns>The number of objectives actually RUN. Failures are counted separately: they
    /// charge the same per-sweep budget, but a sweep that only failed did not attempt anything
    /// in the sense callers and tests mean by the return value.</returns>
    public async Task<int> SweepAsync(CancellationToken cancellationToken = default)
    {
        var pending = _objectives.List(ObjectiveStatus.Pending);
        var attempted = 0;
        var failed = 0;

        foreach (var objective in pending.OrderBy(o => o.Priority).ThenBy(o => o.CreatedAt))
        {
            // Failures charge the budget too. MaxObjectivesPerSweep exists to bound the work — and
            // the SPEND — of one sweep, and a failing objective still paid for a proposer call. If
            // only successes counted, a proposer that is down (429, expired credential) would be
            // called once per pending objective per sweep instead of once per sweep: the loop would
            // hammer hardest exactly when the dependency is already refusing, and a sweep over
            // hundreds of objectives could outrun its own interval.
            if (attempted + failed >= _settings.MaxObjectivesPerSweep || cancellationToken.IsCancellationRequested)
                break;

            var path = ObjectivePath(objective);
            if (path is null)
                continue;

            // One bad objective must not wedge the sweep (the class contract above). The
            // session runner is fail-closed by design — "a session that cannot be started is an
            // exception" — and the harness only catches cancellation, so a missing container
            // engine, a corrupt artifact, or a proposer fault would otherwise abort the whole
            // list mid-way and starve every lower-priority objective behind it, on every sweep.
            try
            {
                var witness = ObjectiveArtifacts.LoadWitness(path, out var witnessCorruption);
                if (witness is null)
                {
                    if (witnessCorruption is not null)
                    {
                        // A witness someone WROTE that no longer counts. Silent here means the
                        // objective is parked forever with nothing said.
                        _logger.LogWarning(
                            "Objective {Id} cannot run: its witness is unusable — {Reason}",
                            objective.Id, witnessCorruption);
                    }
                    else
                    {
                        // Not worth shouting about each sweep: an objective simply is not eligible
                        // until a human has written its acceptance criteria.
                        _logger.LogDebug(
                            "Objective {Id} skipped: no witness beside it ({Path})", objective.Id, path);
                    }

                    continue;
                }

                var proposal = await ProposeAsync(objective, path, cancellationToken).ConfigureAwait(false);
                if (proposal is null)
                {
                    _logger.LogDebug("Objective {Id} skipped: no proposal available", objective.Id);
                    continue;
                }

                attempted++;
                await RunOneAsync(objective, witness, proposal, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Explained refusal for THIS objective, then carry on with the rest — but charged,
                // so a systemic fault cannot turn one budgeted sweep into N calls.
                failed++;
                // THIS TEMPLATE IS PARSED. SweepAsync returns the ATTEMPTED count and discards
                // `failed`, so a sweep whose only objective died here still reports "attempted 1"
                // and exits 0. scripts/dogfood-continuous-proof.sh greps the rendered text for
                // "; continuing the sweep" to tell an infrastructure fault apart from a result,
                // because the exit code cannot. Change the wording and the dogfood ledger starts
                // recording failed sweeps as passes again - it already did once, in run
                // 34889059104. Surface a failure count from SweepAsync before relaxing this.
                _logger.LogWarning(
                    ex, "Objective {Id} failed ({Path}); continuing the sweep", objective.Id, path);
            }
        }

        return attempted;
    }

    private string? ObjectivePath(ObjectiveDocument objective)
    {
        if (_objectives is not ObjectiveStore store)
            return null;

        foreach (var status in Enum.GetValues<ObjectiveStatus>())
        {
            var candidate = Path.Combine(store.Location, status.ToString().ToLowerInvariant(), objective.Id + ".md");
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private async Task<ProposedSource?> ProposeAsync(
        ObjectiveDocument objective, string path, CancellationToken cancellationToken)
    {
        if (_proposals is not null)
        {
            var request = new ProposalRequest(objective.Id, objective.Title, objective.Body, objective.Touch);
            return await _proposals.ProposeAsync(request, cancellationToken).ConfigureAwait(false);
        }

        // No live proposer composed: fall back to a recorded proposal beside the objective
        // (the record/replay discipline the dogfood arcs established).
        var recorded = ObjectiveArtifacts.LoadRecordedProposal(path, out var corruption);
        if (corruption is not null)
        {
            _logger.LogWarning(
                "Objective {Id} has a recorded proposal that cannot be replayed — {Reason}",
                objective.Id, corruption);
        }

        return recorded;
    }

    /// <summary>
    /// Runs the objective through the harness, and — when a live proposer is composed and
    /// the verdict is a rejection with something to say (a certification decision, or a
    /// candidate that did not compile) — hands the proposer a policy-projected view of that
    /// rejection and tries again, up to <see cref="RepairFeedbackPolicy.MaxAttemptsPerObjective"/>
    /// repairs. Every attempt is its own full iteration (session, attestation, chain), so the
    /// evidence for each round stands on its own; nothing here shortcuts the gate.
    /// </summary>
    private async Task RunOneAsync(
        ObjectiveDocument objective,
        WitnessSpec witness,
        ProposedSource proposal,
        CancellationToken cancellationToken)
    {
        var policy = _settings.Repair;
        var current = proposal;

        for (var attempt = 0; ; attempt++)
        {
            var result = await RunIterationAsync(objective, witness, current, cancellationToken)
                .ConfigureAwait(false);

            _logger.LogInformation(
                "Autonomy iteration for {Id} (attempt {Attempt}): {Outcome} — {Explanation}",
                objective.Id, attempt + 1, result.Outcome, result.Explanation);

            // Repair a certification rejection with a decision to project, or a candidate
            // that did not compile (its diagnostics ride on the result); intake refusals,
            // budget exhaustion, and held/admitted outcomes are terminal here. Dogfood
            // campaign 1 showed why the build case matters: a small model's dominant failure
            // is a one-line compile slip, and until this it was terminal after one attempt.
            if (result.Outcome != IterationOutcome.ExplainedFailure)
                return;
            if (result.Decision is { } decision && ProposedBrickHandle.RefusedInProcessExecution(decision))
            {
                // Not a candidate defect and not repairable: the gate executed the loop's
                // identity-only handle IN THIS PROCESS (no execution backend, i.e.
                // ExecuteCandidateInSession=false) and the handle refused, as it must. Handing
                // that refusal to the proposer as feedback would ask a model to fix the host's
                // wiring. It terminates here as the explained failure it is.
                _logger.LogError(
                    "Objective {Id}: the certification gate ran the proposed candidate IN-PROCESS and the "
                    + "identity handle refused. This is host wiring, not the candidate: the loop's candidates "
                    + "execute only inside an attested session. Set {Section}:UseSandboxSessions, "
                    + ":BuildCandidateInSession and :ExecuteCandidateInSession to true (with a SessionImage). "
                    + "Nothing was sent to the proposer.",
                    objective.Id, AshlarAutonomyOptions.SectionName);
                return;
            }
            var repairable = result.Decision is not null || result.BuildDiagnostics is not null;
            if (!repairable)
                return;
            if (_proposals is null || attempt >= policy.MaxAttemptsPerObjective)
            {
                if (_proposals is not null)
                {
                    _logger.LogInformation(
                        "Objective {Id}: repair budget of {Max} exhausted; holding for a human",
                        objective.Id, policy.MaxAttemptsPerObjective);
                }
                return;
            }

            // The proposer sees the projection, never the raw rejection.
            var feedback = result.Decision is not null
                ? RepairFeedback.Render(result.Decision, policy)
                : RepairFeedback.RenderBuildFailure(result.BuildDiagnostics!, policy);
            var request = new ProposalRequest(objective.Id, objective.Title, objective.Body, objective.Touch)
            {
                Repair = new RepairContext(
                    current.SourceCode,
                    feedback,
                    attempt + 1,
                    result.Decision is not null ? RepairKind.Certification : RepairKind.Build),
            };
            var repaired = await _proposals.ProposeAsync(request, cancellationToken).ConfigureAwait(false);
            if (repaired is null)
            {
                _logger.LogInformation("Objective {Id}: proposer declined to repair", objective.Id);
                return;
            }

            current = repaired;
        }
    }

    private async Task<IterationResult> RunIterationAsync(
        ObjectiveDocument objective,
        WitnessSpec witness,
        ProposedSource proposal,
        CancellationToken cancellationToken)
    {
        var context = new ProposalIterationContext
        {
            ObjectiveId = objective.Id,
            Source = objective.Source,
            Touch = objective.Touch,
            // The proposer signature is the provenance channel (R4.1): it rides the lineage
            // and is hash-bound into the certificate's generation-depth input.
            Lineage = GenerationLineage.Child(GenerationLineage.HumanAuthored, proposal.ProposerSignature),
            SessionSpec = BuildSessionSpec(),
        };

        // The project file is a compile-time input only; nothing reads it after the
        // iteration, so it must not outlive it (one sweep used to leak one per objective).
        var projectPath = CleanProjectFile();
        try
        {
            var candidate = new ProposalCandidate
            {
                Brick = new ProposedBrickHandle(witness.BrickId),
                SourceCode = proposal.SourceCode,
                Witness = witness,
                ProjectPath = projectPath,
                CompilationReferences = _settings.CompilationReferences,
                BrickTypeName = proposal.TypeName,
            };

            return await _harness.RunIterationAsync(context, candidate, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            try
            {
                File.Delete(projectPath);
            }
            catch (IOException)
            {
                // Best effort: a locked temp file is not worth failing the iteration over.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    /// The iteration's session spec, or null when the host does not use sessions. Under host
    /// options, sessions follow <see cref="AshlarAutonomyOptions.UseSandboxSessions"/> exactly —
    /// no spec is built for a host that said no — with the image from the loop settings, else
    /// the host options (which the validator guarantees is set when sessions are on). A
    /// hand-composed loop (no options) opens a session when its settings name an image.
    /// </summary>
    private SandboxSpec? BuildSessionSpec()
    {
        var useSessions = _autonomy?.UseSandboxSessions ?? _settings.SessionImage is not null;
        if (!useSessions)
            return null;

        var ceilingSeconds = _autonomy?.IterationCeilingSeconds ?? OptionDefaults.IterationCeilingSeconds;
        return new SandboxSpec(
            Image: _settings.SessionImage ?? _autonomy?.SessionImage,
            // No mounts: sessions may be sibling containers on another daemon, and the
            // candidate travels in over ExecAsync rather than through the filesystem.
            Mounts: Array.Empty<Mount>(),
            Network: NetworkAccess.None,
            // Keepalive outlives the iteration ceiling by a margin (see the constant); the
            // ceiling itself, not the keepalive, is what ends a runaway iteration.
            Command: new[]
            {
                "sleep",
                (ceilingSeconds + SessionKeepaliveMarginSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture),
            },
            Limits: new ResourceLimits(Memory: "512m", Pids: 128, Cpus: "1"))
        {
            // The declared write surface: the backend seals the rootfs read-only and
            // gives back exactly these as ephemeral scratch, so the in-session build
            // and execution legs work and anything writing elsewhere fails loudly.
            ScratchPaths = SessionScratchPaths.Default,
        };
    }

    private static string CleanProjectFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ashlar-objective-{Guid.NewGuid():N}.csproj");
        File.WriteAllText(
            path,
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <ItemGroup>\n"
            + "    <PackageReference Include=\"Ashlar.Brick.Contracts\" Version=\"0.1.0\" />\n"
            + "  </ItemGroup>\n</Project>\n");
        return path;
    }
}
