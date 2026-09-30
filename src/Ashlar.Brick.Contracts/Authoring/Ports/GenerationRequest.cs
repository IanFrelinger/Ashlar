namespace Ashlar.Core.Domain.Bricks.Ports;

/// <summary>Grounding and destination for a generative draft.</summary>
public sealed class GenerationRequest
{
    /// <summary>Profile target id (e.g. language/domain key).</summary>
    public string TargetId { get; init; } = "";

    /// <summary>Context/grounding text the drafter may use.</summary>
    public string Grounding { get; init; } = "";

    /// <summary>
    /// Rendered constraint-manifest instructions (spec R3.5, first use). Set by the
    /// engine from the profile's manifest; drafters include it in proposer prompts.
    /// Empty when the profile declares no manifest.
    /// </summary>
    public string ConstraintInstructions { get; init; } = "";

    /// <summary>
    /// Canonical hash of the assembled proposal context (spec R1.4), when stage-1
    /// assembly produced <see cref="Grounding"/>. Feeds the certificate's inputs.
    /// </summary>
    public string? ContextHash { get; init; }

    /// <summary>Optional filesystem or deployment destination.</summary>
    public string? OutputPath { get; init; }

    /// <summary>Opaque tunable overrides from the caller.</summary>
    public IReadOnlyDictionary<string, object> Overrides { get; init; } =
        new Dictionary<string, object>();
}

/// <summary>Domain-neutral generated artifact (content + optional companion files).</summary>
public sealed class GeneratedArtifact
{
    /// <summary>Primary artifact text (source, SQL, script, …).</summary>
    public string Content { get; init; } = "";

    /// <summary>Optional companion files (relative path → content).</summary>
    public IReadOnlyDictionary<string, string> Files { get; init; } =
        new Dictionary<string, string>();

    /// <summary>Provenance attached after drafting/validation.</summary>
    public GenerativeProvenance? Provenance { get; init; }
}
