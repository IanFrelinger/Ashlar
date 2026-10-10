using System.Diagnostics.CodeAnalysis;
using Ashlar.Core.Application.Autonomy;

namespace Ashlar.BackgroundAgents.Autonomy;

/// <summary>
/// What one pass of <see cref="AutonomyLoopService.SweepAsync"/> actually did.
///
/// <para><b>Why this exists instead of an <c>int</c>.</b> The sweep used to return the ATTEMPTED
/// count alone and drop its failure count on the floor, so a sweep whose only objective died
/// before reaching any verdict returned 1 and exited 0 — indistinguishable from a sweep that
/// certified something. The only thing standing between that and a recorded PASS was a grep for
/// the words <c>"; continuing the sweep"</c> in the log, and that is exactly what happened in run
/// 34889059104: a missing sandbox image, no iteration, and a row that said PASS. Reword one log
/// template and it happens again.</para>
///
/// <para><b>Attempted counts failures.</b> A failing objective still paid for a proposer call, so
/// it charges the sweep's budget the same as a success — see the comment on
/// <c>MaxObjectivesPerSweep</c> in the loop. So <see cref="Attempted"/> is the total charged and
/// <see cref="Failed"/> is the part of it that reached no verdict; a caller wanting the successes
/// wants <see cref="Verdicts"/>.</para>
/// </summary>
/// <param name="Attempted">Objectives the sweep charged its budget for, failures included.</param>
/// <param name="Failed">Of those, the ones whose iteration threw before reaching any verdict. An
/// infrastructure fault, not a result: a missing container engine, a corrupt artifact, a proposer
/// that is down.</param>
[Experimental(AutonomyExperimental.DiagnosticId, UrlFormat = AutonomyExperimental.UrlFormat)]
public readonly record struct SweepOutcome(int Attempted, int Failed)
{
    /// <summary>Failures specifically caused by egress policy. Included in <see cref="Failed"/>.</summary>
    public int Refused { get; init; }

    /// <summary>Objectives that reached a verdict. Never negative: <see cref="Failed"/> is a
    /// subset of <see cref="Attempted"/> by construction, both being incremented on the same
    /// path.</summary>
    public int Verdicts => Attempted - Failed;

    /// <summary>
    /// True when at least one objective was charged and none of them reached a verdict — the
    /// shape of run 34889059104, and the one a caller must never map to success.
    /// </summary>
    public bool NothingReachedAVerdict => Attempted > 0 && Verdicts == 0;
}
