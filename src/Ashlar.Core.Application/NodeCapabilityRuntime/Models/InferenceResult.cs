using Ashlar.Core.Application.Execution;

namespace Ashlar.Core.Application.NodeCapabilityRuntime.Models;

/// <summary>
/// Result of a single model inference request.
/// </summary>
public sealed record InferenceResult
{
    /// <summary>Generated text or structured output from the model.</summary>
    public string Output { get; init; } = string.Empty;

    /// <summary>Legacy output count. Consult <see cref="TokenCountSource"/> for provenance and <see cref="Usage"/> for nullable, full-width reported counts.</summary>
    public int TokenCount { get; init; }

    /// <summary>Provenance of <see cref="TokenCount"/>; unknown for backends that do not identify it.</summary>
    public ProviderUsageSource TokenCountSource { get; init; }

    /// <summary>Provider-reported usage, with null for unavailable counts. Local estimates are not substituted here.</summary>
    public ProviderTokenUsage Usage { get; init; } = new();

    /// <summary>Wall-clock time spent on inference.</summary>
    public TimeSpan Duration { get; init; }
}
