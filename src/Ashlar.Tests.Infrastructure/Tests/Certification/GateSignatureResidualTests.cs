using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Ashlar.Core.Application.Paths;
using Ashlar.Manifest;
using Ashlar.Manifest.Admission;
using Ashlar.Manifest.Signing;
using FluentAssertions;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>
/// What SPEC-006 rule S-6 does NOT close. Every fact here performs an attack through
/// <see cref="GateStore"/>'s public API and asserts that the store does not catch it, so each
/// residual disclosed in the spec is a sentence somebody can check rather than a sentence
/// somebody wrote.
///
/// <para><b>Why a class of inverted facts, and what that costs.</b> Every ordinary test in this
/// repository goes red when somebody breaks something. Every executing fact here goes red when
/// somebody FIXES something. That is the intended signal, and it is also this class's main failure
/// mode: the day an unrelated change strengthens the store, several of these go red at once in a
/// commit whose author has never heard of this file. So the assertion that pins each hole ends by
/// naming the three artefacts that are deleted together — the fact, its row in
/// <see cref="Residuals"/>, and the sentence in SPEC-006 (<see cref="DeleteTogether"/>) — and
/// <see cref="No_residual_fact_is_skipped"/> closes the one escape the binding companions cannot
/// see.</para>
///
/// <para><b>The binding is two-directional, and two rows deliberately have no fact.</b>
/// <see cref="SPEC_006_names_every_residual_this_file_demonstrates"/> refuses a row whose
/// disclosure has been deleted or reworded.
/// <see cref="Every_residual_row_names_a_fact_on_this_class_or_says_why_it_cannot"/> refuses a row
/// naming a fact that does not exist AND a fact that no row names — the second of which is the
/// failure no forward-only inventory can see: an attack this suite demonstrates and the spec is
/// silent about. Two rows carry <c>FactName: null</c> with a mandatory <c>WhyNotExecutable</c>,
/// because the only test available for either would use the operator's key correctly and observe
/// the store accepting what that key signed — which asserts that the system WORKS. A fact dressed
/// up as that attack is exactly the covered debt this class exists to refuse, so the absence is
/// made legible instead of being written badly.</para>
///
/// <para><b>Environment.</b> The pinning set comes from <c>ASHLAR_KEY_DIR</c>, resolved inside
/// <see cref="GateStore"/>'s CONSTRUCTOR through
/// <see cref="OperatorKey.TrustedPublicKeysBase64"/> — so <see cref="Keyed"/> and
/// <see cref="Keyless"/> must be called BEFORE the store is built, and flipping the variable
/// afterwards is a no-op for that instance. Several facts construct two stores for exactly that
/// reason. This class joins the serialized collection because it writes that variable; see
/// <see cref="ProcessGlobalEnvironmentConventionTests"/>. Joining the collection is the remedy,
/// not a row on that class's allowlist.</para>
///
/// <para><b>Fixture duplication is deliberate.</b> Every helper on
/// <see cref="GateSignatureExpectationTests"/> is private, and this class needs members that one
/// does not want: two sibling state roots, <see cref="StoreAt"/>, <see cref="RecordFileIn"/> and
/// <see cref="UnsignedAt"/>. Extracting a shared fixture would mean editing a file of thirty-odd
/// passing facts to serve a file that did not exist when they were written.</para>
/// </summary>
[Trait("Category", "Certification")]
[Collection("EnvironmentVariables")]
public sealed class GateSignatureResidualTests : IDisposable
{
    private const string KeyDirVariable = "ASHLAR_KEY_DIR";
    private const string SpecPath = "docs/specs/SPEC-006-keys-and-signing.md";

    /// <summary>The ending every residual assertion carries. A fact here going red is news about a
    /// FIX, and the author of that fix must be told what else moves with it — otherwise the three
    /// artefacts drift apart and the spec goes on disclosing a hole that is closed, or a closed
    /// hole keeps a demonstration nobody can read.</summary>
    private const string DeleteTogether =
        "When this hole closes this fact goes RED, and that is the intended signal: delete the fact, "
        + "its row in Residuals, and the sentence in SPEC-006, in one commit.";

    /// <summary>The claim deleted from every document in this repository. It was FALSE: the marker
    /// lives in the directory being attacked, so an actor who strips every signature deletes it too
    /// and the keyed reader is left with no anchor either. Safe to spell literally because the scan
    /// in <see cref="The_false_marker_claim_appears_nowhere_under_docs"/> is rooted at <c>docs/</c>
    /// and this file is under <c>src/</c> — invisible to its own scan by SCOPE, not by obfuscation.
    /// Widening that root means concatenating it the way
    /// <see cref="ProcessGlobalEnvironmentConventionTests"/> spells its markers.</summary>
    private const string FalseMarkerClaim = "a keyed reader still catches it through the marker its key wrote";

    private static readonly DateTimeOffset T0 = new(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _root;
    private readonly string _state;
    private readonly string _stateA;
    private readonly string _stateB;
    private readonly string _keyDir;
    private readonly string _otherKeyDir;
    private readonly string _noKeys;
    private readonly string? _previousKeyDir;

    public GateSignatureResidualTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "gate-residual-" + Guid.NewGuid().ToString("N"));
        _state = Path.Combine(_root, ".ashlar");
        _stateA = Path.Combine(_root, "sibling-a", ".ashlar");
        _stateB = Path.Combine(_root, "sibling-b", ".ashlar");
        _keyDir = Path.Combine(_root, "keys");
        _otherKeyDir = Path.Combine(_root, "other-keys");
        _noKeys = Path.Combine(_root, "no-keys");
        Directory.CreateDirectory(_state);
        Directory.CreateDirectory(_stateA);
        Directory.CreateDirectory(_stateB);
        // GateStore's constructor reads this directory for key material, so it must exist and be
        // EMPTY: a keyless reader is one whose key directory yields nothing, not one that throws.
        Directory.CreateDirectory(_noKeys);
        _previousKeyDir = Environment.GetEnvironmentVariable(KeyDirVariable);
        Keyless();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(KeyDirVariable, _previousKeyDir);
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    // ─────────────────────── the residual inventory ───────────────────────

    /// <summary>One hole rule S-6 does not close.</summary>
    /// <param name="Phrase">The shortest distinctive fragment of the SPEC-006 sentence that
    /// discloses it. Spelled mid-sentence, with no em-dash and no backtick, so markdown emphasis
    /// and a sentence-initial capital cannot break an ordinal match after flattening.</param>
    /// <param name="FactName">The fact on this class that executes it, or null when no honest fact
    /// can be written.</param>
    /// <param name="WhyNotExecutable">Required when <paramref name="FactName"/> is null, and empty
    /// otherwise. Without it a row with no fact is indistinguishable from a fact somebody
    /// deleted.</param>
    private sealed record Residual(string Phrase, string? FactName, string WhyNotExecutable);

    private static readonly IReadOnlyList<Residual> Residuals =
    [
        new("leaves no anchor in the state root at all",
            nameof(A_total_strip_with_the_marker_deleted_reads_clean_and_plain_activation_blesses_it), ""),

        new("moved together with the records it names is not bounded",
            nameof(A_sibling_marker_carried_with_its_records_imports_verdicts_the_victim_never_made), ""),

        new("no store identity to deny a record that names another",
            nameof(A_signed_admission_copied_into_a_store_with_no_marker_of_its_own_still_anchors_it), ""),

        // The sibling of the row above, and the distinction is the VICTIM: there the store has no
        // identity, here the store has a perfectly good one and the READER may not believe it.
        new("a keyless consumer gets no S-7 protection",
            nameof(A_keyless_reader_counts_a_siblings_copy_the_stores_own_marker_would_deny), ""),

        new("trusted for the bytes it had at activation and for nothing else",
            nameof(A_record_planted_before_activation_is_grandfathered_permanently), ""),

        new("can never be dated by a derived anchor",
            nameof(With_the_marker_deleted_the_earliest_record_cannot_be_dated_by_a_derived_anchor), ""),

        new("counts what is present and nothing records what should be",
            nameof(Deleting_a_record_raises_the_budget_and_erases_a_refusal), ""),

        new("against an actor who cannot sign, and against nothing else",
            nameof(A_keyless_reader_honours_a_store_whose_every_anchor_the_attacker_signed), ""),

        new("trust root is a directory, and nothing signs it",
            nameof(A_public_key_dropped_into_trusted_makes_a_forged_signature_verify_and_pin), ""),

        new("marker nobody can read halts every read and every write",
            nameof(An_unreadable_marker_halts_the_store_until_it_is_deleted_by_hand), ""),

        new("misnamed record file holds a store permanently un-signable",
            nameof(One_misnamed_record_file_holds_the_store_permanently_un_signable), ""),

        new("pinning set is ambient process state",
            nameof(The_same_bytes_get_two_verdicts_under_two_key_directories), ""),

        new("it is the clock the caller supplied",
            nameof(The_activation_instant_is_whatever_clock_the_caller_supplies), ""),

        new("whose key is stolen can mint any instant and bless any set",
            null,
            "A test of this would load the operator's key, sign with it, and observe that the store "
            + "accepts what that key signed — which asserts that the system WORKS, not that it "
            + "fails. There is no arrangement in which a correctly-used key produces a refusal, so "
            + "a fact here would be a passing assertion about a hole, which is the covered debt "
            + "this whole class exists to refuse. SPEC-006 section 5 already says there is no "
            + "protection against a compromised operator machine; the sentence in S-6 names what "
            + "that costs the inventory and the marker specifically."),

        new("between the record and the amendment leaves a stale inventory entry",
            null,
            "Two reasons, and both matter. (1) The race cannot be reproduced deterministically on "
            + "the cert-gate runner: it needs the marker to be unreadable for the instant between "
            + "WriteAsync moving the record into place and Amend re-signing it, which means holding "
            + "the file with a share-mode denial that is enforced differently across platforms — a "
            + "fact built on that would be green on the Linux runner and red on a developer's box, "
            + "the exact shape Gate_store_kernel_facts_pin_the_key_directory exists to refuse. "
            + "(2) Any non-racing reconstruction plants the stale marker with the operator's own "
            + "key and then observes the store honouring what that key signed, which is the same "
            + "assert-the-system-works problem as the row above. The exposure is real and narrow: "
            + "the store lock is gates/.lock under the state root and the marker is a SIBLING of "
            + "gates/, so the amendment runs outside the lock, and the ordering was chosen "
            + "deliberately because the reverse leaves an unsigned record with no entry and refuses "
            + "the whole store. It is a downgrade-only window, and re-attempting the amendment on "
            + "the next keyed write would close it."),
    ];

    /// <summary>Facts that bind and scan rather than execute an attack, so the reverse arm of the
    /// inventory binding does not demand a row for them.</summary>
    private static readonly HashSet<string> Companions = new(StringComparer.Ordinal)
    {
        nameof(SPEC_006_names_every_residual_this_file_demonstrates),
        nameof(Every_residual_row_names_a_fact_on_this_class_or_says_why_it_cannot),
        nameof(No_residual_fact_is_skipped),
        nameof(The_false_marker_claim_appears_nowhere_under_docs),
    };

    /// <summary>Every <c>[Fact]</c> declared on this class. <c>TheoryAttribute</c> derives from
    /// <c>FactAttribute</c>, so one lookup finds both shapes.</summary>
    private static IEnumerable<MethodInfo> FactMethods() =>
        typeof(GateSignatureResidualTests)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttributes(typeof(FactAttribute), inherit: false).Length > 0);

    // ─────────────────────────── fixtures ───────────────────────────

    /// <summary>The reader holds no key material at all: it verifies intrinsically only.</summary>
    private void Keyless() => Environment.SetEnvironmentVariable(KeyDirVariable, _noKeys);

    /// <summary>The reader's key directory is the one the operator key lives in.</summary>
    private void Keyed() => Environment.SetEnvironmentVariable(KeyDirVariable, _keyDir);

    private GateStore Store(SigningIdentity? signer = null) => new(_state, signer);

    /// <summary>A store over a SIBLING state root, for the transplant facts.</summary>
    private static GateStore StoreAt(string stateRoot, SigningIdentity? signer = null) =>
        new(stateRoot, signer);

    private string RecordFile(string id) => RecordFileIn(_state, id);

    private static string RecordFileIn(string stateRoot, string id) =>
        Path.Combine(stateRoot, "gates", id + ".json");

    private string MarkerFile => GateSigningActivation.PathFor(_state);

    private static ExtensionProposal Proposal(string id) => new()
    {
        Id = id,
        Kind = "brick",
        Summary = "add brick residual.demo",
        ProposedBy = "night-agent",
        ProposedAt = T0,
        Courses = [new CourseResult { Name = "sandbox", Passed = true, Detail = "confined" }],
    };

    private static AdmissionOutcome Held(string reason = "holding for review") =>
        new() { State = ProposalState.Held, Reason = reason };

    /// <summary>The attack: delete both signature fields and leave everything else
    /// byte-identical. Asserts the record WAS signed, so it cannot silently no-op on an unsigned
    /// one and leave a fact proving nothing.</summary>
    private void Strip(string id)
    {
        var node = (JsonObject)JsonNode.Parse(File.ReadAllText(RecordFile(id)))!;
        node.Remove("Sig").Should().BeTrue(
            "the record on disk must have been signed for stripping to mean anything");
        node.Remove("Signer").Should().BeTrue(
            "both signature fields go, or the record is left in a shape the store never writes");
        File.WriteAllText(RecordFile(id), node.ToJsonString());
    }

    /// <summary>The other attack: replace the signature with one from a key of the forger's own.
    /// Works on an unsigned record too, which is what the keyless double-anchor fact needs.</summary>
    private void ReSign(string id, SigningIdentity forger)
    {
        var record = JsonSerializer.Deserialize<GateRecord>(File.ReadAllText(RecordFile(id)), Json)!;
        var unsigned = record with { Sig = null, Signer = null };
        var forged = unsigned with
        {
            Sig = forger.Sign(CanonicalJson.Bytes(unsigned)),
            Signer = forger.PublicKeyBase64,
        };
        File.WriteAllText(RecordFile(id), JsonSerializer.Serialize(forged, Json));
        SignerOnDisk(id).Should().Be(forger.PublicKeyBase64,
            "the re-signature must really be on disk, or every assertion built on it is vacuous");
    }

    /// <summary>A record nobody decided, written straight into <c>gates/</c> at <c>{id}.json</c>
    /// with no signature — the shape an actor who can write the directory produces.</summary>
    private void Fabricate(string id, ProposalState state, DateTimeOffset decidedAt) =>
        UnsignedAt(id + ".json", id, state, decidedAt);

    /// <summary>An unsigned record written to an arbitrary FILE NAME holding an arbitrary id, so
    /// the two can be made to disagree. That state is representable only in a store that has never
    /// been signed: activation refuses to mint over it, and the file-name leg refuses every read
    /// once a marker lands.</summary>
    private void UnsignedAt(string fileName, string id, ProposalState state, DateTimeOffset decidedAt)
    {
        var record = new GateRecord
        {
            Proposal = Proposal(id),
            State = state,
            Reason = "fabricated by hand",
            Actor = "gate",
            DecidedAt = decidedAt,
        };
        Directory.CreateDirectory(Path.Combine(_state, "gates"));
        var path = Path.Combine(_state, "gates", fileName);
        File.WriteAllText(path, JsonSerializer.Serialize(record, Json));
        ((JsonObject)JsonNode.Parse(File.ReadAllText(path))!)["Proposal"]!["Id"]!.GetValue<string>()
            .Should().Be(id, "the plant must really carry the id this fact plants, or nothing is planted");
    }

    /// <summary>Edits one top-level field of an UNSIGNED record in place, and CHECKS the edit
    /// landed on the field it named. The repo's JSON keys are PascalCase: written
    /// <c>node["state"]</c> this adds a second, lower-cased property and leaves <c>State</c>
    /// alone, so the plant never changes and the fact passes against broken code. That happened on
    /// this branch; the assertion is why it cannot happen again.</summary>
    private void Rewrite(string id, string field, string value)
    {
        var node = (JsonObject)JsonNode.Parse(File.ReadAllText(RecordFile(id)))!;
        node[field] = value;
        File.WriteAllText(RecordFile(id), node.ToJsonString());
        ((JsonObject)JsonNode.Parse(File.ReadAllText(RecordFile(id)))!)[field]!.GetValue<string>()
            .Should().Be(value, "the rewrite must really have moved the field this fact names");
    }

    /// <summary>The <c>Signer</c> a record file carries right now, or null when it carries
    /// none — read as JSON rather than through the store, because the store is what is under
    /// test.</summary>
    private string? SignerOnDisk(string id) =>
        ((JsonObject)JsonNode.Parse(File.ReadAllText(RecordFile(id)))!)["Signer"]?.GetValue<string>();

    private static string MarkerJson(GateSigningActivation marker) => JsonSerializer.Serialize(marker, Json);

    // ─────────────────────────── the residuals ───────────────────────────

    /// <summary>
    /// RESIDUAL. An actor who strips EVERY signature and deletes the activation marker leaves no
    /// anchor in the state root at all, so the store resolves to NEVER SIGNED: it reads clean for a
    /// keyed reader exactly as for a keyless one, and the self-extension budget is spendable again.
    ///
    /// <para><b>There is no keyed-reader advantage here, and an earlier draft claimed there was.</b>
    /// A keyed reader holds PUBLIC key material, not a memory of this store. The marker its key
    /// wrote lives in the directory being attacked, so the actor deletes that too and the keyed
    /// reader is left with no anchor either.
    /// <see cref="The_false_marker_claim_appears_nowhere_under_docs"/> keeps the deleted sentence
    /// deleted; this fact is why it was false.</para>
    ///
    /// <para><b>The second half is the sharp edge of the deleted-marker guard.</b>
    /// <c>GateSigningActivation.Activate</c> refuses to mint over a missing marker when
    /// <c>verifyingRecordAnchors &gt; 0</c>, so the operator's plain verb cannot be the way around
    /// the kernel's refusal. That guard keys off records whose signatures STILL VERIFY — so it
    /// stops the actor who deleted only the marker, and does nothing at all against the one who
    /// stripped the signatures too, which drives the count to zero. Plain
    /// <c>ashlar gates sign-activate</c> then mints a fresh inventory over the stripped store and
    /// permanently blesses every record in it under the operator's own key. The guard protects
    /// against the actor who was lazy, and S-6 must say so rather than claim a defence this
    /// residual walks through.</para>
    /// </summary>
    [Fact]
    public async Task A_total_strip_with_the_marker_deleted_reads_clean_and_plain_activation_blesses_it()
    {
        Keyed();
        var signer = OperatorKey.Generate(_keyDir);
        var store = Store(signer);
        await store.RecordAsync(Proposal("ext-1"), Held(), T0);
        await store.DecideAsync("ext-1", admit: true, "alice", "seated", T0.AddMinutes(1));
        await store.RecordAsync(Proposal("ext-2"), Held(), T0.AddHours(1));
        File.Exists(MarkerFile).Should().BeTrue("the first keyed write into an empty store activates it");

        Strip("ext-1");
        Strip("ext-2");
        File.Delete(MarkerFile);
        File.Exists(MarkerFile).Should().BeFalse("the attack must really have removed the marker anchor");

        var keyed = Store(signer);
        (await keyed.ListAsync()).Should().HaveCount(2,
            "RESIDUAL, disclosed in SPEC-006 S-6: with every signature stripped there is no derived "
            + "anchor, and with the marker gone there is no marker anchor, so the store resolves to "
            + "never-signed and every leg of Refuse is scoped out. Closing this needs evidence from "
            + "outside the state root. " + DeleteTogether);
        (await keyed.GetAsync("ext-1"))!.State.Should().Be(ProposalState.Admitted,
            "and the stripped admission reads back as a decision, not as corruption");
        keyed.SignatureTrust!.Expected.Should().BeFalse(
            "and it is not a near miss: the store's own posture says it has never been signed, which "
            + "is the state every refusal S-6 adds is scoped out of");
        keyed.SignatureTrust.Basis.Should().Contain(
            "operator key present but this store has never been signed",
            "one of the two weak tells the residual sentence promises the operator. It is not a "
            + "defence, it is the only signal there is, and deleting it makes the disclosure false");
        (await keyed.AdmittedInWindowAsync(TimeSpan.FromHours(24), T0.AddHours(2))).Should().Be(1,
            "the expectation went false, so unsigned admissions count toward the self-extension "
            + "budget again: after this attack a fabricated admission can deny the budget and a "
            + "deletion can free it, exactly as before this rule existed");

        // The same bytes, a reader holding nothing, and not one file touched in between.
        Keyless();
        var keyless = Store();
        (await keyless.ListAsync()).Should().HaveCount(2,
            "a keyed reader holds PUBLIC key material, not a memory of this store, so it reads "
            + "exactly what a keyless one does. The claim that a keyed reader still catches this "
            + "through the marker its key wrote was false and is deleted");
        keyless.SignatureTrust!.Expected.Should().BeFalse(
            "the two readers agree, which is the point: there is no advantage to holding the key "
            + "material, because the marker that key wrote was in the directory being attacked");

        // The second weak tell, and then the hole in the guard behind it.
        Keyed();
        var write = async () => await Store(signer).RecordAsync(Proposal("ext-3"), Held(), T0.AddHours(3));
        (await write.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*never been signed*sign-activate*",
                "the next keyed write refuses rather than re-activating, and names the verb whose "
                + "printed grandfather count should be ZERO on a store the operator believes has "
                + "been signing");

        var blessed = await Store(signer).ActivateSigningAsync(T0.AddHours(3));   // NO repair flag
        blessed.Marker.Grandfathered!.Select(g => g.Id).Should().BeEquivalentTo(new[] { "ext-1", "ext-2" },
            "RESIDUAL, and the sharp edge of the deleted-marker guard: it refuses a plain re-mint "
            + "only when records whose signatures still VERIFY prove the store was signing. An "
            + "actor who stripped every signature drove that count to zero, so the plain verb mints "
            + "freely and permanently blesses the stripped records under the operator's own key. "
            + DeleteTogether);
        blessed.GrandfatheredAdmitted.Should().Be(1,
            "and the printed count is the whole of what stands between the operator and blessing a "
            + "stripped admission: a number that should have been zero here");
    }

    /// <summary>
    /// RESIDUAL. A marker is signed over its own payload, and the store identity S-7 put inside it is
    /// SELF-ASSERTED — a transplanted marker names its own store consistently — so a reader still
    /// cannot tell a marker it wrote from one it did not. Transplanted ALONE from a sibling store
    /// under the same operator key it is denial of service — its inventory names the sibling's record
    /// ids, so the victim's own unsigned records are refused. Moved TOGETHER with the records it
    /// names it is not bounded at all: the victim store accepts the sibling's unsigned verdicts
    /// verbatim, and an Admitted one among them spends the victim's self-extension budget.
    ///
    /// <para><b>The first clause used to read "carries no store identity", and S-7 made that
    /// false.</b> The marker now carries one. What survives — and what this residual actually rests
    /// on — is that the identity is the marker's own claim about itself, so moving the marker moves
    /// the claim with it and the imported records name exactly the store the imported marker
    /// declares. The identical sentence in SPEC-006 was corrected the same way; this copy is the one
    /// the correction missed, and nothing pins it, so it is written here as prose a reader can check
    /// against <see cref="GateSigningActivation.StoreId"/>.</para>
    ///
    /// <para><b>This fact exists because the sentence it pins used to be wrong.</b> An earlier
    /// draft called the transplant "denial of service against an honestly-adopted store, not
    /// acceptance of a forgery". That is false when the records travel with the marker: the id
    /// matches the inventory, the canonical hash matches, the file name matches the id, and every
    /// leg of <c>Refuse</c> passes. The corrected sentence needs teeth, so both halves are executed
    /// here.</para>
    /// </summary>
    [Fact]
    public async Task A_sibling_marker_carried_with_its_records_imports_verdicts_the_victim_never_made()
    {
        Keyless();
        var a0 = StoreAt(_stateA);
        await a0.RecordAsync(Proposal("ext-a1"), Held(), T0);
        await a0.DecideAsync("ext-a1", admit: true, "alice", "seated", T0.AddMinutes(1));   // unsigned Admitted
        await StoreAt(_stateB).RecordAsync(Proposal("ext-b1"), Held(), T0);                 // unsigned Held

        var signer = OperatorKey.Generate(_keyDir);
        Keyed();
        await StoreAt(_stateA, signer).ActivateSigningAsync(T0.AddHours(1));   // marker A names ext-a1
        await StoreAt(_stateB, signer).ActivateSigningAsync(T0.AddHours(1));   // marker B names ext-b1

        // ── the marker alone: bounded by its own inventory, and the bound is denial of service ──
        File.Copy(GateSigningActivation.PathFor(_stateA), GateSigningActivation.PathFor(_stateB), overwrite: true);
        GateSigningActivation.TryRead(_stateB)!.Grandfathered!.Select(g => g.Id).Should()
            .BeEquivalentTo(new[] { "ext-a1" },
                "the transplant must really have replaced B's marker with A's, or both halves below "
                + "are assertions about a store nobody attacked");

        var list = async () => await StoreAt(_stateB, signer).ListAsync();
        (await list.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*ext-b1.json*not among them*",
                "the inventory BOUNDS a marker moved alone: it names the sibling's record ids, so "
                + "every unsigned record in the victim store is refused and the effect is denial of "
                + "service against a store that was adopted honestly");

        // ── the records travel with it: nothing bounds it at all ──
        File.Copy(RecordFileIn(_stateA, "ext-a1"), RecordFileIn(_stateB, "ext-a1"));
        File.Delete(RecordFileIn(_stateB, "ext-b1"));

        var b = StoreAt(_stateB, signer);
        (await b.GetAsync("ext-a1")).Should().NotBeNull(
            "RESIDUAL: the victim store accepts an unsigned verdict its operator never made in it. "
            + "The id matches the transplanted inventory, the canonical hash matches, and the file "
            + "name matches the id, so every leg of Refuse passes. Closed for records written after "
            + "S-7 by the StoreId inside their signed bytes; a copy of an ENTIRE store stays "
            + "indistinguishable from the original. " + DeleteTogether);
        (await b.AdmittedInWindowAsync(TimeSpan.FromHours(24), T0.AddHours(2))).Should().Be(1,
            "and it spends the victim's self-extension budget: AdmittedInWindowAsync excludes "
            + "unsigned records only on the derived-date basis, and an inventory fired here");
    }

    /// <summary>
    /// RESIDUAL, NARROWED BY S-7 AND STILL OPEN. A signed record now names the store it was decided
    /// in inside its signed bytes, and a store that can say which store IT is refuses a copy from a
    /// sibling — that closure is
    /// <see cref="GateRecordStoreIdentityTests.A_record_signed_for_another_store_is_refused_as_an_anchor_in_this_one"/>.
    /// What survives is the victim this fact uses: a store with NO honoured marker has no attested
    /// identity to deny the copy with, so the imported verdict still verifies, pins, ANCHORS a store
    /// that had never been signed, creating an expectation where there was none, and still counts
    /// toward that store's self-extension budget.
    ///
    /// <para>The identity lives in the marker because that is the one artefact in the state root the
    /// operator's key signs — and the marker is inside the directory being attacked, which is the
    /// first residual in this file restated in a second place. A never-signed store has none to
    /// begin with; a store whose marker was deleted, or one carrying a marker planted under a
    /// foreign key, has none this reader may believe. Group 3's file-name leg does not bite either:
    /// the copy keeps its own <c>{id}.json</c> name.</para>
    ///
    /// <para>Two further survivals are NOT demonstrated here and are disclosed in SPEC-006 instead:
    /// a record signed BEFORE <c>StoreId</c> existed stays transplantable for its lifetime (its
    /// accepting half is a passing fact in
    /// <see cref="GateRecordStoreIdentityTests.A_record_signed_before_the_store_identity_existed_is_accepted_and_is_not_a_mismatch"/>),
    /// and a copy of an ENTIRE store carries a consistent identity and so remains
    /// indistinguishable from the original.</para>
    /// </summary>
    [Fact]
    public async Task A_signed_admission_copied_into_a_store_with_no_marker_of_its_own_still_anchors_it()
    {
        Keyed();
        var signer = OperatorKey.Generate(_keyDir);
        var a = StoreAt(_stateA, signer);
        await a.RecordAsync(Proposal("ext-a1"), Held(), T0);
        await a.DecideAsync("ext-a1", admit: true, "alice", "seated", T0.AddMinutes(1));

        var b0 = StoreAt(_stateB, signer);            // the constructor creates sibling-b/.ashlar/gates
        (await b0.ListAsync()).Should().BeEmpty(
            "B holds nothing at all before the transplant, so anything it accepts afterwards came "
            + "from the copied file and from nowhere else");
        b0.SignatureTrust!.Expected.Should().BeFalse(
            "B has never been signed — this fact demonstrates a CHANGE of posture, so the starting "
            + "posture has to be asserted rather than assumed");

        File.Copy(RecordFileIn(_stateA, "ext-a1"), RecordFileIn(_stateB, "ext-a1"));   // no marker travels

        // NON-VACUITY, and it is the whole hinge of this fact after S-7. The copied verdict must
        // really NAME the store it came from: without this assertion the acceptance below is equally
        // consistent with a build in which no record carries an identity at all, so the fact would
        // pass while reporting nothing about the residual it claims to demonstrate.
        var carried = JsonSerializer.Deserialize<GateRecord>(
            File.ReadAllText(RecordFileIn(_stateB, "ext-a1")), Json)!;
        carried.StoreId.Should().NotBeNull(
            "this half is what makes the guard bite. Both sides of the comparison below are null in "
            + "a build that mints no identity at all, and Be(null) PASSES — so without an explicit "
            + "non-null the guard would fail to exclude the exact state its own comment names")
            .And.Be(GateSigningActivation.TryRead(_stateA)!.StoreId,
            "the copy names A's identity inside its signed bytes, so what follows is about a victim "
            + "that cannot deny it rather than about a record that names nobody");

        var b = StoreAt(_stateB, signer);
        (await b.GetAsync("ext-a1")).Should().NotBeNull(
            "RESIDUAL: the copied record names the store it was signed for, and B has no marker, so "
            + "B has no identity of its own to deny it with. The identity is attested by the marker "
            + "and by nothing else, and a store with no marker this reader honours cannot say which "
            + "store it is. " + DeleteTogether);
        b.SignatureTrust!.SignedRecordAnchors.Should().Be(1,
            "worse than merely accepted: the imported verdict is now an ANCHOR, so it creates an "
            + "expectation in a store that had none and every other record in B is judged against it");
        b.SignatureTrust.Expected.Should().BeTrue(
            "B expected nothing a moment ago and expects signatures now, with one copied file as the "
            + "whole cause — that CHANGE is the residual, not the acceptance alone");
        b.SignatureTrust.MarkerPresent.Should().BeFalse(
            "no marker travelled with it; the record did all of this on its own");
        (await b.AdmittedInWindowAsync(TimeSpan.FromHours(24), T0.AddHours(1))).Should().Be(1,
            "and it spends B's self-extension budget");
    }

    /// <summary>
    /// RESIDUAL, and the one S-7 chose to leave open rather than close wrongly. The victim is NOT a
    /// store missing an identity: B adopted signing honestly, its marker is the operator's own, it
    /// names a store, and a KEYED reader of these exact bytes refuses the copy. What has no
    /// protection is the READER. A consumer holding no key material binds no identity, so the same
    /// three files read clean, the imported admission anchors B through the single-record funnel as
    /// well as the list, and it spends B's self-extension budget.
    ///
    /// <para><b>Why the bound is deliberate and not a fourth accident.</b> A keyless reader honours a
    /// marker that a record under the marker's OWN key corroborates, and an actor who can write the
    /// state root writes both halves of that pair — so an identity taken from there would be the
    /// ATTACKER's, and the records then refused as copies would be the store's own genuine verdicts,
    /// named by the one refusal whose remedy text tells the operator to delete them. That inversion
    /// is measured from the other side by
    /// <see cref="GateRecordStoreIdentityTests.A_keyless_reader_still_reads_its_own_records_under_a_planted_marker_and_record"/>.
    /// This fact is its price: the two read one guard in opposite directions, so the same change
    /// moves both, and the pair is the whole argument that the trade was made knowingly.</para>
    ///
    /// <para><b>Why the inventory wants its OWN fact rather than borrowing that one.</b> That fact
    /// asserts the store still WORKS — a positive property, green today and green under any future
    /// route to keyless protection. The residual is the LOST refusal, and only an inverted fact goes
    /// red when it is regained. <see cref="Residuals"/> also binds rows to facts declared on THIS
    /// class in both directions, so a row naming a fact on another class fails
    /// <see cref="Every_residual_row_names_a_fact_on_this_class_or_says_why_it_cannot"/> as a ghost.
    /// The row and this fact are therefore both new, and neither is a duplicate of the other
    /// class's.</para>
    ///
    /// <para><b>The keyed contrast is inside the same fact on purpose</b>, exactly as
    /// <see cref="A_keyless_reader_honours_a_store_whose_every_anchor_the_attacker_signed"/> does it.
    /// Without it this reads as the no-marker residual above wearing a different hat, and the
    /// sentence it pins is about the reader rather than about the store.</para>
    /// </summary>
    [Fact]
    public async Task A_keyless_reader_counts_a_siblings_copy_the_stores_own_marker_would_deny()
    {
        Keyed();
        var signer = OperatorKey.Generate(_keyDir);
        var a = StoreAt(_stateA, signer);
        await a.RecordAsync(Proposal("ext-a1"), Held(), T0);
        await a.DecideAsync("ext-a1", admit: true, "alice", "seated in A", T0.AddMinutes(1));

        // B adopts signing honestly — the marker is minted under the operator's own key by the first
        // keyed write, and B's own signed record corroborates it. This is the victim the no-marker
        // residual above does NOT cover: B can say which store it is to anybody holding the key
        // material to ask.
        await StoreAt(_stateB, signer).RecordAsync(Proposal("ext-b1"), Held(), T0.AddMinutes(2));

        var identityA = GateSigningActivation.TryRead(_stateA)!.StoreId;
        var identityB = GateSigningActivation.TryRead(_stateB)!.StoreId;
        identityA.Should().NotBeNullOrEmpty(
            "the copy must really name the store it came from, or it is a pre-S-7 record and this "
            + "fact measures the compatibility bound instead");
        identityB.Should().NotBeNullOrEmpty(
            "and B must really have an identity of its own. Both sides are null in a build that mints "
            + "none, and an assertion against null holds there too — so without this the residual "
            + "below would be indistinguishable from the no-marker one above")
            .And.NotBe(identityA,
            "the two must differ, or the acceptance below is about two names that happen to match "
            + "rather than about a refusal the reader could not make");

        File.Copy(RecordFileIn(_stateA, "ext-a1"), RecordFileIn(_stateB, "ext-a1"));   // no marker travels

        // The protection that DOES exist, on these exact bytes, so what follows is about the reader.
        var keyed = async () => await StoreAt(_stateB, signer).ListAsync();
        (await keyed.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*ext-a1.json is signed for store '" + identityA + "'*",
                "a reader holding key material refuses the copy under S-7 — so nothing about the "
                + "files on disk is what makes the read below succeed, and this residual is not the "
                + "no-marker one restated");

        // The same three files, read by a consumer that never ran `keys init`.
        Keyless();
        var keyless = StoreAt(_stateB);
        (await keyless.ListAsync()).Should().HaveCount(2,
            "RESIDUAL, normative in SPEC-006 under S-7 and recorded as a residual under S-6: a "
            + "keyless consumer gets no S-7 protection. The identity binds only off a marker the "
            + "reader holds KEY MATERIAL for, because a keyless reader's marker is corroborated by a "
            + "record under the marker's own key and an actor who can write the state root supplies "
            + "both halves. Closing this needs an identity attested from OUTSIDE the state root, "
            + "which is the same v2 work the first residual in this file names. " + DeleteTogether);
        keyless.SignatureTrust!.MarkerPresent.Should().BeTrue(
            "B's own genuine marker is right there — the victim of this residual is a store that HAS "
            + "an identity, which is what separates it from the row above");
        keyless.SignatureTrust.Expected.Should().BeTrue(
            "and the keyless reader HONOURS it, so the store resolves to signed and every other leg "
            + "of Refuse is in force. Only the S-7 leg is scoped out");
        keyless.SignatureTrust.Basis.Should().Contain("corroborated by a record under the same key",
            "by exactly the corroboration path that makes the identity unsafe to bind. Naming it is "
            + "what stops this fact drifting into a test of the ignored-marker arm");
        keyless.SignatureTrust.StoreId.Should().BeNull(
            "and binds no identity, which is the whole residual: an honoured marker that names the "
            + "store, and a reader with no grounds to believe what it names");
        (await keyless.GetAsync("ext-a1")).Should().NotBeNull(
            "so the imported verdict reads as a decision made here through the single-record funnel "
            + "too, and `ashlar gates --show` on a machine with no key material displays it");
        (await keyless.AdmittedInWindowAsync(TimeSpan.FromHours(24), T0.AddHours(1))).Should().Be(1,
            "and it spends B's self-extension budget, which is the consequence that outlives the read");
    }

    /// <summary>
    /// RESIDUAL. Activation mints the inventory from every record on disk that carries no
    /// signature, reading the store with <c>judge: false</c> and with no test of provenance
    /// whatsoever — so a fabricated Admitted record planted BEFORE the operator activates is
    /// grandfathered permanently, counted by <see cref="GateStore.AdmittedInWindowAsync"/>, and
    /// displayed by <c>ashlar gates --show</c> as a decision nobody made. Nothing ever re-examines
    /// an entry; <c>Amend</c> can only shrink the set.
    ///
    /// <para><b>The only control is a number, and this fact pins the field that number comes
    /// from.</b> <c>ashlar keys init</c> and <c>ashlar gates sign-activate</c> both print how many
    /// records they are grandfathering and how many of those are admitted, before their success
    /// line. Every other mechanism in S-6 is downstream of an operator reading that line. If
    /// <see cref="GateSigningOutcome.GrandfatheredAdmitted"/> stops being computed, or the CLI stops
    /// printing it, the residual's "the only control is" sentence becomes false — so it is asserted
    /// here rather than merely rendered.</para>
    /// </summary>
    [Fact]
    public async Task A_record_planted_before_activation_is_grandfathered_permanently()
    {
        Keyless();
        await Store().RecordAsync(Proposal("ext-1"), Held(), T0);          // one honest record: a plausible store
        Fabricate("ext-evil", ProposalState.Admitted, T0.AddMinutes(5));   // straight into gates/, Sig null

        OperatorKey.Generate(_keyDir);
        Keyed();
        var outcome = await Store(OperatorKey.TryLoad()).ActivateSigningAsync(T0.AddHours(1));

        outcome.Marker.Grandfathered!.Select(g => g.Id).Should().Contain("ext-evil",
            "RESIDUAL: activation reads with judge:false and mints one entry per unsigned record, "
            + "with no way to tell a planted record from an honest one. The closure is an "
            + "`ashlar gates sign-adopt` that re-signs the set under the operator key and empties "
            + "the inventory, and it is not built. " + DeleteTogether);
        outcome.GrandfatheredAdmitted.Should().Be(1,
            "the count printed before the success line is the ONLY control on this mechanism, and "
            + "here it is the operator's single chance to object");

        var keyed = Store(OperatorKey.TryLoad());
        (await keyed.GetAsync("ext-evil"))!.State.Should().Be(ProposalState.Admitted,
            "`ashlar gates --show` now displays a decision nobody made");
        (await keyed.AdmittedInWindowAsync(TimeSpan.FromHours(24), T0.AddHours(1))).Should().Be(1,
            "and it spends self-extension budget");

        var later = await Store(OperatorKey.TryLoad()).ActivateSigningAsync(T0.AddDays(30));
        later.WasAlreadyActive.Should().BeTrue(
            "thirty days later the operator re-runs the verb and is told the store is already "
            + "active — so this second call is the idempotent path, and what it returns is the "
            + "inventory that has been in force all along rather than a fresh scan");
        later.Marker.Grandfathered!.Select(g => g.Id).Should().Contain("ext-evil",
            "permanently: nothing re-examines an inventory entry, and Amend can only shrink the set");
    }

    /// <summary>
    /// RESIDUAL. A store with verifying records and no marker falls back to the derived date floor
    /// — the minimum <c>DecidedAt</c> over the records that still verify — and because that floor
    /// is the minimum over the SURVIVORS it sits at or before the earliest record, so it can never
    /// date that record. Stripping the earliest record's signature therefore leaves it
    /// grandfathered, readable, shown, and editable in place. It is never COUNTED, which is the one
    /// place this weak basis is treated as weak.
    ///
    /// <para><b>Read-path only, and the disclosure must say so or it reads as a live seating
    /// path.</b> A keyed write into this state refuses and names <c>--repair</c>, so no NEW verdict
    /// can be written while the store is in it. That bound is asserted here, in the same fact,
    /// because a residual sentence that overstates its own reach is as dishonest as one that hides
    /// a hole.</para>
    ///
    /// <para>This is the exact inverse of
    /// <c>GateSignatureExpectationTests.A_repaired_marker_cannot_move_the_grace_floor_forward</c> —
    /// the same arrangement with the repair omitted. The pair is what makes both non-vacuous.</para>
    /// </summary>
    [Fact]
    public async Task With_the_marker_deleted_the_earliest_record_cannot_be_dated_by_a_derived_anchor()
    {
        Keyed();
        var signer = OperatorKey.Generate(_keyDir);
        var seed = Store(signer);
        await seed.RecordAsync(Proposal("ext-1"), Held(), T0);
        await seed.RecordAsync(Proposal("ext-2"), Held(), T0.AddHours(1));

        File.Delete(MarkerFile);
        Strip("ext-1");                                   // the EARLIEST record
        SignerOnDisk("ext-2").Should().Be(signer.PublicKeyBase64,
            "ext-2 must still verify, or there is no derived anchor and this fact is about a "
            + "never-signed store instead");

        var store = Store(signer);
        (await store.ListAsync()).Should().HaveCount(2,
            "RESIDUAL: stripping the earliest record removes it from the anchor set, the floor rises "
            + "to the second-earliest, and the stripped record now sits strictly below it. "
            + DeleteTogether);
        (await store.GetAsync("ext-1")).Should().NotBeNull(
            "both funnels agree, so `ashlar gates --show` displays the stripped record too — the "
            + "residual is a READ, and a read is what an operator acts on");
        store.SignatureTrust!.DerivedGraceBefore.Should().Be(T0.AddHours(1),
            "the floor rose to the survivor, leaving the record it should have dated below it");
        store.SignatureTrust.Grandfathered.Should().BeNull(
            "null, not empty: no marker fired, so this is the WEAK derived basis and not an "
            + "operator authorization. The two are different answers and the store must keep "
            + "telling them apart");

        Rewrite("ext-1", "State", nameof(ProposalState.Admitted));
        var after = Store(signer);
        (await after.ListAsync()).Should().HaveCount(2,
            "an unsigned record below a derived floor can be edited in place: DecidedAt is "
            + "unchanged, and DecidedAt is all this basis looks at");
        (await after.AdmittedInWindowAsync(TimeSpan.FromHours(24), T0.AddHours(2))).Should().Be(0,
            "listed and shown, never counted — the budget excludes unsigned records on exactly this "
            + "basis, because here grandfathering is still the writer's own input");

        var write = async () => await Store(signer).RecordAsync(Proposal("ext-3"), Held(), T0.AddHours(3));
        (await write.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*gate-signing.json is missing*sign-activate --repair*",
                "the bound that makes this a READ-path residual: no new verdict can be seated while "
                + "the store is in this state, and the disclosure sentence must say so");
    }

    /// <summary>
    /// RESIDUAL. <see cref="GateStore.AdmittedInWindowAsync"/> counts what is present and nothing
    /// records what should be, so every deleted admission frees one unit of self-extension budget —
    /// and a deletion reads as an ABSENCE rather than as corruption, so a deleted Held proposal
    /// vanishes from the queue with no tell at all.
    ///
    /// <para><b>The second half is larger than budget inflation.</b> Append-once is guarded by the
    /// record file existing, under the store lock. Remove <c>gates/{id}.json</c> and the same id can
    /// be proposed, held and admitted again — so a Refused verdict, which SPEC-004 calls immutable
    /// history with no administrative path to re-decide it, is erasable by anyone who can delete a
    /// file. The resulting admission is signed by the operator's own key and is cryptographically
    /// indistinguishable from an honest one.</para>
    ///
    /// <para>The cure that looks obvious is wrong, and the spec sentence says why: a monotone
    /// high-water mark enforced by refusing every read turns one deletion into a store-wide refusal
    /// whose only exit is an operator command that authorizes precisely the budget the deletion was
    /// stealing. Only SPEC-003's chained append-only ledger closes this.</para>
    /// </summary>
    [Fact]
    public async Task Deleting_a_record_raises_the_budget_and_erases_a_refusal()
    {
        Keyed();
        var signer = OperatorKey.Generate(_keyDir);
        var store = Store(signer);
        await store.RecordAsync(Proposal("ext-1"), Held(), T0);
        await store.DecideAsync("ext-1", admit: true, "alice", "seated", T0.AddMinutes(1));
        await store.RecordAsync(Proposal("ext-2"), Held(), T0.AddMinutes(2));
        await store.DecideAsync("ext-2", admit: true, "alice", "seated", T0.AddMinutes(3));
        await store.RecordAsync(Proposal("ext-3"), Held(), T0.AddMinutes(4));
        await store.DecideAsync("ext-3", admit: false, "alice", "out of envelope", T0.AddMinutes(5));
        (await store.AdmittedInWindowAsync(TimeSpan.FromHours(24), T0.AddHours(1))).Should().Be(2,
            "two honest admissions and one refusal, all signed — the state this attack starts from");

        // ── one deletion, one unit of budget, and no tell ──
        File.Delete(RecordFile("ext-1"));
        File.Exists(RecordFile("ext-1")).Should().BeFalse("the deletion must really have happened");

        (await store.ListAsync()).Should().HaveCount(2,
            "RESIDUAL: no reader notices the gap — ReadStoreAsync enumerates what exists, the "
            + "anchors merely shrink, and the inventory names only unsigned records. "
            + DeleteTogether);
        (await store.GetAsync("ext-1")).Should().BeNull(
            "a deletion reads as an ABSENCE, not as corruption. There is no tell at all, and a "
            + "deleted HELD proposal would simply vanish from the queue");
        (await store.AdmittedInWindowAsync(TimeSpan.FromHours(24), T0.AddHours(1))).Should().Be(1,
            "one deletion, one unit of self-extension budget");

        // ── and the refusal itself is erasable, which is the larger half ──
        File.Delete(RecordFile("ext-3"));
        await store.RecordAsync(Proposal("ext-3"), Held(), T0.AddHours(2));
        var relaunched = await store.DecideAsync(
            "ext-3", admit: true, "mallory", "second thoughts", T0.AddHours(3));

        relaunched.State.Should().Be(ProposalState.Admitted,
            "RESIDUAL, and stronger than budget inflation: append-once is guarded on the record file "
            + "existing, so deleting a Refused record lets the same id be proposed again from "
            + "nothing and admitted. SPEC-004 says there is no administrative path to re-decide a "
            + "refusal; a deletion is that path, and the laundered admission is signed by the "
            + "operator's own key. " + DeleteTogether);
        relaunched.Sig.Should().NotBeNull(
            "signed by the operator's own key, so nothing downstream can tell it from an honest "
            + "admission — which is what makes this worse than a missing record");
        (await store.AdmittedInWindowAsync(TimeSpan.FromHours(24), T0.AddHours(4))).Should().Be(2,
            "the surviving honest admission plus the laundered one");
    }

    /// <summary>
    /// RESIDUAL. A keyless reader is protected against an actor who cannot sign, and against
    /// nothing else. With no key material the marker is honoured when ANY record's signer matches
    /// it, so an actor who re-signs every record under a key of their own and plants a matching
    /// marker supplies BOTH anchors at once: the store resolves to signed, the basis reads
    /// "corroborated by a record under the same key", and a fabricated Admitted record signed by
    /// that key verifies, pins against an empty pinning set, and spends the budget.
    ///
    /// <para>This is not the total-stripping residual above but its opposite — an attacker who
    /// signs EVERYTHING — and no bundle consumer can tell the two stores apart without key material
    /// of its own. The contrast is executed in the same fact so the sentence is exact: a KEYED
    /// reader refuses the identical bytes, because its pinning set is non-empty and the attacker's
    /// key is not in it.</para>
    ///
    /// <para>The keyless corroboration path is deliberate and is not a defect — a keyless reader
    /// that ignored every marker could not be given the protection at all, and one that honoured
    /// every marker could be bricked by a plant. This sentence is the price, and it had never been
    /// written down before.</para>
    /// </summary>
    [Fact]
    public async Task A_keyless_reader_honours_a_store_whose_every_anchor_the_attacker_signed()
    {
        Keyless();
        await Store().RecordAsync(Proposal("ext-1"), Held(), T0);          // honest, unsigned
        Fabricate("ext-evil", ProposalState.Admitted, T0.AddMinutes(5));

        var attacker = OperatorKey.Generate(_otherKeyDir);
        ReSign("ext-1", attacker);
        ReSign("ext-evil", attacker);
        File.WriteAllText(MarkerFile,
            MarkerJson(GateSigningActivation.Signed(attacker, T0.AddHours(-1), [], storeId: null)));
        GateSigningActivation.TryRead(_state)!.Signer.Should().Be(attacker.PublicKeyBase64,
            "the planted marker must really carry the attacker's key, or the corroboration path "
            + "this fact pins is never reached");

        Keyless();
        var r = Store();
        (await r.ListAsync()).Should().HaveCount(2,
            "RESIDUAL: with no key material ResolveExpectation honours the marker because a record "
            + "in the store carries the same signer — and the attacker wrote both. " + DeleteTogether);
        r.SignatureTrust!.Expected.Should().BeTrue(
            "the store resolves to SIGNED off two anchors the attacker supplied, which is what makes "
            + "these bytes indistinguishable from an honestly signed store to a keyless consumer");
        r.SignatureTrust.TrustedSigners.Should().BeEmpty(
            "there is no pinning set for the forgery to fail against");
        r.SignatureTrust.Basis.Should().Contain("corroborated by a record under the same key",
            "the basis names the corroboration path by which both anchors came from one attacker");
        (await r.AdmittedInWindowAsync(TimeSpan.FromHours(24), T0.AddHours(1))).Should().Be(1,
            "a decision nobody made, signed by a key nobody vouches for, spends the budget of a "
            + "store a bundle consumer cannot tell from an honest one");

        // The contrast, on the same bytes: the protection a keyless reader does not get.
        OperatorKey.Generate(_keyDir);
        Keyed();
        var pinned = async () => await Store().ListAsync();
        (await pinned.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*neither the operator key nor any key retained under trusted/*",
                "the same bytes, refused by a reader that holds key material. No file name is "
                + "pinned here on purpose: which record is named depends on enumeration, and the "
                + "refusal's identity is the PIN, not the file");
    }

    /// <summary>
    /// RESIDUAL. The reader's trust root is a DIRECTORY, recomputed from the filesystem in every
    /// <see cref="GateStore"/> constructor, and nothing signs it.
    /// <see cref="OperatorKey.TrustedPublicKeysBase64"/> unions <c>operator.pub</c> with every
    /// <c>trusted/*.pub</c> under the key directory, so an actor who can write that directory adds
    /// a trusted signer by dropping one file in — no key theft, no operator action — and every
    /// verify-and-pin leg in S-6 then passes for records they signed.
    ///
    /// <para><b>Why that directory is reachable.</b> <c>NativeBundle.StageApp</c> documents an
    /// operator who points <c>ASHLAR_KEY_DIR</c> inside the project, which is exactly the
    /// configuration in which the governed process can reach it. SPEC-006 section 5's premise that
    /// <c>keys/</c> is not written by the governed process does not hold there. The first residual
    /// above gestures at this while attaching it to the ABSENCE of an anchor; the exposure here is
    /// that the anchor's TRUST ROOT is writable, which is a different and larger thing.</para>
    ///
    /// <para><b>Why <c>trusted/</c> cannot simply be dropped.</b> It exists for key ROTATION — the
    /// superseded public key moves there so earlier records still verify. The closure is a trust
    /// root the governed process cannot write, not the removal of rotation.</para>
    ///
    /// <para>The defence is shown WORKING first, on the same bytes, so this fact is a demonstrated
    /// change of verdict rather than a state.</para>
    /// </summary>
    [Fact]
    public async Task A_public_key_dropped_into_trusted_makes_a_forged_signature_verify_and_pin()
    {
        Keyed();
        var honest = OperatorKey.Generate(_keyDir);
        await Store(honest).RecordAsync(Proposal("ext-1"), Held(), T0);   // signed; activates the marker

        var forger = OperatorKey.Generate(_otherKeyDir);
        Fabricate("ext-evil", ProposalState.Admitted, T0.AddHours(1));
        ReSign("ext-evil", forger);

        var before = async () => await Store(honest).ListAsync();
        (await before.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*neither the operator key nor any key retained under trusted/*",
                "S-3 pinning is what catches a REPLACED signature, and while trusted/ is clean it "
                + "does. Without this half the fact below would be a state, not a change");

        // One file, no key theft, no operator action.
        var trusted = Path.Combine(_keyDir, "trusted");
        Directory.CreateDirectory(trusted);
        File.Copy(Path.Combine(_otherKeyDir, "operator.pub"), Path.Combine(trusted, "forger.pub"));
        File.Exists(Path.Combine(trusted, "forger.pub")).Should().BeTrue(
            "the plant must really be on disk before a new reader is constructed over it");

        // A NEW store: the pinning set is resolved in the constructor and nowhere else.
        var reader = Store(honest);
        (await reader.ListAsync()).Should().HaveCount(2,
            "RESIDUAL: the trust set is recomputed from disk on every construction and nothing "
            + "signs it, so a .pub file dropped into trusted/ is a new root of trust. "
            + DeleteTogether);
        reader.SignatureTrust!.TrustedSigners.Should().Contain(forger.PublicKeyBase64,
            "the forger's key is now pinned key material, which is the whole mechanism");
        (await reader.GetAsync("ext-evil"))!.State.Should().Be(ProposalState.Admitted,
            "a verdict nobody made, signed by a key nobody issued, indistinguishable from the "
            + "honest record beside it");
        (await reader.AdmittedInWindowAsync(TimeSpan.FromHours(24), T0.AddHours(2))).Should().Be(1,
            "and it spends the self-extension budget");
    }

    /// <summary>
    /// RESIDUAL. The posture is resolved on EVERY operation, and a marker that is unparseable,
    /// unsigned or non-verifying throws there — so one junk file written into the state root, with
    /// no key material at all, refuses <see cref="GateStore.GetAsync"/>,
    /// <see cref="GateStore.ListAsync"/>, <see cref="GateStore.AdmittedInWindowAsync"/> and both
    /// write paths.
    ///
    /// <para><b><c>--repair</c> does not recover it, and that is not obvious.</b> Repair exists for
    /// a marker a reader refuses, and it reads the marker WITHOUT the inventory requirement — which
    /// is enough to get past a MISSING inventory, and not enough to get past a broken signature or
    /// invalid JSON. <see cref="GateStore.ActivateSigningAsync"/> performs that same read first, so
    /// <c>--repair</c> throws on the same file with the same message. The exit is two steps: delete
    /// the marker by hand, then <c>--repair</c>. Both steps are executed below, including that the
    /// re-mint is still dated by the surviving signature rather than by now.</para>
    ///
    /// <para><b>This fact asserts that the refusal NAMES that exit.</b> A product state with no
    /// named exit is not something a specification may record and walk away from — and naming it is
    /// three string literals. The assertion is here so the naming cannot be dropped as noise by
    /// somebody shortening a message.
    /// <c>GateSignatureExpectationTests.A_marker_nothing_can_read_names_the_exit_that_recovers_the_store</c>
    /// pins the same exit from the rule's side; this fact adds the breadth — all four entry points —
    /// that makes the residual sentence's "halts every read and every write" checkable.</para>
    ///
    /// <para>Why the behaviour itself stays: a marker this machine cannot verify must never be
    /// honoured, or anyone with write access bricks a keyless store by planting a far-past instant.
    /// This is a bounded OUTAGE, not a forgery, and it is the price of that rule.</para>
    /// </summary>
    [Fact]
    public async Task An_unreadable_marker_halts_the_store_until_it_is_deleted_by_hand()
    {
        Keyed();
        var signer = OperatorKey.Generate(_keyDir);
        var store = Store(signer);
        await store.RecordAsync(Proposal("ext-1"), Held(), T0);
        (await store.ListAsync()).Should().ContainSingle("the store reads before the attack");

        // Zero key material required: one junk file in the state root.
        File.WriteAllText(MarkerFile, "{ this is not a marker");

        var operations = new (string Name, Func<Task> Invoke)[]
        {
            ("ListAsync", () => store.ListAsync()),
            ("GetAsync", () => store.GetAsync("ext-1")),
            ("AdmittedInWindowAsync", () => store.AdmittedInWindowAsync(TimeSpan.FromHours(24), T0.AddHours(1))),
            ("RecordAsync", () => store.RecordAsync(Proposal("ext-2"), Held(), T0.AddHours(1))),
        };

        foreach (var (name, invoke) in operations)
        {
            (await invoke.Should().ThrowAsync<InvalidOperationException>(
                    "RESIDUAL: the posture is resolved on every operation, so one junk file in the "
                    + "state root refuses the whole store to every caller — including " + name + ". "
                    + DeleteTogether))
                .WithMessage("*gate-signing.json is not valid JSON*",
                    "and it refuses by naming the file that cannot be read, not by a bare parse error");
        }

        var repair = async () => await store.ActivateSigningAsync(T0.AddHours(2), repair: true);
        (await repair.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*gate-signing.json is not valid JSON*",
                "--repair reads through a MISSING inventory, not through unreadable bytes, so it "
                + "fails on the same file it was reached for")
            .WithMessage("*delete*sign-activate --repair*",
                "and the refusal must NAME the two-step exit. A state with no named exit is not "
                + "something this spec may record and walk away from; if this assertion is red, the "
                + "message was shortened and an operator has been handed a dead end");

        // Follow the advice, and the store comes back — bounded.
        File.Delete(MarkerFile);
        var recovered = await store.ActivateSigningAsync(T0.AddHours(2), repair: true);
        recovered.Marker.ActivatedAt.Should().Be(T0,
            "and the exit is still bounded: a re-mint anchors at the earliest instant the surviving "
            + "signatures already prove, never at now, so deleting the marker cannot buy a wider "
            + "grace window");
        (await store.ListAsync()).Should().ContainSingle("the store reads again");
    }

    /// <summary>
    /// RESIDUAL, and it is NEW: it is the price of the activation refusal S-6 now requires, and
    /// nothing had disclosed it.
    ///
    /// <para>Activation refuses a store holding any record file whose name is not the proposal id
    /// inside it, because minting a marker over such a store turns every later read into a refusal
    /// and <c>--repair</c> would re-mint the same broken set. That refusal is right. Its price is
    /// that anyone who can write <c>gates/</c> can drop ONE misnamed file and hold the store
    /// permanently un-signable: reads keep working, so nothing looks wrong, while
    /// <c>ashlar keys init</c> and <c>ashlar gates sign-activate</c> — with or without
    /// <c>--repair</c> — both refuse until somebody finds and removes it, and the actor can put it
    /// back. The attacker does not forge past the security upgrade; they deny it.</para>
    ///
    /// <para><b>And the store gives contradictory instructions in this state.</b> The activation
    /// refusal says to rename or remove the offending files. The keyless write guard and the
    /// membership refusal both say that deleting records from <c>gates/</c> is NOT the remedy. One
    /// operator can meet both in one session. That collision is disclosed in S-6 with this residual
    /// rather than left for an operator to resolve alone, and the two refusals are about different
    /// files — the misnamed one is not a record the store ever wrote.</para>
    ///
    /// <para>The trade is still right: a pre-activation refusal that leaves the store readable and
    /// the files movable is strictly better than a post-activation brick whose named remedy
    /// re-mints the same collision. This residual records what the trade cost.</para>
    /// </summary>
    [Fact]
    public async Task One_misnamed_record_file_holds_the_store_permanently_un_signable()
    {
        Keyless();
        await Store().RecordAsync(Proposal("ext-1"), Held(), T0);
        UnsignedAt("aaa.json", "ext-2", ProposalState.Held, T0.AddMinutes(1));   // name != id inside

        (await Store().ListAsync()).Should().HaveCount(2,
            "before activation nothing refuses this: the file-name leg is scoped to `Expected || "
            + "Sig is not null`, and a never-signed store is neither. Nothing looks wrong");

        Keyed();
        var signer = OperatorKey.Generate(_keyDir);

        foreach (var repair in new[] { false, true })
        {
            var activate = async () => await Store(signer).ActivateSigningAsync(T0.AddHours(1), repair);
            (await activate.Should().ThrowAsync<InvalidOperationException>(
                    "RESIDUAL: one planted file that nobody has to sign holds this store "
                    + "un-signable, and an actor who can write gates/ can put it back after every "
                    + "removal. Closing this needs a way to adopt a store while quarantining a file "
                    + "the store never wrote. " + DeleteTogether))
                .WithMessage("*aaa.json*ext-2*",
                    "the refusal names the offending file and the id it holds — with or without "
                    + "--repair, which is what makes the store PERMANENTLY un-signable rather than "
                    + "merely awkward");
        }

        File.Exists(MarkerFile).Should().BeFalse("nothing was written by either refused activation");

        var write = async () => await Store(signer).RecordAsync(Proposal("ext-3"), Held(), T0.AddHours(2));
        (await write.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*never been signed*sign-activate*",
                "the write path sends the operator to the verb, and the verb refuses: there is no "
                + "route from here to a signed store that does not go through deleting somebody "
                + "else's file");

        (await Store().ListAsync()).Should().HaveCount(2,
            "reads keep working throughout, which is the whole shape of this attack: nothing alerts");
    }

    /// <summary>
    /// RESIDUAL. The reader's pinning set is ambient process state. <see cref="GateStore"/> resolves
    /// <c>ASHLAR_KEY_DIR</c> — or <c>~/.ashlar/keys</c> — inside its CONSTRUCTOR through
    /// <see cref="OperatorKey.TrustedPublicKeysBase64"/>, so which keys a store vouches for is a
    /// property of the process that opened it, not of the store. The same bytes on disk get two
    /// different verdicts under two values of one variable, with nothing on disk changing between
    /// them.
    ///
    /// <para>Tests pin the variable; the kernel still reads it. Injecting the key directory is the
    /// right fix and is deferred because
    /// <c>GateRecordReadFunnelConventionTests.Every_production_store_construction_passes_a_signer_or_is_listed</c>
    /// identifies a keyless construction by COUNTING constructor arguments — a second parameter
    /// would blind that gate silently, so the injection MUST replace it before it lands.</para>
    ///
    /// <para>Two stores are constructed rather than one because flipping the variable after
    /// construction is a no-op for an existing instance; that is the same fact stated from the
    /// other side.</para>
    /// </summary>
    [Fact]
    public async Task The_same_bytes_get_two_verdicts_under_two_key_directories()
    {
        Keyless();
        await Store().RecordAsync(Proposal("ext-1"), Held(), T0);   // one unsigned record

        var foreign = OperatorKey.Generate(_otherKeyDir);
        File.WriteAllText(MarkerFile, MarkerJson(GateSigningActivation.Signed(foreign, T0, [], storeId: null)));
        GateSigningActivation.TryRead(_state)!.Signer.Should().Be(foreign.PublicKeyBase64,
            "the marker under test must really be the foreign one, or both verdicts below are about "
            + "a store nobody planted anything in");

        Keyless();
        var ignoring = Store();
        (await ignoring.ListAsync()).Should().ContainSingle(
            "no key material here, and no verifying record corroborates the marker's signer, so the "
            + "marker is ignored and the record reads");
        ignoring.SignatureTrust!.Expected.Should().BeFalse(
            "this reader's verdict on these bytes is 'never signed' — the opposite of the one the "
            + "next reader reaches over the identical bytes");
        ignoring.SignatureTrust.Basis.Should().Contain(
            "no operator key here and no verifying record corroborates its signer",
            "the basis says WHY the marker was ignored, which is the half that makes the second "
            + "verdict below a change of mind rather than a change of data");

        // Not one file is touched between the two readers. One variable is.
        Environment.SetEnvironmentVariable(KeyDirVariable, _otherKeyDir);
        var honouring = Store();
        var list = async () => await honouring.ListAsync();
        (await list.Should().ThrowAsync<InvalidOperationException>(
                "RESIDUAL: the store's verdict on these exact bytes was chosen by an environment "
                + "variable, not by the store. A caller cannot pass it and a reader cannot see it. "
                + DeleteTogether))
            .WithMessage("*ext-1.json*not among them*",
                "the honouring reader now holds the marker's signer as key material, so the "
                + "inventory it carries is in force and the unsigned record is refused");
    }

    /// <summary>
    /// RESIDUAL. Nothing attests the activation instant.
    /// <see cref="GateStore.ActivateSigningAsync"/> takes it from its caller — both CLI call sites
    /// pass <c>DateTimeOffset.UtcNow</c> — and the earliest-proven clamp only ever LOWERS it toward
    /// what the surviving signatures already prove, so on a store with nothing proven the caller's
    /// value is written verbatim and signed. There is no external clock, no timestamping authority
    /// and no transparency log, and a signed lie is still a valid signature.
    ///
    /// <para><b>The other half of this residual has no executing fact, on purpose, and that gap is
    /// declared rather than inferred.</b> An operator whose key is STOLEN can mint any instant and
    /// bless any set. The only test that could be written for it would load the key, sign with it,
    /// and observe that the store accepts what the key signed — which asserts that the system
    /// WORKS, not that it fails. A fact dressed up as that attack is exactly the covered debt this
    /// class exists to refuse. It is carried in <see cref="Residuals"/> as a row with a null
    /// <c>FactName</c> and a mandatory <c>WhyNotExecutable</c>, and
    /// <see cref="Every_residual_row_names_a_fact_on_this_class_or_says_why_it_cannot"/> enforces
    /// that contract in both directions.</para>
    /// </summary>
    [Fact]
    public async Task The_activation_instant_is_whatever_clock_the_caller_supplies()
    {
        Keyless();
        await Store().RecordAsync(Proposal("ext-1"), Held(), T0);   // UNSIGNED: nothing can clamp the instant

        OperatorKey.Generate(_keyDir);
        Keyed();
        var absurd = new DateTimeOffset(1999, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var outcome = await Store(OperatorKey.TryLoad()).ActivateSigningAsync(absurd);

        outcome.Marker.ActivatedAt.Should().Be(absurd,
            "RESIDUAL: no external clock, timestamping authority or transparency log attests this. "
            + "It is the value the caller passed, and the earliest-proven clamp can only lower it. "
            + DeleteTogether);
        GateSigningActivation.TryRead(_state)!.Verifies().Should().BeTrue(
            "and it VERIFIES: a signed lie is a valid signature, which is the whole of this residual");
        outcome.Marker.Grandfathered.Should().ContainSingle(
            "the inventory is minted over the same call's scan, whatever the clock said");

        var keyed = Store(OperatorKey.TryLoad());
        (await keyed.ListAsync()).Should().ContainSingle(
            "the store reads clean on a marker dated twenty-seven years before the record it "
            + "grandfathers");
        keyed.SignatureTrust!.MarkerActivatedAt.Should().Be(absurd,
            "and every reader downstream repeats it");
    }

    // ─────────────────────────── the binding ───────────────────────────

    /// <summary>
    /// Every residual in <see cref="Residuals"/> is disclosed by a sentence that is actually
    /// present in SPEC-006, matched as PROSE rather than as bytes.
    ///
    /// <para><b>Why <see cref="Flatten"/> is mandatory and not defensive decoration.</b> Measured
    /// on this tree: a raw <c>Contains</c> for a residual sentence finds NOTHING while the sentence
    /// is present, because the spec is CRLF in a Windows working tree, LF on the Linux cert-gate
    /// runner (<c>.gitattributes</c> has <c>* text=auto</c>), and markdown wraps the sentences
    /// mid-clause. An unflattened scan would be green against text that is present and green
    /// against text that is absent, in different ways — a vacuous tripwire of exactly the kind the
    /// date tripwire in <c>GateRecordReadFunnelConventionTests</c> was repaired from. Flattening the
    /// NEEDLE as well lets a phrase be spelled across several source lines with <c>+</c>.</para>
    ///
    /// <para><b>Its own limit, stated.</b> This scans the whole spec file, not only section 4, so a
    /// phrase moved to another section still satisfies it. That is deliberate: this fact pins
    /// DISCLOSURE, not location. And the needles are short prose fragments, which are brittle — a
    /// copy edit that changes "and" to a comma reddens one. The answer to the first false red is to
    /// lengthen the SPEC SENTENCE around the needle, not to loosen the needle.</para>
    /// </summary>
    [Fact]
    public void SPEC_006_names_every_residual_this_file_demonstrates()
    {
        var root = RepoPathResolver.FindRepoRoot();
        var specPath = Path.Combine(root, SpecPath);
        File.Exists(specPath).Should().BeTrue(
            "this scan is vacuous if the spec moved: RepoPathResolver.FindRepoRoot returns the "
            + "current directory rather than throwing when Ashlar.sln is not found, so an unguarded "
            + "scan passes by reading nothing. Point {0} at the new path", nameof(SpecPath));

        Residuals.Should().NotBeEmpty(
            "an empty inventory would make every arm of this binding pass while disclosing nothing");

        var spec = Flatten(File.ReadAllText(specPath));
        var missing = Residuals
            .Where(r => !spec.Contains(Flatten(r.Phrase), StringComparison.Ordinal))
            .Select(r => (r.FactName ?? "(no fact)") + " -> \"" + r.Phrase + "\"")
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        missing.Should().BeEmpty(
            "each row in this class is a hole the store does NOT close, and the only thing that "
            + "keeps it from reading as covered debt is the sentence in {0} that says so out loud. "
            + "A residual whose disclosure has been deleted or reworded is an UNDISCLOSED hole, "
            + "which is worse than an undemonstrated one. Restore the sentence, or close the hole "
            + "and delete the row, the fact and the sentence in one commit. Missing: {1}",
            SpecPath, string.Join(" | ", missing));
    }

    /// <summary>
    /// The row set and the executing facts are the same set in BOTH directions, and a row with no
    /// fact carries a written reason.
    ///
    /// <para><b>The reverse arm is the one that matters and the one no inventory-only design
    /// produces.</b> A forward map catches a deleted spec sentence. It does not catch a FACT WITH
    /// NO ROW — an attack this suite executes and demonstrates, and that the specification is
    /// silent about. That is the same failure as a row with no fact, one level up: a hole the
    /// maintainers have already proven and the readers have never been told about.</para>
    ///
    /// <para>Rows are keyed by <c>nameof(...)</c>, so a renamed or deleted fact fails to COMPILE
    /// rather than silently orphaning a row; the forward arm is kept anyway because a row can be
    /// written with a bare string literal by somebody in a hurry, and because the failure message
    /// is more useful than a compiler error about a missing symbol.</para>
    ///
    /// <para>Two rows carry no fact, and both reasons are of the same kind: the only test available
    /// would use the operator's key correctly and observe the store accepting what that key signed,
    /// which asserts that the system works rather than that it fails. Writing one anyway is the
    /// covered debt this class exists to refuse, so the ABSENCE is made legible instead.</para>
    /// </summary>
    [Fact]
    public void Every_residual_row_names_a_fact_on_this_class_or_says_why_it_cannot()
    {
        var facts = FactMethods().Select(m => m.Name).ToList();

        var missingCompanions = Companions
            .Where(name => !facts.Contains(name, StringComparer.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
        missingCompanions.Should().BeEmpty(
            "the companion set is what excuses a fact from needing a row, so a name in it that is "
            + "no longer a fact would excuse nothing and quietly narrow the reverse arm below — and "
            + "if reflection found no facts at all, every arm of this binding would pass while "
            + "nothing was bound. Not found as facts: {0}",
            string.Join(", ", missingCompanions));

        var ghosts = Residuals
            .Where(r => r.FactName is not null && !facts.Contains(r.FactName, StringComparer.Ordinal))
            .Select(r => r.FactName!)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        ghosts.Should().BeEmpty(
            "a row naming a fact that does not exist is indistinguishable from a hole that is still "
            + "demonstrated, and SPEC-006 would go on claiming an executing test for it. Ghosts: {0}",
            string.Join(", ", ghosts));

        var undeclared = Residuals
            .Where(r => r.FactName is null && string.IsNullOrWhiteSpace(r.WhyNotExecutable))
            .Select(r => r.Phrase)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
        undeclared.Should().BeEmpty(
            "a residual with no executing fact is acceptable only when the reason no HONEST fact "
            + "exists is written down beside it. Without that the row is a promise nobody kept, and "
            + "nobody can tell it from a fact somebody deleted. Unexplained: {0}",
            string.Join(" | ", undeclared));

        var contradictory = Residuals
            .Where(r => r.FactName is not null && !string.IsNullOrWhiteSpace(r.WhyNotExecutable))
            .Select(r => r.FactName!)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        contradictory.Should().BeEmpty(
            "a row cannot both name an executing fact and explain why none can exist. One of the "
            + "two is stale, and a reader cannot tell which. Contradictory: {0}",
            string.Join(", ", contradictory));

        var named = Residuals
            .Where(r => r.FactName is not null)
            .Select(r => r.FactName!)
            .ToHashSet(StringComparer.Ordinal);
        var orphans = facts
            .Where(n => !named.Contains(n) && !Companions.Contains(n))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        orphans.Should().BeEmpty(
            "every attack this class executes must be disclosed by a sentence in SPEC-006. A fact "
            + "with no row demonstrates a hole nobody wrote down, so the specification is silent "
            + "about something this repository has already proven. Add the row and the sentence. "
            + "Undisclosed: {0}",
            string.Join(", ", orphans));
    }

    /// <summary>
    /// No fact on this class carries a <c>Skip</c>.
    ///
    /// <para><b>Why this exists, and why it is not a tripwire about a tripwire.</b> Every ordinary
    /// test in this repository goes red when somebody BREAKS something. Every executing fact in
    /// this class goes red when somebody FIXES something — and the failure message then asks for a
    /// three-part deletion across a test file, an inventory and a specification, at the moment a
    /// build is red on somebody else's change. The realistic failure is not a careless deletion; it
    /// is <c>[Fact(Skip = "flaky")]</c>. The two binding companions above cannot see that: the
    /// attribute is still present, reflection still finds the method, the row still matches, and
    /// the class demonstrates nothing while the spec goes on claiming each hole is executed.</para>
    ///
    /// <para>This is the cheapest assertion in the whole file and it closes the only hole in the
    /// binding. <c>LaneBlameWindowConventionTests</c> already reads <c>FactAttribute.Skip</c> the
    /// same way.</para>
    /// </summary>
    [Fact]
    public void No_residual_fact_is_skipped()
    {
        var skipped = FactMethods()
            .Select(m => (m.Name, Skip: m.GetCustomAttributes(typeof(FactAttribute), inherit: false)
                .Cast<FactAttribute>()
                .Select(f => f.Skip)
                .FirstOrDefault(s => s is not null)))
            .Where(t => t.Skip is not null)
            .Select(t => t.Name + " (" + t.Skip + ")")
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        skipped.Should().BeEmpty(
            "a skipped residual fact is the worst outcome available here: the row still matches, "
            + "the binding companions stay green, and SPEC-006 goes on telling readers that this "
            + "hole is demonstrated by an executing test while nothing executes. If a fact here has "
            + "gone red because the hole CLOSED, that is the intended signal — delete the fact, its "
            + "row in Residuals, and the sentence in SPEC-006, in one commit. Skipped: {0}",
            string.Join(", ", skipped));
    }

    /// <summary>
    /// The claim that a keyed reader still catches a total strip through the marker its key wrote is
    /// FALSE and is deleted, and nothing under <c>docs/</c> says it again.
    ///
    /// <para><b>Why it is false.</b> The marker lives in the directory being attacked, so an actor
    /// who strips every signature deletes it too and the keyed reader is left with no anchor either
    /// — which is what
    /// <see cref="A_total_strip_with_the_marker_deleted_reads_clean_and_plain_activation_blesses_it"/>
    /// executes, showing both readers accepting the store. A keyed reader holds public key material,
    /// not a memory of this store. Asserting the sentence's ABSENCE is what stops it being pasted
    /// back from an old draft, a decision log or a release note.</para>
    ///
    /// <para><b>Measured, and load-bearing.</b> With the sentence present, a raw
    /// <c>File.ReadAllText(...).Contains(needle)</c> over <c>docs/</c> finds ZERO files, because the
    /// spec wraps it across two CRLF lines; the flattened scan finds the file. An unflattened
    /// absence test would have shipped GREEN before the deletion and gone on being green
    /// afterwards, proving nothing either way.</para>
    ///
    /// <para><b>Why the needle is spelled literally rather than concatenated the way
    /// <see cref="ProcessGlobalEnvironmentConventionTests"/> spells its markers.</b> The scan is
    /// rooted at <c>docs/</c> and this file is under <c>src/</c>, so it is invisible to its own scan
    /// by SCOPE, not by obfuscation. Applying the trick reflexively would teach the next reader a
    /// false lesson about why it is there. Widening the root to <c>src/</c> or the repo root makes
    /// concatenation mandatory.</para>
    ///
    /// <para><b>Scope, stated:</b> <c>CHANGELOG.md</c> is at the repo root, not under <c>docs/</c>,
    /// so the same wording there is outside this fact and is removed by the changelog edit
    /// unpinned.</para>
    /// </summary>
    [Fact]
    public void The_false_marker_claim_appears_nowhere_under_docs()
    {
        var root = RepoPathResolver.FindRepoRoot();
        var docs = Path.Combine(root, "docs");
        Directory.Exists(docs).Should().BeTrue(
            "this scan is vacuous if docs/ moved: FindRepoRoot falls back to the current directory "
            + "rather than throwing, so an unguarded walk finds no files and passes");

        var markdown = MarkdownUnder(docs).ToList();
        markdown.Should().NotBeEmpty(
            "an absence assertion over an empty file set is green for the wrong reason");

        var needle = Flatten(FalseMarkerClaim);
        var offenders = markdown
            .Where(f => Flatten(File.ReadAllText(f)).Contains(needle, StringComparison.Ordinal))
            .Select(f => Normalize(Path.GetRelativePath(root, f)))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        offenders.Should().BeEmpty(
            "this sentence claimed a keyed-reader advantage that does not exist, and the residual it "
            + "sat in is the largest one in S-6. The marker lives in the directory being attacked. "
            + "Restoring it anywhere under docs/ re-asserts a defence nobody has. Found in: {0}",
            string.Join(", ", offenders));
    }

    // ─────────────────────────── the scan ───────────────────────────

    /// <summary>
    /// Every run of whitespace collapsed to one space, leading and trailing whitespace dropped.
    /// Both the haystack and the NEEDLE go through it.
    ///
    /// <para>Measured on this tree: a raw <c>Contains</c> over SPEC-006 finds none of these phrases
    /// while they are present, because the file is CRLF in a Windows working tree, LF on the Linux
    /// cert-gate runner (<c>.gitattributes</c> has <c>* text=auto</c>), and markdown wraps the
    /// sentences mid-clause. Flattening the needle as well is what lets a phrase be spelled across
    /// several source lines.</para>
    /// </summary>
    private static string Flatten(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = sb.Length > 0;
                continue;
            }
            if (pendingSpace)
            {
                sb.Append(' ');
                pendingSpace = false;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Markdown under a directory, using the house structural pruning rather than
    /// <c>SearchOption.AllDirectories</c>. There is no nested checkout or build output under
    /// <c>docs/</c> today, and "today" is the assumption every other convention test in this
    /// namespace refuses to make.</summary>
    private static IEnumerable<string> MarkdownUnder(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*.md"))
        {
            yield return file;
        }

        foreach (var child in Directory.EnumerateDirectories(directory))
        {
            if (IsPruned(child))
            {
                continue;
            }

            foreach (var file in MarkdownUnder(child))
            {
                yield return file;
            }
        }
    }

    /// <summary>
    /// Build output, agent scratch space, and the root of any nested checkout — the structural rule
    /// the other convention tests use, so a vendored copy this repository never names is pruned too.
    /// Copied verbatim from <c>GateRecordReadFunnelConventionTests</c>, whose version is private;
    /// the house pattern here is duplication per class rather than a shared helper this class would
    /// have to depend on.
    /// </summary>
    private static bool IsPruned(string directory)
    {
        var name = Path.GetFileName(directory);

        if (string.Equals(name, "bin", StringComparison.Ordinal)
            || string.Equals(name, "obj", StringComparison.Ordinal)
            || string.Equals(name, ".claude", StringComparison.Ordinal))
        {
            return true;
        }

        var git = Path.Combine(directory, ".git");
        return File.Exists(git) || Directory.Exists(git);
    }

    /// <summary>Copied verbatim from <c>GateRecordReadFunnelConventionTests</c>: a failure message
    /// that names a path must name the same path on every platform.</summary>
    private static string Normalize(string path) => path.Replace('\\', '/').Trim();
}
