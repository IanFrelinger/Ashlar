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

    /// <summary>A record nobody decided, written straight into gates/ with no signature.</summary>
    private void Fabricate(string id, ProposalState state, DateTimeOffset decidedAt)
    {
        var record = new GateRecord
        {
            Proposal = Proposal(id),
            State = state,
            Reason = "fabricated by hand",
            Actor = "gate",
            DecidedAt = decidedAt,
        };
        File.WriteAllText(RecordFile(id), JsonSerializer.Serialize(record, Json));
    }

    private static string MarkerJson(GateSigningActivation marker) =>
        JsonSerializer.Serialize(marker, Json);

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
        // The adoption path when NO explicit activation was run: the first signed write anchors
        // the marker to its own DecidedAt, so the unsigned history before it is grandfathered.
        Keyless();
        await Store().RecordAsync(Proposal("ext-0"), Held(), T0);

        Keyed();
        var signer = OperatorKey.Generate(_keyDir);
        var activatedAt = T0.AddDays(1);
        await Store(signer).RecordAsync(Proposal("ext-1"), Held(), activatedAt);

        var keyed = Store(signer);
        (await keyed.ListAsync()).Should().HaveCount(2, "ext-0 was decided before signing existed");
        (await keyed.GetAsync("ext-0"))!.Sig.Should().BeNull();
        keyed.SignatureTrust!.GraceBefore.Should().Be(activatedAt);

        var marker = GateSigningActivation.TryRead(_state);
        marker.Should().NotBeNull("the first signed write activates");
        marker!.ActivatedAt.Should().Be(activatedAt, "anchored to the first signed record's DecidedAt, not to a clock");
        marker.Signer.Should().Be(signer.PublicKeyBase64);
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
        // signature they then strip, hoping the record reads as pre-activation. The grace floor is
        // the MIN over every anchor, and the store's earliest verifying record dates the marker.
        Keyed();
        var signer = OperatorKey.Generate(_keyDir);
        var store = Store(signer);
        await store.RecordAsync(Proposal("ext-1"), Held(), T0);
        await store.RecordAsync(Proposal("ext-2"), Held(), T0.AddHours(1));

        // A VALIDLY signed marker (the operator's own key — a stolen-key or careless-operator
        // scenario), dated after both records.
        File.Delete(MarkerFile);
        File.WriteAllText(MarkerFile, MarkerJson(GateSigningActivation.Signed(signer, T0.AddHours(2))));
        Strip("ext-2");

        var list = async () => await store.ListAsync();
        (await list.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*ext-2.json*carries no signature*",
                "ext-1 verifies and was decided at T0, so the grace floor is T0 whatever the marker says");
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
        File.WriteAllText(MarkerFile, MarkerJson(GateSigningActivation.Signed(foreign, new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero))));

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
}
