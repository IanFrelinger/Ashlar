namespace Ashlar.Abstractions.Security;

/// <summary>
/// An agent's clearance and the decision to compose that agent with a sealed skill (SPEC-007 PR 5).
/// </summary>
/// <remarks>
/// <para>The policy file is the ceiling. A host may only tighten it: the effective clearance is the
/// <see cref="SecurityLabel.Meet"/> of the two, which cannot dominate the policy clearance. A missing policy
/// clearance is <see cref="SecurityLabel.Public"/>. A missing host ceiling leaves the policy clearance. An
/// unparseable host ceiling fails closed to <see cref="SecurityLabel.Public"/>.</para>
/// <para>A skill declares its highest level in the sealed package, not in the signed gate record. A missing or
/// unparseable skill level is <see cref="SecurityLabel.SystemHigh"/>, so only a SystemHigh clearance may compose
/// it. Composition is <see cref="ReferenceMonitor.CanRead"/>: the agent may hold the skill only when its
/// clearance dominates that level.</para>
/// <para>This provides classification-style controls inside the runtime. It is not an accredited
/// cross-domain solution.</para>
/// </remarks>
public static class SubjectClearance
{
    /// <summary>
    /// Resolves the clearance an agent actually holds.
    /// </summary>
    /// <param name="policyText">Canonical label text from <c>ashlar.policy.yaml</c>, or null or empty when the
    /// policy names no clearance for this agent.</param>
    /// <param name="hostText">Canonical label text from the host, or null or empty when the host sets no ceiling.
    /// Any other unparseable value fails closed to <see cref="SecurityLabel.Public"/>.</param>
    /// <returns>The policy clearance, tightened by the host when the host names one.</returns>
    public static SecurityLabel Resolve(string? policyText, string? hostText)
    {
        var policy = SecurityLabel.ParseOrPublic(policyText);
        if (hostText is null || hostText.Length == 0)
            return policy;

        if (!SecurityLabel.TryParse(hostText, out var host))
            return SecurityLabel.Public;

        return policy.Meet(host);
    }

    /// <summary>
    /// Whether <paramref name="clearance"/> may be composed with a skill whose highest level is
    /// <paramref name="skillHighestLevel"/>.
    /// </summary>
    /// <param name="clearance">The agent's effective clearance.</param>
    /// <param name="skillHighestLevel">Canonical label text from the sealed package, or null when the package
    /// declares none.</param>
    /// <returns>Allowed when the clearance dominates the skill's level; otherwise an explained refusal.</returns>
    public static AccessDecision MayCompose(SecurityLabel clearance, string? skillHighestLevel)
    {
        SecurityGuard.ThrowIfNull(clearance, nameof(clearance));
        var skill = string.IsNullOrWhiteSpace(skillHighestLevel)
            ? SecurityLabel.SystemHigh
            : SecurityLabel.ParseOrSystemHigh(skillHighestLevel);
        return ReferenceMonitor.CanRead(clearance, skill);
    }
}
