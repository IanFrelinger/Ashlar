namespace Ashlar.Bricks.GitHub;

/// <summary>Provider-aware evaluation shared by the legacy answer and the evidence report.</summary>
internal static class RequiredCheckEvaluation
{
    internal static bool ReadProviderId(object? value, out long? id)
    {
        id = value switch { int number => number, long number => number, _ => null };
        return value is null or int or long;
    }

    internal static long? PositiveProviderId(object? value) =>
        ReadProviderId(value, out var id) && id is > 0 ? id : null;

    internal sealed record Observation(RequiredCheckRequirement Requirement, IReadOnlyList<CheckFact> Checks,
        IReadOnlyList<StatusFact> Statuses, bool AmbiguousOrigin, bool Complete, string Status,
        bool Absent, bool Pending, bool KnownFailure);

    internal static IReadOnlyList<RequiredCheckRequirement> Requirements(ProtectionFacts protection, string name)
    {
        if (protection.CheckRequirements is { } bindings)
        {
            var matching = bindings.Where(r => r.Name == name).Distinct().ToArray();
            return matching.Length == 0 ? [new(name, null, false)] : matching;
        }
        // Compatibility for callers constructing the older internal facts directly.
        return [new(name, null, protection.RequiredCheckOriginsKnown)];
    }

    internal static bool Complete(CiFacts? ci) => ci is { Checks: { } checks, Statuses: { } statuses, ChecksReadComplete: true }
        && ci.ChecksReported == checks.Count && ci.StatusesReported == statuses.Count;

    internal static bool Pending(string? state) => state?.ToLowerInvariant()
        is "queued" or "in_progress" or "waiting" or "pending" or "requested";

    internal static bool Failed(CheckFact check) => check.Status == "completed" && check.Conclusion is
        "failure" or "timed_out" or "cancelled" or "action_required" or "startup_failure" or "stale";

    internal static bool Matches(RequiredCheckRequirement requirement, CheckFact check) => requirement.OriginKnown
        && requirement.Name == check.Name && (requirement.AppId is null || requirement.AppId == check.AppId);

    internal static bool Matches(RequiredCheckRequirement requirement, StatusFact status) => requirement.OriginKnown
        && requirement.Name == status.Context && requirement.AppId is null;

    internal static bool Ambiguous(RequiredCheckRequirement requirement, CheckFact check) => requirement.Name == check.Name
        && (!requirement.OriginKnown || requirement.AppId is not null && check.AppId is null or <= 0);

    internal static bool Ambiguous(RequiredCheckRequirement requirement, StatusFact status) => requirement.Name == status.Context
        && (!requirement.OriginKnown || requirement.AppId is not null);

    internal static Observation Observe(RequiredCheckRequirement requirement, CiFacts? ci)
    {
        var checks = ci?.Checks?.Where(c => Matches(requirement, c)).ToArray() ?? [];
        var statuses = ci?.Statuses?.Where(s => Matches(requirement, s)).ToArray() ?? [];
        var ambiguous = ci?.Checks?.Any(c => Ambiguous(requirement, c)) == true
            || ci?.Statuses?.Any(s => Ambiguous(requirement, s)) == true;
        var complete = Complete(ci);
        var absent = checks.Length + statuses.Length == 0;
        var failed = checks.Any(Failed) || statuses.Any(s => s.State is "failure" or "error");
        var pending = checks.Any(c => Pending(c.Status)) || statuses.Any(s => s.State == "pending");
        var allPassed = checks.All(c => c.Status == "completed" && c.Conclusion is "success" or "neutral" or "skipped")
            && statuses.All(s => s.State == "success");
        var status = !requirement.OriginKnown ? "unknown" : failed ? "fail"
            : !complete || ambiguous ? "unknown" : absent || pending ? "fail" : allPassed ? "pass" : "unknown";
        return new(requirement, checks, statuses, ambiguous, complete, status, absent, pending, failed);
    }
}
