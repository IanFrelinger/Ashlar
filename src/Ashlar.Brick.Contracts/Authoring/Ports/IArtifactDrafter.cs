namespace Ashlar.Core.Domain.Bricks.Ports;

/// <summary>
/// Profile-supplied model/template drafter. Receives prior validation feedback
/// for repair loops.
/// </summary>
public interface IArtifactDrafter
{
    /// <summary>Drafts an artifact, optionally repairing from prior feedback.</summary>
    Task<GeneratedArtifact> DraftAsync(
        GenerationRequest request,
        IReadOnlyList<string> priorFeedback,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Optional deterministic path that avoids a model call when it can produce
/// a complete artifact.
/// </summary>
public interface IDeterministicDrafter
{
    /// <summary>
    /// Attempts a deterministic draft. Returns false when the request is not
    /// suitable for the deterministic path.
    /// </summary>
    bool TryDraft(GenerationRequest request, out GeneratedArtifact? artifact);
}

/// <summary>
/// Builds a sandbox description for verifying an artifact. Lives in contracts
/// as a profile port; the concrete runner is infrastructure.
/// </summary>
public interface ISandboxProvider
{
    /// <summary>
    /// Builds an opaque sandbox description for the artifact
    /// (typically a SandboxSpec instance from the execution ports).
    /// </summary>
    object BuildSpec(GeneratedArtifact artifact, string scratchRoot);
}

/// <summary>Applies a verified artifact to a deployment/install target.</summary>
public interface IDeploymentTarget
{
    /// <summary>Applies the artifact; may snapshot/rollback via managed file set.</summary>
    Task<DeploymentApplyResult> ApplyAsync(
        GeneratedArtifact artifact,
        CancellationToken cancellationToken = default);
}

/// <summary>Result of applying an artifact to a deployment target.</summary>
public sealed class DeploymentApplyResult
{
    /// <summary>True when apply succeeded.</summary>
    public bool Succeeded { get; init; }

    /// <summary>Human-readable detail.</summary>
    public string? Message { get; init; }

    /// <summary>Destination reference (path, image tag, …).</summary>
    public string? Destination { get; init; }
}
