namespace Ashlar.Abstractions.Security.Egress;

/// <summary>
/// Receives every egress decision record once it is subscribed with <see cref="EgressDecisionLog.Subscribe"/>.
/// </summary>
/// <remarks>
/// <see cref="Record"/> runs synchronously on the thread that made the decision, so it must be quick. An
/// exception it throws is swallowed and counted; it never reaches the caller that made the decision.
/// </remarks>
public interface IEgressDecisionSink
{
    /// <summary>Records one decision.</summary>
    /// <param name="decision">The decision record.</param>
    void Record(EgressDecision decision);
}
