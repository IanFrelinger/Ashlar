namespace Ashlar.Abstractions.Security;

/// <summary>
/// Immutable operator grants, bounded by optional host limits. Build this from trusted policy and
/// host configuration, never from an application manifest, request, or agent-proposed configuration.
/// A subject's clearance is independent of the high-water mark of data it has already read.
/// </summary>
public sealed class SubjectClearanceAuthority
{
    private readonly Dictionary<string, SecurityLabel> _grants;

    /// <summary>
    /// Snapshots canonical policy ceilings and optional host narrowing. Missing subjects receive Public.
    /// An omitted host limit preserves the policy ceiling; an explicit limit is intersected with it.
    /// SystemHigh must be explicitly named in the trusted policy. A host limit cannot create a grant.
    /// Malformed configuration is refused at construction rather than converted to a permissive label.
    /// </summary>
    public SubjectClearanceAuthority(
        IReadOnlyDictionary<string, string?> policyCeilings,
        IReadOnlyDictionary<string, string?>? hostLimits = null)
    {
        SecurityGuard.ThrowIfNull(policyCeilings, nameof(policyCeilings));
        var ceilings = Parse(policyCeilings, nameof(policyCeilings));
        var limits = hostLimits is null ? new Dictionary<string, SecurityLabel>(StringComparer.Ordinal)
            : Parse(hostLimits, nameof(hostLimits));
        _grants = new Dictionary<string, SecurityLabel>(StringComparer.Ordinal);
        foreach (var entry in ceilings)
            _grants.Add(entry.Key, limits.TryGetValue(entry.Key, out var limit)
                ? entry.Value.Meet(limit) : entry.Value);
    }

    /// <summary>Returns the grant for an exact host-authenticated identity; missing or unknown means Public.</summary>
    public SecurityLabel Resolve(string? subjectId) =>
        subjectId is not null && _grants.TryGetValue(subjectId, out var label) ? label : SecurityLabel.Public;

    private static Dictionary<string, SecurityLabel> Parse(IReadOnlyDictionary<string, string?> values, string parameter)
    {
        var result = new Dictionary<string, SecurityLabel>(StringComparer.Ordinal);
        foreach (var entry in values)
        {
            if (string.IsNullOrWhiteSpace(entry.Key) || entry.Key != entry.Key.Trim()
                || !SecurityLabel.TryParse(entry.Value, out var label))
                throw new ArgumentException("Clearances require an exact subject identity and a canonical label.", parameter);
            result.Add(entry.Key, label);
        }
        return result;
    }
}
