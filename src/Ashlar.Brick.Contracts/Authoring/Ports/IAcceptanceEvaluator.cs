namespace Ashlar.Core.Domain.Bricks.Ports;

/// <summary>
/// Post-install verdict. Distinct from the in-loop <c>IPostValidator</c> verdict:
/// a validator answers "does this draft hold together?" during the repair loop,
/// this answers "given what the deployment actually produced, do we keep it?".
/// </summary>
public enum AcceptanceDecision
{
    /// <summary>Commit the deployment.</summary>
    Ship = 0,

    /// <summary>Undo the deployment and restore the prior state.</summary>
    Rollback = 1
}

/// <summary>
/// Already-captured output of a deployment smoke run. The deployment target
/// runs the smoke and hands the captured result in; nothing here performs I/O.
/// </summary>
/// <param name="Ran">False when the target was configured to skip smoke entirely.</param>
/// <param name="Succeeded">The target's own coarse pass/fail for the smoke run.</param>
/// <param name="Detail">Human-readable summary or failure text.</param>
/// <param name="Outputs">
/// Opaque captured payloads keyed by a profile-chosen name (report bodies, logs, …).
/// Contracts assigns no meaning to the keys or the content.
/// </param>
public sealed record DeploymentSmokeResult(
    bool Ran,
    bool Succeeded,
    string? Detail,
    IReadOnlyDictionary<string, string> Outputs)
{
    private static readonly IReadOnlyDictionary<string, string> NoOutputs =
        new Dictionary<string, string>();

    /// <summary>No smoke was configured for this deployment.</summary>
    public static DeploymentSmokeResult NotRun { get; } =
        new(false, false, "smoke not run", NoOutputs);

    /// <summary>A smoke run that the target considers passing.</summary>
    public static DeploymentSmokeResult Pass(
        string? detail,
        IReadOnlyDictionary<string, string>? outputs = null) =>
        new(true, true, detail, outputs ?? NoOutputs);

    /// <summary>A smoke run that the target considers failing.</summary>
    public static DeploymentSmokeResult Fail(
        string? detail,
        IReadOnlyDictionary<string, string>? outputs = null) =>
        new(true, false, detail, outputs ?? NoOutputs);
}

/// <summary>
/// Everything an acceptance evaluator is allowed to see. Fully materialized —
/// an evaluator is a pure function of this value.
/// </summary>
/// <param name="Artifact">The artifact that was deployed.</param>
/// <param name="Provenance">How the artifact was produced and whether it was verified.</param>
/// <param name="Smoke">Captured smoke output from the deployment target.</param>
public sealed record AcceptanceContext(
    GeneratedArtifact Artifact,
    GenerativeProvenance Provenance,
    DeploymentSmokeResult Smoke);

/// <summary>The ship/rollback verdict plus the evidence behind it.</summary>
/// <param name="Decision">Ship or Rollback.</param>
/// <param name="Reason">One-line explanation, surfaced in the deployment result.</param>
/// <param name="Signals">Machine-readable markers the deploy flow may route on.</param>
public sealed record AcceptanceResult(
    AcceptanceDecision Decision,
    string Reason,
    IReadOnlyList<string> Signals)
{
    /// <summary>Builds a Ship verdict.</summary>
    public static AcceptanceResult Ship(string reason, IReadOnlyList<string>? signals = null) =>
        new(AcceptanceDecision.Ship, reason, signals ?? Array.Empty<string>());

    /// <summary>Builds a Rollback verdict.</summary>
    public static AcceptanceResult Rollback(string reason, IReadOnlyList<string>? signals = null) =>
        new(AcceptanceDecision.Rollback, reason, signals ?? Array.Empty<string>());
}

/// <summary>
/// Golden ship/rollback function. Profile-supplied, PURE — no I/O, no process
/// launch, no container. The deployment target captures the evidence; this only
/// decides what to do with it.
/// </summary>
public interface IAcceptanceEvaluator
{
    /// <summary>Decides whether the captured deployment output is acceptable.</summary>
    AcceptanceResult Evaluate(AcceptanceContext context);
}

/// <summary>
/// Conservative built-in used when a profile supplies no evaluator. Rolls back on
/// a failed smoke or unverified provenance, and emits
/// <see cref="HumanReviewSignal"/> when the artifact requires human review so the
/// deploy flow can route it through an approval gate.
/// </summary>
public sealed class DefaultAcceptanceEvaluator : IAcceptanceEvaluator
{
    /// <summary>Signal meaning "route this through the approval gate before shipping".</summary>
    public const string HumanReviewSignal = "requires-human-review";

    /// <summary>Shared stateless instance.</summary>
    public static DefaultAcceptanceEvaluator Instance { get; } = new();

    /// <inheritdoc />
    public AcceptanceResult Evaluate(AcceptanceContext context)
    {
        if (context is null) throw new ArgumentNullException(nameof(context));

        if (context.Smoke.Ran && !context.Smoke.Succeeded)
        {
            return AcceptanceResult.Rollback(
                "smoke failed: " + (context.Smoke.Detail ?? "no detail"),
                new[] { "smoke-failed" });
        }

        if (!context.Provenance.Verified)
        {
            return AcceptanceResult.Rollback(
                "artifact provenance is not verified",
                new[] { "unverified" });
        }

        if (context.Provenance.RequiresHumanReview)
        {
            return AcceptanceResult.Rollback(
                "artifact requires human review before it can ship",
                new[] { HumanReviewSignal });
        }

        return AcceptanceResult.Ship(
            context.Smoke.Ran ? "smoke passed" : "no smoke configured",
            context.Smoke.Ran ? new[] { "smoke-passed" } : new[] { "smoke-not-run" });
    }
}

/// <summary>
/// Opt-in extension of <see cref="IDeploymentTarget"/> for targets that gate the
/// commit on an acceptance verdict. The engine passes the profile's evaluator down
/// so <c>AgentProfile.Acceptance</c> stays the single source of truth; targets that
/// do not implement this are deployed through the plain
/// <see cref="IDeploymentTarget.ApplyAsync"/> entry point unchanged.
/// </summary>
public interface IAcceptanceGatedDeploymentTarget : IDeploymentTarget
{
    /// <summary>
    /// Applies the artifact, evaluating <paramref name="acceptance"/> after the
    /// smoke run and before the deployment is committed.
    /// </summary>
    Task<DeploymentApplyResult> ApplyAsync(
        GeneratedArtifact artifact,
        IAcceptanceEvaluator acceptance,
        CancellationToken cancellationToken = default);
}
