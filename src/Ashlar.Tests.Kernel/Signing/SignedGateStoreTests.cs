using System.Text.Json.Nodes;
using FluentAssertions;
using Ashlar.Manifest;
using Ashlar.Manifest.Admission;
using Ashlar.Manifest.Signing;
using Xunit;

namespace Ashlar.Tests.Kernel.Signing;

/// <summary>
/// Signing wired into the admission store, end to end. The store is where a signature earns
/// its keep: a gate verdict is the most security-sensitive record in the system, and the
/// rule under test is <strong>S-1 — a record whose signature does not match its contents is
/// corrupt, and a corrupt verdict is refused loudly rather than trusted quietly</strong>. The
/// companion rule <strong>S-2</strong> is that a store with no key writes exactly today's
/// honest unsigned record.
///
/// <para><b>Hermetic against the machine's key directory.</b> <see cref="GateStore"/>'s constructor
/// pins its signer set from <see cref="OperatorKey.TrustedPublicKeysBase64"/>, which resolves
/// <c>ASHLAR_KEY_DIR</c> / <c>~/.ashlar/keys</c> — so on a developer box that has run
/// <c>ashlar keys init</c>, the readers these facts call KEYLESS would hold that machine's
/// <c>operator.pub</c> and refuse records signed by the test's own key. The variable is pointed at
/// a fresh EMPTY directory for the lifetime of the class, never at <see cref="_keyDir"/>: pointing
/// it there would turn the keyless facts green while making their reader keyed, which is the
/// opposite of what their names promise. The keyed facts are unaffected, because the store appends
/// the signer it was constructed with to the pinning set.</para>
/// </summary>
[Collection("EnvironmentSensitive")]
public sealed class SignedGateStoreTests : IDisposable
{
    private const string KeyDirVariable = "ASHLAR_KEY_DIR";

    private readonly string _root;
    private readonly string _keyDir;
    private readonly string? _previousKeyDir;

    public SignedGateStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "signed-gate-" + Guid.NewGuid().ToString("N"));
        _keyDir = Path.Combine(_root, "keys");
        Directory.CreateDirectory(_root);
        _previousKeyDir = Environment.GetEnvironmentVariable(KeyDirVariable);
        var ambient = Path.Combine(_root, "no-ambient-keys");
        Directory.CreateDirectory(ambient);
        Environment.SetEnvironmentVariable(KeyDirVariable, ambient);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(KeyDirVariable, _previousKeyDir);
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static readonly DateTimeOffset Now = new(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);

    private static ExtensionProposal Proposal(string id) => new()
    {
        Id = id,
        Kind = "brick",
        Summary = "add brick signed.demo",
        ProposedBy = "author",
        ProposedAt = Now,
        Courses = [new CourseResult { Name = "tests", Passed = true, Detail = "ok" }],
    };

    private static AdmissionOutcome Held(string reason) =>
        new() { State = ProposalState.Held, Reason = reason };

    private string RecordFile(string id) => Path.Combine(_root, "gates", id + ".json");

    // ─────────────────────────── S-1: signed and verifying ───────────────────────────

    [Fact]
    public async Task A_signed_record_carries_the_signer_and_reads_back_clean()
    {
        var signer = OperatorKey.Generate(_keyDir);
        var store = new GateStore(_root, signer);

        await store.RecordAsync(Proposal("ext-signed"), Held("holding for review"), Now);

        var read = await store.GetAsync("ext-signed");
        read.Should().NotBeNull();
        read!.Sig.Should().NotBeNullOrEmpty("a store with a key signs every record");
        read.Signer.Should().Be(signer.PublicKeyBase64);
    }

    [Fact]
    public async Task A_tampered_body_fails_closed_on_read()
    {
        var signer = OperatorKey.Generate(_keyDir);
        var store = new GateStore(_root, signer);
        await store.RecordAsync(Proposal("ext-body"), Held("ORIGINAL-REASON"), Now);

        // Flip a signed field on disk — exactly what a forged verdict looks like. The
        // signature was computed over the original bytes and cannot cover this.
        var file = RecordFile("ext-body");
        var text = await File.ReadAllTextAsync(file);
        text.Should().Contain("ORIGINAL-REASON");
        await File.WriteAllTextAsync(file, text.Replace("ORIGINAL-REASON", "FORGED-REASON"));

        var act = async () => await store.GetAsync("ext-body");

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*signature that does not verify*forged verdict is worse than a missing one*");
    }

    [Fact]
    public async Task A_tampered_signature_fails_closed_on_read()
    {
        var signer = OperatorKey.Generate(_keyDir);
        var store = new GateStore(_root, signer);
        await store.RecordAsync(Proposal("ext-sig"), Held("holding"), Now);

        // Mutate one character of the signature but keep it well-formed base64, so the failure
        // is a verification failure, not a parse failure — both fail closed, but this proves
        // the cryptographic check itself is what refuses.
        var file = RecordFile("ext-sig");
        var node = JsonNode.Parse(await File.ReadAllTextAsync(file))!;
        var sig = node["Sig"]!.GetValue<string>();
        var flipped = (sig[0] == 'A' ? 'B' : 'A') + sig[1..];
        node["Sig"] = flipped;
        await File.WriteAllTextAsync(file, node.ToJsonString());

        var act = async () => await store.GetAsync("ext-sig");

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task A_signed_record_verifies_even_for_a_reader_that_holds_no_key()
    {
        // Verification is intrinsic to the record (it carries its own public key), so a fresh
        // checkout with no operator key can still detect a forged verdict. The fail-closed
        // guarantee must not depend on the reader happening to hold keys.
        var signer = OperatorKey.Generate(_keyDir);
        await new GateStore(_root, signer).RecordAsync(Proposal("ext-intrinsic"), Held("holding"), Now);

        var keyless = new GateStore(_root);   // no signer at all

        var read = await keyless.GetAsync("ext-intrinsic");
        read.Should().NotBeNull();
        read!.Signer.Should().Be(signer.PublicKeyBase64);

        // And the keyless reader still catches tampering.
        var file = RecordFile("ext-intrinsic");
        await File.WriteAllTextAsync(file, (await File.ReadAllTextAsync(file)).Replace("holding", "seated"));
        var act = async () => await keyless.GetAsync("ext-intrinsic");
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task A_keyless_store_refuses_to_decide_a_record_in_a_signed_store()
    {
        // A DELIBERATE CONTRACT REVERSAL. This test used to assert that a keyless DecideAsync
        // rewrote a signed Held record UNSIGNED so the store was not bricked by a stale signature.
        // That rewrite is exactly the silent self-downgrade rule S-6 forbids: once a store is known
        // to be signed, an unsigned record decided after activation is indistinguishable from one
        // whose signature was stripped, so the keyless write is refused BEFORE anything lands —
        // by name, pointing at the remedy — and the record stays Held and signed. The concern the
        // old test carried (a bricked enumeration) is kept: ListAsync must still read cleanly.
        var signer = OperatorKey.Generate(_keyDir);
        await new GateStore(_root, signer).RecordAsync(Proposal("ext-decide"), Held("holding for review"), Now);

        var keyless = new GateStore(_root);   // key removed, or a peer that never had it
        var act = async () => await keyless.DecideAsync("ext-decide", admit: true, "alice", "looks good", Now.AddMinutes(1));

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*ashlar keys init*", "the refusal must name the remedy")
            .WithMessage("*NOT the remedy*", "and must say out loud that deleting records is not it");

        var read = await keyless.GetAsync("ext-decide");
        read!.State.Should().Be(ProposalState.Held, "nothing was written");
        read.Sig.Should().NotBeNullOrEmpty("the signed record is untouched");

        // The bricking failure would have taken ListAsync down too; prove the store still enumerates.
        (await keyless.ListAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task A_keyed_store_redeciding_resigns_over_the_new_content()
    {
        // The signed path must re-sign the mutated record, not carry the Held signature onto
        // the Admitted content. GetAsync verifies fail-closed, so a passing read is the proof:
        // if the signature still covered the old bytes it would throw here.
        var signer = OperatorKey.Generate(_keyDir);
        var store = new GateStore(_root, signer);
        await store.RecordAsync(Proposal("ext-redecide"), Held("holding"), Now);

        await store.DecideAsync("ext-redecide", admit: true, "alice", "ok", Now.AddMinutes(1));

        var read = await store.GetAsync("ext-redecide");
        read!.State.Should().Be(ProposalState.Admitted);
        read.Sig.Should().NotBeNullOrEmpty("the new content is signed over, not the old");
        read.Signer.Should().Be(signer.PublicKeyBase64);
    }

    [Fact]
    public async Task Returned_records_are_exactly_what_was_persisted_signature_included()
    {
        // The wart the packaging tests caught: WriteAsync used to sign a local copy and persist
        // it while callers returned THEIR unsigned/stale copy — so RecordAsync returned Sig=null
        // and DecideAsync returned the pre-decision signature over post-decision content. Any
        // consumer of a returned record (the package exporter above all) would hand out a verdict
        // that fails its own verification. The API contract now: what returns is what persisted.
        var signer = OperatorKey.Generate(_keyDir);
        var store = new GateStore(_root, signer);

        var recorded = await store.RecordAsync(Proposal("ext-returned"), Held("holding"), Now);
        recorded.Sig.Should().NotBeNullOrEmpty("RecordAsync must return the signed record it wrote");
        recorded.Should().BeEquivalentTo(await store.GetAsync("ext-returned"));

        var decided = await store.DecideAsync("ext-returned", admit: true, "alice", "ok", Now.AddMinutes(1));
        decided.Sig.Should().NotBe(recorded.Sig, "the decision re-signs over the NEW content");
        decided.Should().BeEquivalentTo(await store.GetAsync("ext-returned"),
            "the returned decision must be byte-for-byte what a re-read sees — GetAsync verifies "
            + "fail-closed, so equivalence here proves the returned signature covers the returned content");
    }

    // ─────────────────────────── S-2: no key, unsigned ───────────────────────────

    [Fact]
    public async Task Without_a_key_the_record_is_written_unsigned()
    {
        var store = new GateStore(_root);   // presence-activated: no signer supplied

        await store.RecordAsync(Proposal("ext-unsigned"), Held("holding"), Now);

        var read = await store.GetAsync("ext-unsigned");
        read.Should().NotBeNull();
        read!.Sig.Should().BeNull("no key means no signature — never a half-signed record");
        read.Signer.Should().BeNull();
    }

    [Fact]
    public async Task An_unsigned_record_is_not_subjected_to_verification()
    {
        // The read path only verifies records that claim a signature. An unsigned record edited
        // in place is not a signature failure — it simply has no signature to check. (Integrity
        // of unsigned records is not a promise v1 makes, and pretending otherwise would be a lie
        // in the other direction.)
        var store = new GateStore(_root);
        await store.RecordAsync(Proposal("ext-plain"), Held("holding"), Now);

        var file = RecordFile("ext-plain");
        await File.WriteAllTextAsync(file, (await File.ReadAllTextAsync(file)).Replace("holding", "seated"));

        var read = await store.GetAsync("ext-plain");
        read!.Reason.Should().Be("seated", "an unsigned record carries no cryptographic promise");
    }
}
