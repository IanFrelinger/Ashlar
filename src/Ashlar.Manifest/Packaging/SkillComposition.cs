using Ashlar.Abstractions.Security;

namespace Ashlar.Manifest.Packaging;

/// <summary>
/// Composes one agent with one sealed skill (SPEC-007 PR 5). The agent's clearance comes from
/// <c>ashlar.policy.yaml</c>, tightened by an optional host ceiling. The skill's highest level comes
/// from the sealed package. A skill above the clearance is refused.
/// </summary>
public static class SkillComposition
{
    /// <summary>
    /// Decides whether <paramref name="agentName"/> may hold <paramref name="package"/>.
    /// </summary>
    /// <param name="policy">The loaded operator policy. Its clearances are the ceiling.</param>
    /// <param name="agentName">The agent being composed. Compared ordinally against
    /// <see cref="PolicyClearances.Agents"/>; any other name uses <see cref="PolicyClearances.Default"/>.</param>
    /// <param name="hostClearance">Canonical label text from the host, or null when the host sets no ceiling.
    /// The host can only tighten the policy clearance.</param>
    /// <param name="package">The sealed skill. <see cref="ExtensionPackage.HighestLevel"/> is its highest level.</param>
    /// <returns>Allowed when the effective clearance dominates the skill's level.</returns>
    public static AccessDecision Decide(
        AshlarPolicy policy,
        string agentName,
        string? hostClearance,
        ExtensionPackage package)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(package);
        if (string.IsNullOrWhiteSpace(agentName))
            throw new ArgumentException("An agent name is required.", nameof(agentName));

        var clearances = policy.Clearances ?? new PolicyClearances();
        string? policyText = null;
        if (clearances.Agents is not null && clearances.Agents.TryGetValue(agentName, out var named))
            policyText = named;
        else if (!string.IsNullOrEmpty(clearances.Default))
            policyText = clearances.Default;

        var clearance = SubjectClearance.Resolve(policyText, hostClearance);
        return SubjectClearance.MayCompose(clearance, package.HighestLevel);
    }
}
