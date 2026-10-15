using Ashlar.Abstractions.Security.Egress;

namespace Ashlar.Core.Application.Execution.Ports;

/// <summary>
/// Result of a provider availability probe that can carry a typed egress refusal.
/// </summary>
/// <remarks>
/// <see cref="IProviderFactory.IsProviderAvailable"/> remains the boolean status check.
/// Callers that must surface a refused probe's identity use
/// <see cref="IProviderFactory.ProbeProviderAvailability"/> instead.
/// </remarks>
public sealed class ProviderAvailabilityResult
{
    /// <summary>Creates a result that may carry an observed refusal.</summary>
    public ProviderAvailabilityResult(bool available, EgressRefusedException? refusal = null)
    {
        Available = available;
        Refusal = refusal;
    }

    /// <summary>True when the provider is configured and reachable.</summary>
    public bool Available { get; }

    /// <summary>
    /// The first refusal observed while probing, when the probe was stopped by policy.
    /// Null when the provider is unavailable for a non-refusal reason (misconfiguration, transport).
    /// </summary>
    public EgressRefusedException? Refusal { get; }

    /// <summary>True when the probe observed a typed egress refusal.</summary>
    public bool WasRefused => Refusal is not null;
}
