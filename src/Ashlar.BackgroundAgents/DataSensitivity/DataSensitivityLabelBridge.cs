using System.Diagnostics.CodeAnalysis;
using Ashlar.Abstractions.Security;

namespace Ashlar.BackgroundAgents.DataSensitivity;

/// <summary>
/// SPEC-007 bridge: maps an <see cref="IDataSensitivityLevel"/> onto a <see cref="SecurityLabel"/>, once as a label
/// on data (<see cref="ToDataLabel"/>) and once as a clearance (<see cref="TryToClearance"/>).
/// </summary>
/// <remarks>
/// <para><b>A bridge only.</b> Nothing consults it yet. <see cref="DataSensitivityRegistry.CanAccess"/>,
/// <see cref="DataSensitivityFallbacks"/>, the markers and the RAG filters decide exactly as they did before it
/// existed, and none of them calls this class or <see cref="ReferenceMonitor"/>. A cert-gate parity test holds the
/// two decisions together: for levels whose <see cref="IDataSensitivityLevel.SensitivityValue"/> lies in 0 to 4,
/// <c>registry.CanAccess(agent, data)</c> equals
/// <c>ReferenceMonitor.CanRead(clearance, data.ToDataLabel()).Allowed</c>, and outside that range the bridge is
/// never wider than the registry.</para>
/// <para><b>Keyed on <see cref="IDataSensitivityLevel.SensitivityValue"/> alone</b>, the only thing
/// <see cref="DataSensitivityRegistry.CanAccess"/> compares. The value 0 to 4 is the <see cref="SecurityLevel"/>
/// of the same number. Nothing is derived from <c>Value</c>, <c>Display</c> or <c>Description</c>: a custom level
/// may be named <c>SystemHigh</c> or <c>Top Secret</c>, and it maps by its value like any other. Two levels with
/// the same value map to equal labels, just as the registry treats them as equal.</para>
/// <para><b>Data and clearance fail closed in OPPOSITE directions</b>, as the record and caller sides of the RAG
/// pipeline's <c>TrustTierOrder</c> do, because a value that says nothing must never widen access:</para>
/// <list type="bullet">
/// <item>as <b>data</b>, no level at all is <see cref="SecurityLabel.SystemHigh"/>, which only a SystemHigh
/// clearance may read; a value above 4 is SystemHigh too; a value below 0 is <see cref="SecurityLabel.Public"/>,
/// since raising a data label only narrows who may read it;</item>
/// <item>as a <b>clearance</b>, no level at all floors to <see cref="SecurityLabel.Public"/>; a value above 4 is
/// narrowed to a bare TopSecret label, never SystemHigh, which would read everything; a value below 0 is refused,
/// because there is no label below Public to give it.</item>
/// </list>
/// <para><b>Level only; the flags are not carried.</b> <see cref="IDataSensitivityLevel.AllowsExternalLLM"/>,
/// <see cref="IDataSensitivityLevel.AllowsWebSearch"/>, <see cref="IDataSensitivityLevel.RequiresLocalOnly"/> and
/// <see cref="IDataSensitivityLevel.AllowsNetworkExports"/> have no counterpart in what this bridge produces: every
/// label it returns has no compartments and no caveats, and two levels that differ only in their flags map to equal
/// labels. Whether the flags become caveats (a no-web caveat, say) is for a later change to decide. Until then a
/// consumer that needs a flag reads it from the level, not from the label.</para>
/// <para><b>Resolving a name</b> depends on the role, as the legacy resolvers do. A DATA label name: resolve it with
/// <see cref="IDataSensitivityRegistry.GetByName"/> and pass the result straight in, <see langword="null"/> included,
/// so an unknown name gives no level, which is SystemHigh. A CLEARANCE name: resolve it with
/// <see cref="DataSensitivityFallbacks.ResolveClearance"/> (the registry's floor for an unknown name) and then call
/// <see cref="TryToClearance"/>; when the floor is a custom level below Public the bridge refuses it, so the result is
/// never wider than the legacy rule. Passing <see cref="IDataSensitivityRegistry.GetByName"/>'s
/// <see langword="null"/> for a clearance would floor to Public, which is wider than a floor below Public. Never
/// parse a level name as label text with <see cref="SecurityLabel.TryParse"/> or
/// <see cref="SecurityLabel.ParseOrSystemHigh"/>: they accept only the exact-case canonical spelling, so names the
/// registry accepts, such as <c>secret</c> or <c>top-secret</c>, would become SystemHigh. And do not resolve a data
/// label with <see cref="DataSensitivityFallbacks.ResolveLabel"/> first: its fallback for an unknown name is a
/// real level (TopSecret for the primitives), which maps to a TopSecret label that a TopSecret clearance can read,
/// not to SystemHigh.</para>
/// <para><b>One way only.</b> Never derive a level name back from <see cref="SecurityLabel.Level"/>:
/// <see cref="SecurityLabel.SystemHigh"/> reports TopSecret there.</para>
/// </remarks>
public static class DataSensitivityLabelBridge
{
    private const int LowestValue = (int)SecurityLevel.Public;
    private const int HighestValue = (int)SecurityLevel.TopSecret;

    // The bare label of each level, indexed by its value. Labels are immutable, so the instances are shared.
    private static readonly SecurityLabel[] BareLabels =
    {
        SecurityLabel.Public,
        new(SecurityLevel.Internal),
        new(SecurityLevel.Confidential),
        new(SecurityLevel.Secret),
        new(SecurityLevel.TopSecret),
    };

    /// <summary>
    /// The label of data at <paramref name="level"/>: the bare label (no compartments, no caveats) of the
    /// <see cref="SecurityLevel"/> numbered <see cref="IDataSensitivityLevel.SensitivityValue"/>, failing closed
    /// upward.
    /// </summary>
    /// <param name="level">The data's level, or <see langword="null"/> when the data is unlabelled (or its name did
    /// not resolve).</param>
    /// <returns>
    /// <see cref="SecurityLabel.SystemHigh"/> for no level and for a value above 4; <see cref="SecurityLabel.Public"/>
    /// for a value below 0; otherwise the bare label of that level. Never <see langword="null"/>, never with
    /// compartments or caveats.
    /// </returns>
    public static SecurityLabel ToDataLabel(this IDataSensitivityLevel? level)
    {
        if (level is null)
        {
            return SecurityLabel.SystemHigh;
        }

        // Read once: the decision and the label must come from the same value.
        var value = level.SensitivityValue;
        if (value > HighestValue)
        {
            return SecurityLabel.SystemHigh;
        }

        return value < LowestValue ? SecurityLabel.Public : BareLabels[value];
    }

    /// <summary>
    /// The clearance of a subject (an agent or a caller) allowed up to <paramref name="level"/>: the bare label of
    /// the <see cref="SecurityLevel"/> numbered <see cref="IDataSensitivityLevel.SensitivityValue"/>, failing closed
    /// downward.
    /// </summary>
    /// <param name="level">The subject's maximum level, or <see langword="null"/> when none was given (or its name
    /// did not resolve).</param>
    /// <param name="clearance">
    /// The clearance: <see cref="SecurityLabel.Public"/> for no level; a bare TopSecret label for a value above 4
    /// (narrowed, never <see cref="SecurityLabel.SystemHigh"/>); otherwise the bare label of that level. Never with
    /// compartments or caveats. <see langword="null"/> when the method returns <see langword="false"/>.
    /// </param>
    /// <returns>
    /// <see langword="false"/> only for a value below 0, which no label can express: the subject gets no clearance
    /// at all, and the caller must refuse rather than substitute one.
    /// </returns>
    public static bool TryToClearance(
        this IDataSensitivityLevel? level,
        [NotNullWhen(true)] out SecurityLabel? clearance)
    {
        if (level is null)
        {
            clearance = SecurityLabel.Public;
            return true;
        }

        // Read once: the decision and the label must come from the same value.
        var value = level.SensitivityValue;
        if (value < LowestValue)
        {
            clearance = null;
            return false;
        }

        clearance = BareLabels[value > HighestValue ? HighestValue : value];
        return true;
    }
}
