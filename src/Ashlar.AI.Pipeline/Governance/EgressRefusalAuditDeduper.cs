using System.Threading;

namespace Ashlar.AI.Pipeline.Governance;

/// <summary>
/// Ensures one <c>denied</c>/<c>egress_refused</c> invocation audit per refusal reference when both
/// <see cref="EgressGuardChatClient"/> and an outer <see cref="AuditingChatClient"/> (router or target)
/// would otherwise record the same refusal.
/// </summary>
internal static class EgressRefusalAuditDeduper
{
    private static readonly AsyncLocal<string?> LastAuditedRef = new();

    /// <summary>
    /// Returns <see langword="true"/> the first time <paramref name="refusalRef"/> is seen on this async
    /// flow; later observations of the same reference return <see langword="false"/>.
    /// </summary>
    internal static bool ShouldRecord(string refusalRef)
    {
        if (string.IsNullOrEmpty(refusalRef))
            return true;

        if (string.Equals(LastAuditedRef.Value, refusalRef, StringComparison.Ordinal))
            return false;

        LastAuditedRef.Value = refusalRef;
        return true;
    }

    /// <summary>Test seam: clears the last-seen reference on this async flow.</summary>
    internal static void Reset() => LastAuditedRef.Value = null;
}
