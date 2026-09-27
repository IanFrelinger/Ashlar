using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Ashlar.Manifest;
using Ashlar.Manifest.Admission;
using Ashlar.Manifest.Signing;
using FluentAssertions;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>
/// SPEC-006 rule S-7: a signed gate record names the store it was decided in INSIDE its own signed
/// bytes, so a record signed for one store cannot anchor another.
///
/// <para><b>What was wrong before this rule.</b> A signed record said WHAT was decided and by WHICH
/// key, and nothing about WHERE. One copied out of a sibling store under the same operator key
/// verified, pinned, and ANCHORED the store it was copied into — creating an expectation where there
/// was none, against which every other record there was then judged — and counted toward that
/// store's self-extension budget. The file-name leg of S-6 does not catch it, because the copy keeps
/// its own <c>{id}.json</c> name.</para>
///
/// <para><b>Where the identity comes from, and why not from the path.</b> It is minted from 128
/// random bits when signing is activated and lives inside the activation marker's signed bytes, so
/// the operator's key attests it and a clone, a remount or a move carries it. Deriving it from the
/// store's path would refuse every record in a relocated store — a brick delivered by an ordinary
/// checkout — and deriving it from the store's contents would let an actor who can write the state
/// root COMPUTE the identity of the store they are copying into.</para>
///
/// <para><b>Two bounds, asserted here rather than left to be discovered.</b> A record signed BEFORE
/// the field existed carries none and is accepted everywhere, because it cannot be distinguished
/// from one written under any store — pinned by
/// <see cref="A_record_signed_before_the_store_identity_existed_is_accepted_and_is_not_a_mismatch"/>.
/// And a store with no honoured marker has no attested identity to deny a copy with, which is why
/// <see cref="GateSignatureResidualTests.A_signed_admission_copied_into_a_store_with_no_marker_of_its_own_still_anchors_it"/>
/// is still a residual. This class is the closure for the case that matters in a live store: the
/// victim has adopted signing, so it has a marker, so it can say which store it is.</para>
///
/// <para><b>Environment.</b> The pinning set comes from <c>ASHLAR_KEY_DIR</c>, read inside
/// <see cref="GateStore"/>'s CONSTRUCTOR, so it is set before any store is built. The class writes a
/// process-global variable and therefore joins the serialized collection, which is the remedy
/// <see cref="ProcessGlobalEnvironmentConventionTests"/> asks for rather than a row on its
/// allowlist.</para>
/// </summary>
[Trait("Category", "Certification")]
[Collection("EnvironmentVariables")]
public sealed class GateRecordStoreIdentityTests : IDisposable
{
    private const string KeyDirVariable = "ASHLAR_KEY_DIR";

    private static readonly DateTimeOffset T0 = new(2026, 9, 27, 9, 0, 0, TimeSpan.Zero);

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _root;
    private readonly string _stateA;
    private readonly string _stateB;
    private readonly string _keyDir;
    private readonly string _otherKeyDir;
    private readonly string? _previousKeyDir;

    public GateRecordStoreIdentityTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "gate-storeid-" + Guid.NewGuid().ToString("N"));
        _stateA = Path.Combine(_root, "sibling-a", ".ashlar");
        _stateB = Path.Combine(_root, "sibling-b", ".ashlar");
        _keyDir = Path.Combine(_root, "keys");
        _otherKeyDir = Path.Combine(_root, "other-keys");
        Directory.CreateDirectory(_stateA);
        Directory.CreateDirectory(_stateB);
        _previousKeyDir = Environment.GetEnvironmentVariable(KeyDirVariable);
        Environment.SetEnvironmentVariable(KeyDirVariable, _keyDir);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(KeyDirVariable, _previousKeyDir);
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>
    /// THE fact. Two stores under one operator key, each with its own marker and therefore its own
    /// identity. A signed admission copied out of A is refused by B: it cannot be read as a decision
    /// made there, cannot become an anchor, and cannot spend B's self-extension budget.
    ///
    /// <para>Three preconditions are asserted rather than assumed, because each of them is a way this
    /// fact could pass for the wrong reason. The two identities must DIFFER, or the comparison is
    /// trivially unequal for no reason the rule owns. The copied file's signature must still VERIFY
    /// under the operator's key, or the refusal is S-1's and not S-7's. And the file must be named
    /// for the id inside it, or the refusal is S-6's file-name leg. Afterwards the copy is removed
    /// and B reads again, which shows the refusal is about the copied file rather than a store this
    /// fact broke on the way in.</para>
    /// </summary>
    [Fact]
    public async Task A_record_signed_for_another_store_is_refused_as_an_anchor_in_this_one()
    {
        var signer = OperatorKey.Generate(_keyDir);

        var a = new GateStore(_stateA, signer);
        await a.RecordAsync(Proposal("ext-a1"), Held(), T0);
        await a.DecideAsync("ext-a1", admit: true, "alice", "seated in A", T0.AddMinutes(1));

        var b = new GateStore(_stateB, signer);
        await b.RecordAsync(Proposal("ext-b1"), Held(), T0.AddMinutes(2));

        var identityA = GateSigningActivation.TryRead(_stateA)!.StoreId;
        var identityB = GateSigningActivation.TryRead(_stateB)!.StoreId;
        identityA.Should().NotBeNull("activating signing mints this store's identity");
        identityB.Should().NotBeNullOrEmpty().And.NotBe(identityA,
            "two stores must not share an identity, or the refusal below would be about two names "
            + "that happen to differ rather than about two stores");

        var copied = RecordFileIn(_stateA, "ext-a1");
        var planted = RecordFileIn(_stateB, "ext-a1");
        File.Copy(copied, planted);

        var transplanted = JsonSerializer.Deserialize<GateRecord>(File.ReadAllText(planted), Json)!;
        transplanted.StoreId.Should().Be(identityA,
            "the copied verdict must really carry A's identity inside it, or this fact plants nothing");
        transplanted.State.Should().Be(ProposalState.Admitted,
            "an Admitted record is what spends a budget; a Held copy would demonstrate less");
        OperatorKey.Verify(
            transplanted.Signer!,
            CanonicalJson.Bytes(transplanted with { Sig = null, Signer = null }),
            transplanted.Sig!).Should().BeTrue(
            "the signature on the copy still VERIFIES under the operator's own key, and its file is "
            + "named for the id inside it. Every leg S-6 has passes. That is precisely why the store "
            + "needs S-7 to refuse it, and why this fact would be about S-1 if the copy were broken");

        var reader = new GateStore(_stateB, signer);
        var list = async () => await reader.ListAsync();
        (await list.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*ext-a1.json is signed for store '" + identityA + "'*this store is '" + identityB + "'*",
                "the refusal names both identities and the file, because the operator's action is to "
                + "remove a file somebody copied in — not to re-sign, re-mint, or delete a record");

        var get = async () => await reader.GetAsync("ext-a1");
        (await get.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*signed for store*",
                "both funnels resolve one posture, so the imported verdict is not readable through "
                + "the single-record path either — `ashlar gates --show` cannot display it as a "
                + "decision made here, and DecideAsync cannot act on it");

        var count = async () => await reader.AdmittedInWindowAsync(TimeSpan.FromHours(24), T0.AddHours(1));
        (await count.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*signed for store*",
                "the budget count reads through the same funnel, so an imported admission cannot "
                + "spend the victim's self-extension budget: the store refuses rather than counting");

        File.Delete(planted);
        var recovered = new GateStore(_stateB, signer);
        (await recovered.ListAsync()).Should().ContainSingle(
            "removing the copied file is the whole remedy, and the refusal says so. B's own record "
            + "was never in question, which is what makes the refusal above about the copy");
        recovered.SignatureTrust!.StoreId.Should().Be(identityB,
            "and B's identity is unchanged by the whole episode — an attacker who copies a file in "
            + "must not be able to move the identity the operator's marker attests");
    }

    /// <summary>
    /// The compatibility half of S-7, and it is normative: a record signed BEFORE the identity
    /// existed carries no field, and a reader MUST accept it rather than treating the absence as a
    /// mismatch. A record signed without it cannot be distinguished from one written under any
    /// store, so refusing it would refuse every record in every store signed before this rule — an
    /// upgrade that bricks every existing store is not a security improvement.
    ///
    /// <para>The pre-S-7 record is built the way the store built one then: the canonical bytes of the
    /// unsigned form with no identity in them, signed by the operator's key. It is the same shape the
    /// residual in <c>GateSignatureResidualTests</c> says stays transplantable for its lifetime, so
    /// this fact and that residual are two readings of one deliberate decision.</para>
    /// </summary>
    [Fact]
    public async Task A_record_signed_before_the_store_identity_existed_is_accepted_and_is_not_a_mismatch()
    {
        var signer = OperatorKey.Generate(_keyDir);
        var b = new GateStore(_stateB, signer);
        await b.RecordAsync(Proposal("ext-b1"), Held(), T0);

        var identityB = GateSigningActivation.TryRead(_stateB)!.StoreId;
        identityB.Should().NotBeNull("this fact is about a store that HAS an identity to mismatch against");

        var old = PlantSignedRecord(_stateB, "ext-old", storeId: null, signer);
        Encoding.UTF8.GetString(CanonicalJson.Bytes(old with { Sig = null, Signer = null }))
            .Should().NotContain("StoreId",
                "the canonical form omits nulls, so the bytes a pre-S-7 signature covers do not "
                + "mention the field at all — which is why adding the member moved no existing "
                + "record's canonical hash and invalidated no grandfather inventory entry");

        var reader = new GateStore(_stateB, signer);
        (await reader.ListAsync()).Should().HaveCount(2,
            "a record that names no store is accepted in a store that names one. This is the "
            + "compatibility the rule is REQUIRED to keep, not an oversight in it");
        (await reader.GetAsync("ext-old")).Should().NotBeNull();
        reader.SignatureTrust!.SignedRecordAnchors.Should().Be(2,
            "and it still anchors, exactly as it did before S-7 — which is the cost of the "
            + "compatibility, and is why the residual for pre-S-7 records stays open in SPEC-006");
    }

    /// <summary>
    /// The identity is INSIDE the signed bytes, on both artefacts that carry one. Without this the
    /// whole rule is a label an attacker rewrites: copy the record, edit one field to the victim's
    /// identity, and the comparison passes.
    ///
    /// <para>Both halves are the same mutation — rewrite <c>StoreId</c> in place — and both produce
    /// the refusal that covers a signature over bytes that changed. The record half is the one that
    /// matters for the attack; the marker half matters because a marker whose identity could be
    /// edited would let an actor rename the victim store to the store they are copying from.</para>
    /// </summary>
    [Fact]
    public async Task The_store_identity_cannot_be_relabelled_because_it_is_under_the_signature()
    {
        var signer = OperatorKey.Generate(_keyDir);

        var a = new GateStore(_stateA, signer);
        await a.RecordAsync(Proposal("ext-a1"), Held(), T0);
        var b = new GateStore(_stateB, signer);
        await b.RecordAsync(Proposal("ext-b1"), Held(), T0.AddMinutes(1));

        var identityB = GateSigningActivation.TryRead(_stateB)!.StoreId!;
        var planted = RecordFileIn(_stateB, "ext-a1");
        File.Copy(RecordFileIn(_stateA, "ext-a1"), planted);

        // The obvious evasion: relabel the copy as belonging here.
        Rewrite(planted, "StoreId", identityB);

        var list = async () => await new GateStore(_stateB, signer).ListAsync();
        (await list.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*ext-a1.json carries a signature that does not verify*",
                "editing the identity is editing the signed bytes, so the record fails S-1 instead "
                + "of passing S-7. An identity outside the signature would be a label, and a label "
                + "an attacker can rewrite protects nobody");

        File.Delete(planted);

        // The same evasion against the marker: rename the victim store to the source store.
        var markerPath = GateSigningActivation.PathFor(_stateB);
        Rewrite(markerPath, "StoreId", GateSigningActivation.TryRead(_stateA)!.StoreId!);
        var read = () => GateSigningActivation.TryRead(_stateB);
        read.Should().Throw<InvalidOperationException>()
            .WithMessage("*carries a signature that does not verify*",
                "the marker's identity is inside its signed bytes too, so a store cannot be renamed "
                + "into its sibling by an edit. A marker that does not verify is never honoured");
    }

    /// <summary>
    /// The identity behaves like the activation instant: stable. Repeated activation, key-rotation-era
    /// re-activation and <c>--repair</c> all keep it, and a repair over a DELETED marker carries
    /// forward the identity the surviving records name.
    ///
    /// <para><b>Why the repair half is load-bearing rather than tidy.</b> Every record on disk names
    /// the identity it was signed for. A repair that minted a fresh one would refuse every one of
    /// them, so the single command the store's own refusals tell an operator to run would brick the
    /// store it was reached for — the failure shape this rule's own activation refusal exists to
    /// avoid. The inheritance is bounded and disclosed: only a record signed under a key this machine
    /// vouches for can contribute an identity, and two disagreeing identities yield neither.</para>
    /// </summary>
    [Fact]
    public async Task The_store_identity_survives_repeated_activation_and_a_repair()
    {
        var signer = OperatorKey.Generate(_keyDir);
        var store = new GateStore(_stateB, signer);
        await store.RecordAsync(Proposal("ext-b1"), Held(), T0);

        var minted = GateSigningActivation.TryRead(_stateB)!.StoreId;
        minted.Should().NotBeNull();

        var again = await new GateStore(_stateB, signer).ActivateSigningAsync(T0.AddHours(1));
        again.WasAlreadyActive.Should().BeTrue();
        again.Marker.StoreId.Should().Be(minted,
            "re-activating is idempotent, and an identity that moved would refuse every record "
            + "already signed under the old one");

        var repaired = await new GateStore(_stateB, signer).ActivateSigningAsync(T0.AddHours(2), repair: true);
        repaired.Marker.StoreId.Should().Be(minted,
            "--repair re-mints WHAT was authorized, never WHEN and never WHICH STORE");

        // The state --repair exists for: the marker is gone, and the records that survive name the
        // store it was for.
        File.Delete(GateSigningActivation.PathFor(_stateB));
        var afterDeletion = await new GateStore(_stateB, signer).ActivateSigningAsync(T0.AddHours(3), repair: true);
        afterDeletion.Marker.StoreId.Should().Be(minted,
            "the identity is carried forward from the records that still verify, so the repair does "
            + "not brick the store by disowning its own verdicts");
        (await new GateStore(_stateB, signer).ListAsync()).Should().ContainSingle(
            "and the store reads afterwards, which is the only proof that the carried-forward "
            + "identity is the one the records name");
    }

    /// <summary>
    /// A marker signed by a key this machine does not vouch for is somebody else's declaration about
    /// this store, and the operator's plain verb replaces it. The replacement keeps the identity the
    /// store's own verifying records name, for the same reason repair does: a fresh identity here
    /// would refuse every record the operator already signed, and the operator would have bricked
    /// their store by running the command the refusal told them to run.
    /// </summary>
    [Fact]
    public async Task Replacing_a_foreign_marker_keeps_the_identity_this_stores_records_name()
    {
        var signer = OperatorKey.Generate(_keyDir);
        var store = new GateStore(_stateB, signer);
        await store.RecordAsync(Proposal("ext-b1"), Held(), T0);
        var minted = GateSigningActivation.TryRead(_stateB)!.StoreId;

        var foreign = OperatorKey.Generate(_otherKeyDir);
        File.WriteAllText(
            GateSigningActivation.PathFor(_stateB),
            JsonSerializer.Serialize(
                GateSigningActivation.Signed(foreign, T0.AddYears(-20), [], storeId: null), Json));
        GateSigningActivation.TryRead(_stateB)!.Signer.Should().Be(foreign.PublicKeyBase64,
            "the planted marker must really be the foreign one, or this fact replaces nothing");

        var outcome = await new GateStore(_stateB, signer).ActivateSigningAsync(T0.AddHours(1));
        outcome.ReplacedUnvouchedMarker.Should().BeTrue("the operator's verb replaces a foreign marker");
        outcome.Marker.StoreId.Should().Be(minted,
            "and the replacement keeps the identity this store's own records name. A fresh one would "
            + "refuse every record signed under the old identity, so the named remedy would brick "
            + "the store");
        (await new GateStore(_stateB, signer).ListAsync()).Should().ContainSingle(
            "the store reads after the replacement, which is what that identity being right means");
    }

    /// <summary>
    /// A keyless write records NO identity. An unsigned record's fields are its writer's own input,
    /// so an identity on one would assert a provenance nothing attests — and it would move the
    /// canonical hash that the grandfather inventory pins for exactly these records.
    /// </summary>
    [Fact]
    public async Task An_unsigned_record_carries_no_store_identity()
    {
        // A keyless reader is one whose key directory yields nothing, not one that throws, so the
        // directory exists and is empty before any store is constructed against it.
        var noKeys = Path.Combine(_root, "no-keys");
        Directory.CreateDirectory(noKeys);
        Environment.SetEnvironmentVariable(KeyDirVariable, noKeys);

        await new GateStore(_stateB).RecordAsync(Proposal("ext-b1"), Held(), T0);

        var node = (JsonObject)JsonNode.Parse(File.ReadAllText(RecordFileIn(_stateB, "ext-b1")))!;
        node["Sig"].Should().BeNull(
            "the record really is the keyless shape, or the assertion below is about a record this "
            + "fact did not produce");
        node["StoreId"].Should().BeNull(
            "S-2 writes unsigned records and nothing signs an identity for them, so the field is "
            + "empty rather than filled in with a store nobody attested. An identity on an unsigned "
            + "record would also move the canonical hash the grandfather inventory pins for exactly "
            + "these records");
    }

    private static ExtensionProposal Proposal(string id) => new()
    {
        Id = id,
        Kind = "brick",
        Summary = "add brick storeid.demo",
        ProposedBy = "night-agent",
        ProposedAt = T0,
        Courses = [new CourseResult { Name = "sandbox", Passed = true, Detail = "confined" }],
    };

    private static AdmissionOutcome Held(string reason = "holding for review") =>
        new() { State = ProposalState.Held, Reason = reason };

    private static string RecordFileIn(string stateRoot, string id) =>
        Path.Combine(stateRoot, "gates", id + ".json");

    /// <summary>
    /// A record signed the way the store signs one, with <paramref name="storeId"/> inside the bytes
    /// the signature covers — null for the shape the store wrote before S-7 existed. Written through
    /// the signing primitives rather than through <see cref="GateStore"/>, because the store can no
    /// longer produce a pre-S-7 record and a compatibility fact that cannot build the old shape is
    /// not a compatibility fact.
    /// </summary>
    private static GateRecord PlantSignedRecord(
        string stateRoot, string id, string? storeId, SigningIdentity signer)
    {
        var unsigned = new GateRecord
        {
            Proposal = Proposal(id),
            State = ProposalState.Admitted,
            Reason = "seated before S-7",
            Actor = "alice",
            DecidedAt = T0,
            StoreId = storeId,
        };
        var signed = unsigned with
        {
            Sig = signer.Sign(CanonicalJson.Bytes(unsigned)),
            Signer = signer.PublicKeyBase64,
        };
        File.WriteAllText(RecordFileIn(stateRoot, id), JsonSerializer.Serialize(signed, Json));
        return signed;
    }

    /// <summary>Rewrites one top-level string field of a JSON artefact in place, and CHECKS the
    /// edit landed on the field it named: the repo's keys are PascalCase, and a mis-cased key adds a
    /// second property while leaving the real one alone — a plant that plants nothing and a fact
    /// that passes against broken code.</summary>
    private static void Rewrite(string path, string field, string value)
    {
        var node = (JsonObject)JsonNode.Parse(File.ReadAllText(path))!;
        node.ContainsKey(field).Should().BeTrue(
            "the artefact must already carry {0}, or this edit is adding a property rather than "
            + "rewriting the one under the signature", field);
        node[field] = value;
        File.WriteAllText(path, node.ToJsonString());
        ((JsonObject)JsonNode.Parse(File.ReadAllText(path))!)[field]!.GetValue<string>()
            .Should().Be(value, "the rewrite must really have moved the field this fact names");
    }
}
