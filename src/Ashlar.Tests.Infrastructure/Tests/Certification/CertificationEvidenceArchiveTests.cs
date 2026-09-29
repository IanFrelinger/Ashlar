using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Ashlar.Certification.Contracts;
using Ashlar.Core.Application.Certification.Models;
using Ashlar.Infrastructure.Certification;
using Ashlar.Manifest.Signing;
using NSec.Cryptography;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>
/// The evidence archive: a certification record is written to disk, read back FROM THAT FILE, and
/// re-verified under the operator's configured strictness — the mechanism a dogfood ledger row
/// cites when it names a record path and a signer fingerprint instead of a console line.
///
/// <para><b>What these facts are about, said narrowly so nothing quotes them wider.</b> They are
/// about record persistence. None of them says anything about the admission path: the archive runs
/// above the operator hold, nothing here admits or swaps anything, and a verified record is not an
/// admitted brick. <see cref="EvidenceArchiveCompositionConventionTests"/> is what holds that
/// boundary in the shipped composition.</para>
///
/// <para><b>No process-global environment writes.</b> Keys and trust policies arrive as constructor
/// arguments, so this file needs no serialized <c>[Collection]</c> and does not appear in
/// <see cref="ProcessGlobalEnvironmentConventionTests"/>'s inventory.</para>
///
/// <para><b>No timing assertions.</b> Nothing here measures how long a persist or a verify takes,
/// and the archive takes no clock.</para>
/// </summary>
[Trait("Category", "Certification")]
public sealed class CertificationEvidenceArchiveTests
{
    private const string BrickSource = "public sealed class RgbHexParse { public int Answer => 42; }";

    private const string OtherSource = "public sealed class Impostor { public int Answer => 43; }";

    /// <summary>
    /// The exact camelCase names <c>scripts/dogfood-continuous-proof.sh</c> greps out of the
    /// sidecar. THESE LITERALS ARE THE CONTRACT with that script; changing one here without
    /// changing it there makes every sweep's verdict a permanent GAP.
    /// </summary>
    private static readonly string[] SidecarFieldNames =
    [
        "verified",
        "brickId",
        "recordPath",
        "recordSha256",
        "signerFingerprint",
        "pinningEnabled",
        "usesDevHmacKey",
        "failureCode",
        "failureReason",
        "verifiedAtUtc",
    ];

    [Fact]
    public void PersistAndReverify_WritesTheRecordWhereTheStoreKeepsIt_AndHashesThatFilesBytes()
    {
        var root = TempRoot();
        var (privateKey, _) = CreateEd25519Key();
        var signer = new CertificationRecordSigner(ed25519PrivateKeyBase64: privateKey);
        var archive = new CertificationEvidenceArchive(root, signer, CertificationTrustPolicy.Unpinned);

        var result = archive.PersistAndReverify(SignedRecord(signer, "rgb-hex-parse"), BrickSource, ArtifactBytes);

        // The pass itself is the arrange-took-effect assertion for everything below: a refusal here
        // would make the path and hash assertions statements about a fixture nobody verified.
        result.Verified.Should().BeTrue(
            "the fixture record must verify before its file path and hash mean anything; refused with {0}: {1}",
            result.FailureCode, result.FailureReason);

        var expectedPath = FileCertificationRecordStore.RecordPathFor(archive.RecordsDirectory, "rgb-hex-parse");
        result.RecordPath.Should().Be(expectedPath,
            "the archive must cite the file the STORE writes, which is why both go through "
            + "FileCertificationRecordStore.RecordPathFor rather than each spelling {brickId}.json");
        File.Exists(result.RecordPath).Should().BeTrue("a row cites this path; it has to exist");

        var onDisk = File.ReadAllBytes(result.RecordPath);
        onDisk.Should().NotBeEmpty("an empty record file would hash to a constant and prove nothing");
        result.RecordSha256.Should().Be(
            Convert.ToHexString(SHA256.HashData(onDisk)).ToLowerInvariant(),
            "the cited sha256 must be over the FILE's bytes — that is what makes a citation checkable "
            + "by someone who only has the artefact");
        Regex.IsMatch(result.RecordSha256!, "^[0-9a-f]{64}$").Should().BeTrue(
            "SPEC-shaped lowercase hex, so a row's text can be compared by eye; got '{0}'", result.RecordSha256);
    }

    [Fact]
    public void ReverifyFromDisk_RefusesARecordFileThatWasAlteredAfterItVerified()
    {
        // THIS IS THE FACT THAT MAKES "re-read from disk" MEAN SOMETHING. Without it, every other
        // assertion in this file would still pass against an archive that verified the in-memory
        // record it was handed and never opened the file at all.
        var root = TempRoot();
        var (privateKey, _) = CreateEd25519Key();
        var signer = new CertificationRecordSigner(ed25519PrivateKeyBase64: privateKey);
        var archive = new CertificationEvidenceArchive(root, signer, CertificationTrustPolicy.Unpinned);

        var pristine = archive.PersistAndReverify(SignedRecord(signer, "rgb-hex-parse"), BrickSource, ArtifactBytes);

        // POSITIVE CONTROL. The refusal below has to be caused by the alteration, not by a fixture
        // that never verified in the first place.
        pristine.Verified.Should().BeTrue(
            "the unaltered file must verify, or the refusal below says nothing about the alteration; "
            + "refused with {0}: {1}", pristine.FailureCode, pristine.FailureReason);

        var text = File.ReadAllText(pristine.RecordPath);
        text.Should().Contain("\"stage\": \"S0-S2\"",
            "the edit below targets this literal; if the store's serialization changed, the test "
            + "would otherwise silently alter nothing and still 'pass'");
        File.WriteAllText(pristine.RecordPath, text.Replace("\"stage\": \"S0-S2\"", "\"stage\": \"S0-S9\""));

        var altered = archive.ReverifyFromDisk("rgb-hex-parse", BrickSource, ArtifactBytes);

        altered.Verified.Should().BeFalse("the bytes on disk changed under a signature that covers them");
        altered.FailureCode.Should().Be("signature-invalid");
        altered.RecordSha256.Should().NotBe(pristine.RecordSha256,
            "the verdict is computed over the file as it is NOW, so its hash must have moved too");
    }

    [Fact]
    public void ReverifyFromDisk_SeparatesAnAbsentRecordFileFromAnUnreadableOne()
    {
        var root = TempRoot();
        var (privateKey, _) = CreateEd25519Key();
        var signer = new CertificationRecordSigner(ed25519PrivateKeyBase64: privateKey);
        var archive = new CertificationEvidenceArchive(root, signer, CertificationTrustPolicy.Unpinned);

        var pristine = archive.PersistAndReverify(SignedRecord(signer, "rgb-hex-parse"), BrickSource, ArtifactBytes);
        pristine.Verified.Should().BeTrue(
            "positive control: this fixture verifies when its file is intact, so the two refusals "
            + "below are about the file and not about the record; refused with {0}", pristine.FailureCode);

        File.Delete(pristine.RecordPath);
        var absent = archive.ReverifyFromDisk("rgb-hex-parse", BrickSource, ArtifactBytes);
        absent.Verified.Should().BeFalse();
        absent.FailureCode.Should().Be("record-file-missing",
            "absent and refused are different rows in a ledger, and conflating them is exactly what "
            + "produced the pre-#630 false PASS");

        File.WriteAllText(pristine.RecordPath, "{ this is not a certification record");
        var unreadable = archive.ReverifyFromDisk("rgb-hex-parse", BrickSource, ArtifactBytes);
        unreadable.Verified.Should().BeFalse();
        unreadable.FailureCode.Should().Be("record-file-unparseable");
        unreadable.RecordSha256.Should().NotBeNullOrEmpty(
            "the file was read even though it did not parse, so the row can still say which bytes it was");
    }

    [Fact]
    public void PersistAndReverify_RefusesARecordWithNoEd25519Signature()
    {
        // The name used to end "_UnderStrict". It was dropped because it was not true: Default
        // requires an Ed25519 signature too (only Legacy does not), so this fact passes unchanged
        // when the archive is downgraded from Strict to Default - measured. What it actually pins
        // is the floor below Default, which is worth having under its own honest name.
        var root = TempRoot();

        // POSITIVE CONTROL first: the same record shape, signed with an Ed25519 key, verifies. So
        // the refusal below is attributable to the missing signature and to nothing else about the
        // fixture.
        var (privateKey, _) = CreateEd25519Key();
        var withKey = new CertificationRecordSigner(ed25519PrivateKeyBase64: privateKey);
        var signedArchive = new CertificationEvidenceArchive(
            Path.Combine(root, "signed"), withKey, CertificationTrustPolicy.Unpinned);
        signedArchive.PersistAndReverify(SignedRecord(withKey, "rgb-hex-parse"), BrickSource, ArtifactBytes)
            .Verified.Should().BeTrue("an Ed25519-signed record of this shape verifies under Strict");

        // HMAC-signed and nothing else. Built by signing the canonical payload directly rather than
        // through a CertificationRecordSigner, because a signer resolves ASHLAR_CERT_ED25519_KEY
        // from the PROCESS environment — a machine that happens to have it set would otherwise turn
        // this fixture into a signed record and make the fact pass for the wrong reason. Both sides
        // use the same key ladder (null → env → committed constant), so the HMAC half agrees
        // whatever that environment is, and only the Ed25519 half is under test.
        var archive = new CertificationEvidenceArchive(
            Path.Combine(root, "unsigned"), new CertificationRecordSigner(), CertificationTrustPolicy.Unpinned);

        var unsigned = UnsignedShape("rgb-hex-parse");
        var record = unsigned with
        {
            Signature = CertificationRecordSigning.Sign(CertificationRecordMapper.ToData(unsigned), hmacKey: null),
        };
        record.Ed25519Signature.Should().BeNull(
            "arrange check: this record must carry no Ed25519 signature, or the refusal below would "
            + "be about something else entirely");

        var result = archive.PersistAndReverify(record, BrickSource, ArtifactBytes);

        result.Verified.Should().BeFalse();
        result.FailureCode.Should().Be("ed25519-signature-required");
        result.SignerFingerprint.Should().BeNull("there is no key to fingerprint, and absent is never guessed at");
    }

    [Fact]
    public void PersistAndReverify_ReportsTheFingerprintOfTheKeyThatSignedTheRecord()
    {
        var root = TempRoot();
        var (privateKey, publicKey) = CreateEd25519Key();
        var signer = new CertificationRecordSigner(ed25519PrivateKeyBase64: privateKey);
        var archive = new CertificationEvidenceArchive(root, signer, CertificationTrustPolicy.Unpinned);

        var result = archive.PersistAndReverify(SignedRecord(signer, "rgb-hex-parse"), BrickSource, ArtifactBytes);

        // Asserted against the REAL SPEC-006 §3 implementation in Ashlar.Manifest, not against a
        // restatement of its format. CertificationSignerFingerprint exists only because
        // Ashlar.Manifest is not in the sweep binary's closure; this is what keeps the second
        // declaration honest, and it is why the two cannot drift.
        result.SignerFingerprint.Should().NotBeNull(
            "a ledger row cites this string; null would make the comparison below vacuous");
        result.SignerFingerprint.Should().Be(OperatorKey.Fingerprint(Convert.FromBase64String(publicKey)));
    }

    [Fact]
    public void PersistAndReverify_ReportsPinning_AndRefusesAKeyTheOperatorDidNotTrust()
    {
        var root = TempRoot();
        var (privateKey, publicKey) = CreateEd25519Key();
        var (_, foreignPublicKey) = CreateEd25519Key();
        var signer = new CertificationRecordSigner(ed25519PrivateKeyBase64: privateKey);

        // Unpinned: verifies, and SAYS it was unpinned — so a row built from this cannot imply
        // custody it does not have.
        var unpinned = new CertificationEvidenceArchive(
            Path.Combine(root, "unpinned"), signer, CertificationTrustPolicy.Unpinned)
            .PersistAndReverify(SignedRecord(signer, "rgb-hex-parse"), BrickSource, ArtifactBytes);
        unpinned.Verified.Should().BeTrue("refused with {0}: {1}", unpinned.FailureCode, unpinned.FailureReason);
        unpinned.PinningEnabled.Should().BeFalse(
            "nothing pins this key, so this record verifies exactly as one signed by any other "
            + "keypair would");

        // Pinned to the key that actually signed: still verifies. Without this leg, "pinning
        // refuses" would be satisfied by a pinning implementation that refuses everything.
        var pinnedToSigner = new CertificationEvidenceArchive(
            Path.Combine(root, "pinned-right"), signer, CertificationTrustPolicy.FromTrustedKeys([publicKey]))
            .PersistAndReverify(SignedRecord(signer, "rgb-hex-parse"), BrickSource, ArtifactBytes);
        pinnedToSigner.Verified.Should().BeTrue(
            "the operator pinned exactly this signer; refused with {0}: {1}",
            pinnedToSigner.FailureCode, pinnedToSigner.FailureReason);
        pinnedToSigner.PinningEnabled.Should().BeTrue();

        // Pinned to somebody else: refused by name. This leg is 3b's half, already built and proven
        // — turning pinning on for the sweep needs no second edit to the archive.
        var pinnedElsewhere = new CertificationEvidenceArchive(
            Path.Combine(root, "pinned-wrong"), signer, CertificationTrustPolicy.FromTrustedKeys([foreignPublicKey]))
            .PersistAndReverify(SignedRecord(signer, "rgb-hex-parse"), BrickSource, ArtifactBytes);
        pinnedElsewhere.Verified.Should().BeFalse();
        pinnedElsewhere.FailureCode.Should().Be("ed25519-key-not-trusted");
        pinnedElsewhere.PinningEnabled.Should().BeTrue();
    }

    [Fact]
    public void PersistAndReverify_ReportsWhetherTheCommittedDevHmacKeyIsInUse()
    {
        var root = TempRoot();
        var (privateKey, _) = CreateEd25519Key();

        var devKeySigner = new CertificationRecordSigner(ed25519PrivateKeyBase64: privateKey);
        var onDevKey = new CertificationEvidenceArchive(
            Path.Combine(root, "dev"), devKeySigner, CertificationTrustPolicy.Unpinned)
            .PersistAndReverify(SignedRecord(devKeySigner, "rgb-hex-parse"), BrickSource, ArtifactBytes);
        onDevKey.Verified.Should().BeTrue("refused with {0}: {1}", onDevKey.FailureCode, onDevKey.FailureReason);
        onDevKey.UsesDevHmacKey.Should().BeTrue(
            "no explicit HMAC key was supplied, so the symmetric half is forgeable by anyone with "
            + "the source — and a row built from this result has to say so");

        var realKeySigner = new CertificationRecordSigner(
            hmacKey: "an-operator-held-hmac-key", ed25519PrivateKeyBase64: privateKey);
        var onRealKey = new CertificationEvidenceArchive(
            Path.Combine(root, "real"), realKeySigner, CertificationTrustPolicy.Unpinned)
            .PersistAndReverify(SignedRecord(realKeySigner, "rgb-hex-parse"), BrickSource, ArtifactBytes);
        onRealKey.UsesDevHmacKey.Should().BeFalse("an explicit key was supplied");

        // And the honest consequence, which is the reachable half of the lane-disagreement code:
        // the host's signer holds a key the CONSUMER verifier's ladder cannot reach, because
        // nothing extracts a key from a signer. That is reported as a key-routing fault by its own
        // name, not as a bad signature and not as a pass.
        onRealKey.Verified.Should().BeFalse();
        onRealKey.FailureCode.Should().Be("hmac-key-lane-disagreement");
    }

    [Fact]
    public void ReverifyFromDisk_RefusesARecordThatDoesNotBindTheCandidateSource()
    {
        var root = TempRoot();
        var (privateKey, _) = CreateEd25519Key();
        var signer = new CertificationRecordSigner(ed25519PrivateKeyBase64: privateKey);
        var archive = new CertificationEvidenceArchive(root, signer, CertificationTrustPolicy.Unpinned);

        var bound = archive.PersistAndReverify(SignedRecord(signer, "rgb-hex-parse"), BrickSource, ArtifactBytes);
        bound.Verified.Should().BeTrue(
            "positive control: the record binds THIS source; refused with {0}", bound.FailureCode);

        var unbound = archive.ReverifyFromDisk("rgb-hex-parse", OtherSource, ArtifactBytes);

        unbound.Verified.Should().BeFalse(
            "a signature says the record was not edited; only the content binding says the record "
            + "is about the candidate in front of us");
        unbound.FailureCode.Should().Be("content-hash-mismatch");
    }

    [Fact]
    public void PersistAndReverify_WritesASidecarWithTheFieldNamesTheSweepScriptParses()
    {
        var root = TempRoot();
        var (privateKey, _) = CreateEd25519Key();
        var signer = new CertificationRecordSigner(ed25519PrivateKeyBase64: privateKey);
        var archive = new CertificationEvidenceArchive(root, signer, CertificationTrustPolicy.Unpinned);

        archive.PersistAndReverify(SignedRecord(signer, "rgb-hex-parse"), BrickSource, ArtifactBytes)
            .Verified.Should().BeTrue("the sidecar's `verified` field must be able to be true at all");

        var sidecar = Path.Combine(root, "rgb-hex-parse.evidence.json");
        File.Exists(sidecar).Should().BeTrue("the ledger classifier reads this file and nothing else");

        var text = File.ReadAllText(sidecar);
        using (var parsed = JsonDocument.Parse(text))
        {
            parsed.RootElement.ValueKind.Should().Be(JsonValueKind.Object,
                "the classifier greps this file; a non-object would still grep");
        }

        foreach (var field in SidecarFieldNames)
        {
            text.Should().Contain($"\"{field}\":",
                "scripts/dogfood-continuous-proof.sh parses '{0}' out of this file by name. Renaming "
                + "it here makes every sweep's verdict a permanent GAP, silently.", field);
        }

        // The literal the classifier's happy path matches, exactly as it will appear.
        text.Should().Contain("\"verified\": true");

        // A control on the control: assert something that must NOT be there, so a test that would
        // pass against a file containing every plausible spelling fails instead.
        text.Should().NotContain("\"isVerified\"",
            "only the frozen names above are the contract; a second spelling means the rename "
            + "already happened and something is reading the wrong one");
    }

    [Fact]
    public void PersistAndReverify_WritesTheFAILUREShapeTheSweepScriptParses()
    {
        // The happy half of the sidecar contract had a fact; this half did not, and this is the
        // half that decides what an operator is TOLD when something is wrong.
        // scripts/dogfood-continuous-proof.sh reads `failureCode` and `failureReason` out of this
        // file to build the GAP reason, and its `_evidence_string` returns empty for an unquoted
        // null - so a refusal serialised with nulls degrades the row to "see the attached log",
        // which is the exact outcome the workflow says it exists to avoid. Nothing proved the
        // archive ever emits this shape, because every sidecar fact ran on a verified result.
        var root = TempRoot();
        var archive = new CertificationEvidenceArchive(
            root, new CertificationRecordSigner(), CertificationTrustPolicy.Unpinned);

        var refused = archive.PersistAndReverify(
            new CertificationRecordSigner().SignRecord(UnsignedShape("rgb-hex-parse")),
            BrickSource, ArtifactBytes);

        refused.Verified.Should().BeFalse(
            "POSITIVE CONTROL: this fixture must actually be refused, or the assertions below are "
            + "about a verified sidecar wearing the wrong name");

        var text = File.ReadAllText(Path.Combine(root, "rgb-hex-parse.evidence.json"));

        text.Should().Contain("\"verified\": false",
            "the classifier matches this literal to decide a run produced a refusal rather than a pass");

        // QUOTED, not null. The shell reads these with a string extractor; a null serialises
        // unquoted and reads back as empty, which is how a real refusal turns into a row that
        // cannot say what went wrong.
        text.Should().MatchRegex("\"failureCode\":\\s*\"[^\"]+\"",
            "an unquoted null here degrades the ledger's GAP reason to nothing");
        text.Should().MatchRegex("\"failureReason\":\\s*\"[^\"]+\"",
            "an unquoted null here degrades the ledger's GAP reason to nothing");
    }

    [Fact]
    public void PersistAndReverify_RefusesARecordBindingNoGateEmittedArtifact_WhenArtifactBytesAreSupplied()
    {
        // The shape the real gate mints for an identity-handle probe or an incomplete proposal: the
        // gate binds the gate-emitted-artifact input ONLY when the candidate emitted one, and
        // refuses nothing when it did not. This fact pins what the archive does with such a record.
        //
        // WHAT THIS DOES NOT PROVE, stated because the first draft of it claimed otherwise and was
        // wrong. This refusal is NOT evidence that the archive verifies under Strict. Two sites in
        // CertificationTrustVerifier emit "gate-emitted-artifact-missing": one gated on
        // strictness.RequireGateEmittedArtifact, and one in the four-argument overload that fires
        // whenever a consumer supplies artifact bytes and the record binds no artifact hash,
        // whatever the strictness. The archive always supplies bytes, so it is the SECOND that
        // refuses here - and this fact passes unchanged when the archive is downgraded to Default.
        // Measured, not assumed.
        //
        // For the archive, the only clause that actually separates Strict from Default is
        // RequireCertifierIdentity, and the fact holding that line is
        // EvidenceArchiveCompositionConventionTests
        //   .TheComposedArchiveVerifiesUnderStrict_NotUnderTheLooserPresets,
        // which does redden on that downgrade. Do not put "under Strict" in this name.
        var root = TempRoot();
        var (privateKey, _) = CreateEd25519Key();
        var signer = new CertificationRecordSigner(ed25519PrivateKeyBase64: privateKey);

        // POSITIVE CONTROL: same key, same archive, same everything except the bound artifact.
        var bound = new CertificationEvidenceArchive(
            Path.Combine(root, "bound"), signer, CertificationTrustPolicy.Unpinned);
        bound.PersistAndReverify(SignedRecord(signer, "rgb-hex-parse"), BrickSource, ArtifactBytes)
            .Verified.Should().BeTrue("the only difference below is the missing artifact input");

        var archive = new CertificationEvidenceArchive(
            Path.Combine(root, "unbound"), signer, CertificationTrustPolicy.Unpinned);
        var result = archive.PersistAndReverify(
            SignedRecord(signer, "rgb-hex-parse", bindArtifact: false), BrickSource, ArtifactBytes);

        result.Verified.Should().BeFalse();
        result.FailureCode.Should().Be("gate-emitted-artifact-missing",
            "a consumer that supplies artifact bytes must not be told a record binds them when it "
            + "does not - the four-argument overload's own check, independent of strictness");
    }

    [Fact]
    public void TheSidecarDoesNotLiveInTheRecordStoreDirectory()
    {
        var root = TempRoot();
        var (privateKey, _) = CreateEd25519Key();
        var signer = new CertificationRecordSigner(ed25519PrivateKeyBase64: privateKey);
        var archive = new CertificationEvidenceArchive(root, signer, CertificationTrustPolicy.Unpinned);

        archive.PersistAndReverify(SignedRecord(signer, "rgb-hex-parse"), BrickSource, ArtifactBytes)
            .Verified.Should().BeTrue();

        // POSITIVE CONTROL for the emptiness assertion below: the sidecar exists SOMEWHERE. Without
        // this, an archive that never wrote a sidecar at all would satisfy "none under records/".
        File.Exists(Path.Combine(root, "rgb-hex-parse.evidence.json")).Should().BeTrue(
            "the sidecar must exist for 'not in records/' to be a statement about its location");

        Directory.GetFiles(archive.RecordsDirectory, "*.evidence.json").Should().BeEmpty(
            "FileCertificationRecordStore.All() enumerates *.json in this directory and routes each "
            + "one through Get; a verdict sidecar sitting there would be read as a record");

        var store = new FileCertificationRecordStore(
            archive.RecordsDirectory, signer, CertificationTrustPolicy.Unpinned);
        store.All().Should().ContainSingle(
            "the records directory holds exactly the one record that was persisted")
            .Which.BrickId.Should().Be("rgb-hex-parse");
    }

    [Fact]
    public void AnArchiveRootThatCannotBeCreatedFailsAtConstruction()
    {
        // A configured archive that cannot exist is a boot fault, not a silent skip: a run that
        // quietly produced no evidence would be indistinguishable from one that produced bad
        // evidence, and the ledger classifier would call both a GAP with the wrong reason.
        var blocker = Path.Combine(TempRoot(), "not-a-directory");
        File.WriteAllText(blocker, "this path is a file");

        var construct = () => new CertificationEvidenceArchive(
            Path.Combine(blocker, "archive"), new CertificationRecordSigner(), CertificationTrustPolicy.Unpinned);

        construct.Should().Throw<IOException>();
    }

    // --- helpers -------------------------------------------------------------------------------

    /// <summary>
    /// Stand-in for the gate-emitted assembly. Only its HASH is compared, and the record below
    /// binds that same hash, so real IL would add nothing this fact could observe.
    /// </summary>
    private static readonly byte[] ArtifactBytes = Encoding.UTF8.GetBytes("gate-emitted-assembly-bytes");

    private static string TempRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ashlar-evidence-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// A record of the shape the real gate emits on an admitted candidate: schema v2, a certifier
    /// identity input and a gate-emitted-artifact input (the two clauses <c>Strict</c> adds over
    /// <c>Default</c>), content-bound to <see cref="BrickSource"/> — signed by
    /// <paramref name="signer"/>, whose own key material decides whether an Ed25519 signature is
    /// present.
    /// </summary>
    private static CertificationRecord SignedRecord(
        CertificationRecordSigner signer, string brickId, bool bindArtifact = true) =>
        signer.SignRecord(UnsignedShape(brickId, bindArtifact));

    /// <summary>
    /// The record shape, before any signature is attached. <paramref name="bindArtifact"/> exists
    /// because the real gate binds the gate-emitted-artifact input ONLY when the candidate emitted
    /// one, so a record without it is a shape production actually mints - and until it was
    /// expressible here, every fixture in this file supplied both of the clauses Strict adds, which
    /// made "under Strict" in a fact's name unfalsifiable.
    /// </summary>
    private static CertificationRecord UnsignedShape(string brickId, bool bindArtifact = true)
    {
        return new CertificationRecord
        {
            Status = "PASS",
            Stage = "S0-S2",
            Admitted = true,
            Signed = true,
            Timestamp = DateTimeOffset.UtcNow,
            BrickId = brickId,
            ContentHash = BrickContentHasher.ComputeSha256(BrickSource),
            Gate = "evidence-archive-tests",
            SchemaVersion = CertificationRecordData.TrustLoopSchemaVersion,
            Inputs = bindArtifact
                ?
                [
                    CertifierIdentity.ToInput(),
                    new CertificationInput
                    {
                        Kind = CertificationInputKinds.GateEmittedArtifact,
                        Id = brickId,
                        Hash = BrickContentHasher.ComputeSha256(ArtifactBytes),
                    },
                ]
                : [CertifierIdentity.ToInput()],
        };
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
