using Ashlar.Core.Application.Certification.Models;

namespace Ashlar.Tests.Infrastructure.Certification.Fixtures;

/// <summary>
/// Witness cases shared by every test that certifies the mutation probe brick (and its
/// nondeterministic twin) with a STRONG witness.
///
/// <para>The probe brick guards its first-error lookup with <c>errorCount &gt; 0</c>. When the
/// catalog gained the relational-boundary operator, that guard grew a <c>&gt;=</c> mutant — and a
/// witness whose only log contains two ERROR lines cannot tell <c>&gt; 0</c> from <c>&gt;= 0</c>,
/// so the mutant survived and every "strong witness admits" test in the suite went red. That was
/// the gate doing its job: those witnesses had never exercised the zero-error boundary, so they had
/// never shown they would notice the guard being wrong. The fix is the case below, not a weaker
/// mutant. With no ERROR line the mutated guard indexes an empty list and throws, so the mutant is
/// killed by the very input that proves the witness has teeth at the boundary.</para>
/// </summary>
public static class MutationProbeWitnesses
{
    /// <summary>A log with no ERROR line at all — the zero-error boundary of the probe brick.</summary>
    public const string QuietLog =
        "2024-01-01 INFO Started\n2024-01-01 WARN Retrying\n2024-01-01 INFO Finished";

    /// <summary>
    /// The case every strong probe witness must carry: no errors, so <c>errorCount</c> is 0 and
    /// <c>firstErrorMessage</c> is empty. Distinguishes <c>errorCount &gt; 0</c> from
    /// <c>errorCount &gt;= 0</c>.
    /// </summary>
    public static WitnessCase ZeroErrorCase => new(
        new Dictionary<string, object> { ["logText"] = QuietLog },
        new Dictionary<string, object>
        {
            ["errorCount"] = 0,
            ["firstErrorMessage"] = string.Empty,
        });
}
