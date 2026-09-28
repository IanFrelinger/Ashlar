using Ashlar.Certification.Contracts;
using Ashlar.Infrastructure.Certification;
using Ashlar.Infrastructure.Certification.HotSwap;
using FluentAssertions;
using NSec.Cryptography;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>
/// The hot-swap host's verify-at-load path under a configured pinning set.
///
/// <para><b>Why this fact exists separately.</b> Seven production sites now apply
/// <c>CertificationTrustPolicy.Ambient.Strict</c> in place of the bare <c>Strict</c> preset, and
/// five of them were unfalsifiable: with nothing configured <c>Ambient.Strict</c> IS the
/// <c>CertificationVerifyOptions.Strict</c> instance, reference-identically, so reverting a call
/// site to the preset is invisible to every test that does not configure a policy. A reviewer
/// reverted all five at once and the suite stayed green. <c>CertifiedBrickHotSwapHost</c> is the
/// largest of the five and the one a hot-swap attacker actually reaches: it is the path that
/// decides whether freshly supplied bytes get loaded into the running process.</para>
///
/// <para>Joins <c>hot-swap-host</c> like every other class that commits a generation: a committed
/// swap creates a real <c>AssemblyLoadContext</c> named <c>BrickGeneration_*</c>, and sibling
/// classes assert on the set of those contexts.</para>
/// </summary>
[Trait("Category", "Certification")]
[Collection("hot-swap-host")]
public sealed class PinnedHotSwapVerifyAtLoadTests
{
    private const string HmacKey = "pinned-hot-swap-test-hmac";
    private const string ProbeBrickId = "pinned-hot-swap-probe";

    private const string ProbeBrickSource = """
using Ashlar.Core.Domain.Bricks;
using Ashlar.Core.Domain.Execution;

namespace Ashlar.Tests.PinnedHotSwapProbe;

public sealed class PinnedHotSwapProbeBrick : DomainBrick
{
    public PinnedHotSwapProbeBrick()
    {
        Id = "pinned-hot-swap-probe";
        Name = "Pinned Hot Swap Probe";
        Description = "Marker brick for pinned verify-at-load tests.";
    }

    public override Task<BrickOutput> ExecuteAsync(
        BrickInput input,
        ImplementationType implementation,
        IExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var output = new BrickOutput { Summary = "pinned" };
        output.Set("marker", "pinned");
        return Task.FromResult(output);
    }
}
""";

    /// <summary>
    /// A record signed by a key the operator did not pin is refused at load with the trust
    /// verifier's own failure code, and the generation never commits. The positive control — the
    /// SAME request against a host pinned to the key that actually minted the record — is what
    /// makes the refusal load-bearing: this host refuses for a dozen other reasons, and a
    /// mis-built request would produce a refusal that proves nothing about pinning.
    /// </summary>
    [Fact]
    public async Task A_configured_pinning_set_makes_the_hot_swap_host_refuse_a_foreign_signer()
    {
        var ours = CreateEd25519Key();
        var theirs = CreateEd25519Key();
        var request = RequestSignedBy(theirs);

        using var pinnedElsewhere = HostPinnedTo(ours.PublicKeyBase64);
        var refused = await pinnedElsewhere.SwapAsync(new[] { request });

        refused.Swapped.Should().BeFalse(
            "the certificate is signed by a key this operator did not pin, and verify-at-load "
            + "checks the signature against the key the RECORD carries, so pinning is the only "
            + "thing that can tell the two keys apart");
        refused.Refusals.Should().ContainSingle(r =>
            r.BrickId == ProbeBrickId
            && r.Stage == BrickSwapRefusalStage.Verification
            && r.FailureCode == "ed25519-key-not-trusted");
        pinnedElsewhere.CurrentBrickIds.Should().NotContain(ProbeBrickId);

        using var pinnedToTheSigner = HostPinnedTo(theirs.PublicKeyBase64);
        var landed = await pinnedToTheSigner.SwapAsync(new[] { request });

        landed.Refusals.Should().BeEmpty(Describe(landed));
        landed.Swapped.Should().BeTrue(
            "pinned to the minting key, the identical request must still land — otherwise the "
            + "refusal above could have come from anything this host checks");
        pinnedToTheSigner.CurrentBrickIds.Should().Contain(ProbeBrickId);
    }

    /// <summary>
    /// A host pinned to one signer. The policy comes from <c>FromConfiguration</c> rather than
    /// <c>FromTrustedKeys</c> deliberately: the claim under test is the HOST's wiring, and a fact
    /// about it should redden for a change to that wiring and for nothing else.
    /// </summary>
    private static CertifiedBrickHotSwapHost HostPinnedTo(string publicKeyBase64) =>
        new(
            hmacKey: HmacKey,
            drainTimeout: TimeSpan.FromSeconds(10),
            trustPolicy: CertificationTrustPolicy.FromConfiguration(
                name => name == CertificationTrustPolicy.TrustedKeysVariable ? publicKeyBase64 : null));

    /// <summary>
    /// A load request whose certificate is minted by <paramref name="signer"/>, satisfying every
    /// other <c>Strict</c> clause — trust-loop schema, admitted PASS, content hash over the exact
    /// source, gate-emitted artifact over the exact PE, certifier identity — so the pinning set is
    /// the only free variable.
    /// </summary>
    private static CertifiedBrickLoadRequest RequestSignedBy(
        (string PrivateKeyBase64, string PublicKeyBase64) signer)
    {
        var pe = GateEmittedArtifactCompiler.Compile(
            ProbeBrickSource, BrickCertificationProjectLoader.DefaultCompilationReferences());

        var unsigned = new CertificationRecordData
        {
            Status = "PASS",
            Stage = "S0-S2",
            Admitted = true,
            Signed = true,
            Timestamp = DateTimeOffset.UtcNow,
            BrickId = ProbeBrickId,
            ContentHash = BrickContentHasher.ComputeSha256(ProbeBrickSource),
            Gate = "pinned-hot-swap-test-harness",
            SchemaVersion = CertificationRecordData.TrustLoopSchemaVersion,
            Inputs =
            [
                new CertificationInput
                {
                    Kind = CertificationInputKinds.GateEmittedArtifact,
                    Id = pe.BrickTypeName,
                    Hash = pe.AssemblySha256,
                },
                CertifierIdentity.ToInput(),
            ],
            Ed25519PublicKey = signer.PublicKeyBase64,
        };

        return new CertifiedBrickLoadRequest
        {
            BrickId = ProbeBrickId,
            SourceCode = ProbeBrickSource,
            Record = unsigned with
            {
                Signature = CertificationRecordSigning.Sign(unsigned, HmacKey),
                Ed25519Signature = CertificationRecordEd25519.Sign(
                    unsigned, Convert.FromBase64String(signer.PrivateKeyBase64)),
            },
            PrecompiledAssembly = pe.AssemblyBytes,
        };
    }

    private static string Describe(CertifiedBrickSwapResult result) =>
        result.Swapped
            ? "swapped"
            : string.Join("; ", result.Refusals.Select(r => $"{r.BrickId} {r.Stage} {r.FailureCode}: {r.Reason}"));

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
