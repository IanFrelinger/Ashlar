namespace Ashlar.Tests.Infrastructure.Certification.Fixtures;

/// <summary>
/// The mutation probe brick with its namespace declaration removed — byte-for-byte the same
/// brick otherwise, so any difference in how the gate treats it comes from the missing
/// namespace and nothing else.
///
/// <para>A brick with no namespace is the first thing a newcomer writes. It is also the shape
/// that used to hand the mutation leg a vacuous pass: the candidate wrapper only injected the
/// deterministic <c>CertAuditContext</c> inside a namespace brace, so every mutant threw at
/// run time, every throw was counted as a kill, and the gate signed <c>escape_rate=0</c> for a
/// leg that proved nothing.</para>
/// </summary>
public static class NoNamespaceProbeBrickSource
{
    /// <summary>The probe brick source with every <c>namespace</c> declaration line removed.</summary>
    public static string Code { get; } = string.Join(
        "\n",
        MutationProbeBrickSource.Code
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Where(line => !line.StartsWith("namespace ", StringComparison.Ordinal)));
}
