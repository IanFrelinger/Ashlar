using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ashlar.Certification.Contracts;
using Ashlar.Core.Application.Autonomy;
using Ashlar.Core.Application.Certification.Models;
using Ashlar.Core.Application.Certification.Ports;
using Ashlar.Core.Domain.Bricks;
using Ashlar.Core.Domain.Execution;
using Ashlar.Infrastructure.Autonomy;
using Ashlar.Infrastructure.Certification;
using Ashlar.Infrastructure.Certification.HotSwap;
using Ashlar.Infrastructure.Certification.Sdk.Extensions;
using Ashlar.Tests.Infrastructure.Certification.Fixtures;
using NSec.Cryptography;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>
/// The evidence archive must never become the admission store, and the composition is where that
/// could happen by accident.
///
/// <para><b>Why this is the most important guard in this change.</b> The convenient wiring —
/// <c>AddCertificationInfrastructure(recordStorePath: evidenceDirectory)</c>, so <c>Save</c> is
/// already on the path with no new code at all — is also the dangerous one. A persisted PASS record
/// carries <c>Admitted: true</c>, <c>Signed: true</c>, <c>Status: PASS</c>, so a
/// <c>FileCertificationRecordStore</c> over the evidence directory would report
/// <c>IsAdmitted == true</c> for a brick the operator is deliberately HOLDING. Persisting evidence
/// would manufacture admission. Nothing about that mis-wiring looks wrong at the call site, and
/// every functional fact in this change would stay green.</para>
///
/// <para><b>These facts run through the REAL composition</b> — <c>AddCertificationGate()</c> plus
/// <c>AddAshlarAutonomy(configuration)</c>, resolved out of a real container and driven by a real
/// iteration — rather than reflecting on a private field. A private-field assertion survives any
/// behavioural break, which is the failure mode this whole change is written against.</para>
///
/// <para>The certification gate is substituted so these facts are about COMPOSITION rather than
/// about certification: the stub mints exactly the record each leg needs, signed by the container's
/// own <c>CertificationRecordSigner</c>. Everything downstream of the gate — the archive, the
/// record store, the harness, the hold — is the shipped wiring.</para>
///
/// <para>No process-global environment writes: the Ed25519 key is a constructor argument on a
/// signer registered ahead of <c>AddCertificationInfrastructure</c>'s <c>TryAddSingleton</c>.</para>
/// </summary>
[Trait("Category", "Certification")]
public sealed class EvidenceArchiveCompositionConventionTests
{
    [Fact]
    public async Task TheEvidenceArchiveIsNeverTheAdmissionRecordStore()
    {
        var archiveRoot = TempRoot();
        using var provider = Compose(archiveRoot, StubMode.Full);

        var store = provider.GetRequiredService<ICertificationRecordStore>();
        store.Should().BeOfType<InMemoryCertificationRecordStore>(
            "configuring an evidence directory must not turn it into the process's admission store");
        provider.GetServices<ICertificationRecordStore>()
            .Should().NotContain(s => s is FileCertificationRecordStore,
                "no file-backed store may be registered under the admission port by the evidence "
                + "wiring; if one is, a brick the operator is holding reads as admitted");

        var result = await RunOneIteration(provider);

        result.Outcome.Should().Be(IterationOutcome.CertifiedButHeld, result.Explanation);
        result.Evidence.Should().NotBeNull("the archive was configured, so a verdict must exist");

        // POSITIVE CONTROL for the two negative assertions that follow. The record really was
        // persisted under this brick id, so "not admitted" is a statement about admission and not
        // about a brick id that never appeared anywhere.
        File.Exists(result.Evidence!.RecordPath).Should().BeTrue(
            "the evidence archive did write this brick's record to disk");
        var brickId = result.Evidence.BrickId;

        store.IsAdmitted(brickId).Should().BeFalse(
            "the operator is holding this brick. A record exists ON DISK in the evidence archive, "
            + "and that must not make the admission store say yes");
        store.Get(brickId).Should().BeNull("nothing wrote this brick into the admission store at all");

        // SECOND POSITIVE CONTROL, on the detector itself: this store's IsAdmitted CAN return true
        // for exactly this record. Without this leg, a broken IsAdmitted that always returned false
        // would satisfy the assertion above and the guard would be worthless.
        store.Save(LastMintedRecord(provider));
        store.IsAdmitted(brickId).Should().BeTrue(
            "the persisted record IS an admitted PASS — which is precisely why routing the archive "
            + "through the admission port would have manufactured admission");
    }

    [Fact]
    public async Task TheComposedHarnessGetsAnArchiveOnlyWhenTheDirectoryIsConfigured()
    {
        using var configured = Compose(TempRoot(), StubMode.Full);
        var withArchive = await RunOneIteration(configured);
        withArchive.Evidence.Should().NotBeNull(
            "Ashlar:Autonomy:EvidenceArchiveDirectory was set, so the composed harness must have "
            + "been handed an archive");

        using var unconfigured = Compose(archiveRoot: null, StubMode.Full);
        var withoutArchive = await RunOneIteration(unconfigured);
        withoutArchive.Evidence.Should().BeNull(
            "no directory was configured, so nothing may be persisted and no verdict may be "
            + "invented — the dial is the only thing that turns this on");
        withoutArchive.Outcome.Should().Be(IterationOutcome.CertifiedButHeld,
            "and the iteration is otherwise unchanged");
    }

    [Fact]
    public async Task TheComposedArchiveVerifiesUnderStrict_NotUnderTheLooserPresets()
    {
        // POSITIVE CONTROL: the full record shape verifies through the composed archive, so each
        // refusal below is attributable to the one clause it removes.
        using var full = Compose(TempRoot(), StubMode.Full);
        var verified = (await RunOneIteration(full)).Evidence!;
        verified.Verified.Should().BeTrue(
            "refused with {0}: {1}", verified.FailureCode, verified.FailureReason);

        // Strict adds RequireCertifierIdentity over Default. A record that does not name its judge
        // verifies under Default and is refused under Strict, so this assertion is about STRICT
        // rather than about strictness in general.
        using var noJudge = Compose(TempRoot(), StubMode.NoCertifierIdentity);
        var judgeless = (await RunOneIteration(noJudge)).Evidence!;
        judgeless.Verified.Should().BeFalse();
        judgeless.FailureCode.Should().Be("certifier-identity-missing",
            "Default would have accepted this record; only Strict requires the certifier identity, "
            + "so a Strict->Default slip in the composition reddens here");

        // And both Strict and Default require an Ed25519 signature, which Legacy does not: this
        // catches a slip all the way down to Legacy, which the clause above would not.
        using var unsigned = Compose(TempRoot(), StubMode.NoEd25519);
        var hmacOnly = (await RunOneIteration(unsigned)).Evidence!;
        hmacOnly.Verified.Should().BeFalse();
        hmacOnly.FailureCode.Should().Be("ed25519-signature-required",
            "Legacy would have accepted an HMAC-only record");
    }

    // --- composition -----------------------------------------------------------------------------

    private enum StubMode
    {
        /// <summary>Everything Strict requires.</summary>
        Full,

        /// <summary>No certifier-identity input: accepted by Default, refused by Strict.</summary>
        NoCertifierIdentity,

        /// <summary>HMAC signature only: accepted by Legacy, refused by Default and Strict.</summary>
        NoEd25519,
    }

    private static ServiceProvider Compose(string? archiveRoot, StubMode mode)
    {
        var (privateKey, _) = CreateEd25519Key();
        var settings = new Dictionary<string, string?>
        {
            ["Ashlar:Autonomy:HoldAdmission"] = "true",
        };
        if (archiveRoot is not null)
            settings["Ashlar:Autonomy:EvidenceArchiveDirectory"] = archiveRoot;

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));

        // Registered BEFORE AddCertificationGate, whose TryAddSingleton then leaves it alone. This
        // is the documented way a host supplies its own signer, and it is how these facts get an
        // Ed25519 key without writing a process-global environment variable.
        services.AddSingleton(new CertificationRecordSigner(ed25519PrivateKeyBase64: privateKey));

        services.AddCertificationGate();
        services.AddAshlarAutonomy(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());

        // AddCertificationInfrastructure registers the real gate with AddSingleton, so this one wins
        // by being last. Everything the facts above are about sits downstream of it.
        services.AddSingleton<ICertificationGate>(sp =>
            new StubGate(sp.GetRequiredService<CertificationRecordSigner>(), mode));

        return services.BuildServiceProvider();
    }

    private static async Task<IterationResult> RunOneIteration(ServiceProvider provider)
    {
        var harness = provider.GetRequiredService<AutonomousIterationHarness>();
        return await harness.RunIterationAsync(
            new ProposalIterationContext
            {
                ObjectiveId = "evidence-archive-composition",
                Source = ObjectiveSource.Triage,
                Touch = new TouchSet
                {
                    PathPrefixes = ["src/Ashlar.Bricks.Probe/"],
                    Namespaces = ["Ashlar.Tests.Infrastructure.Certification.Fixtures"],
                },
            },
            new ProposalCandidate
            {
                Brick = new MutationProbeBrick(),
                SourceCode = MutationProbeBrickSource.Code,
                Witness = new WitnessSpec("mutation-probe-brick", []),
                ProjectPath = CleanProjectFile(),
                CompilationReferences =
                [
                    typeof(DomainBrick).Assembly.Location,
                    typeof(BrickInput).Assembly.Location,
                ],
                BrickTypeName = typeof(MutationProbeBrick).FullName,
            });
    }

    private static CertificationRecord LastMintedRecord(ServiceProvider provider) =>
        ((StubGate)provider.GetRequiredService<ICertificationGate>()).LastRecord!;

    /// <summary>
    /// Admits every candidate with a record of the shape the real gate emits, minus whichever
    /// clause the mode removes. It exists so these facts are about the COMPOSITION downstream of
    /// the gate rather than about the gate's own judgement, which has its own suite.
    /// </summary>
    private sealed class StubGate(CertificationRecordSigner signer, StubMode mode) : ICertificationGate
    {
        public CertificationRecord? LastRecord { get; private set; }

        public Task<CertificationDecision> CertifyAsync(
            CertificationRequest request,
            CancellationToken cancellationToken = default)
        {
            var inputs = new List<CertificationInput>();
            if (mode != StubMode.NoCertifierIdentity)
                inputs.Add(CertifierIdentity.ToInput());
            if (request.EmittedArtifact is { } artifact)
            {
                inputs.Add(new CertificationInput
                {
                    Kind = CertificationInputKinds.GateEmittedArtifact,
                    Id = artifact.BrickTypeName,
                    Hash = artifact.AssemblySha256,
                });
            }

            var record = new CertificationRecord
            {
                Status = "PASS",
                Stage = "S0-S2",
                Admitted = true,
                Signed = true,
                Timestamp = DateTimeOffset.UtcNow,
                BrickId = "evidence-archive-composition",
                ContentHash = BrickContentHasher.ComputeSha256(request.SourceCode!),
                Gate = "evidence-archive-composition-stub",
                SchemaVersion = CertificationRecordData.TrustLoopSchemaVersion,
                Inputs = inputs,
            };

            // The HMAC-only lane is signed directly rather than through the signer, because a signer
            // resolves ASHLAR_CERT_ED25519_KEY from the PROCESS environment: on a machine that has
            // it set, SignRecord would attach a signature and the "Legacy would have accepted this"
            // leg would pass for the wrong reason. Both sides use the same HMAC ladder, so the
            // symmetric half agrees whatever that environment is.
            LastRecord = mode == StubMode.NoEd25519
                ? record with
                {
                    Signature = CertificationRecordSigning.Sign(
                        CertificationRecordMapper.ToData(record), hmacKey: null),
                }
                : signer.SignRecord(record);

            return Task.FromResult(new CertificationDecision { Admitted = true, Record = LastRecord });
        }
    }

    private static string TempRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ashlar-evidence-di-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static string CleanProjectFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ashlar-evidence-di-{Guid.NewGuid():N}.csproj");
        File.WriteAllText(path, """
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <PackageReference Include="Ashlar.Brick.Contracts" Version="0.1.0" />
  </ItemGroup>
</Project>
""");
        return path;
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
