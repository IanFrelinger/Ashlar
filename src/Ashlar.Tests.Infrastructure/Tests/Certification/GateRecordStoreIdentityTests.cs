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
/// <para><b>Where the identity may NOT come from, which is half the rule and the half that can
/// invert it.</b> A store binds an identity only off a marker it HONOURS and holds KEY MATERIAL for.
/// An ignored marker contributes none, and neither does one a keyless reader honours by
/// corroboration, because an actor who can write the state root supplies both halves of that
/// corroboration. Get either wrong and S-7 stops refusing copies and starts making a store refuse the
/// records it really wrote, on the say-so of one file the actor planted — so
/// <see cref="An_ignored_marker_contributes_no_store_identity"/> and
/// <see cref="A_keyless_reader_still_reads_its_own_records_under_a_planted_marker_and_record"/> are
/// load-bearing rather than defensive, and each one plants a marker that DECLARES an identity, which
/// no fact in this suite used to do.</para>
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
    ///
    /// <para><b>The fourth leg exists because the first three did not pin the line this fact
    /// names.</b> A re-mint over a vouched-for marker reads
    /// <c>existing.StoreId ?? inheritStoreId ?? NewStoreId()</c>, and in the three arrangements above
    /// the marker's identity and the one the store's records name are the SAME value — so deleting
    /// the marker's half of that expression left this fact green, which a review measured. The fourth
    /// leg makes the two sources DISAGREE, both non-null: B's marker still says what it always said,
    /// and the only verifying record left in B is a copy that names the sibling. The marker wins,
    /// because it is the artefact the operator's key attested; a build that preferred the records
    /// would let one planted file choose the name the operator's own repair verb signs, which is the
    /// same inversion <see cref="An_ignored_marker_contributes_no_store_identity"/> refuses on the
    /// read side.</para>
    /// </summary>
    [Fact]
    public async Task The_store_identity_survives_repeated_activation_and_a_repair()
    {
        var signer = OperatorKey.Generate(_keyDir);
        var store = new GateStore(_stateB, signer);
        await store.RecordAsync(Proposal("ext-b1"), Held(), T0);

        var minted = GateSigningActivation.TryRead(_stateB)!.StoreId;
        minted.Should().NotBeNullOrEmpty(
            "every assertion in this fact compares against this value, and Be(null) passes when both "
            + "sides are null — so without a non-null arrange guard the whole fact would hold against "
            + "a build that mints no identity at all");

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

        // ── the two sources made to DISAGREE, which is what pins the marker's half of the expression ──
        var a = new GateStore(_stateA, signer);
        await a.RecordAsync(Proposal("ext-a1"), Held(), T0);
        var identityA = GateSigningActivation.TryRead(_stateA)!.StoreId;
        identityA.Should().NotBeNullOrEmpty().And.NotBe(minted,
            "the sibling must name a DIFFERENT store, or this leg is the third one again and the "
            + "marker's half of the expression stays unmeasured");

        // B's own record is moved OUT of gates/ rather than deleted: parking it inside would either
        // trip the file-name refusal or leave a second vote, and the tail below needs it back to show
        // the kept identity is still the one B's own verdict names. The copy is then the only
        // verifying record here, so the identity the records agree on is the sibling's while the
        // marker still carries B's.
        var parked = Path.Combine(_root, "parked-ext-b1.json");
        File.Move(RecordFileIn(_stateB, "ext-b1"), parked);
        File.Copy(RecordFileIn(_stateA, "ext-a1"), RecordFileIn(_stateB, "ext-a1"));

        JsonSerializer.Deserialize<GateRecord>(
                File.ReadAllText(RecordFileIn(_stateB, "ext-a1")), Json)!.StoreId
            .Should().Be(identityA,
                "the plant must really name the sibling inside its signed bytes, or the two sources "
                + "still agree and the assertion below cannot tell which one the re-mint read");
        GateSigningActivation.TryRead(_stateB)!.StoreId.Should().Be(minted,
            "and B's marker must still carry its own identity, or there is nothing here to keep");

        var contested = await new GateStore(_stateB, signer).ActivateSigningAsync(T0.AddHours(4), repair: true);
        contested.Marker.StoreId.Should().Be(minted,
            "a repair keeps the identity the HONOURED MARKER carries: that is the one artefact here "
            + "the operator's key attested, and it outranks what the files in gates/ name")
            .And.NotBe(identityA,
            "and it does NOT adopt what a planted file names. Dropping the marker's half of "
            + "`existing.StoreId ?? inheritStoreId ?? NewStoreId()` reads the records instead, which "
            + "hands anyone who can write gates/ the name the operator's own repair verb then signs — "
            + "and every record B really wrote would be refused under it");

        // Put B back as it was, so the tail proves the kept identity is B's own records' identity.
        File.Delete(RecordFileIn(_stateB, "ext-a1"));
        File.Move(parked, RecordFileIn(_stateB, "ext-b1"));
        var recovered = new GateStore(_stateB, signer);
        (await recovered.ListAsync()).Should().ContainSingle(
            "with the copy removed B reads its own record again — the contest changed nothing about B");
        recovered.SignatureTrust!.StoreId.Should().Be(minted,
            "under the identity it has carried through repeated activation, a repair, a deleted marker "
            + "and a contested one, which is what stability means for this field");
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
        minted.Should().NotBeNullOrEmpty(
            "every assertion below compares against this value, and Be(null) passes — so without a "
            + "non-null arrange guard this fact would hold equally against a build that mints no "
            + "identity at all");

        var foreign = OperatorKey.Generate(_otherKeyDir);
        PlantMarker(_stateB, foreign, T0.AddYears(-20), storeId: null);
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

    /// <summary>
    /// THE OTHER HALF OF THE RULE, and until this fact existed it had no test at all. A marker this
    /// reader IGNORES contributes NO identity. The refusal fact above proves that a store which names
    /// itself refuses a foreigner; this one proves the store only ever gets that name from a marker it
    /// vouches for — which is what stops the rule being inverted.
    ///
    /// <para><b>The attack this closes.</b> An actor who can write the state root — the threat model
    /// the whole rule assumes — overwrites B's marker with one signed under a key of their OWN that
    /// declares A's identity, and copies A's signed admission into B. If an ignored marker could
    /// contribute an identity, B would resolve itself as A: the copy would anchor B and spend B's
    /// self-extension budget, and B's own records would be the ones refused. Every marker plant in
    /// this suite before this fact passed <c>storeId: null</c>, so nothing exercised an unvouched
    /// marker that DECLARES an identity, and hoisting the assignment out of the honoured branch left
    /// the whole gate green.</para>
    /// </summary>
    [Fact]
    public async Task An_ignored_marker_contributes_no_store_identity()
    {
        var signer = OperatorKey.Generate(_keyDir);

        var a = new GateStore(_stateA, signer);
        await a.RecordAsync(Proposal("ext-a1"), Held(), T0);
        var identityA = GateSigningActivation.TryRead(_stateA)!.StoreId;

        var b = new GateStore(_stateB, signer);
        await b.RecordAsync(Proposal("ext-b1"), Held(), T0.AddMinutes(1));
        var identityB = GateSigningActivation.TryRead(_stateB)!.StoreId;
        identityA.Should().NotBeNullOrEmpty();
        identityB.Should().NotBeNullOrEmpty().And.NotBe(identityA,
            "the two identities must differ, or a marker declaring A's is indistinguishable from "
            + "one declaring B's and this fact measures nothing");

        var ownRecord = JsonSerializer.Deserialize<GateRecord>(
            File.ReadAllText(RecordFileIn(_stateB, "ext-b1")), Json)!;
        ownRecord.StoreId.Should().Be(identityB,
            "B's own record must really name B inside its signed bytes, or there is nothing here for "
            + "a wrongly-adopted identity to refuse");

        // The actor cannot sign with the operator's key, so they bring their own — and they declare
        // this store to be the one whose records they are copying in.
        var attacker = OperatorKey.Generate(_otherKeyDir);
        PlantMarker(_stateB, attacker, T0.AddMinutes(2), identityA);
        var planted = GateSigningActivation.TryRead(_stateB)!;
        planted.Signer.Should().Be(attacker.PublicKeyBase64,
            "the plant must really be under a key this machine does not vouch for");
        planted.StoreId.Should().Be(identityA,
            "and it must really DECLARE the sibling's identity. A plant carrying no identity is the "
            + "shape every other marker plant in this suite already had, and it cannot fail this way");

        var reader = new GateStore(_stateB, signer);
        (await reader.ListAsync()).Should().ContainSingle(
            "B reads its OWN record. An identity taken off a marker B does not vouch for would make "
            + "B call itself A, and B's genuine verdict would be refused as a copy — the rule "
            + "inverted into a one-file brick by the actor it exists to stop");
        reader.SignatureTrust!.Basis.Should().Contain("activation marker ignored",
            "the marker must really have been IGNORED, or this fact is about a marker that failed to "
            + "parse rather than about one this reader declined to believe");
        reader.SignatureTrust.StoreId.Should().BeNull(
            "and the posture names no store at all: the identity is attested by a marker this reader "
            + "vouches for or it does not exist. Null binds nothing, which is the honest bound");
    }

    /// <summary>
    /// The same rule on the WRITE side of the marker. The operator's plain verb replaces a foreign
    /// marker, and the replacement takes the identity the store's own verifying records name — never
    /// the one the foreign marker DECLARED.
    ///
    /// <para>Its sibling fact plants a foreign marker carrying no identity, which cannot distinguish
    /// "ignores what the plant declared" from "there was nothing to take". This one declares the
    /// sibling's identity, so an <c>existing.StoreId ??</c> added to the replacement — the one-token
    /// tidy-up that would let an actor choose the victim's new name — reddens here and nowhere
    /// else.</para>
    /// </summary>
    [Fact]
    public async Task Replacing_a_foreign_marker_ignores_the_identity_that_marker_declared()
    {
        var signer = OperatorKey.Generate(_keyDir);

        var a = new GateStore(_stateA, signer);
        await a.RecordAsync(Proposal("ext-a1"), Held(), T0);
        var identityA = GateSigningActivation.TryRead(_stateA)!.StoreId;

        var b = new GateStore(_stateB, signer);
        await b.RecordAsync(Proposal("ext-b1"), Held(), T0.AddMinutes(1));
        var identityB = GateSigningActivation.TryRead(_stateB)!.StoreId;
        identityA.Should().NotBeNullOrEmpty();
        identityB.Should().NotBeNullOrEmpty().And.NotBe(identityA);

        var foreign = OperatorKey.Generate(_otherKeyDir);
        PlantMarker(_stateB, foreign, T0.AddYears(-20), identityA);
        GateSigningActivation.TryRead(_stateB)!.StoreId.Should().Be(identityA,
            "the plant must really declare the sibling's identity, or this fact cannot tell "
            + "'ignored what it declared' from 'it declared nothing'");

        var outcome = await new GateStore(_stateB, signer).ActivateSigningAsync(T0.AddHours(1));
        outcome.ReplacedUnvouchedMarker.Should().BeTrue("the operator's verb replaces a foreign marker");
        outcome.Marker.StoreId.Should().Be(identityB,
            "the replacement keeps the identity B's own verifying records name, so the store still "
            + "reads afterwards")
            .And.NotBe(identityA,
            "and it does NOT adopt what the foreign marker declared. Adopting it would let anyone who "
            + "can write the state root choose the name the operator's own repair verb then signs");
        (await new GateStore(_stateB, signer).ListAsync()).Should().ContainSingle(
            "which is what that identity being the right one actually means");
    }

    /// <summary>
    /// A reader holding NO key material must not be brickable by a plant. It honours a marker that a
    /// record under the marker's own key corroborates — and an actor who can write the state root
    /// supplies BOTH halves of that pair, so an identity taken from there is the ATTACKER's value.
    /// Binding it would make the store refuse its own genuine records, with a message telling the
    /// operator to delete the files it really wrote.
    ///
    /// <para><b>This is a regression fact, not a feature fact.</b> These same three files read clean
    /// before S-7 existed, so the version of this rule that bound a keyless reader's identity turned a
    /// hardening into a one-file denial of service. The remedy is the rule
    /// <see cref="GateStore"/> already states for the pinning set — key material only, never derived
    /// from the records, because a derivation accepts whatever key an attacker re-signed them all with
    /// — applied to the identity as well. The cost is disclosed in SPEC-006: a keyless consumer gets
    /// no S-7 protection at all.</para>
    /// </summary>
    [Fact]
    public async Task A_keyless_reader_still_reads_its_own_records_under_a_planted_marker_and_record()
    {
        var signer = OperatorKey.Generate(_keyDir);
        await new GateStore(_stateB, signer).RecordAsync(Proposal("ext-b1"), Held(), T0);
        var identityB = GateSigningActivation.TryRead(_stateB)!.StoreId;
        identityB.Should().NotBeNullOrEmpty("B's records name B, which is what a wrong identity refuses");

        // The actor writes the state root and signs with their own key: a marker declaring a store of
        // their choosing, plus one record under the same key to corroborate it.
        var attacker = OperatorKey.Generate(_otherKeyDir);
        var declared = GateSigningActivation.NewStoreId();
        declared.Should().NotBe(identityB);
        PlantMarker(_stateB, attacker, T0.AddMinutes(5), declared);
        PlantSignedRecord(_stateB, "ext-evil", declared, attacker);

        // A consumer with no key material at all — `ashlar gates` on a machine that never ran
        // `keys init`. The directory exists and is empty: keyless means yielding nothing, not throwing.
        var noKeys = Path.Combine(_root, "no-keys");
        Directory.CreateDirectory(noKeys);
        Environment.SetEnvironmentVariable(KeyDirVariable, noKeys);

        var reader = new GateStore(_stateB);
        var read = await reader.ListAsync();
        read.Select(r => r.Proposal.Id).Should().Contain("ext-b1",
            "the store's OWN record is still readable. Taking the identity off a marker this reader "
            + "cannot vouch for would refuse it — and the refusal would say 'Remove the copied file; "
            + "this store never wrote it' about a file this store did write");
        (await reader.GetAsync("ext-b1")).Should().NotBeNull(
            "and through the single-record funnel too, because both read the one posture");
        reader.SignatureTrust!.Basis.Should().Contain("corroborated by a record under the same key",
            "the marker really IS honoured by this reader, and by exactly the corroboration the actor "
            + "supplied both halves of — without that, this fact is about an ignored marker and "
            + "measures the sibling rule instead of this one");
        reader.SignatureTrust.StoreId.Should().BeNull(
            "so the posture binds no identity: honouring a marker is not vouching for it. A keyless "
            + "consumer gets no S-7 protection, which SPEC-006 discloses, and that is strictly better "
            + "than handing an actor a way to make a store disown its own verdicts");
    }

    /// <summary>
    /// The disagreement rule, which SPEC-006 states as a MUST and nothing tested. Two verifying
    /// records naming two different stores carry forward NEITHER identity: the re-mint gets a fresh
    /// one.
    ///
    /// <para>This fact exists to stop the rule being widened silently. "Majority", "first wins" or
    /// "most recent wins" all leave every other fact in this class green, and each of them hands an
    /// actor who can plant one file a vote on what the operator's repair verb signs. Two identities in
    /// one store means at least one of these records was signed somewhere else, and this code must not
    /// guess which. The consequence is deliberately harsh and is disclosed rather than asserted here:
    /// a fresh identity refuses every record in the store, so the operator has to move the copied file
    /// out and repair again.</para>
    /// </summary>
    [Fact]
    public async Task Two_disagreeing_store_identities_carry_forward_neither_on_a_repair()
    {
        var signer = OperatorKey.Generate(_keyDir);

        var a = new GateStore(_stateA, signer);
        await a.RecordAsync(Proposal("ext-a1"), Held(), T0);
        await a.DecideAsync("ext-a1", admit: true, "alice", "seated in A", T0.AddMinutes(1));
        var identityA = GateSigningActivation.TryRead(_stateA)!.StoreId;

        var b = new GateStore(_stateB, signer);
        await b.RecordAsync(Proposal("ext-b1"), Held(), T0.AddMinutes(2));
        var identityB = GateSigningActivation.TryRead(_stateB)!.StoreId;
        identityA.Should().NotBeNullOrEmpty();
        identityB.Should().NotBeNullOrEmpty().And.NotBe(identityA,
            "the two records must name DIFFERENT stores, or there is no disagreement to resolve");

        File.Copy(RecordFileIn(_stateA, "ext-a1"), RecordFileIn(_stateB, "ext-a1"));
        File.Delete(GateSigningActivation.PathFor(_stateB));

        var repaired = await new GateStore(_stateB, signer).ActivateSigningAsync(T0.AddHours(1), repair: true);
        repaired.Marker.StoreId.Should().NotBeNullOrEmpty(
            "a re-mint always leaves the store carrying an identity; the question is WHOSE")
            .And.NotBe(identityA,
            "not the copied record's. 'First wins' would hand the actor who planted one file the "
            + "identity the operator's own verb signs")
            .And.NotBe(identityB,
            "and not the store's own either, because this code cannot tell which of two disagreeing "
            + "records was signed somewhere else. Neither, and a fresh one, is the only honest answer");
    }

    /// <summary>
    /// The minted identity's normative SHAPE — and an explicit statement of the half of the normative
    /// sentence a fact cannot reach. SPEC-006 S-7 makes the value "32 lowercase hex characters over
    /// 128 cryptographically random bits" normative, and nothing asserted any of it: measured on this
    /// branch, <c>ToUpperInvariant()</c> left the whole gate green DETERMINISTICALLY, because every
    /// comparison on this field is deliberately <c>OrdinalIgnoreCase</c>, and <c>GetBytes(2)</c> left
    /// it green almost always.
    ///
    /// <para><b>What is provable here, and what is NOT.</b> Length, alphabet, case and DISTINCTNESS
    /// across mints are properties of a sample, and they are asserted. ENTROPY is not, and this fact
    /// does not pretend to it: no number of samples distinguishes 128 random bits from 128 bits of a
    /// counter nobody has watched roll over, so an assertion dressed up as a randomness test would be
    /// exactly the covered debt this repository keeps paying for. The one thing that establishes the
    /// entropy is reading the single line that mints it — <c>RandomNumberGenerator.GetBytes(16)</c> in
    /// <see cref="GateSigningActivation.NewStoreId"/> — and what this fact does is pin the shape any
    /// substitution there would also have to reproduce, which is what makes the review of that line a
    /// narrow one rather than a broad one.</para>
    ///
    /// <para><b>Why the shape is load-bearing rather than cosmetic.</b> The LENGTH is the collision
    /// resistance, and collision resistance is the whole of what stops an actor who can write the
    /// state root from GUESSING the identity of the store they are copying into — the attack the
    /// "minted, never derived" clause exists to prevent, and the one property a shorter value would
    /// silently give away. The CASE is load-bearing in the opposite direction: because the comparison
    /// legs are OrdinalIgnoreCase on purpose (refusing a store over two strings that look identical in
    /// the message would be a brick with no diagnosis), an implementation that emitted uppercase would
    /// keep working while making the normative sentence false. No comparison in the system can catch
    /// that, which is why it is asserted at the mint.</para>
    ///
    /// <para><b>Both paths, deliberately.</b> The value on a real marker is asserted, so the shape is
    /// pinned where an operator actually meets it, and a batch straight from
    /// <see cref="GateSigningActivation.NewStoreId"/> is asserted too, because distinctness needs more
    /// samples than one store's activation produces.</para>
    /// </summary>
    [Fact]
    public async Task The_minted_store_identity_is_thirty_two_lowercase_hex_characters_and_distinct_per_mint()
    {
        const string HexLower = "0123456789abcdef";
        const int Samples = 64;

        // First the value an operator actually gets, through the path a real store takes.
        var signer = OperatorKey.Generate(_keyDir);
        await new GateStore(_stateB, signer).RecordAsync(Proposal("ext-b1"), Held(), T0);
        var onTheMarker = GateSigningActivation.TryRead(_stateB)!.StoreId;
        onTheMarker.Should().NotBeNull(
            "activation mints one, and every assertion below is about that value rather than about a "
            + "build in which the field is never filled in");
        onTheMarker!.Should().HaveLength(32,
            "SPEC-006 S-7 makes 32 lowercase hex characters over 128 random bits normative, and the "
            + "length is the collision resistance: a shorter value lets an actor who can write the "
            + "state root GUESS the identity of the store they are copying into, which is precisely "
            + "what the minted-never-derived clause exists to stop. GetBytes(2) is caught here and "
            + "nowhere else");
        onTheMarker.Where(c => !HexLower.Contains(c, StringComparison.Ordinal)).Should().BeEmpty(
            "lowercase hex and nothing else. Every comparison on this field is OrdinalIgnoreCase on "
            + "purpose, so an uppercase or non-hex value would keep WORKING while making the normative "
            + "sentence false — no comparison in the system can catch that, so it is caught at the "
            + "mint. Offending characters, if any, are listed above");

        // Two real stores must not share one, which is the same property as distinctness but through
        // the path that matters. The sample below is what makes that more than an anecdote.
        var a = new GateStore(_stateA, signer);
        await a.RecordAsync(Proposal("ext-a1"), Held(), T0);
        GateSigningActivation.TryRead(_stateA)!.StoreId.Should().NotBe(onTheMarker,
            "two stores activated under one operator key get different identities, or S-7 refuses "
            + "nothing at all");

        // DISTINCTNESS, compared the way the store compares: two values differing only in case are
        // the SAME identity to every leg that reads this field, so OrdinalIgnoreCase is the honest
        // comparer here and Ordinal would overstate the result.
        var mints = Enumerable.Range(0, Samples)
            .Select(_ => GateSigningActivation.NewStoreId())
            .ToList();
        mints.Where(id => id.Length != 32).Should().BeEmpty(
            "the shape holds for every mint and not only for the one a store happened to write");
        mints.SelectMany(id => id).Where(c => !HexLower.Contains(c, StringComparison.Ordinal))
            .Should().BeEmpty("and so does the alphabet");
        mints.Distinct(StringComparer.OrdinalIgnoreCase).Should().HaveCount(Samples,
            "and {0} mints yield {0} distinct identities. This is DISTINCTNESS, not randomness: it "
            + "refuses a constant, a counter reset per process, and a value derived from the store, "
            + "and it says nothing whatever about the quality of the bits. That property is "
            + "established by reading RandomNumberGenerator.GetBytes(16) at the mint and by nothing "
            + "a test can assert",
            Samples);
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

    /// <summary>
    /// Overwrites the activation marker with one signed by <paramref name="signer"/> declaring
    /// <paramref name="storeId"/> — the shape an actor who can write the state root but cannot sign
    /// with the operator's key produces. The inventory is empty rather than null on purpose:
    /// <see cref="GateSigningActivation.TryRead"/> REFUSES a marker without one, so a null there
    /// would make every fact below a test of the corrupt-marker path instead.
    /// </summary>
    private static void PlantMarker(
        string stateRoot, SigningIdentity signer, DateTimeOffset activatedAt, string? storeId) =>
        File.WriteAllText(
            GateSigningActivation.PathFor(stateRoot),
            JsonSerializer.Serialize(GateSigningActivation.Signed(signer, activatedAt, [], storeId), Json));

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
