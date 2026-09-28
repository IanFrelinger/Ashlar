using Ashlar.Certification.Contracts;
using Ashlar.Core.Application.Certification.Models;
using Ashlar.Core.Application.Certification.Ports;

namespace Ashlar.Infrastructure.Certification.Composition;

/// <summary>Verifies all constituent bricks in a composition are certified and admitted.</summary>
internal static class CompositionConstituentChecker
{
    /// <summary>Check.</summary>
    /// <param name="spec">Composition whose constituents are checked.</param>
    /// <param name="brickCertificationStore">Store holding each constituent's record.</param>
    /// <param name="signer">Signer used to verify each record.</param>
    /// <param name="trustPolicy">
    /// Operator trust configuration supplying the pinned signer set. Defaults to
    /// <see cref="CertificationTrustPolicy.Ambient"/>; with nothing configured this is the
    /// <c>Strict</c> preset exactly as before.
    /// </param>
    public static ConstituentCheckResult Check(
        CompositionSpec spec,
        ICertificationRecordStore brickCertificationStore,
        CertificationRecordSigner signer,
        CertificationTrustPolicy? trustPolicy = null)
    {
        var violations = new List<string>();
        // Falsifiable through the trustPolicy parameter:
        // TrustedKeyPinningConfigurationTests.A_configured_pinning_set_makes_the_composition_constituent_check_refuse_a_foreign_signer
        // reddens if this reverts to the bare preset. Keep the parameter — without it, reverting
        // this line is invisible, because with nothing configured Ambient.Strict IS that preset.
        var verifyOptions = (trustPolicy ?? CertificationTrustPolicy.Ambient).Strict;

        foreach (var node in spec.Nodes)
        {
            var record = brickCertificationStore.Get(node.BrickId);
            if (record is null)
            {
                violations.Add($"Constituent brick '{node.BrickId}' has no certification record");
                continue;
            }

            if (!record.Admitted || !record.Signed || !string.Equals(record.Status, "PASS", StringComparison.OrdinalIgnoreCase))
            {
                violations.Add($"Constituent brick '{node.BrickId}' is not admitted (status={record.Status})");
                continue;
            }

            if (!signer.Verify(record, verifyOptions))
            {
                violations.Add($"Constituent brick '{node.BrickId}' has invalid certification signature");
            }
        }

        return new ConstituentCheckResult(violations.Count == 0, violations);
    }

    internal sealed record ConstituentCheckResult(bool Passed, IReadOnlyList<string> Violations);
}
