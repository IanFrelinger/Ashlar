using Ashlar.Certification.Contracts;
using Ashlar.Core.Application.Certification.Models;
using Ashlar.Infrastructure.Certification;
using NSec.Cryptography;

namespace Ashlar.Tests.Infrastructure.Helpers;

/// <summary>Real signed admission records for CLI adaptation success-path fixtures.</summary>
internal static class AdmittedBrickFixture
{
    /// <summary>
    /// Writes a genuinely signed, admitted record for <c>observation.context</c> where the
    /// CLI will look for it.
    /// </summary>
    /// <remarks>
    /// Signed with the same <see cref="CertificationRecordSigner"/> the CLI uses, so the
    /// record survives the re-verification the store performs on load. Hand-writing the
    /// JSON would not work, and should not: an unsigned or edited record is rejected as
    /// uncertified, which is the property that makes durable certification safe.
    ///
    /// The path mirrors AddAdaptationInfrastructure, which places records beside the
    /// pattern store.
    /// </remarks>
    internal static void SeedObservationContext(string storePath)
    {
        var (privateKey, _) = CreateEd25519Key();
        var signer = new CertificationRecordSigner(ed25519PrivateKeyBase64: privateKey);
        var record = new CertificationRecord
        {
            Status = "PASS",
            Stage = "certification",
            Admitted = true,
            Signed = true,
            Timestamp = DateTimeOffset.UtcNow,
            BrickId = "observation.context",
            ContentHash = "seeded-e2e-fixture",
            EscapeRate = 0d,
            TotalMutants = 0,
            SurvivingMutants = 0,
            SchemaVersion = CertificationRecordData.TrustLoopSchemaVersion,
            Gate = "Phases14CliE2ETests",
            Inputs = new[]
            {
                new CertificationInput { Kind = CertificationInputKinds.GateEmittedArtifact, Id = "observation.context", Hash = "sha256:artifact" },
                new CertificationInput { Kind = CertificationInputKinds.CertifierIdentity, Id = "test-certifier", Hash = "sha256:certifier" }
            }
        };

        var signed = signer.SignRecord(record);

        var store = new FileCertificationRecordStore(
            Path.Combine(storePath, "ashlar-certifications"),
            signer);
        store.Save(signed);
    }

    private static (string PrivateKeyBase64, string PublicKeyBase64) CreateEd25519Key()
    {
        using var key = Key.Create(
            SignatureAlgorithm.Ed25519,
            new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
        return (
            Convert.ToBase64String(key.Export(KeyBlobFormat.RawPrivateKey)),
            Convert.ToBase64String(key.PublicKey.Export(KeyBlobFormat.RawPublicKey)));
    }

}
