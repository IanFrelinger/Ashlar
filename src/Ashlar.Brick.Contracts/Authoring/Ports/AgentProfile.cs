namespace Ashlar.Core.Domain.Bricks.Ports;

/// <summary>
/// Tunables for generation/repair bounds. Domain-neutral.
/// </summary>
public sealed class GenerationTunables
{
    /// <summary>Max draft→validate cycles.</summary>
    public int MaxRepairAttempts { get; init; } = 3;

    /// <summary>When true, prefer deterministic drafter before the model path.</summary>
    public bool PreferDeterministic { get; init; } = true;

    /// <summary>
    /// When true (default), model-drafted artifacts always require human review
    /// before deployment, regardless of validation outcome. The engine routes
    /// the review through the host's approval gate when one is configured;
    /// without a gate, review-required artifacts are never deployed.
    /// </summary>
    public bool RequireHumanReviewForModelDrafts { get; init; } = true;
}

/// <summary>
/// Capability flags for a profile. Opaque to the fixed engine — profiles set
/// what they support; the engine branches on these flags only.
/// </summary>
public sealed class AgentProfileCapabilities
{
    /// <summary>Profile can produce a draft without a model.</summary>
    public bool SupportsDeterministic { get; init; }

    /// <summary>
    /// Profile supplies a sandbox provider for verification. NOTE: the fixed
    /// engine never invokes the sandbox itself — by convention the profile's own
    /// validators consume <see cref="AgentProfile.Sandbox"/> when they need
    /// isolated execution. This flag is advisory metadata for hosts/schedulers.
    /// </summary>
    public bool SupportsSandbox { get; init; }

    /// <summary>Profile can deploy/install the artifact.</summary>
    public bool SupportsDeployment { get; init; }
}

/// <summary>
/// One registered target profile. Adding a language/domain is registering a
/// new profile — the fixed engine is never edited.
/// </summary>
public sealed class AgentProfile
{
    /// <summary>Stable target id used by <c>GenerativeArtifactBrick</c> input.</summary>
    public string TargetId { get; init; } = "";

    /// <summary>Primary drafter (model or templated).</summary>
    public IArtifactDrafter Drafter { get; init; } = default!;

    /// <summary>Optional deterministic path.</summary>
    public IDeterministicDrafter? DeterministicDrafter { get; init; }

    /// <summary>Post-draft validators (domain-neutral port; typically IPostValidator wrappers).</summary>
    public IReadOnlyList<object> Validators { get; init; } = Array.Empty<object>();

    /// <summary>
    /// Optional sandbox provider. Consumed by the profile's own validators
    /// (the fixed engine does not invoke it directly) — see
    /// <see cref="AgentProfileCapabilities.SupportsSandbox"/>.
    /// </summary>
    public ISandboxProvider? Sandbox { get; init; }

    /// <summary>Optional deployment target.</summary>
    public IDeploymentTarget? Deployment { get; init; }

    /// <summary>
    /// Optional post-install ship/rollback function. Distinct from
    /// <see cref="Validators"/>, which give the in-loop verdict on a draft. When
    /// null the deploy flow uses <see cref="DefaultAcceptanceEvaluator"/>.
    /// </summary>
    public IAcceptanceEvaluator? Acceptance { get; init; }

    /// <summary>
    /// Optional machine-checkable structural constraints for this brick class
    /// (trust-loop spec R3.5). When set, the engine injects
    /// <see cref="BrickConstraintManifest.RenderInstructions"/> into the
    /// <c>GenerationRequest</c> and enforces the same manifest as a pre-gate ahead of
    /// <see cref="Validators"/>. Profiles never add the enforcement validator to
    /// <see cref="Validators"/> themselves.
    /// </summary>
    public BrickConstraintManifest? ConstraintManifest { get; init; }

    /// <summary>Domain knowledge for prompts/rules.</summary>
    public DomainKnowledge Knowledge { get; init; } = new();

    /// <summary>LLM configuration for the agentic path.</summary>
    public LLMConfig Llm { get; init; } = new();

    /// <summary>Repair/generation tunables.</summary>
    public GenerationTunables Tunables { get; init; } = new();

    /// <summary>Capability flags.</summary>
    public AgentProfileCapabilities Capabilities { get; init; } = new();
}
