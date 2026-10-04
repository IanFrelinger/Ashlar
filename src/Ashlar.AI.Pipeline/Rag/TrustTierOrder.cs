using Ashlar.Abstractions.Security;

namespace Ashlar.AI.Pipeline.Rag;

/// <summary>
/// Orders trust/sensitivity tier names for RAG filtering (lower = less sensitive).
/// Mirrors Ashlar data-sensitivity primitives without referencing BackgroundAgents.
/// </summary>
/// <remarks>
/// <para><b>A caller and a record fail closed in OPPOSITE directions.</b> A name this table does
/// not know -- blank, misspelt, "Unclassified", or a custom level only the BackgroundAgents
/// registry knows -- tells us nothing, and "nothing" must never widen access:</para>
/// <list type="bullet">
/// <item>for a CALLER (a clearance) it ranks at the <see cref="Floor"/>, so an omitted or
/// unrecognised clearance sees only floor data;</item>
/// <item>for a RECORD (a label) it ranks at <see cref="MostRestrictive"/>, so an unlabelled or
/// unrecognised record is visible only to the top clearance.</item>
/// </list>
/// <para>The single <c>Rank</c> this replaces applied one rule to both, and it was wrong
/// in both directions at once: a blank name ranked as Public, so an unlabelled record was served
/// to everyone; an unknown name ranked as TopSecret, so a typo'd caller clearance saw everything.</para>
/// <para><b>Bridge to <see cref="SecurityLabel"/>.</b> <see cref="RecordLabel"/> and
/// <see cref="CallerLabel"/> map a tier onto the SPEC-007 label model, level only, with the same
/// two fallback directions, except that an unlabelled record becomes
/// <see cref="SecurityLabel.SystemHigh"/> rather than TopSecret. Nothing decides through them yet:
/// <see cref="IsAllowed"/>, the RAG search filter and the re-index downgrade check still compare
/// ranks, so the bridge changes no behaviour.</para>
/// </remarks>
public static class TrustTierOrder
{
    /// <summary>The lowest tier: what an omitted or unrecognised caller clearance is treated as.</summary>
    public const string Floor = "Public";

    /// <summary>The highest tier: what an unlabelled or unrecognised record is treated as.</summary>
    public const string MostRestrictive = "TopSecret";

    /// <summary>Audit basis: the caller named a tier this table knows.</summary>
    public const string BasisExplicit = "explicit";

    /// <summary>Audit basis: the caller named no tier, so the floor was applied.</summary>
    public const string BasisOmitted = "omitted";

    /// <summary>Audit basis: the caller named a tier this table does not know, so the floor was applied.</summary>
    public const string BasisUnrecognised = "unrecognised";

    private static readonly string[] CanonicalNames = { "Public", "Internal", "Confidential", "Secret", "TopSecret" };

    private static readonly Dictionary<string, int> Ranks = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Public"] = 0,
        ["Internal"] = 1,
        ["Confidential"] = 2,
        ["Secret"] = 3,
        ["TopSecret"] = 4,
        // The spelling Ashlar.BackgroundAgents.DataSensitivity.DataSensitivityLevels.FromName also
        // accepts. Without it a clearance the registry validated would floor here.
        ["top-secret"] = 4,
    };

    /// <summary>Returns the rank of a known tier name; false for a blank or unknown one.</summary>
    public static bool TryRank(string? tierName, out int rank)
    {
        rank = 0;
        return !string.IsNullOrWhiteSpace(tierName) && Ranks.TryGetValue(tierName.Trim(), out rank);
    }

    /// <summary>Rank of a RECORD label: blank or unknown ranks at <see cref="MostRestrictive"/>.</summary>
    public static int RecordRank(string? recordTier) =>
        TryRank(recordTier, out var rank) ? rank : Ranks[MostRestrictive];

    /// <summary>Rank of a CALLER clearance: blank or unknown ranks at <see cref="Floor"/>.</summary>
    public static int CallerRank(string? callerTier) =>
        TryRank(callerTier, out var rank) ? rank : Ranks[Floor];

    /// <summary>
    /// The <see cref="SecurityLabel"/> of a RECORD tier: a known tier is the label at its level, and a
    /// blank or unknown one is <see cref="SecurityLabel.SystemHigh"/>.
    /// </summary>
    /// <remarks>
    /// <para><b>Resolution goes through <see cref="TryRank"/></b>, so it accepts exactly what
    /// <see cref="RecordRank"/> accepts: trimmed, any case, and <c>top-secret</c>. It never parses label
    /// text: <see cref="SecurityLabel.TryParse"/> takes only exact-case canonical text and would turn
    /// <c>secret</c>, <c>top-secret</c> or <c> Secret </c> into <see cref="SecurityLabel.SystemHigh"/>.</para>
    /// <para><b>The one intended difference from <see cref="RecordRank"/>.</b> An unlabelled or
    /// unrecognised record ranks at <see cref="MostRestrictive"/> there, which a TopSecret caller may read;
    /// here it is <see cref="SecurityLabel.SystemHigh"/>, which only a SystemHigh clearance may read
    /// (<see cref="ReferenceMonitor.CanRead"/>). It is stricter, and it is not wired into
    /// <see cref="IsAllowed"/>, the search filter or the re-index downgrade check, which still use
    /// ranks.</para>
    /// <para><b>Level only.</b> The label carries no compartments and no caveats. A tier name has none to
    /// give, and the flags of the BackgroundAgents data-sensitivity levels (<c>AllowsExternalLLM</c>,
    /// <c>AllowsWebSearch</c>, <c>RequiresLocalOnly</c>, <c>AllowsNetworkExports</c>) are not carried; a
    /// later change decides whether they become caveats.</para>
    /// <para>Never derive a tier name back from <see cref="SecurityLabel.Level"/>: the
    /// <see cref="SecurityLabel.SystemHigh"/> label reports <see cref="SecurityLevel.TopSecret"/> there,
    /// so an unlabelled record would come back as a TopSecret one. Store the tier with
    /// <see cref="NormalizeRecordTier"/>.</para>
    /// </remarks>
    /// <param name="recordTier">The record's stored or proposed tier name; null or blank means unlabelled.</param>
    public static SecurityLabel RecordLabel(string? recordTier) =>
        TryRank(recordTier, out var rank) ? new SecurityLabel((SecurityLevel)rank) : SecurityLabel.SystemHigh;

    /// <summary>
    /// The <see cref="SecurityLabel"/> of a CALLER clearance: a known tier is the label at its level, and
    /// an omitted or unknown one is <see cref="SecurityLabel.Public"/>, as <see cref="CallerRank"/> floors it.
    /// </summary>
    /// <remarks>
    /// <para>Resolution goes through <see cref="TryRank"/> (trimmed, any case, <c>top-secret</c>), never label
    /// parsing, so it agrees with <see cref="CallerRank"/> and <see cref="ResolveCaller"/> on every input.
    /// The result is never <see cref="SecurityLabel.SystemHigh"/>: a SystemHigh clearance reads everything,
    /// so no tier name, known or not, may produce one.</para>
    /// <para>Level only, as for <see cref="RecordLabel"/>: no compartments, no caveats. Never derive a tier
    /// name back from <see cref="SecurityLabel.Level"/>; use <see cref="ResolveCaller"/> for the clearance
    /// applied.</para>
    /// </remarks>
    /// <param name="callerTier">The caller's maximum tier; null, blank or unknown means the floor.</param>
    public static SecurityLabel CallerLabel(string? callerTier) =>
        TryRank(callerTier, out var rank) ? new SecurityLabel((SecurityLevel)rank) : SecurityLabel.Public;

    /// <summary>
    /// The clearance actually applied for <paramref name="callerTier"/>, in canonical spelling, and
    /// why: <see cref="BasisExplicit"/>, <see cref="BasisOmitted"/> or <see cref="BasisUnrecognised"/>.
    /// </summary>
    public static (string Applied, string Basis) ResolveCaller(string? callerTier)
    {
        if (string.IsNullOrWhiteSpace(callerTier))
        {
            return (Floor, BasisOmitted);
        }

        return TryRank(callerTier, out var rank)
            ? (CanonicalNames[rank], BasisExplicit)
            : (Floor, BasisUnrecognised);
    }

    /// <summary>
    /// The label to STORE for <paramref name="recordTier"/>: a known tier in canonical spelling, an
    /// unknown one trimmed but kept (it ranks at <see cref="MostRestrictive"/>), and a blank one as
    /// the empty string -- unlabelled, which also ranks at <see cref="MostRestrictive"/>. No label
    /// is invented for a record nobody classified.
    /// </summary>
    public static string NormalizeRecordTier(string? recordTier)
    {
        if (string.IsNullOrWhiteSpace(recordTier))
        {
            return string.Empty;
        }

        return TryRank(recordTier, out var rank) ? CanonicalNames[rank] : recordTier.Trim();
    }

    /// <summary>True when the record's tier is at or below the caller's max tier.</summary>
    public static bool IsAllowed(string? recordTier, string? callerMaxTier) =>
        RecordRank(recordTier) <= CallerRank(callerMaxTier);

    /// <summary>Rank of a RECORD label. Kept for source compatibility; say which side you mean.</summary>
    [Obsolete("A caller and a record fail closed in opposite directions. Use RecordRank for a label or CallerRank for a clearance.")]
    public static int Rank(string? tierName) => RecordRank(tierName);
}
