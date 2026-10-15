using System.Runtime.CompilerServices;
using Ashlar.Abstractions.Security.Egress;

namespace Ashlar.AI.Pipeline.Governance;

/// <summary>
/// Ensures one <c>denied</c>/<c>egress_refused</c> invocation audit per <see cref="EgressDecision"/>
/// when both <see cref="EgressGuardChatClient"/> and an outer <see cref="AuditingChatClient"/> (router
/// or target) would otherwise record the same refusal. Keyed by decision instance so it survives
/// nested <c>await</c> (AsyncLocal would restore the caller's pre-await context).
/// </summary>
internal static class EgressRefusalAuditDeduper
{
    private static readonly ConditionalWeakTable<EgressDecision, object> Audited = new();
    private static readonly object Marker = new();

    /// <summary>
    /// Returns <see langword="true"/> the first time <paramref name="decision"/> is claimed for an
    /// invocation audit; later calls with the same instance return <see langword="false"/>.
    /// </summary>
    internal static bool ShouldRecord(EgressDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        if (Audited.TryGetValue(decision, out _))
            return false;

        try
        {
            Audited.Add(decision, Marker);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
