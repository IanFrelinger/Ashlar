using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Ashlar.Certification.Contracts;
using Ashlar.Core.Application.Autonomy;
using Ashlar.Core.Domain.Bricks;
using Ashlar.Core.Domain.Execution;
using Ashlar.Infrastructure.Testing.CodeAnalysis;

namespace Ashlar.Infrastructure.Certification.HotSwap;

/// <summary>
/// Hot-reloads certified bricks into a running host, one collectible load context per
/// <em>generation</em> of the brick set — never per brick.
/// </summary>
/// <remarks>
/// Trust properties (trust-loop integration plan §3):
/// <list type="number">
/// <item><description><b>Verify-at-load.</b> Every brick's certification record is re-verified
/// against the exact source bytes being loaded (<see cref="CertificationTrustVerifier"/>
/// with <see cref="CertificationVerifyOptions.Strict"/>). When a supplied PE matches
/// the record's <c>gate-emitted-artifact</c> hash, the artifact-bytes overload binds
/// those bytes; otherwise the host rematerializes from wrapped source.</description></item>
/// <item><description><b>Fail-closed swap.</b> Any verification, compile, load, or
/// instantiation failure refuses the <em>entire</em> swap and leaves the previous
/// generation serving. There is no partial swap.</description></item>
/// <item><description><b>Serialized transitions.</b> Generation load/unload/collection runs
/// under <see cref="CollectibleLoadContextGate"/>, shared with the mutation engine, so
/// collectible <c>LoaderAllocator</c>s are never finalized concurrently
/// (<c>0x80131506</c>).</description></item>
/// <item><description><b>Provenance.</b> Every swap — committed or refused — and every
/// generation lifecycle transition emits a <see cref="BrickSwapProvenanceEvent"/>.</description></item>
/// <item><description><b>A rollback is not an absorption.</b> The pause, cadence floor,
/// in-flight watch window, lineage demotion and recursion ceiling bound how the runtime
/// takes on change; <see cref="RollbackToAsync"/> replays content this host retained and
/// is gated only by verify-at-load and revocation. The intent is a property of the call
/// path (an internal <see cref="SwapIntent"/> on a private overload), never of the
/// request, so no caller can select the exempted lane.</description></item>
/// </list>
/// Swap sequence: verify all → load generation N+1 → route new invocations to it →
/// drain generation N → unload → drive collection and report leak suspicion by context
/// name (the <see cref="MutantAssemblyLoadContext"/> attribution trick).
/// </remarks>
[Experimental(AutonomyExperimental.DiagnosticId, UrlFormat = AutonomyExperimental.UrlFormat)]
public sealed class CertifiedBrickHotSwapHost : IDisposable
{
    private static readonly TimeSpan DefaultDrainTimeout = TimeSpan.FromSeconds(30);

    private readonly ICertifiedBrickSwapProvenanceSink? _provenanceSink;
    private readonly ILogger<CertifiedBrickHotSwapHost>? _logger;
    private readonly string? _hmacKey;
    private readonly TimeSpan _drainTimeout;
    private readonly Ashlar.Core.Application.Autonomy.ICertificateRevocationList? _revocations;
    private readonly int _retentionWindow;
    private readonly List<RetainedGeneration> _retained = new();
    private readonly WatchThresholds? _watchThresholds;
    private readonly Ashlar.Core.Application.Autonomy.ILineageAuthority? _lineageAuthority;
    private readonly Ashlar.Core.Application.Autonomy.LoopPauseControl? _pauseControl;
    private readonly TimeSpan? _cadenceFloor;
    private readonly TimeProvider _clock;

    private BrickGeneration? _current;
    private int _generationCounter;
    private GenerationWatchStats? _watchBaseline;
    private GenerationWatchStats? _watchCurrent;
    private int _watchGenerationId;
    private int _watchBreachLatch;
    private DateTimeOffset? _lastAutonomousSwapUtc;
    private IReadOnlyList<string> _currentLineageKeys = Array.Empty<string>();

    /// <summary>
    /// Serializes the DECISION to swap with the swap. <see cref="VerifyAll"/> runs outside
    /// the collectible-context gate by design (a refusal must cost no ALC churn), so without
    /// this nothing orders one swap's verdict against another's commit: a rollback could
    /// queue behind a forward swap and displace the generation it just committed, or the
    /// reverse. Acquired before the ALC gate — the only order anything in the tree uses —
    /// and released right after the commit bookkeeping, before the retiring generation
    /// drains, so the next decision never inherits the drain timeout.
    /// </summary>
    private readonly SemaphoreSlim _swapDecisionGate = new(1, 1);

    /// <summary>
    /// Guards <see cref="_lastAutonomousSwapUtc"/> (a nullable <see cref="DateTimeOffset"/>,
    /// wider than a word and so torn-readable) and <see cref="_currentLineageKeys"/>;
    /// never held across an await.
    /// </summary>
    private readonly object _autonomyGate = new();

    /// <summary>
    /// The most recent rollback generation and the retained generation it replayed.
    /// A rollback is not retained (it would evict the oldest entry and leave the revoked
    /// breacher as the next target), so when the restored generation itself breaches the
    /// quarantine resolves its subject through this link to the retained origin — same
    /// requests, same content hashes — rather than finding nothing to revoke and replaying
    /// the same content again. Guarded by the <see cref="_retained"/> lock.
    /// </summary>
    private (int GenerationId, int RestoredFrom)? _lastRestore;

    /// <summary>Initializes the host.</summary>
    /// <param name="provenanceSink">Receives swap/generation provenance events; null records nothing.</param>
    /// <param name="logger">Optional diagnostics logger.</param>
    /// <param name="hmacKey">Explicit record-verification key; falls back to environment/dev key.</param>
    /// <param name="drainTimeout">How long a retired generation may drain before it is unloaded anyway.</param>
    /// <param name="revocations">Quarantine list; any request whose certificate content hash is revoked is refused permanently (R5.3).</param>
    /// <param name="retentionWindow">How many committed generations stay reactivatable via <see cref="RollbackToAsync"/> (R5.1). 0 disables retention.</param>
    /// <param name="watchThresholds">Post-swap watch thresholds (R5.2); breach quarantines the generation and rolls back automatically. Null = no watch (human-driven flow).</param>
    /// <param name="lineageAuthority">Rollback ledger per objective lineage (R5.5); demoted lineages lose auto-swap.</param>
    /// <param name="pauseControl">Global pause (R6.2): while paused, autonomous swaps are refused; human-driven swaps proceed.</param>
    /// <param name="cadenceFloor">Minimum interval between autonomous swaps (R6.1) so the runtime never absorbs changes faster than watch windows clear.</param>
    /// <param name="clock">Clock for cadence decisions; system time when null.</param>
    public CertifiedBrickHotSwapHost(
        ICertifiedBrickSwapProvenanceSink? provenanceSink = null,
        ILogger<CertifiedBrickHotSwapHost>? logger = null,
        string? hmacKey = null,
        TimeSpan? drainTimeout = null,
        Ashlar.Core.Application.Autonomy.ICertificateRevocationList? revocations = null,
        int retentionWindow = 2,
        WatchThresholds? watchThresholds = null,
        Ashlar.Core.Application.Autonomy.ILineageAuthority? lineageAuthority = null,
        Ashlar.Core.Application.Autonomy.LoopPauseControl? pauseControl = null,
        TimeSpan? cadenceFloor = null,
        TimeProvider? clock = null)
    {
        _provenanceSink = provenanceSink;
        _logger = logger;
        _hmacKey = hmacKey;
        _drainTimeout = drainTimeout ?? DefaultDrainTimeout;
        _revocations = revocations;
        _retentionWindow = Math.Max(0, retentionWindow);
        _watchThresholds = watchThresholds;
        _lineageAuthority = lineageAuthority;
        _pauseControl = pauseControl;
        _cadenceFloor = cadenceFloor;
        _clock = clock ?? TimeProvider.System;
    }

    private sealed record RetainedGeneration(int GenerationId, IReadOnlyList<CertifiedBrickLoadRequest> Requests);

    /// <summary>Per-generation runtime signals for the watch window (R5.2). Interlocked counters only.</summary>
    private sealed class GenerationWatchStats
    {
        private long _invocations;
        private long _faults;
        private long _latencyTicks;
        private long _undeclaredWrites;
        private long _maxLatencyTicks;

        public void Record(long elapsedTicks, bool faulted, int undeclaredWrites)
        {
            Interlocked.Increment(ref _invocations);
            Interlocked.Add(ref _latencyTicks, elapsedTicks);
            if (faulted)
                Interlocked.Increment(ref _faults);
            if (undeclaredWrites > 0)
                Interlocked.Add(ref _undeclaredWrites, undeclaredWrites);

            // Lock-free running maximum for the absolute-duration leg.
            for (var seen = Interlocked.Read(ref _maxLatencyTicks);
                 elapsedTicks > seen;
                 seen = Interlocked.Read(ref _maxLatencyTicks))
            {
                if (Interlocked.CompareExchange(ref _maxLatencyTicks, elapsedTicks, seen) == seen)
                    break;
            }
        }

        public (long Invocations, long Faults, long LatencyTicks, long UndeclaredWrites, long MaxLatencyTicks) Snapshot() => (
            Interlocked.Read(ref _invocations),
            Interlocked.Read(ref _faults),
            Interlocked.Read(ref _latencyTicks),
            Interlocked.Read(ref _undeclaredWrites),
            Interlocked.Read(ref _maxLatencyTicks));
    }

    /// <summary>Generation currently serving, or null before the first successful swap.</summary>
    public int? CurrentGenerationId => Volatile.Read(ref _current)?.Id;

    /// <summary>Brick ids serving in the current generation.</summary>
    public IReadOnlyCollection<string> CurrentBrickIds =>
        Volatile.Read(ref _current)?.BrickIds ?? Array.Empty<string>();

    /// <summary>
    /// Verifies, loads, and atomically publishes a new generation of certified bricks.
    /// On any refusal the previous generation keeps serving untouched.
    /// </summary>
    public Task<CertifiedBrickSwapResult> SwapAsync(
        IReadOnlyList<CertifiedBrickLoadRequest> requests,
        CancellationToken cancellationToken = default)
    {
        if (requests is null)
            throw new ArgumentNullException(nameof(requests));
        if (requests.Count == 0)
            throw new ArgumentException("A swap needs at least one brick.", nameof(requests));

        // The public surface is always an absorption. Only RollbackToAsync — which replays
        // content this host retained and looked up by id — reaches the Rollback intent.
        return SwapAsync(requests, SwapIntent.Forward, restoredFrom: null, cancellationToken);
    }

    private async Task<CertifiedBrickSwapResult> SwapAsync(
        IReadOnlyList<CertifiedBrickLoadRequest> requests,
        SwapIntent intent,
        int? restoredFrom,
        CancellationToken cancellationToken)
    {
        await _swapDecisionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var decisionGateHeld = true;
        try
        {
            // Verify-at-load happens before any load context exists: pure computation over
            // in-memory source, so it needs no ALC gate and a refusal costs no ALC churn.
            var refusals = VerifyAll(requests, intent);
            if (refusals.Count > 0)
                return Refuse(Volatile.Read(ref _generationCounter) + 1, requests, refusals, intent);

            await CollectibleLoadContextGate.Instance.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var generationId = _generationCounter + 1;
                var materialized = await MaterializeAsync(generationId, requests, cancellationToken).ConfigureAwait(false);

                if (materialized.Generation is null)
                {
                    // Half-built context: unload was already requested inside the frame that
                    // owned it; drive collection so at most one allocator awaits finalization.
                    await WaitForContextReleaseAsync(materialized.AbortedContextRef).ConfigureAwait(false);
                    TryDeleteDirectory(materialized.TempDirectory);
                    cancellationToken.ThrowIfCancellationRequested();
                    return Refuse(generationId, requests, materialized.Refusals, intent);
                }

                BrickGeneration? previous;
                try
                {
                    _generationCounter = generationId;
                    previous = Interlocked.Exchange(ref _current, materialized.Generation);

                    // Only an absorption is retained: retaining a restore would evict the
                    // oldest entry and leave the revoked breacher as the next rollback target.
                    if (intent == SwapIntent.Forward)
                        RetainCommitted(generationId, requests, materialized.EmittedImages);

                    // Watch rotation (R5.2). On an absorption the outgoing generation's
                    // runtime signals become the baseline the incoming one is judged
                    // against. On a rollback the outgoing generation is the BREACHER: its
                    // signals would blind the error leg (baseline rate near 1.0) and cascade
                    // a latency quarantine onto the restored generation that actually does
                    // work, so the pre-breach baseline stays as the comparand — not rotated,
                    // not nulled. A fresh window and a cleared latch apply to both.
                    if (intent == SwapIntent.Forward)
                        Volatile.Write(ref _watchBaseline, Volatile.Read(ref _watchCurrent));
                    Volatile.Write(ref _watchCurrent, new GenerationWatchStats());
                    Volatile.Write(ref _watchGenerationId, generationId);
                    Volatile.Write(ref _watchBreachLatch, 0);

                    // Cadence + in-flight bookkeeping (R6.1). The cadence clock marks the
                    // last ABSORPTION: refreshing it on a rollback would add a full floor to
                    // the delay the breach already cost the next admission.
                    var autonomousKeys = requests
                        .Where(r => r.Autonomous is not null)
                        .Select(r => r.Autonomous!.LineageKey)
                        .Where(k => !string.IsNullOrWhiteSpace(k))
                        .Select(k => k!)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    var absorbedAutonomously = intent == SwapIntent.Forward
                        && requests.Any(r => r.Autonomous is not null);
                    var absorbedAtUtc = absorbedAutonomously ? _clock.GetUtcNow() : (DateTimeOffset?)null;
                    lock (_autonomyGate)
                    {
                        if (absorbedAtUtc is { } at)
                            _lastAutonomousSwapUtc = at;
                        _currentLineageKeys = autonomousKeys;
                    }

                    if (intent == SwapIntent.Rollback && restoredFrom is { } origin)
                    {
                        lock (_retained)
                        {
                            _lastRestore = (generationId, origin);
                        }
                    }

                    foreach (var request in requests)
                    {
                        Emit(new BrickSwapProvenanceEvent
                        {
                            Generation = generationId,
                            Outcome = BrickSwapProvenanceOutcomes.BrickLoaded,
                            Timestamp = DateTimeOffset.UtcNow,
                            BrickId = request.BrickId,
                            ContentHash = request.Record.ContentHash,
                            CertificateSignature = request.Record.Signature,
                            ContextName = materialized.Generation.ContextName
                        });
                    }

                    Emit(new BrickSwapProvenanceEvent
                    {
                        Generation = generationId,
                        Outcome = BrickSwapProvenanceOutcomes.SwapCommitted,
                        Timestamp = DateTimeOffset.UtcNow,
                        ContextName = materialized.Generation.ContextName,
                        Reason = restoredFrom is { } from ? $"Restored from retained generation {from}." : null
                    });
                    _logger?.LogInformation(
                        "hot-swap committed generation {Generation} ({Context}) with {Count} brick(s){Restored}",
                        generationId, materialized.Generation.ContextName, requests.Count,
                        restoredFrom is { } fromId ? $" restored from retained generation {fromId}" : "");
                }
                finally
                {
                    // The decision is committed and published; the next decision may proceed
                    // while this generation's predecessor drains.
                    decisionGateHeld = false;
                    _swapDecisionGate.Release();
                }

                var previousCollected = false;
                string? previousName = null;
                if (previous is not null)
                {
                    previousName = previous.ContextName;
                    previousCollected = await RetireAsync(previous).ConfigureAwait(false);
                }

                return new CertifiedBrickSwapResult
                {
                    Swapped = true,
                    GenerationId = generationId,
                    GenerationContextName = materialized.Generation.ContextName,
                    LoadedBrickIds = materialized.Generation.BrickIds.ToArray(),
                    PreviousGenerationCollected = previousCollected,
                    PreviousGenerationContextName = previousName
                };
            }
            finally
            {
                CollectibleLoadContextGate.Instance.Release();
            }
        }
        finally
        {
            if (decisionGateHeld)
                _swapDecisionGate.Release();
        }
    }

    /// <summary>
    /// Executes a certified brick from the currently serving generation. Invocations are
    /// leased so a retiring generation drains before its context is unloaded.
    /// </summary>
    public async Task<BrickOutput> ExecuteAsync(
        string brickId,
        BrickInput input,
        ImplementationType implementation,
        IExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(brickId))
            throw new ArgumentException("Brick id is required.", nameof(brickId));

        // A generation read can race its own retirement; when the lease is refused the
        // freshly published generation is already routable, so re-read and retry.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var generation = Volatile.Read(ref _current)
                ?? throw new InvalidOperationException("No certified brick generation is loaded.");

            if (!generation.TryEnter())
                continue;

            BrickOutput? output = null;
            Exception? brickFault = null;
            DomainBrick? brick = null;
            var startTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                brick = generation.GetBrick(brickId)
                    ?? throw new KeyNotFoundException(
                        $"No certified brick '{brickId}' in generation {generation.Id}.");
                try
                {
                    output = await brick.ExecuteAsync(input, implementation, context, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Host-side cancellation is not brick misbehavior; no watch signal.
                    throw;
                }
                catch (Exception ex)
                {
                    brickFault = ex;
                }
            }
            finally
            {
                generation.Exit();
            }

            // Watch observation runs OUTSIDE the lease so a breach-triggered rollback can
            // drain this generation without deadlocking on its own invocation (R5.2).
            if (_watchThresholds is not null && brick is not null
                && generation.Id == Volatile.Read(ref _watchGenerationId))
            {
                // Captured once, up front: a concurrent forward swap rewrites the stats
                // field mid-observation, and this invocation's signal belongs to the
                // generation that served it, not to whichever one is current by the time
                // the counters are read.
                var current = Volatile.Read(ref _watchCurrent);
                var elapsed = System.Diagnostics.Stopwatch.GetTimestamp() - startTicks;
                var undeclared = output is null ? 0 : CountUndeclaredWrites(brick, output);
                current?.Record(elapsed, brickFault is not null, undeclared);
                var breachReasons = EvaluateWatch(current);
                // The enclosing guard established that THIS generation owns the window, so
                // this is the one place the breaching id is unambiguous; the quarantine
                // resolves its subject by that id, never positionally.
                if (breachReasons is not null)
                    await QuarantineCurrentAsync(breachReasons, generation.Id, cancellationToken).ConfigureAwait(false);
            }

            if (brickFault is not null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(brickFault).Throw();
            return output!;
        }

        throw new InvalidOperationException(
            "Could not lease a serving generation; swaps kept retiring it mid-read.");
    }

    private static int CountUndeclaredWrites(DomainBrick brick, BrickOutput output)
    {
        var declared = brick.Interface?.Outputs;
        if (declared is null || declared.Count == 0)
            return 0; // No declared contract to check — honesty over guessing.

        var declaredNames = new HashSet<string>(declared.Select(o => o.Name), StringComparer.Ordinal);
        return output.ToDictionary().Keys.Count(k => !declaredNames.Contains(k));
    }

    /// <summary>
    /// Whether the current generation's watch window is still in flight for a lineage:
    /// thresholds active, the lineage is among the serving generation's autonomous keys,
    /// no breach yet, and fewer than MinInvocations observed (R6.1).
    /// </summary>
    private bool WatchWindowInFlight(string lineageKey, IReadOnlyList<string> currentLineageKeys)
    {
        var current = Volatile.Read(ref _watchCurrent);
        if (_watchThresholds is null || current is null)
            return false;
        if (!currentLineageKeys.Contains(lineageKey, StringComparer.OrdinalIgnoreCase))
            return false;
        if (Volatile.Read(ref _watchBreachLatch) != 0)
            return false; // Breached windows resolve via quarantine, not by blocking.

        var (invocations, _, _, _, _) = current.Snapshot();
        return invocations < _watchThresholds.MinInvocations;
    }

    /// <summary>
    /// Breach reasons when the watch thresholds are crossed; null otherwise (R5.2).
    /// Judges the stats the caller captured for the generation that served the invocation.
    /// </summary>
    private IReadOnlyList<string>? EvaluateWatch(GenerationWatchStats? current)
    {
        var thresholds = _watchThresholds;
        if (thresholds is null || current is null || Volatile.Read(ref _watchBreachLatch) != 0)
            return null;

        var (invocations, faults, latencyTicks, undeclared, maxLatencyTicks) = current.Snapshot();
        var reasons = new List<string>();

        // Contract conformance is absolute — no baseline needed (R5.2).
        if (undeclared > thresholds.MaxUndeclaredWrites)
            reasons.Add($"{undeclared} undeclared output write(s) exceed the tolerated {thresholds.MaxUndeclaredWrites}");

        // The duration ceiling is absolute too: a first-generation deploy has no baseline
        // for the relative legs, and a pathological single invocation must not hide in a
        // healthy mean.
        if (thresholds.MaxInvocationDuration is { } durationCap && maxLatencyTicks > durationCap.Ticks)
        {
            reasons.Add($"an invocation took {TimeSpan.FromTicks(maxLatencyTicks).TotalMilliseconds:F0}ms, "
                + $"exceeding the absolute ceiling of {durationCap.TotalMilliseconds:F0}ms");
        }

        if (invocations >= thresholds.MinInvocations && Volatile.Read(ref _watchBaseline) is { } baseline)
        {
            var (bInv, bFaults, bLatency, _, _) = baseline.Snapshot();
            if (bInv > 0)
            {
                var errorRate = (double)faults / invocations;
                var baselineRate = (double)bFaults / bInv;
                if (errorRate > baselineRate + thresholds.MaxErrorRateDelta)
                    reasons.Add($"error rate {errorRate:F2} exceeds baseline {baselineRate:F2} by more than {thresholds.MaxErrorRateDelta:F2}");

                var meanLatency = (double)latencyTicks / invocations;
                var baselineMean = (double)bLatency / bInv;
                if (baselineMean > 0 && meanLatency > baselineMean * thresholds.MaxLatencyFactor)
                    reasons.Add($"mean latency is {meanLatency / baselineMean:F1}x the baseline (max {thresholds.MaxLatencyFactor:F1}x)");
            }
        }

        if (reasons.Count == 0)
            return null;

        // One quarantine per generation: first observer wins the latch.
        return Interlocked.CompareExchange(ref _watchBreachLatch, 1, 0) == 0 ? reasons : null;
    }

    /// <summary>
    /// Quarantines the breaching generation after a watch breach (R5.2/R5.3): revokes its
    /// certificate hashes, emits provenance, restores the newest earlier retained
    /// generation whose hashes are unrevoked, and only then records the rollback against
    /// the breacher's lineage (R5.5). The subject is resolved by the id the watch
    /// established, never positionally — a forward swap landing between detection and
    /// quarantine must not get an innocent newcomer's hash revoked or the breacher
    /// restored. With no eligible target, or a restore that does not land, the terminal
    /// state is loud: <see cref="BrickSwapProvenanceOutcomes.RollbackExhausted"/>, an
    /// Error log, and a paused loop. The breach latch is never cleared here: clearing it
    /// to retry would produce a revoke/retry loop.
    /// </summary>
    private async Task QuarantineCurrentAsync(
        IReadOnlyList<string> reasons,
        int breachedGenerationId,
        CancellationToken cancellationToken)
    {
        RetainedGeneration? subject;
        lock (_retained)
        {
            subject = _retained.FirstOrDefault(r => r.GenerationId == breachedGenerationId);
            // A rollback generation is not retained under its own id; it replayed a retained
            // origin whose requests — and content hashes — are the ones to quarantine.
            if (subject is null && _lastRestore is { } restore && restore.GenerationId == breachedGenerationId)
                subject = _retained.FirstOrDefault(r => r.GenerationId == restore.RestoredFrom);
        }

        var reason = "Watch breach: " + string.Join(" | ", reasons);
        _logger?.LogWarning("hot-swap watch breach on generation {Generation}: {Reason}",
            breachedGenerationId, reason);

        if (subject is not null && _revocations is not null)
        {
            foreach (var request in subject.Requests)
            {
                if (!string.IsNullOrWhiteSpace(request.Record.ContentHash))
                    _revocations.Revoke(request.Record.ContentHash!, reason);
            }
        }

        Emit(new BrickSwapProvenanceEvent
        {
            Generation = breachedGenerationId,
            Outcome = BrickSwapProvenanceOutcomes.WatchBreachQuarantined,
            Timestamp = DateTimeOffset.UtcNow,
            Reason = reason
        });

        var target = SelectRollbackTarget(breachedGenerationId);
        if (target is null)
        {
            ExhaustRollback(breachedGenerationId,
                "no earlier retained generation with unrevoked certificate hashes remains; "
                + "the breaching generation is still serving");
            return;
        }

        var result = await RollbackToAsync(target.GenerationId, cancellationToken).ConfigureAwait(false);
        if (!result.Swapped)
        {
            ExhaustRollback(breachedGenerationId,
                $"rollback to retained generation {target.GenerationId} did not land ("
                + string.Join(" | ", result.Refusals.Select(r => $"{r.FailureCode}: {r.Reason}"))
                + "); the breaching generation is still serving");
            return;
        }

        // R5.5 evidence is recorded once per lineage and only for a rollback that happened:
        // recording per request would let a single breach of a two-brick generation reach
        // the demotion threshold, and recording before the restore would let the demotion
        // it caused refuse the very rollback that produced it.
        if (_lineageAuthority is not null && subject is not null)
        {
            var lineageKeys = subject.Requests
                .Select(r => r.Autonomous?.LineageKey)
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .Select(k => k!)
                .Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var lineageKey in lineageKeys)
                _lineageAuthority.RecordRollback(lineageKey);
        }
    }

    /// <summary>
    /// The newest retained generation older than the breacher none of whose certificate
    /// hashes is revoked; null when nothing eligible remains. Skipping revoked targets is
    /// what makes a second breach recoverable at the default retention window.
    /// </summary>
    private RetainedGeneration? SelectRollbackTarget(int breachedGenerationId)
    {
        lock (_retained)
        {
            return _retained
                .Where(r => r.GenerationId < breachedGenerationId)
                .Where(r => r.Requests.All(q => _revocations?.IsRevoked(q.Record.ContentHash ?? "") != true))
                .MaxBy(r => r.GenerationId);
        }
    }

    /// <summary>
    /// The loud terminal state of a breach that could not be contained by rollback. The
    /// breaching generation keeps serving (R5.2 mandates rollback, not refusing to serve),
    /// the loop pauses so no further autonomous change lands on top of it, and the latch
    /// stays set so the same breach is not re-detected into a revoke/retry loop.
    /// </summary>
    private void ExhaustRollback(int breachedGenerationId, string detail)
    {
        Emit(new BrickSwapProvenanceEvent
        {
            Generation = breachedGenerationId,
            Outcome = BrickSwapProvenanceOutcomes.RollbackExhausted,
            Timestamp = DateTimeOffset.UtcNow,
            Reason = detail
        });
        _logger?.LogError(
            "hot-swap rollback exhausted after watch breach on generation {Generation}: {Detail}",
            breachedGenerationId, detail);
        _pauseControl?.Pause($"rollback exhausted after watch breach on generation {breachedGenerationId}");
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // Best-effort teardown at end of host lifetime: no drain, no forced collection —
        // in-flight invocations keep the context alive until they return, and the GC
        // reclaims it once the last reference drops.
        var current = Interlocked.Exchange(ref _current, null);
        if (current is null)
            return;
        _ = current.RetireAndSignal();
        _ = current.DetachAndUnload();
        TryDeleteDirectory(current.TempDirectory);
    }

    /// <summary>
    /// Reactivates a retained generation (autonomy spec R5.1): the retained certificates
    /// and source are re-verified, the retained EMITTED IMAGES load into a fresh context —
    /// no build, no network, no model — and the standard fail-closed swap semantics apply
    /// (a revoked hash in the retained set refuses the rollback; R5.3 outranks R5.1).
    /// A rollback is not an absorption: the pause, cadence floor, in-flight watch window,
    /// lineage demotion and recursion ceiling bound how the runtime takes on CHANGE and
    /// never gate putting back a generation this host already committed — so this is
    /// equally the home of the operator's single-op rollback (R7.2) and of the breach
    /// rollback, whose only difference is who asked.
    /// </summary>
    public async Task<CertifiedBrickSwapResult> RollbackToAsync(
        int generationId,
        CancellationToken cancellationToken = default)
    {
        RetainedGeneration? retained;
        lock (_retained)
        {
            retained = _retained.FirstOrDefault(r => r.GenerationId == generationId);
        }

        if (retained is null)
        {
            return new CertifiedBrickSwapResult
            {
                Swapped = false,
                Refusals = new[]
                {
                    new BrickSwapRefusal
                    {
                        BrickId = "*",
                        Stage = BrickSwapRefusalStage.Request,
                        FailureCode = "no-retained-generation",
                        Reason = $"Generation {generationId} is not in the retention window "
                            + $"({_retentionWindow} generation(s) retained)."
                    }
                }
            };
        }

        var result = await SwapAsync(retained.Requests, SwapIntent.Rollback, restoredFrom: retained.GenerationId, cancellationToken)
            .ConfigureAwait(false);
        if (result.Swapped)
        {
            var pausedNote = _pauseControl?.IsPaused == true
                ? $" The autonomy loop is paused ({_pauseControl.PausedReason}); containment is never gated by the pause (R6.2)."
                : "";
            Emit(new BrickSwapProvenanceEvent
            {
                Generation = result.GenerationId ?? 0,
                Outcome = BrickSwapProvenanceOutcomes.RollbackCommitted,
                Timestamp = DateTimeOffset.UtcNow,
                ContextName = result.GenerationContextName,
                Reason = $"Reactivated retained generation {generationId} from its emitted images.{pausedNote}"
            });
        }
        else
        {
            Emit(new BrickSwapProvenanceEvent
            {
                Generation = generationId,
                Outcome = BrickSwapProvenanceOutcomes.RollbackRefused,
                Timestamp = DateTimeOffset.UtcNow,
                Reason = $"Rollback to retained generation {generationId} did not land: "
                    + string.Join(" | ", result.Refusals.Select(r => $"{r.FailureCode}: {r.Reason}"))
            });
        }

        return result;
    }

    private void RetainCommitted(
        int generationId,
        IReadOnlyList<CertifiedBrickLoadRequest> requests,
        IReadOnlyDictionary<string, byte[]>? emittedImages)
    {
        if (_retentionWindow == 0 || emittedImages is null)
            return;

        var snapshot = requests
            .Select(r => r with
            {
                PrecompiledAssembly = emittedImages.TryGetValue(r.BrickId, out var image) ? image : null,
            })
            .ToArray();
        if (snapshot.Any(r => r.PrecompiledAssembly is null))
            return; // A generation we cannot fully reactivate without a build is not retained.

        lock (_retained)
        {
            _retained.Add(new RetainedGeneration(generationId, snapshot));
            while (_retained.Count > _retentionWindow)
                _retained.RemoveAt(0);
        }
    }

    /// <summary>
    /// Verify-at-load plus the autonomy gates. The verification set (duplicate id, record
    /// mismatch, trust, revocation) applies to every intent; the pacing and authority
    /// gates (pause, cadence floor, in-flight window, lineage demotion, recursion ceiling)
    /// apply only to an absorption, because they bound how fast and on whose authority the
    /// runtime takes on CHANGE, and a rollback replays content this host already committed.
    /// The tier gate is not exempted: it cannot legitimately fire on a replay, so when it
    /// does, retention is corrupt and the honest response is to say so loudly.
    /// </summary>
    private List<BrickSwapRefusal> VerifyAll(IReadOnlyList<CertifiedBrickLoadRequest> requests, SwapIntent intent)
    {
        // One consistent read of the cadence clock and the serving lineage keys; both are
        // written under the same lock in the commit block.
        DateTimeOffset? lastAutonomousSwapUtc;
        IReadOnlyList<string> currentLineageKeys;
        lock (_autonomyGate)
        {
            lastAutonomousSwapUtc = _lastAutonomousSwapUtc;
            currentLineageKeys = _currentLineageKeys;
        }

        var absorbing = intent == SwapIntent.Forward;
        var refusals = new List<BrickSwapRefusal>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var request in requests)
        {
            if (!seen.Add(request.BrickId))
            {
                refusals.Add(new BrickSwapRefusal
                {
                    BrickId = request.BrickId,
                    Stage = BrickSwapRefusalStage.Request,
                    FailureCode = "duplicate-brick-id",
                    Reason = $"Brick '{request.BrickId}' appears more than once in the swap."
                });
                continue;
            }

            if (!string.Equals(request.Record.BrickId, request.BrickId, StringComparison.OrdinalIgnoreCase))
            {
                refusals.Add(new BrickSwapRefusal
                {
                    BrickId = request.BrickId,
                    Stage = BrickSwapRefusalStage.Request,
                    FailureCode = "record-brick-mismatch",
                    Reason = $"Certification record was minted for '{request.Record.BrickId}', not '{request.BrickId}'."
                });
                continue;
            }

            var boundPe = BindPrecompiledAssembly(request);
            var trust = boundPe is not null
                ? CertificationTrustVerifier.Verify(
                    request.Record,
                    request.SourceCode,
                    boundPe,
                    _hmacKey,
                    CertificationVerifyOptions.Strict)
                : CertificationTrustVerifier.Verify(
                    request.Record,
                    request.SourceCode,
                    _hmacKey,
                    CertificationVerifyOptions.Strict);
            if (!trust.Trusted)
            {
                refusals.Add(new BrickSwapRefusal
                {
                    BrickId = request.BrickId,
                    Stage = BrickSwapRefusalStage.Verification,
                    FailureCode = trust.FailureCode,
                    Reason = trust.Reason
                });
                continue;
            }

            // R5.3: quarantine is permanent. A revoked content hash never loads again —
            // rollbacks and bit-identical resubmissions included. Re-earning admission
            // means re-certifying a new candidate, never resurrecting the old hash.
            if (_revocations?.IsRevoked(request.Record.ContentHash ?? "") == true)
            {
                refusals.Add(new BrickSwapRefusal
                {
                    BrickId = request.BrickId,
                    Stage = BrickSwapRefusalStage.Verification,
                    FailureCode = "revoked-hash",
                    Reason = "Certificate content hash is revoked (quarantined): "
                        + (_revocations.TryGetReason(request.Record.ContentHash ?? "") ?? "no reason recorded")
                });
                continue;
            }

            // Autonomy spec R3.2 (swap-host leg) + R4.2 (independent ceiling): when the
            // LOOP drives the swap, only Tier-0 classifications may auto-swap, and the
            // recursion rules are re-checked here regardless of what the certifier did.
            if (request.Autonomous is { } autonomous)
            {
                // R6.2: the global pause halts autonomous swaps immediately. Human-driven
                // swaps (null Autonomous) proceed — pause bounds the LOOP, not the operator —
                // and a rollback proceeds: pause bounds absorption, never containment.
                if (absorbing && _pauseControl?.IsPaused == true)
                {
                    refusals.Add(new BrickSwapRefusal
                    {
                        BrickId = request.BrickId,
                        Stage = BrickSwapRefusalStage.Request,
                        FailureCode = "loop-paused",
                        Reason = $"The autonomy loop is paused: {_pauseControl.PausedReason}"
                    });
                    continue;
                }

                // R6.1: the cadence floor keeps the runtime from absorbing autonomous
                // changes faster than watch windows can clear them. A rollback absorbs
                // nothing; inside the floor it is exactly where a breach rollback lives.
                if (absorbing && _cadenceFloor is { } floor && lastAutonomousSwapUtc is { } last
                    && _clock.GetUtcNow() - last < floor)
                {
                    refusals.Add(new BrickSwapRefusal
                    {
                        BrickId = request.BrickId,
                        Stage = BrickSwapRefusalStage.Request,
                        FailureCode = "cadence-floor",
                        Reason = $"Autonomous swap cadence floor of {floor.TotalSeconds:F0}s has not elapsed "
                            + "since the previous autonomous swap (R6.1)."
                    });
                    continue;
                }

                // R6.1: an in-flight watch window blocks the next autonomous swap of the
                // SAME lineage until the window clears (MinInvocations without breach). An
                // operator rollback of a lineage whose window is still open is not the
                // next swap of that lineage; it is the undoing of the last one.
                if (absorbing && autonomous.LineageKey is { } inFlightKey
                    && WatchWindowInFlight(inFlightKey, currentLineageKeys))
                {
                    refusals.Add(new BrickSwapRefusal
                    {
                        BrickId = request.BrickId,
                        Stage = BrickSwapRefusalStage.Request,
                        FailureCode = "watch-window-in-flight",
                        Reason = $"Lineage '{inFlightKey}' has an in-flight watch window; the next "
                            + "autonomous swap of this lineage waits for it to clear (R6.1)."
                    });
                    continue;
                }

                // R5.5: a lineage demoted on rollback evidence has lost Tier-0 autonomy —
                // its swaps wait for the human gate no matter what tier the objective
                // classified at. Autonomy is lost on evidence, never gained on it. The
                // demotion bounds what the lineage may absorb next; it never refuses the
                // rollback whose evidence produced it.
                if (absorbing && _lineageAuthority is not null
                    && autonomous.LineageKey is { } lineageKey
                    && _lineageAuthority.IsDemoted(lineageKey))
                {
                    refusals.Add(new BrickSwapRefusal
                    {
                        BrickId = request.BrickId,
                        Stage = BrickSwapRefusalStage.Request,
                        FailureCode = "lineage-demoted",
                        Reason = $"Objective lineage '{lineageKey}' lost Tier-0 autonomy after repeated "
                            + "rollback (R5.5); admission now waits for the human gate."
                    });
                    continue;
                }

                // Unconditional across intents. A retained generation carrying a non-Tier-0
                // admission cannot have been committed by this host, so on a rollback this
                // firing means retention is corrupt — named as such, never exempted.
                if (autonomous.Tier != Ashlar.Core.Application.Autonomy.ObjectiveTier.Tier0Autonomous)
                {
                    var tierReason = $"Objective tier {autonomous.Tier} cannot auto-swap; admission waits for "
                        + "the human gate (autonomy spec R3.1).";
                    if (!absorbing)
                    {
                        Emit(new BrickSwapProvenanceEvent
                        {
                            Generation = Volatile.Read(ref _generationCounter) + 1,
                            Outcome = BrickSwapProvenanceOutcomes.RetentionInvariantViolated,
                            Timestamp = DateTimeOffset.UtcNow,
                            BrickId = request.BrickId,
                            ContentHash = request.Record.ContentHash,
                            CertificateSignature = request.Record.Signature,
                            FailureCode = "tier-requires-human-admission",
                            Reason = "A retained generation carries a non-Tier-0 autonomous admission, which this "
                                + "host never commits; retention is corrupt. " + tierReason
                        });
                        _logger?.LogError(
                            "hot-swap retention invariant violated: retained brick {BrickId} carries tier {Tier}; the rollback is refused",
                            request.BrickId, autonomous.Tier);
                    }

                    refusals.Add(new BrickSwapRefusal
                    {
                        BrickId = request.BrickId,
                        Stage = BrickSwapRefusalStage.Request,
                        FailureCode = "tier-requires-human-admission",
                        Reason = tierReason
                    });
                    continue;
                }

                // RecursionDiscipline.ResolveCeiling re-reads ASHLAR_GENERATION_DEPTH_CEILING
                // on every call, so an operator tightening the ceiling mid-process would
                // otherwise strand an already-serving retained generation: absorption only.
                if (!absorbing)
                    continue;

                var lineage = autonomous.Lineage ?? Ashlar.Core.Application.Autonomy.GenerationLineage.HumanAuthored;
                var recursion = Ashlar.Core.Application.Autonomy.RecursionDiscipline.FindViolations(lineage);
                if (recursion.Count > 0)
                {
                    refusals.Add(new BrickSwapRefusal
                    {
                        BrickId = request.BrickId,
                        Stage = BrickSwapRefusalStage.Request,
                        FailureCode = "recursion-refused",
                        Reason = "Recursion discipline failed at the swap host: " + string.Join(" | ", recursion)
                    });
                }
            }
        }

        return refusals;
    }

    private CertifiedBrickSwapResult Refuse(
        int generationId,
        IReadOnlyList<CertifiedBrickLoadRequest> requests,
        IReadOnlyList<BrickSwapRefusal> refusals,
        SwapIntent intent)
    {
        // TryAdd: duplicate brick ids are themselves a refusal cause, so they must not
        // blow up refusal reporting.
        var byBrick = new Dictionary<string, CertifiedBrickLoadRequest>(StringComparer.OrdinalIgnoreCase);
        foreach (var request in requests)
            byBrick.TryAdd(request.BrickId, request);
        foreach (var refusal in refusals)
        {
            byBrick.TryGetValue(refusal.BrickId, out var request);
            Emit(new BrickSwapProvenanceEvent
            {
                Generation = generationId,
                Outcome = BrickSwapProvenanceOutcomes.BrickRefused,
                Timestamp = DateTimeOffset.UtcNow,
                BrickId = refusal.BrickId,
                ContentHash = request?.Record.ContentHash,
                CertificateSignature = request?.Record.Signature,
                FailureCode = refusal.FailureCode,
                Reason = refusal.Reason
            });
        }

        // On a rollback "previous generation keeps serving" would be actively false: the
        // generation still serving is the one the rollback was meant to displace.
        var rollback = intent == SwapIntent.Rollback;
        Emit(new BrickSwapProvenanceEvent
        {
            Generation = generationId,
            Outcome = rollback
                ? BrickSwapProvenanceOutcomes.RollbackRefused
                : BrickSwapProvenanceOutcomes.SwapRefused,
            Timestamp = DateTimeOffset.UtcNow,
            Reason = rollback
                ? $"{refusals.Count} of {requests.Count} brick(s) refused; the ROLLBACK did not land and the breaching generation is still serving."
                : $"{refusals.Count} of {requests.Count} brick(s) refused; previous generation keeps serving."
        });
        _logger?.LogWarning(
            "hot-swap {Kind} refused for would-be generation {Generation}: {Refusals}",
            rollback ? "rollback" : "swap",
            generationId,
            string.Join("; ", refusals.Select(r => $"{r.BrickId}:{r.FailureCode}")));

        return new CertifiedBrickSwapResult { Swapped = false, Refusals = refusals };
    }

    private sealed record MaterializeOutcome(
        BrickGeneration? Generation,
        IReadOnlyList<BrickSwapRefusal> Refusals,
        WeakReference AbortedContextRef,
        string TempDirectory,
        IReadOnlyDictionary<string, byte[]>? EmittedImages = null);

    /// <summary>
    /// Compiles and loads every brick into one new collectible context. On any failure the
    /// half-built context is unloaded inside this frame — nothing context-owned escapes a
    /// refusal — and only a weak handle is returned for collection. NoInlining keeps
    /// context-owned locals out of the caller's frame (the mutation engine's discipline).
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private async Task<MaterializeOutcome> MaterializeAsync(
        int generationId,
        IReadOnlyList<CertifiedBrickLoadRequest> requests,
        CancellationToken cancellationToken)
    {
        var contextName = $"BrickGeneration_{generationId:D4}_{Guid.NewGuid():N}";
        var tempDirectory = Path.Combine(Path.GetTempPath(), "ashlar-hot-swap", contextName);
        Directory.CreateDirectory(tempDirectory);

        var context = new BrickGenerationLoadContext(contextName);
        var bricks = new Dictionary<string, DomainBrick>(StringComparer.OrdinalIgnoreCase);
        var emittedImages = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var refusals = new List<BrickSwapRefusal>();
        var compiler = new RoslynCodeAnalysisService(NullLogger<RoslynCodeAnalysisService>.Instance);

        try
        {
            foreach (var request in requests)
            {
                var assemblyName = $"CertifiedBrick_{generationId:D4}_{Guid.NewGuid():N}";
                var outputPath = Path.Combine(tempDirectory, $"{assemblyName}.dll");
                var references = new List<string>
                {
                    typeof(DomainBrick).Assembly.Location,
                    typeof(BrickInput).Assembly.Location
                };
                references.AddRange(request.AdditionalCompilationReferences);

                // Rollback / autonomy may supply a PE. Load it only when the certificate
                // names those exact bytes. An unbound or mismatched image is rematerialized
                // from wrapped source — never loaded. HMAC-era records without an artifact
                // input take the rematerialize path for the same reason.
                var precompiled = BindPrecompiledAssembly(request);
                if (precompiled is not null)
                {
                    File.WriteAllBytes(outputPath, precompiled);
                }
                else
                {
                    var compile = await compiler.CompileAsync(
                        CandidateSourceWrapper.Wrap(request.SourceCode),
                        assemblyName,
                        outputPath,
                        references,
                        cancellationToken).ConfigureAwait(false);

                    if (!compile.Success || string.IsNullOrWhiteSpace(compile.AssemblyPath) || !File.Exists(compile.AssemblyPath))
                    {
                        refusals.Add(new BrickSwapRefusal
                        {
                            BrickId = request.BrickId,
                            Stage = BrickSwapRefusalStage.Compilation,
                            FailureCode = "compile-failed",
                            Reason = string.Join("; ", compile.Errors.DefaultIfEmpty("no assembly produced"))
                        });
                        continue;
                    }
                }

                byte[] loadedImage;
                try
                {
                    loadedImage = File.ReadAllBytes(outputPath);
                    IlImportFence.Inspect(loadedImage);
                }
                catch (InvalidOperationException ex)
                {
                    refusals.Add(new BrickSwapRefusal
                    {
                        BrickId = request.BrickId,
                        Stage = BrickSwapRefusalStage.Load,
                        FailureCode = "il-import-fence",
                        Reason = ex.Message
                    });
                    continue;
                }

                Assembly assembly;
                try
                {
                    assembly = context.LoadFromAssemblyPath(outputPath);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    refusals.Add(new BrickSwapRefusal
                    {
                        BrickId = request.BrickId,
                        Stage = BrickSwapRefusalStage.Load,
                        FailureCode = "assembly-load-failed",
                        Reason = ex.Message
                    });
                    continue;
                }

                var type = request.BrickTypeName is not null
                    ? assembly.GetType(request.BrickTypeName)
                    : assembly.GetTypes().FirstOrDefault(t =>
                        t.IsClass && !t.IsAbstract && typeof(DomainBrick).IsAssignableFrom(t));
                if (type is null)
                {
                    refusals.Add(new BrickSwapRefusal
                    {
                        BrickId = request.BrickId,
                        Stage = BrickSwapRefusalStage.Load,
                        FailureCode = "brick-type-not-found",
                        Reason = request.BrickTypeName is not null
                            ? $"Type '{request.BrickTypeName}' not found in the compiled assembly."
                            : "No concrete Brick-derived type found in the compiled assembly."
                    });
                    continue;
                }

                DomainBrick brick;
                try
                {
                    brick = (DomainBrick)Activator.CreateInstance(type)!;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    refusals.Add(new BrickSwapRefusal
                    {
                        BrickId = request.BrickId,
                        Stage = BrickSwapRefusalStage.Instantiation,
                        FailureCode = "brick-instantiation-failed",
                        Reason = ex.Message
                    });
                    continue;
                }

                if (!string.Equals(brick.Id, request.BrickId, StringComparison.OrdinalIgnoreCase))
                {
                    refusals.Add(new BrickSwapRefusal
                    {
                        BrickId = request.BrickId,
                        Stage = BrickSwapRefusalStage.Instantiation,
                        FailureCode = "brick-id-mismatch",
                        Reason = $"Instantiated brick declares Id '{brick.Id}', but the certified record is for '{request.BrickId}'."
                    });
                    continue;
                }

                bricks[request.BrickId] = brick;
                // Captured for generation retention (R5.1): rollback reactivates from
                // these exact bytes with no compiler involved.
                emittedImages[request.BrickId] = loadedImage;
            }
        }
        catch (OperationCanceledException)
        {
            bricks.Clear();
            context.Unload();
            return new MaterializeOutcome(
                null,
                new[]
                {
                    new BrickSwapRefusal
                    {
                        BrickId = "*",
                        Stage = BrickSwapRefusalStage.Load,
                        FailureCode = "swap-cancelled",
                        Reason = "The swap was cancelled while materializing the generation."
                    }
                },
                new WeakReference(context),
                tempDirectory);
        }

        if (refusals.Count > 0)
        {
            bricks.Clear();
            context.Unload();
            return new MaterializeOutcome(null, refusals, new WeakReference(context), tempDirectory);
        }

        return new MaterializeOutcome(
            new BrickGeneration(generationId, contextName, tempDirectory, context, bricks),
            Array.Empty<BrickSwapRefusal>(),
            new WeakReference(null),
            tempDirectory,
            emittedImages);
    }

    private async Task<bool> RetireAsync(BrickGeneration previous)
    {
        var drained = previous.RetireAndSignal();
        // Deliberately not tied to the caller's token: once the new generation is
        // published, the old one must be torn down regardless of who cancelled.
        var drainedInTime = await Task.WhenAny(drained, Task.Delay(_drainTimeout, CancellationToken.None))
            .ConfigureAwait(false) == drained;

        var inFlight = previous.InFlightCount;
        var contextRef = previous.DetachAndUnload();
        Emit(new BrickSwapProvenanceEvent
        {
            Generation = previous.Id,
            Outcome = BrickSwapProvenanceOutcomes.GenerationRetired,
            Timestamp = DateTimeOffset.UtcNow,
            ContextName = previous.ContextName,
            Reason = drainedInTime
                ? "Drained; unload requested."
                : $"Drain timeout after {_drainTimeout.TotalSeconds:F0}s with {inFlight} invocation(s) in flight; unload requested anyway."
        });

        await WaitForContextReleaseAsync(contextRef).ConfigureAwait(false);
        var collected = !contextRef.IsAlive;
        Emit(new BrickSwapProvenanceEvent
        {
            Generation = previous.Id,
            Outcome = collected
                ? BrickSwapProvenanceOutcomes.GenerationCollected
                : BrickSwapProvenanceOutcomes.GenerationLeakSuspected,
            Timestamp = DateTimeOffset.UtcNow,
            ContextName = previous.ContextName,
            Reason = collected
                ? null
                : "Load context still reachable after forced collection; find it by name in AssemblyLoadContext.All."
        });
        if (!collected)
        {
            _logger?.LogWarning(
                "hot-swap generation {Generation} load context '{Context}' survived forced collection",
                previous.Id, previous.ContextName);
        }

        TryDeleteDirectory(previous.TempDirectory);
        return collected;
    }

    private void Emit(BrickSwapProvenanceEvent provenanceEvent)
    {
        try
        {
            _provenanceSink?.Record(provenanceEvent);
        }
        catch (Exception ex)
        {
            // A provenance sink failure must never fail a swap.
            _logger?.LogError(ex, "hot-swap provenance sink threw for outcome {Outcome}", provenanceEvent.Outcome);
        }
    }

    /// <summary>Drives collection after Unload, which only requests it: the allocator is
    /// freed once the last reference drops, and that needs a collection. (The mutation
    /// engine still uses a synchronous back-to-back loop; bringing it to parity is a follow-up.)
    /// Bounded retry with a real yield between passes rather than back-to-back passes: the
    /// swap is often reached inline on the thread that just completed the last invocation
    /// of the retiring generation, and that thread's frames still root the finished
    /// invocation's state machine (and with it a brick instance from the old context) until
    /// they unwind. No number of collections frees a stack-rooted object; giving the stack
    /// back does, so the pass after the first yield is the one that normally succeeds.</summary>
    private static async Task WaitForContextReleaseAsync(WeakReference contextRef)
    {
        if (!contextRef.IsAlive)
            return;

        const int maxAttempts = 20;
        for (var attempt = 0; attempt < maxAttempts && contextRef.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            if (contextRef.IsAlive)
                await Task.Delay(TimeSpan.FromMilliseconds(10), CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static void TryDeleteDirectory(string directory)
    {
        // While an assembly in the directory is still mapped the delete silently fails;
        // leftovers are attributable via the context-named directory.
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch
        {
            // best effort
        }
    }

    /// <summary>
    /// Returns the supplied PE only when the certificate names those exact bytes.
    /// Unbound or mismatched images are discarded so Materialize rematerializes from source.
    /// </summary>
    private static byte[]? BindPrecompiledAssembly(CertifiedBrickLoadRequest request)
    {
        var image = request.PrecompiledAssembly;
        if (image is null || image.Length == 0)
            return null;

        var expected = request.Record.Inputs
            .FirstOrDefault(i => string.Equals(i.Kind, CertificationInputKinds.GateEmittedArtifact, StringComparison.Ordinal))
            ?.Hash;
        if (string.IsNullOrWhiteSpace(expected))
            return null;

        var actual = BrickContentHasher.ComputeSha256(image);
        return string.Equals(actual, expected, StringComparison.Ordinal) ? image : null;
    }

    /// <summary>
    /// One immutable generation: its collectible context, its brick instances, and an
    /// invocation lease count so retirement can drain before unload.
    /// </summary>
    private sealed class BrickGeneration
    {
        private readonly Dictionary<string, DomainBrick> _bricks;
        private readonly TaskCompletionSource _drained =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private BrickGenerationLoadContext? _context;
        private int _inFlight;
        private volatile bool _retired;

        public BrickGeneration(
            int id,
            string contextName,
            string tempDirectory,
            BrickGenerationLoadContext context,
            Dictionary<string, DomainBrick> bricks)
        {
            Id = id;
            ContextName = contextName;
            TempDirectory = tempDirectory;
            _context = context;
            _bricks = bricks;
        }

        public int Id { get; }

        public string ContextName { get; }

        public string TempDirectory { get; }

        public int InFlightCount => Volatile.Read(ref _inFlight);

        public IReadOnlyCollection<string> BrickIds => _bricks.Keys.ToArray();

        public DomainBrick? GetBrick(string brickId) =>
            _bricks.TryGetValue(brickId, out var brick) ? brick : null;

        /// <summary>Leases the generation for one invocation; false once retired.</summary>
        public bool TryEnter()
        {
            Interlocked.Increment(ref _inFlight);
            if (_retired)
            {
                Exit();
                return false;
            }

            return true;
        }

        /// <summary>Releases one invocation lease; completes the drain when retired and idle.</summary>
        public void Exit()
        {
            if (Interlocked.Decrement(ref _inFlight) == 0 && _retired)
                _drained.TrySetResult();
        }

        /// <summary>Marks the generation retired and returns a task completing when drained.</summary>
        public Task RetireAndSignal()
        {
            _retired = true;
            if (Volatile.Read(ref _inFlight) == 0)
                _drained.TrySetResult();
            return _drained.Task;
        }

        /// <summary>
        /// Drops every strong reference into the load context (brick instances, the
        /// context itself), requests unload, and returns only a weak handle. After this
        /// returns, nothing in the generation keeps the allocator alive.
        /// </summary>
        public WeakReference DetachAndUnload()
        {
            _bricks.Clear();
            var context = _context;
            _context = null;
            var reference = new WeakReference(context);
            context?.Unload();
            return reference;
        }
    }
}
