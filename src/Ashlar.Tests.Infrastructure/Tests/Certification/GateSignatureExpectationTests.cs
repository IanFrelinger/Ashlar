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
/// SPEC-006 rule S-6: a REMOVED gate-record signature is corruption once the store is known to
/// be signed. S-1 already refuses a signature that fails verification; it is silent about one
/// that was simply deleted, and from the record's own bytes a stripped signature and an honestly
/// unsigned record are the same bytes. The store therefore resolves a
/// <see cref="GateSignatureExpectation"/> from two anchors that are NOT the record being judged —
/// its other verifying records, and the signed activation marker at
/// <c>{stateRoot}/gate-signing.json</c> when the reader's key material vouches for it — and
/// judges every record it lets out against that.
///
/// <para><b>Why this is a certification test.</b> <see cref="GateStore.AdmittedInWindowAsync"/>
/// counts admitted records to enforce the self-extension budget. Before this rule, anyone who
/// could write <c>gates/</c> could hand-write an unsigned <c>Admitted</c> record and it counted,
/// or strip a real admission's signature and it still counted. The last fact below ties the two
/// together: a fabricated admission now refuses the whole listing rather than spending the budget.</para>
///
/// <para><b>Environment.</b> The pinning set comes from <c>ASHLAR_KEY_DIR</c>, so every fact here
/// sets it explicitly — to an EMPTY directory for a keyless reader, to the generated key's
/// directory for a keyed one — and restores it afterwards. The class joins the serialized
/// collection for exactly that reason; see <see cref="ProcessGlobalEnvironmentConventionTests"/>.</para>
/// </summary>
[Trait("Category", "Certification")]
[Collection("EnvironmentVariables")]
public sealed class GateSignatureExpectationTests : IDisposable
{
    private const string KeyDirVariable = "ASHLAR_KEY_DIR";

    private static readonly DateTimeOffset T0 = new(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _root;
    private readonly string _state;
    private readonly string _keyDir;
    private readonly string _otherKeyDir;
    private readonly string _noKeys;
    private readonly string? _previousKeyDir;

    public GateSignatureExpectationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "gate-expectation-" + Guid.NewGuid().ToString("N"));
        _state = Path.Combine(_root, ".ashlar");
        _keyDir = Path.Combine(_root, "keys");
        _otherKeyDir = Path.Combine(_root, "other-keys");
        _noKeys = Path.Combine(_root, "no-keys");
        Directory.CreateDirectory(_state);
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

    // ─────────────────────────── fixtures ───────────────────────────

    /// <summary>The reader holds no key material at all: it verifies intrinsically only.</summary>
    private void Keyless() => Environment.SetEnvironmentVariable(KeyDirVariable, _noKeys);

    /// <summary>The reader's key directory is the one the test's operator key lives in.</summary>
    private void Keyed() => Environment.SetEnvironmentVariable(KeyDirVariable, _keyDir);

    private GateStore Store(SigningIdentity? signer = null) => new(_state, signer);

    private string RecordFile(string id) => Path.Combine(_state, "gates", id + ".json");

    private string MarkerFile => GateSigningActivation.PathFor(_state);

    private static ExtensionProposal Proposal(string id) => new()
    {
        Id = id,
        Kind = "brick",
        Summary = "add brick expectation.demo",
        ProposedBy = "night-agent",
        ProposedAt = T0,
        Courses = [new CourseResult { Name = "sandbox", Passed = true, Detail = "confined" }],
    };

    private static AdmissionOutcome Held(string reason = "holding for review") =>
        new() { State = ProposalState.Held, Reason = reason };

    /// <summary>Self-extending, bricks only, no gates required — the budget is the only rule.</summary>
    private static AshlarPolicy Policy(int extensions) => new()
    {
        SelfExtend = new PolicySelfExtend
        {
            Mode = SelfExtendMode.SelfExtending,
            Budget = new PolicyBudget { Extensions = extensions, Window = "24h" },
            MayAdd = ["brick"],
            GatesRequired = [],
        },
    };

    /// <summary>The attack: delete both signature fields and leave everything else byte-identical.</summary>
    private void Strip(string id)
    {
        var node = (JsonObject)JsonNode.Parse(File.ReadAllText(RecordFile(id)))!;
        node.Remove("Sig").Should().BeTrue("the record on disk must have been signed for stripping to mean anything");
        node.Remove("Signer").Should().BeTrue();
        File.WriteAllText(RecordFile(id), node.ToJsonString());
    }

    /// <summary>The other attack: replace the signature with one from a key of the forger's own.</summary>
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
    }

    /// <summary>An unsigned record on disk, exactly the shape a keyless store writes.</summary>
    private void Unsigned(string id, ProposalState state, DateTimeOffset decidedAt, string reason)
    {
        var record = new GateRecord
        {
            Proposal = Proposal(id),
            State = state,
            Reason = reason,
            Actor = "gate",
            DecidedAt = decidedAt,
        };
        File.WriteAllText(RecordFile(id), JsonSerializer.Serialize(record, Json));
    }

    /// <summary>A record nobody decided, written straight into gates/ with no signature.</summary>
    private void Fabricate(string id, ProposalState state, DateTimeOffset decidedAt) =>
        Unsigned(id, state, decidedAt, "fabricated by hand");

    /// <summary>Rewrites a record's file with different whitespace and the opposite key order, and
    /// the same VALUES — what a reformat, a re-serialization or a line-ending normalisation does.
    /// Nothing the signature or the inventory hash covers may move.</summary>
    private void Reformat(string id)
    {
        var node = (JsonObject)JsonNode.Parse(File.ReadAllText(RecordFile(id)))!;
        var reordered = new JsonObject();
        foreach (var kv in node.OrderByDescending(k => k.Key, StringComparer.Ordinal))
        {
            reordered[kv.Key] = kv.Value?.DeepClone();
        }
        File.WriteAllText(
            RecordFile(id),
            "\n\n" + reordered.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }

    /// <summary>Edits one top-level field of an UNSIGNED record in place.</summary>
    private void Rewrite(string id, string field, string value)
    {
        var node = (JsonObject)JsonNode.Parse(File.ReadAllText(RecordFile(id)))!;
        node[field] = value;
        File.WriteAllText(RecordFile(id), node.ToJsonString());
    }

    private static string MarkerJson(GateSigningActivation marker) =>
        JsonSerializer.Serialize(marker, Json);

    /// <summary>A marker in the shape that shipped before the inventory existed: signed over
    /// <c>{ActivatedAt}</c> alone, with no <c>Grandfathered</c> member at all. It verifies —
    /// <c>CanonicalJson</c> omits nulls, so the current type canonicalizes identically — which is
    /// exactly why it has to be refused by name rather than by a signature check.</summary>
    private static string V1MarkerJson(SigningIdentity signer, DateTimeOffset activatedAt)
    {
        var v1 = new V1Marker { ActivatedAt = activatedAt };
        var node = (JsonObject)JsonNode.Parse(JsonSerializer.Serialize(v1, Json))!;
        node["Sig"] = signer.Sign(CanonicalJson.Bytes(v1));
        node["Signer"] = signer.PublicKeyBase64;
        return node.ToJsonString();
    }

    private sealed record V1Marker
    {
        public required DateTimeOffset ActivatedAt { get; init; }
    }

    // ─────────────────────────── the rule ───────────────────────────

    [Fact]
    public async Task A_stripped_signature_is_corrupt_once_the_store_is_signed()
    {
        Keyed();
        var signer = OperatorKey.Generate(_keyDir);
        var store = Store(signer);
        await store.RecordAsync(Proposal("ext-1"), Held(), T0);
        await store.RecordAsync(Proposal("ext-2"), Held(), T0.AddHours(1));

        Strip("ext-2");

        var get = async () => await store.GetAsync("ext-2");
        (await get.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*ext-2.json*carries no signature*",
                "a keyed reader honours the activation marker, so even a single read sees the expectation");

        var list = async () => await store.ListAsync();
        (await list.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*ext-2.json*carries no signature*");
    }

    [Fact]
    public async Task A_record_signed_by_an_untrusted_key_is_corrupt()
    {
        // Presence-only checking is defeated by SIGNING instead of stripping: a forger with a
        // keypair of their own re-signs the record and every intrinsic check passes. Only pinning
        // the signer to this machine's key material catches it — and the pinning set must come
        // from key material, never from the records, or the forger's key would pin itself.
        Keyed();
        var operatorKey = OperatorKey.Generate(_keyDir);
        await Store(operatorKey).RecordAsync(Proposal("ext-1"), Held(), T0);
        var forger = OperatorKey.Generate(_otherKeyDir);

        ReSign("ext-1", forger);

        var keyed = Store(operatorKey);
        var get = async () => await keyed.GetAsync("ext-1");
        (await get.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*ext-1.json*neither the operator key nor any key retained under trusted/*");
        var list = async () => await keyed.ListAsync();
        await list.Should().ThrowAsync<InvalidOperationException>();

        // The signature itself is cryptographically sound — a keyless reader, which pins nothing,
        // accepts it. That is what proves the refusal above is the PIN and not a broken signature,
        // and it is the behaviour bundle consumers with no keys depend on.
        Keyless();
        var keyless = Store();
        (await keyless.GetAsync("ext-1"))!.Signer.Should().Be(forger.PublicKeyBase64);
    }

    [Fact]
    public async Task A_keyless_reader_still_detects_a_stripped_signature()
    {
        // The reader's keys are NOT the anchor — the store's own verifying records are. A fresh
        // checkout, a container, a bundle consumer: none hold the operator key, and all must still
        // refuse a store where one record is signed and another has had its signature removed.
        Keyless();
        var signer = OperatorKey.Generate(_keyDir);   // explicit directory; nothing ambient points here
        var writer = Store(signer);
        await writer.RecordAsync(Proposal("ext-1"), Held(), T0);
        await writer.RecordAsync(Proposal("ext-2"), Held(), T0.AddHours(1));

        // The attacker strips one signature AND deletes the marker, so the only anchor left is
        // the DERIVED one: ext-1 still verifies, and a verifying record is intrinsic proof the
        // store is signed. (Stripping EVERY signature as well is the recorded keyless residual.)
        Strip("ext-2");
        File.Delete(MarkerFile);

        var keyless = Store();
        var list = async () => await keyless.ListAsync();
        (await list.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*ext-2.json*carries no signature*",
                "ext-1 still verifies, and a verifying record is intrinsic proof the store is signed");
    }

    [Fact]
    public async Task Running_keys_init_on_an_unsigned_store_does_not_refuse_it()
    {
        // SPEC-006 S-2, the other direction: a store that was honestly unsigned must keep reading
        // after the operator generates a key — activation grandfathers everything already on
        // disk. Brick-on-adoption would make `ashlar keys init` a destructive command.
        Keyless();
        await Store().RecordAsync(Proposal("ext-1"), Held(), T0);
        await Store().RecordAsync(Proposal("ext-2"), Held(), T0.AddHours(1));

        OperatorKey.Generate(_keyDir);
        Keyed();
        var keyed = Store(OperatorKey.TryLoad());

        (await keyed.ListAsync()).Should().HaveCount(2, "keys alone create no expectation");

        // What `ashlar keys init` does in a project: activate at the moment the key was made.
        await keyed.ActivateSigningAsync(T0.AddHours(2));

        (await keyed.ListAsync()).Should().HaveCount(2, "both records predate activation");
        (await keyed.GetAsync("ext-1")).Should().NotBeNull();
        keyed.SignatureTrust!.Expected.Should().BeTrue("the marker is honoured by the key that wrote it");

        // And from here on the store signs; the marker did not move.
        await keyed.RecordAsync(Proposal("ext-3"), Held(), T0.AddHours(3));
        (await keyed.ListAsync()).Should().HaveCount(3);
        GateSigningActivation.TryRead(_state)!.ActivatedAt.Should().Be(T0.AddHours(2));
    }

    [Fact]
    public async Task Records_decided_before_activation_keep_verifying()
    {
        // The adoption path, re-expressed against the inventory. A keyed write used to activate
        // silently at its own DecidedAt, so everything already on disk fell under the floor with
        // nobody looking — which is the adoption path an attacker rides: plant records, wait for
        // the operator's next admit to bless them. The keyed write now REFUSES and names the verb;
        // the verb mints the inventory; and after that the history still reads.
        Keyless();
        await Store().RecordAsync(Proposal("ext-0"), Held(), T0);

        Keyed();
        var signer = OperatorKey.Generate(_keyDir);
        var activatedAt = T0.AddDays(1);

        var blind = async () => await Store(signer).RecordAsync(Proposal("ext-1"), Held(), activatedAt);
        (await blind.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*1 unsigned record(s)*ashlar gates sign-activate*",
                "signing must not grandfather a set the operator has never been shown");
        File.Exists(MarkerFile).Should().BeFalse("the refused write wrote no marker");

        var (marker, wasActive, _, _) = await Store(signer).ActivateSigningAsync(activatedAt);
        wasActive.Should().BeFalse();
        marker.Grandfathered.Should().ContainSingle().Which.Id.Should().Be("ext-0");
        marker.Signer.Should().Be(signer.PublicKeyBase64);
        marker.ActivatedAt.Should().Be(activatedAt);

        await Store(signer).RecordAsync(Proposal("ext-1"), Held(), activatedAt);

        var keyed = Store(signer);
        (await keyed.ListAsync()).Should().HaveCount(2, "ext-0 was authorized by the operator's own key");
        (await keyed.GetAsync("ext-0"))!.Sig.Should().BeNull();
        keyed.SignatureTrust!.Grandfathered.Should().ContainSingle(
            "the posture's grandfather set is the marker's, not a date");
        GateSigningActivation.TryRead(_state)!.ActivatedAt.Should().Be(
            activatedAt, "the instant never moves once it is written");
    }

    [Fact]
    public async Task A_rotated_key_does_not_invalidate_earlier_records_or_move_activation()
    {
        // Generate(rotate: true) keeps the superseded PUBLIC key under trusted/, and the pinning
        // set is operator.pub UNION trusted/*.pub — so records under the old key stay trusted.
        // The marker was signed by the old key and is never rewritten, so a rotation cannot
        // re-open the grace window either.
        Keyed();
        var first = OperatorKey.Generate(_keyDir);
        await Store(first).RecordAsync(Proposal("ext-1"), Held(), T0);

        var rotated = OperatorKey.Generate(_keyDir, rotate: true);
        await Store(rotated).RecordAsync(Proposal("ext-2"), Held(), T0.AddHours(1));

        var keyed = Store(OperatorKey.TryLoad());
        var records = await keyed.ListAsync();
        records.Should().HaveCount(2);
        records.Single(r => r.Proposal.Id == "ext-1").Signer.Should().Be(first.PublicKeyBase64);
        records.Single(r => r.Proposal.Id == "ext-2").Signer.Should().Be(rotated.PublicKeyBase64);
        keyed.SignatureTrust!.TrustedSigners.Should().Contain(first.PublicKeyBase64)
            .And.Contain(rotated.PublicKeyBase64);

        var marker = GateSigningActivation.TryRead(_state)!;
        marker.ActivatedAt.Should().Be(T0, "activation is the first signed write, not the rotation");
        marker.Signer.Should().Be(first.PublicKeyBase64);
    }

    [Fact]
    public async Task A_forward_dated_marker_cannot_grandfather_a_stripped_record()
    {
        // An attacker who can write the state root re-dates the marker LATER than a record whose
        // signature they then strip, hoping the record reads as pre-activation. Under the
        // inventory the date is not the question at all: the marker they minted names the records
        // its own inventory names, and a record they stripped is not one of them. The attack now
        // fails on membership rather than on a min-over-anchors comparison they could also have
        // moved, which is the whole reason the floor stopped being a date.
        Keyed();
        var signer = OperatorKey.Generate(_keyDir);
        var store = Store(signer);
        await store.RecordAsync(Proposal("ext-1"), Held(), T0);
        await store.RecordAsync(Proposal("ext-2"), Held(), T0.AddHours(1));

        // A VALIDLY signed marker (the operator's own key — a stolen-key or careless-operator
        // scenario), dated after both records and grandfathering nothing.
        File.Delete(MarkerFile);
        File.WriteAllText(MarkerFile, MarkerJson(GateSigningActivation.Signed(signer, T0.AddHours(2), [])));
        Strip("ext-2");

        var list = async () => await store.ListAsync();
        (await list.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*ext-2.json*carries no signature*not among them*",
                "the marker names who is grandfathered, and ext-2 is not on the list");

        // And the stronger half: even a marker whose inventory names ext-2 does not save it,
        // because the inventory pins the bytes ext-2 had when it was signed, not the stripped ones.
        File.Delete(MarkerFile);
        File.WriteAllText(MarkerFile, MarkerJson(GateSigningActivation.Signed(
            signer, T0.AddHours(2), [new GrandfatheredRecord("ext-2", new string('0', 64))])));
        (await list.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*ext-2.json*bytes have changed*");
    }

    [Fact]
    public async Task A_planted_unsigned_marker_is_corruption_and_a_planted_foreign_marker_is_ignored()
    {
        // Two halves of the plant attack against a keyless store of honest unsigned records.
        Keyless();
        await Store().RecordAsync(Proposal("ext-1"), Held(), T0);

        // Half one: an UNSIGNED marker with a far-past instant. Honouring it would brick the
        // store; it is refused as corruption instead, on every read path.
        File.WriteAllText(MarkerFile, """{ "ActivatedAt": "2000-01-01T00:00:00+00:00" }""");
        var list = async () => await Store().ListAsync();
        (await list.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*gate-signing.json*carries no signature*never honoured*");
        var get = async () => await Store().GetAsync("ext-1");
        await get.Should().ThrowAsync<InvalidOperationException>();

        // Half two: a marker validly signed by a key NOBODY here vouches for. Ignored — by the
        // keyless reader (no record corroborates the key) and by a keyed one (not in its material).
        var foreign = OperatorKey.Generate(_otherKeyDir);
        File.WriteAllText(MarkerFile, MarkerJson(GateSigningActivation.Signed(foreign, new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero), [])));

        var keyless = Store();
        (await keyless.ListAsync()).Should().ContainSingle();
        keyless.SignatureTrust!.Expected.Should().BeFalse();
        keyless.SignatureTrust.Basis.Should().Contain("ignored");

        OperatorKey.Generate(_keyDir);
        Keyed();
        var keyed = Store(OperatorKey.TryLoad());
        (await keyed.ListAsync()).Should().ContainSingle();
        keyed.SignatureTrust!.Expected.Should().BeFalse("a foreign marker is not this machine's declaration");
    }

    [Fact]
    public async Task The_activation_marker_is_never_read_as_a_gate_record()
    {
        // ListAsync globs gates/*.json and GateRecord has required members, so a marker inside
        // gates/ would deserialize to a JsonException and brick every listing — including the
        // budget count. It lives beside gates/, not in it, and not as a dotfile.
        Keyed();
        var signer = OperatorKey.Generate(_keyDir);
        var store = Store(signer);
        await store.RecordAsync(Proposal("ext-1"), Held(), T0);
        await store.RecordAsync(Proposal("ext-2"), Held(), T0.AddHours(1));

        File.Exists(MarkerFile).Should().BeTrue();
        Path.GetDirectoryName(MarkerFile).Should().Be(_state, "a sibling of gates/, where an operator will see it");
        Path.GetFileName(MarkerFile).Should().NotStartWith(".");
        Directory.EnumerateFiles(Path.Combine(_state, "gates"), "*.json").Should().HaveCount(2, "only records live in gates/");

        (await store.ListAsync()).Select(r => r.Proposal.Id).Should().BeEquivalentTo(new[] { "ext-1", "ext-2" });
    }

    [Fact]
    public async Task A_keyless_write_into_a_signed_store_is_refused_by_name()
    {
        // The guard that makes the invariant total. Without it a keyless DecideAsync rewrites a
        // signed record unsigned — legitimate code performing the exact self-downgrade the read
        // rule then refuses — and the store bricks itself on the next read.
        Keyed();
        var signer = OperatorKey.Generate(_keyDir);
        await Store(signer).RecordAsync(Proposal("ext-1"), Held(), T0);

        Keyless();
        var keyless = Store();

        var decide = async () => await keyless.DecideAsync("ext-1", admit: true, "alice", "looks good", T0.AddHours(1));
        (await decide.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*ashlar keys init*", "the refusal names the remedy")
            .WithMessage("*NOT the remedy*", "and says that deleting records is not it");

        var record = async () => await keyless.RecordAsync(Proposal("ext-2"), Held(), T0.AddHours(1));
        (await record.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*ashlar keys init*");
        File.Exists(RecordFile("ext-2")).Should().BeFalse("nothing landed");

        (await keyless.ListAsync()).Should().ContainSingle("the store still enumerates cleanly")
            .Which.State.Should().Be(ProposalState.Held, "the refused decision changed nothing");
    }

    // ─────────────────────── one posture serves every read ───────────────────────

    [Fact]
    public async Task Get_and_list_agree_on_the_posture_of_the_same_store()
    {
        // The two funnels used to resolve separately: GetAsync from the activation marker alone,
        // ListAsync from the marker AND the store's other verifying records. Delete the marker —
        // a file in the directory the attacker already writes — and GetAsync had no anchor left,
        // so it handed back a record ListAsync beside it refused. One store, one posture.
        Keyed();
        var signer = OperatorKey.Generate(_keyDir);
        var store = Store(signer);
        await store.RecordAsync(Proposal("ext-1"), Held(), T0);
        await store.RecordAsync(Proposal("ext-2"), Held(), T0.AddHours(1));

        File.Delete(MarkerFile);
        Strip("ext-2");

        var reader = Store(OperatorKey.TryLoad());
        var list = async () => await reader.ListAsync();
        var get = async () => await reader.GetAsync("ext-2");

        var fromList = (await list.Should().ThrowAsync<InvalidOperationException>()).Which.Message;
        var fromGet = (await get.Should().ThrowAsync<InvalidOperationException>()).Which.Message;

        fromGet.Should().Be(fromList, "the same store judged by the same expectation says the same thing");
        fromGet.Should().Contain("ext-2.json").And.Contain("carries no signature");
    }

    [Fact]
    public async Task A_keyed_decision_cannot_be_made_on_a_record_the_store_would_refuse()
    {
        // The laundering step, executed. DecideAsync reads through GetAsync, so while GetAsync
        // carried the weaker posture, `ashlar gates --admit` on a stripped and tampered Held
        // record signed the attacker's content under the OPERATOR'S key and handed back a verdict
        // that verified — legitimate code minting the forgery the read rule exists to catch.
        Keyed();
        var signer = OperatorKey.Generate(_keyDir);
        var store = Store(signer);
        await store.RecordAsync(Proposal("ext-1"), Held(), T0);
        await store.RecordAsync(Proposal("ext-2"), Held(), T0.AddHours(1));

        File.Delete(MarkerFile);
        Strip("ext-2");
        var tampered = (JsonObject)JsonNode.Parse(File.ReadAllText(RecordFile("ext-2")))!;
        tampered["Proposal"]!["Summary"] = "TAMPERED: add brick evil.backdoor";
        File.WriteAllText(RecordFile("ext-2"), tampered.ToJsonString());
        var before = await File.ReadAllBytesAsync(RecordFile("ext-2"));

        var decide = async () => await store.DecideAsync("ext-2", admit: true, "alice", "looks good", T0.AddHours(2));
        (await decide.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*ext-2.json*carries no signature*");

        (await File.ReadAllBytesAsync(RecordFile("ext-2"))).Should().Equal(
            before, "nothing may be signed over content the store would refuse to read");
        File.Exists(MarkerFile).Should().BeFalse(
            "a refused decision must not re-create the marker the attacker deleted, at the decision instant");
    }

    [Fact]
    public async Task A_corrupt_neighbour_names_itself_not_the_requested_record()
    {
        // GetAsync is no longer a single-file read, which is the price of the two funnels
        // agreeing. The operator must therefore be pointed at the file that is actually broken,
        // not at the one they asked about — a refusal built from the requested id would send them
        // to inspect a healthy record.
        Keyless();
        await Store().RecordAsync(Proposal("ext-good"), Held(), T0);
        File.WriteAllText(RecordFile("ext-bad"), "{ \"Proposal\": ");

        var get = async () => await Store().GetAsync("ext-good");
        (await get.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*Corrupt gate record*")
            .WithMessage("*ext-bad.json*", "the refusal names the file that is broken");
    }

    // ─────────────────── a file's name is the id inside its bytes ───────────────────

    [Fact]
    public async Task A_duplicated_admitted_record_under_a_second_file_name_is_refused()
    {
        // No stripping needed. The proposal id is inside the signed bytes; the FILE NAME is not.
        // Copy one legitimately signed admission to two more names and every copy verifies, every
        // copy is enumerated, and AdmittedInWindowAsync — which counts files, because files are
        // all there are to count — reads three admissions where the operator made one, holding an
        // honest proposal as over-budget.
        Keyed();
        var signer = OperatorKey.Generate(_keyDir);
        var store = Store(signer);
        await store.RecordAsync(Proposal("ext-1"), Held(), T0);
        await store.DecideAsync("ext-1", admit: true, "alice", "seated", T0.AddMinutes(1));

        File.Copy(RecordFile("ext-1"), RecordFile("ext-1-dup1"));
        File.Copy(RecordFile("ext-1"), RecordFile("ext-1-dup2"));

        var list = async () => await store.ListAsync();
        (await list.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*ext-1-dup*holds proposal 'ext-1'*ext-1.json*",
                "the refusal names the offending file and the id it holds");

        var count = async () => await store.AdmittedInWindowAsync(TimeSpan.FromHours(24), T0.AddHours(2));
        await count.Should().ThrowAsync<InvalidOperationException>(
            "the budget must never see three admissions where one was made");
    }

    [Fact]
    public async Task A_never_signed_store_behaves_exactly_as_today_for_both_readers()
    {
        // SPEC-006 S-2, stated as a fact rather than as an intention. A store that has never been
        // signed — no marker, no verifying record — must read exactly as it did before rule S-6
        // existed, for a reader holding the operator key and for one holding nothing. Every
        // refusal S-6 adds is scoped to a store that IS signed; if one of them ever goes
        // unconditional, zero-setup breaks here first.
        Keyless();
        var writer = Store();
        await writer.RecordAsync(Proposal("ext-1"), Held(), T0);
        await writer.DecideAsync("ext-1", admit: true, "alice", "seated", T0.AddMinutes(1));
        await writer.RecordAsync(Proposal("ext-2"), Held(), T0.AddHours(1));

        // A copy under a second name is NOT refused here: the file-name binding is scoped to a
        // signed store. It is a pre-existing hole in an unsigned store, and S-2 owns it.
        File.Copy(RecordFile("ext-1"), RecordFile("ext-1-copy"));

        var keyless = Store();
        (await keyless.ListAsync()).Should().HaveCount(3, "an unsigned store enumerates everything, copies included");
        (await keyless.GetAsync("ext-2")).Should().NotBeNull();
        (await keyless.AdmittedInWindowAsync(TimeSpan.FromHours(24), T0.AddHours(2))).Should().Be(2,
            "the copy counts, exactly as it did before this rule");
        keyless.SignatureTrust!.Expected.Should().BeFalse();

        OperatorKey.Generate(_keyDir);
        Keyed();
        var keyed = Store(OperatorKey.TryLoad());
        (await keyed.ListAsync()).Should().HaveCount(3, "a key is not an expectation");
        (await keyed.GetAsync("ext-1")).Should().NotBeNull();
        (await keyed.AdmittedInWindowAsync(TimeSpan.FromHours(24), T0.AddHours(2))).Should().Be(2);
        keyed.SignatureTrust!.Expected.Should().BeFalse("keys alone create no expectation");

        // And a keyless writer may still write: nothing here is signed, so nothing is downgraded.
        await Store().RecordAsync(Proposal("ext-3"), Held(), T0.AddHours(2));
        File.Exists(RecordFile("ext-3")).Should().BeTrue();
    }

    // ──────────── grandfathering is a signed list, not a date comparison ────────────

    [Fact]
    public async Task A_fabricated_record_under_the_grace_floor_is_refused_not_seated()
    {
        // The flaw the date floor always had, executed. The attacker does not need to strip
        // anything: DecidedAt on an unsigned record is a field they write, and the floor was a
        // number they could read off the store's own records. One second below it and the
        // fabrication was grandfathered, listed, counted and shown as a decision nobody made.
        Keyed();
        var signer = OperatorKey.Generate(_keyDir);
        var store = Store(signer);
        await store.RecordAsync(Proposal("ext-1"), Held(), T0);
        await store.RecordAsync(Proposal("ext-2"), Held(), T0.AddHours(1));

        Fabricate("evil", ProposalState.Held, T0.AddSeconds(-1));

        var list = async () => await store.ListAsync();
        (await list.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*evil.json*not among them*");

        // The `gates --admit` pre-flight and `gates --show` both read through GetAsync, and they
        // refuse too — including for a record that is not the forged one.
        var getEvil = async () => await store.GetAsync("evil");
        await getEvil.Should().ThrowAsync<InvalidOperationException>();
        var getHonest = async () => await store.GetAsync("ext-1");
        await getHonest.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task A_backdated_unsigned_admission_is_not_grandfathered()
    {
        // The same field, spent on the budget. Strip a signed REFUSAL, flip it to Admitted, and
        // date it below activation: under a date floor that is a free admission, which both denies
        // an honest proposal its budget and rewrites history.
        Keyed();
        var signer = OperatorKey.Generate(_keyDir);
        var store = Store(signer);
        await store.RecordAsync(Proposal("ext-1"), Held(), T0);
        await store.RecordAsync(Proposal("ext-2"), Held(), T0.AddHours(1));
        await store.DecideAsync("ext-2", admit: false, "alice", "no", T0.AddHours(2));

        Strip("ext-2");
        Rewrite("ext-2", "State", nameof(ProposalState.Admitted));
        Rewrite("ext-2", "DecidedAt", T0.AddMinutes(-1).ToString("O"));

        var list = async () => await store.ListAsync();
        (await list.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*ext-2.json*not among them*");

        var count = async () => await store.AdmittedInWindowAsync(TimeSpan.FromHours(24), T0.AddHours(3));
        await count.Should().ThrowAsync<InvalidOperationException>("the budget never sees a verdict nobody made");
    }

    [Fact]
    public async Task A_grandfathered_record_rewritten_after_activation_is_refused()
    {
        // A grandfathered record is trusted for the BYTES it had at activation and for nothing
        // else. Without the hash the inventory would be a list of ids, and an id the operator
        // authorized as Held could be edited into an admission afterwards.
        Keyless();
        await Store().RecordAsync(Proposal("ext-1"), Held(), T0);

        OperatorKey.Generate(_keyDir);
        Keyed();
        var keyed = Store(OperatorKey.TryLoad());
        await keyed.ActivateSigningAsync(T0.AddHours(1));

        (await keyed.ListAsync()).Should().ContainSingle("the operator authorized it as it stood");

        Rewrite("ext-1", "State", nameof(ProposalState.Admitted));

        var list = async () => await keyed.ListAsync();
        (await list.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*ext-1.json*bytes have changed*");
    }

    [Fact]
    public async Task A_grandfathered_record_reformatted_but_unchanged_still_reads()
    {
        // The brick this design had to avoid: hashing the FILE rather than the record would make a
        // trailing newline, a reformat or a text=auto line-ending normalisation refuse the whole
        // store forever, with the operator's only exit being the command that re-blesses the set.
        Keyless();
        await Store().RecordAsync(Proposal("ext-1"), Held(), T0);

        OperatorKey.Generate(_keyDir);
        Keyed();
        var keyed = Store(OperatorKey.TryLoad());
        await keyed.ActivateSigningAsync(T0.AddHours(1));

        Reformat("ext-1");

        (await keyed.ListAsync()).Should().ContainSingle("only the VALUES are hashed, and none moved");
        (await keyed.GetAsync("ext-1")).Should().NotBeNull();
    }

    [Fact]
    public async Task A_grandfathered_id_leaves_the_inventory_once_it_is_signed()
    {
        // Once the operator decides a grandfathered record under their key it is signed, and its
        // inventory entry now pins bytes that are no longer on disk. Leave the entry and an actor
        // can put those exact bytes back — a downgrade to the pre-decision verdict.
        Keyless();
        await Store().RecordAsync(Proposal("ext-1"), Held(), T0);

        OperatorKey.Generate(_keyDir);
        Keyed();
        var keyed = Store(OperatorKey.TryLoad());
        var (before, _, _, _) = await keyed.ActivateSigningAsync(T0.AddHours(1));
        before.Grandfathered.Should().ContainSingle().Which.Id.Should().Be("ext-1");

        await keyed.DecideAsync("ext-1", admit: true, "alice", "seated", T0.AddHours(2));
        GateSigningActivation.TryRead(_state)!.Grandfathered.Should().BeEmpty("the id left the inventory with the signature");

        Strip("ext-1");
        var list = async () => await keyed.ListAsync();
        (await list.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*ext-1.json*not among them*");
    }

    [Fact]
    public async Task Adopting_a_store_of_unsigned_records_keeps_every_one_readable()
    {
        // Brick-on-adoption would make `ashlar keys init` a destructive command, and a store with a
        // real history is where that would bite. Every record the operator authorized keeps
        // reading, and every admission among them keeps counting.
        Keyless();
        await Store().RecordAsync(Proposal("ext-seed"), Held(), T0);
        for (var i = 0; i < 300; i++)
        {
            Unsigned($"ext-{i:D3}", i % 3 == 0 ? ProposalState.Admitted : ProposalState.Held, T0.AddMinutes(i), "held for review");
        }

        OperatorKey.Generate(_keyDir);
        Keyed();
        var keyed = Store(OperatorKey.TryLoad());
        var (marker, _, _, _) = await keyed.ActivateSigningAsync(T0.AddDays(1));
        marker.Grandfathered.Should().HaveCount(301);
        marker.Grandfathered!.Select(g => g.Id).Should().BeInAscendingOrder(StringComparer.Ordinal);

        (await keyed.ListAsync()).Should().HaveCount(301);
        (await keyed.AdmittedInWindowAsync(TimeSpan.FromDays(2), T0.AddDays(1))).Should().Be(100);
    }

    [Fact]
    public async Task Keys_init_does_not_free_self_extension_budget()
    {
        // The price of excluding unsigned records from the count: running the one command an
        // operator is told to run would silently free up to a full window of budget. An unsigned
        // record that survived an inventory-bearing marker was witnessed by the operator's own
        // key, so counting it is counting what they authorized.
        Keyless();
        var keyless = Store();
        await keyless.RecordAsync(Proposal("ext-1"), Held(), T0);
        await keyless.DecideAsync("ext-1", admit: true, "alice", "seated", T0.AddMinutes(1));

        var before = await keyless.AdmittedInWindowAsync(TimeSpan.FromHours(24), T0.AddHours(1));
        before.Should().Be(1);

        OperatorKey.Generate(_keyDir);
        Keyed();
        var keyed = Store(OperatorKey.TryLoad());
        await keyed.ActivateSigningAsync(T0.AddHours(1));

        (await keyed.AdmittedInWindowAsync(TimeSpan.FromHours(24), T0.AddHours(1))).Should().Be(
            before, "activation authorizes a set; it does not forget one");
    }

    // ──────────── the marker is the operator's, and only they may move it ────────────

    [Fact]
    public async Task A_keyed_write_refuses_to_re_activate_over_a_deleted_marker()
    {
        // The marker is written once and never removed, so a store with verifying records and no
        // marker got there by deletion. Re-minting an inventory over whatever is on disk NOW is
        // the laundering step, and the automatic path must not perform it — a keyed admit would
        // otherwise bless every record the attacker left behind.
        Keyed();
        var signer = OperatorKey.Generate(_keyDir);
        var store = Store(signer);
        await store.RecordAsync(Proposal("ext-1"), Held(), T0);
        await store.RecordAsync(Proposal("ext-2"), Held(), T0.AddHours(1));
        File.Delete(MarkerFile);

        var write = async () => await store.RecordAsync(Proposal("ext-3"), Held(), T0.AddHours(2));
        (await write.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*gate-signing.json is missing*sign-activate --repair*");

        File.Exists(MarkerFile).Should().BeFalse("a refused write must not re-create the marker at the decision instant");
        File.Exists(RecordFile("ext-3")).Should().BeFalse();
    }

    [Fact]
    public async Task A_repaired_marker_cannot_move_the_grace_floor_forward()
    {
        // `--repair` is the operator's exit from the state above, and it is the most dangerous verb
        // here: it re-blesses whatever is on disk. Two things bound it, and this is the second —
        // a re-mint is dated at the earliest instant the surviving signatures already prove, never
        // at "now", so deleting the marker cannot buy the attacker a wider grace window.
        Keyed();
        var signer = OperatorKey.Generate(_keyDir);
        var store = Store(signer);
        await store.RecordAsync(Proposal("ext-1"), Held(), T0);
        await store.RecordAsync(Proposal("ext-2"), Held(), T0.AddHours(1));
        File.Delete(MarkerFile);

        var (marker, wasActive, _, _) = await store.ActivateSigningAsync(T0.AddHours(5), repair: true);
        wasActive.Should().BeFalse("there was no marker to be already active");
        marker.ActivatedAt.Should().Be(T0, "never dated later than this store's own signatures prove");
        marker.Grandfathered.Should().BeEmpty("both records are signed; there is nothing to grandfather");

        Strip("ext-1");
        var list = async () => await store.ListAsync();
        (await list.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*ext-1.json*not among them*",
                "the EARLIEST record is exactly the one a derived date floor could never date");
    }

    [Fact]
    public async Task A_planted_foreign_marker_does_not_block_activation_and_does_not_ride_a_keyed_write()
    {
        // A marker planted under a key nobody here vouches for is somebody else's declaration about
        // this store. Honouring it would ride a foreign activation; returning it as success would
        // tell the operator their store is signed when it is not; refusing forever would brick
        // every keyed write with no named exit. So: the automatic path refuses and names the verb,
        // and the verb replaces.
        Keyed();
        var signer = OperatorKey.Generate(_keyDir);
        var foreign = OperatorKey.Generate(_otherKeyDir);
        File.WriteAllText(MarkerFile, MarkerJson(
            GateSigningActivation.Signed(foreign, new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero), [])));

        var store = Store(signer);
        var write = async () => await store.RecordAsync(Proposal("ext-1"), Held(), T0);
        (await write.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*does not vouch for*ashlar gates sign-activate*");
        File.Exists(RecordFile("ext-1")).Should().BeFalse();
        GateSigningActivation.TryRead(_state)!.Signer.Should().Be(
            foreign.PublicKeyBase64, "the refused write left the plant exactly where it was");

        var (marker, wasActive, replaced, _) = await store.ActivateSigningAsync(T0.AddHours(1));
        wasActive.Should().BeTrue("a marker was there — it was simply not this operator's");
        replaced.Should().BeTrue(
            "the caller must be TOLD it overwrote a foreign declaration about this store. Reported as a "
            + "bare already-active, the operator learns nothing about the one event on this path that "
            + "warrants their attention");
        marker.Signer.Should().Be(signer.PublicKeyBase64);
        marker.ActivatedAt.Should().Be(T0.AddHours(1), "a foreign marker's instant is not ours to keep");

        await store.RecordAsync(Proposal("ext-1"), Held(), T0.AddHours(2));
        (await store.ListAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task A_v1_marker_is_refused_by_name_not_reinterpreted()
    {
        // A marker written before the inventory existed verifies perfectly — CanonicalJson omits
        // nulls, so the two shapes canonicalize identically — and says nothing about which unsigned
        // records the operator authorized. The two wrong readings are a bare JSON error, which
        // tells the operator nothing, and "grandfather everything", which is the forgery. Hence a
        // nullable member and a named refusal: `required` would make this arm unreachable.
        Keyed();
        var signer = OperatorKey.Generate(_keyDir);
        await Store(signer).RecordAsync(Proposal("ext-1"), Held(), T0);
        File.WriteAllText(MarkerFile, V1MarkerJson(signer, T0));

        var list = async () => await Store(signer).ListAsync();
        (await list.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*gate-signing.json*no grandfather inventory*sign-activate --repair*");

        var (repaired, wasActive, _, _) = await Store(signer).ActivateSigningAsync(T0.AddHours(1), repair: true);
        wasActive.Should().BeTrue();
        repaired.Grandfathered.Should().NotBeNull().And.BeEmpty();
        repaired.ActivatedAt.Should().Be(T0, "a marker this machine vouches for keeps its instant even under repair");
        (await Store(signer).ListAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task A_keyless_write_is_refused_by_a_marker_with_no_verifying_record()
    {
        // The write guard is stricter than the read rule ON PURPOSE, and this is the arm that says
        // so: a marker that verifies but that THIS reader cannot vouch for creates no expectation,
        // so the read is permissive — and the keyless write is still refused, because a keyless
        // writer cannot vouch for the marker and must not gamble that a keyed reader will not
        // honour it. Nothing else in this file pinned that arm.
        Keyless();
        await Store().RecordAsync(Proposal("ext-1"), Held(), T0);
        var elsewhere = OperatorKey.Generate(_otherKeyDir);
        File.WriteAllText(MarkerFile, MarkerJson(GateSigningActivation.Signed(elsewhere, T0, [])));

        var keyless = Store();
        (await keyless.ListAsync()).Should().ContainSingle("no key here and no record corroborates the marker");
        keyless.SignatureTrust!.Expected.Should().BeFalse();
        keyless.SignatureTrust.MarkerPresent.Should().BeTrue();

        var record = async () => await keyless.RecordAsync(Proposal("ext-2"), Held(), T0.AddHours(1));
        (await record.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*ashlar keys init*");
        File.Exists(RecordFile("ext-2")).Should().BeFalse("nothing landed");
    }

    [Fact]
    public async Task A_fabricated_unsigned_admission_cannot_spend_the_self_extension_budget()
    {
        // The threat model this rule exists for. AdmittedInWindowAsync counts what is present
        // in gates/; a hand-written Admitted record — no gate ran, nobody decided — used to count
        // toward the budget exactly like a real one (and, deleted, would raise it). Once the store
        // is signed, the fabrication refuses the WHOLE propose transaction before anything is
        // counted or written. Budget 1: with the forgery counted, the honest proposal would be
        // held as over-budget; without it, admitted.
        Keyed();
        var signer = OperatorKey.Generate(_keyDir);
        var store = Store(signer);
        await store.ActivateSigningAsync(T0);   // `ashlar keys init` in the project

        Fabricate("ext-fake", ProposalState.Admitted, T0.AddHours(1));

        var propose = async () => await store.ProposeAsync(Policy(extensions: 1), Proposal("ext-1"), T0.AddHours(2));
        (await propose.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*ext-fake.json*carries no signature*",
                "the fabricated admission is refused, not counted");
        File.Exists(RecordFile("ext-1")).Should().BeFalse("the transaction never reached the write");
    }

    /// <summary>
    /// A refusal is worth nothing if the verb it names performs the very step it refused.
    ///
    /// <para>Rule (e) of the keyed write branch refuses to auto-activate over a store whose marker
    /// has been deleted, because re-minting an inventory over whatever is on disk NOW permanently
    /// blesses every unsigned record present at this instant — including one an actor placed there
    /// while the marker was gone, dated under a floor the store's own surviving records expose.
    /// The operator reading that refusal is told to run a verb. If the PLAIN verb re-mints, the
    /// refusal has not stopped the attack: it has routed it through the one command the operator
    /// was told to run, and they see a tick.</para>
    ///
    /// <para>So the plain verb refuses too, and names the one that re-mints deliberately. The
    /// second half of this fact is the part that keeps the design honest — the named exit EXISTS,
    /// it anchors at the earliest instant the surviving signatures prove rather than at now, and
    /// it says out loud that it is blessing the planted record. That printed number is the only
    /// control on <c>--repair</c>, which is why it is asserted here and not merely rendered.</para>
    /// </summary>
    [Fact]
    public async Task A_plain_activation_refuses_to_re_mint_over_a_deleted_marker()
    {
        Keyed();
        var signer = OperatorKey.Generate(_keyDir);
        await Store(signer).RecordAsync(Proposal("ext-1"), Held(), T0);
        File.Exists(MarkerFile).Should().BeTrue("the first keyed write activates this store");

        // The marker is gone. The signed record still verifies, so the store still PROVES it was
        // signing — which is what distinguishes a deleted marker from a store that never had one.
        File.Delete(MarkerFile);
        Fabricate("evil", ProposalState.Admitted, T0.AddHours(-1));

        var plain = async () => await Store(signer).ActivateSigningAsync(T0.AddHours(5));
        (await plain.Should().ThrowAsync<InvalidOperationException>(
                "records that verify beside a marker that is gone mean the marker was deleted, and "
                + "minting a fresh inventory over the store as it stands is the laundering step the "
                + "write path already refuses — the operator verb must not be the way around it"))
            .WithMessage("*sign-activate --repair*");
        File.Exists(MarkerFile).Should().BeFalse("the refused activation wrote no marker");

        var repaired = await Store(signer).ActivateSigningAsync(T0.AddHours(5), repair: true);
        repaired.Marker.ActivatedAt.Should().Be(T0,
            "a re-mint anchors at the earliest instant the surviving signatures prove, never at now, "
            + "or deleting the marker and the newest records would walk the floor forward");
        repaired.Marker.Grandfathered.Should().ContainSingle().Which.Id.Should().Be("evil");
        repaired.GrandfatheredAdmitted.Should().Be(1,
            "the planted record is admitted, and this number is the whole of what stands between the "
            + "operator and blessing it — an exit that blessed it silently would be worse than none");
    }

    /// <summary>
    /// <see cref="GateStore.GetAsync"/> and <see cref="GateStore.ListAsync"/> must answer about the
    /// same store, and the id the caller asked for is the id INSIDE the record — never the name of
    /// the file it happens to sit in.
    ///
    /// <para><b>The shape this refuses.</b> Get used to resolve <c>{id}.json</c>, check
    /// <c>File.Exists</c>, and then match the file name ordinally. On Windows and default macOS
    /// <c>File.Exists("ext-1.json")</c> succeeds against a file called <c>EXT-1.json</c> while the
    /// ordinal compare fails; with any other rename it returns null on every platform. Either way
    /// List returned a record Get called absent — so <c>DecideAsync</c> reported no such proposal
    /// and <c>PackageImport</c>'s dedup probe re-imported it. That is precisely the two-funnels-
    /// disagree defect this class exists to remove, reintroduced by a string comparison.</para>
    ///
    /// <para>The store here has never been signed, which is the only state in which a file's name
    /// and the id inside it are allowed to differ: Group 3's file-name leg binds them from the
    /// instant the store is signed, and refuses through BOTH funnels when they diverge.</para>
    /// </summary>
    [Fact]
    public async Task Get_and_list_agree_about_a_record_whose_file_name_is_not_its_id()
    {
        Keyless();
        await Store().RecordAsync(Proposal("ext-1"), Held(), T0);
        File.Move(RecordFile("ext-1"), Path.Combine(_state, "gates", "renamed.json"));

        var store = Store();
        (await store.ListAsync()).Should().ContainSingle()
            .Which.Proposal.Id.Should().Be("ext-1");

        var got = await store.GetAsync("ext-1");
        got.Should().NotBeNull(
            "ListAsync returns this record, so GetAsync must too. A single-record read that answers "
            + "from the filesystem's naming rules rather than from the store is a second, weaker "
            + "resolution point wearing a cheaper disguise");
        got!.Proposal.Id.Should().Be("ext-1");

        (await store.GetAsync("ext-absent")).Should().BeNull(
            "an id no record here carries still reads as absent rather than as an error");
    }

    /// <summary>
    /// Activation must refuse a store it is about to make unreadable, rather than bless it and
    /// leave the operator holding the pieces.
    ///
    /// <para>Activation flips <c>Expected</c> true, and from that instant Group 3's file-name leg
    /// refuses any record whose file is not named for the id inside it. A store holding an
    /// operator's manual copy — or any misnamed file — reads perfectly well beforehand, because the
    /// leg is inert while the store has never been signed. Minting a marker over it turns EVERY
    /// read into a refusal, and the remedy the refusal names, <c>--repair</c>, re-mints the same
    /// broken set: the operator's exit leads back to the same wall. So <c>keys init</c> — the one
    /// command an operator is told to run — could brick the store it was asked to bless.</para>
    ///
    /// <para>The refusal names the offending file, happens before anything is written, and leaves
    /// the store readable, which is the state in which the files can still be moved.</para>
    /// </summary>
    [Fact]
    public async Task Activation_refuses_a_store_it_would_brick_rather_than_blessing_it()
    {
        Keyless();
        await Store().RecordAsync(Proposal("ext-1"), Held(), T0);
        File.Copy(RecordFile("ext-1"), Path.Combine(_state, "gates", "ext-1-backup.json"));
        (await Store().ListAsync()).Should().HaveCount(2, "nothing refuses this store yet");

        Keyed();
        var signer = OperatorKey.Generate(_keyDir);
        var activate = async () => await Store(signer).ActivateSigningAsync(T0.AddHours(1));

        (await activate.Should().ThrowAsync<InvalidOperationException>(
                "a marker here would refuse every subsequent read of this store, and --repair would "
                + "re-mint the same set, so the operator would have no exit that is not a deletion"))
            .WithMessage("*ext-1-backup.json*");

        File.Exists(MarkerFile).Should().BeFalse("the refused activation wrote no marker");
        (await Store(signer).ListAsync()).Should().HaveCount(2,
            "the store still reads, which is the state the operator needs in order to fix it");
    }
}
