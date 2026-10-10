namespace Ashlar.Abstractions.Security;

/// <summary>Read mediation against the live runner identity and immutable operator clearance grants.</summary>
public static class SubjectReadGuard
{
    /// <summary>
    /// Requires the current subject to cover the data label. An absent authority or live subject grants Public only.
    /// Does not observe data: the producer observes admitted data separately. Missing data labels must be supplied
    /// as SystemHigh, never Public. A refusal contains only a reason category and random reference.
    /// </summary>
    public static void RequireRead(SubjectClearanceAuthority? authority, SecurityLabel data)
    {
        var decision = ReferenceMonitor.CanRead(authority?.ResolveCurrent() ?? SecurityLabel.Public, data);
        if (!decision.Allowed)
            throw new ReadRefusedException(decision.Reason);
    }
}
