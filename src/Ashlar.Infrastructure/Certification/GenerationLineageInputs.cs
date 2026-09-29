#pragma warning disable ASHLAREXP001 // Projects the experimental lineage contract into certificate evidence by design; see docs/SdkCompatibilityPolicy.md.
using Ashlar.Certification.Contracts;
using Ashlar.Core.Application.Autonomy;

namespace Ashlar.Infrastructure.Certification;

/// <summary>
/// Projects a candidate's recursion pedigree into a certificate input (autonomy spec
/// R4.1/§8 depth laundering): the recorded depth is bound to a hash over the parent
/// certificate signatures, so a later verifier can prove the claimed depth rests on the
/// named lineage — a fresh session cannot re-mint the same artifact at depth 0 without
/// the input chain visibly changing (R5.4 revocation propagation also keys off these).
/// </summary>
internal static class GenerationLineageInputs
{
    /// <summary>Input kind for the recursion pedigree.</summary>
    public const string GenerationDepthKind = "generation-depth";

    /// <summary>Builds the lineage input. Call only with a coherent lineage (validated by the gate).</summary>
    public static CertificationInput From(GenerationLineage lineage)
    {
        if (lineage is null)
            throw new ArgumentNullException(nameof(lineage));

        // The depth is part of the hashed payload alongside the parent chain: the claim
        // and the lineage it rests on are bound into ONE digest, and a depth-0 lineage
        // (empty chain) still hashes deterministically.
        var payload = "depth:" + lineage.Depth.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + "\n" + string.Join("\n", lineage.ParentCertificateSignatures);
        return new CertificationInput
        {
            Kind = GenerationDepthKind,
            Id = lineage.Depth.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Hash = BrickContentHasher.ComputeSha256(payload),
        };
    }
}
