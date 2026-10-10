namespace Ashlar.Abstractions.Security.Egress;

/// <summary>
/// Decides whether a request may leave the process for a destination, and records the decision.
/// </summary>
/// <remarks>
/// <para>The guard compares the subject's current label (what it has read) with the destination's label, using
/// <see cref="ReferenceMonitor.CanWrite"/> (no write down). See <see cref="EgressGuard"/> for the rules.</para>
/// <para>Routes must honor <see cref="EgressDecision.Refused"/>. HTTP and governed model routes stop under
/// opt-in enforcement; report mode records decisions without stopping the send.</para>
/// <para>This provides classification-style controls inside the runtime. It is not an accredited cross-domain
/// solution.</para>
/// </remarks>
public interface IEgressGuard
{
    /// <summary>
    /// Decides the request, publishes the decision record, and returns it. Never throws: a failure while deciding
    /// is returned as a record whose <see cref="EgressDecision.Fault"/> is set.
    /// </summary>
    /// <param name="request">What is about to leave, and where to.</param>
    /// <returns>The decision record, including whether the route must refuse.</returns>
    EgressDecision Evaluate(EgressRequest request);
}
