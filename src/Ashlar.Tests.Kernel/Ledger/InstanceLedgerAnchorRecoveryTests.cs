using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Ashlar.Manifest.Ledger;
using Ashlar.Manifest.Signing;
using Xunit;

namespace Ashlar.Tests.Kernel.Ledger;

/// <summary>
/// The anchor's refusals were right about the danger and wrong about the cure.
///
/// <para>All five ended with "Fix: re-certify with a signed 'ashlar verify', which rewrites the
/// anchor over the real head". Append called the same anchor check the read path uses, before it
/// wrote anything — so every state that made a read refuse made the named fix refuse too, with the
/// byte-identical message. There was no re-anchor path and no <c>--force</c>; the only thing that
/// worked was deleting <c>.ashlar/ledger</c> and <c>ledger.head.json</c> by hand, which is exactly
/// the destructive act the anchor exists to detect. And it was not only an attacker scenario: an
/// append moves the entry and then the anchor, so a kill, a power loss or an ENOSPC in that window
/// left a chain longer than its anchor and the project could NEVER be certified again.</para>
///
/// <para>These facts pin both halves. A refusal that names <c>ashlar verify</c> as the fix must be
/// one that <c>ashlar verify</c> actually fixes; a refusal that names restoring from backup must be
/// one that append still refuses, because clearing it is indistinguishable from accepting a
/// truncation. And the crash window itself is closed: the anchor declares the entry it is about to
/// write before that entry lands, so an interrupted append reads clean instead of reading as
/// tampering.</para>
/// </summary>
public sealed class InstanceLedgerAnchorRecoveryTests : IDisposable
{
    private readonly string _root;
    private readonly SigningIdentity _signer;

    public InstanceLedgerAnchorRecoveryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ledger-recover-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _signer = OperatorKey.Generate(Path.Combine(_root, "keys"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static readonly DateTimeOffset Now = new(2026, 9, 2, 3, 0, 0, TimeSpan.Zero);
    private string StateRoot => Path.Combine(_root, ".ashlar");
    private InstanceLedger Open() => new(StateRoot);
    private string LedgerDir => Path.Combine(StateRoot, "ledger");
    private string EntryFile(int seq) => Path.Combine(LedgerDir, seq.ToString("D6") + ".json");
    private string AnchorFile => Path.Combine(StateRoot, "ledger.head.json");

    private static IReadOnlyList<LedgerCourse> Courses() =>
        [new LedgerCourse { Name = "contract", Passed = true, Detail = "both documents load" }];

    private Task<LedgerEntry> Append(InstanceLedger led, string subject, int atMinutes = 0) =>
        led.AppendVerificationAsync(_signer, subject, verified: true, Courses(), Now.AddMinutes(atMinutes));

    // ---- The half that must now be repairable ------------------------------------------------

    [Fact]
    public async Task A_crash_between_the_entry_and_the_anchor_no_longer_bricks_the_project()
    {
        // The exact shape a kill or a power cut used to leave behind: the chain has outrun its
        // anchor. Reproduced here by rolling the anchor back to the copy taken one entry earlier.
        var led = Open();
        await Append(led, "s1", 0);
        var earlier = await File.ReadAllTextAsync(AnchorFile);
        await Append(led, "s2", 1);
        await File.WriteAllTextAsync(AnchorFile, earlier);

        // The read still refuses — detection is not what changed.
        var read = async () => await led.VerifyChainAsync();
        var refusal = (await read.Should().ThrowAsync<InvalidOperationException>()).Subject.Single();
        refusal.Message.Should().Contain("re-certify with a signed");

        // And the fix it names actually works. Before, this threw the identical message.
        var entry = await Append(led, "s3", 2);
        entry.Seq.Should().Be(3);
        (await led.VerifyChainAsync()).Count.Should().Be(3);
    }

    [Fact]
    public async Task The_repair_is_recorded_in_the_history_not_performed_in_place_of_it()
    {
        // Re-pinning silently would erase the only evidence that anything was ever wrong. The
        // entry that carries the repair says so, signed, at the sequence where it happened.
        var led = Open();
        await Append(led, "s1", 0);
        var earlier = await File.ReadAllTextAsync(AnchorFile);
        await Append(led, "s2", 1);
        await File.WriteAllTextAsync(AnchorFile, earlier);

        var entry = await Append(led, "s3", 2);

        var repair = entry.Courses.Should().ContainSingle(c => c.Name == "ledger-anchor").Subject;
        repair.Passed.Should().BeFalse("a repaired anchor is not a clean verification");
        repair.Detail.Should().Contain("re-pinned");
    }

    [Fact]
    public async Task A_missing_anchor_beside_a_chain_that_verifies_is_repairable()
    {
        // Deleting the anchor after a genesis append is also what an interrupted first append
        // leaves behind. The read names `ashlar verify` as the fix, so `ashlar verify` must fix it.
        var led = Open();
        await Append(led, "s1", 0);
        File.Delete(AnchorFile);

        var read = async () => await led.VerifyChainAsync();
        (await read.Should().ThrowAsync<InvalidOperationException>())
            .And.Message.Should().Contain("re-certify with a signed");

        await Append(led, "s2", 1);
        (await led.VerifyChainAsync()).Count.Should().Be(2);
    }

    [Fact]
    public async Task An_unreadable_anchor_is_repairable()
    {
        var led = Open();
        await Append(led, "s1", 0);
        await File.WriteAllTextAsync(AnchorFile, "{ this is not json");

        var read = async () => await led.VerifyChainAsync();
        (await read.Should().ThrowAsync<InvalidOperationException>())
            .And.Message.Should().Contain("re-certify with a signed");

        await Append(led, "s2", 1);
        (await led.VerifyChainAsync()).Count.Should().Be(2);
    }

    [Fact]
    public async Task An_anchor_whose_signature_does_not_verify_is_repairable()
    {
        var led = Open();
        await Append(led, "s1", 0);
        var node = JsonNode.Parse(await File.ReadAllTextAsync(AnchorFile))!;
        node["Seq"] = 99;
        await File.WriteAllTextAsync(AnchorFile, node.ToJsonString());

        var read = async () => await led.VerifyChainAsync();
        (await read.Should().ThrowAsync<InvalidOperationException>())
            .And.Message.Should().Contain("re-certify with a signed");

        await Append(led, "s2", 1);
        (await led.VerifyChainAsync()).Count.Should().Be(2);
    }

    [Fact]
    public async Task An_anchor_signed_by_a_stranger_is_repairable_and_the_swap_is_recorded()
    {
        // The fifth refusal names a signed verify too, so it has to be one a signed verify clears.
        // The chain itself is untouched and verifies end to end; what was replaced is the pin, and
        // re-pinning it with the key that signs the new entry is the whole repair.
        var led = Open();
        var entry1 = await Append(led, "s1", 0);
        var stranger = OperatorKey.Generate(Path.Combine(_root, "stranger-keys"));
        var forged = new LedgerHeadAnchor { Seq = 1, Hash = CanonicalHash(entry1), At = Now };
        var signedForgery = forged with
        {
            Sig = stranger.Sign(CanonicalJson.Bytes(forged)),
            Signer = stranger.PublicKeyBase64,
        };
        await File.WriteAllTextAsync(AnchorFile, JsonSerializer.Serialize(signedForgery));

        var read = async () => await led.VerifyChainAsync();
        var refusal = (await read.Should().ThrowAsync<InvalidOperationException>()).Subject.Single();
        refusal.Message.Should().Contain("signed by a different key");
        refusal.Message.Should().Contain("re-certify with a signed");

        var entry = await Append(led, "s2", 1);

        entry.Courses.Should().ContainSingle(c => c.Name == "ledger-anchor")
            .Which.Detail.Should().Contain("different key");
        (await led.VerifyChainAsync()).Count.Should().Be(2);
    }

    // ---- The half that must stay refused -----------------------------------------------------

    [Fact]
    public async Task A_truncated_history_still_cannot_be_certified_over_and_does_not_promise_otherwise()
    {
        // Truncation is the one thing the anchor exists to catch. Letting an ordinary verify clear
        // it would mean a fresh, valid-looking head could always be written over deleted entries.
        var led = Open();
        await Append(led, "s1", 0);
        await Append(led, "s2", 1);
        File.Delete(EntryFile(2));

        var read = async () => await led.VerifyChainAsync();
        var refusal = (await read.Should().ThrowAsync<InvalidOperationException>()).Subject.Single();
        refusal.Message.Should().Contain("deleted from the end");
        refusal.Message.Should().NotContain(
            "re-certify with a signed",
            "this is the message that used to name a fix which returned the identical refusal");
        refusal.Message.Should().Contain("restore").And.Contain("ReanchorAsync");

        var append = async () => await Append(led, "s3", 2);
        (await append.Should().ThrowAsync<InvalidOperationException>())
            .And.Message.Should().Contain("deleted from the end");
    }

    [Fact]
    public async Task A_destroyed_history_still_cannot_be_restarted_by_verifying()
    {
        var led = Open();
        await Append(led, "s1", 0);
        Directory.Delete(LedgerDir, recursive: true);

        var read = async () => await led.VerifyChainAsync();
        var refusal = (await read.Should().ThrowAsync<InvalidOperationException>()).Subject.Single();
        refusal.Message.Should().Contain("no entries at all");
        refusal.Message.Should().NotContain("re-certify with a signed");

        var append = async () => await Append(led, "s2", 1);
        (await append.Should().ThrowAsync<InvalidOperationException>())
            .And.Message.Should().Contain("no entries at all");
    }

    [Fact]
    public async Task A_tampered_entry_is_still_refused_by_the_chain_before_any_re_pin()
    {
        // The re-pin must never vouch for text that did not verify. The chain-level check runs
        // first and in full.
        var led = Open();
        await Append(led, "s1", 0);
        await Append(led, "s2", 1);

        var node = JsonNode.Parse(await File.ReadAllTextAsync(EntryFile(1)))!;
        node["Subject"] = "tampered";
        await File.WriteAllTextAsync(EntryFile(1), node.ToJsonString());
        File.Delete(AnchorFile);

        var append = async () => await Append(led, "s3", 2);
        (await append.Should().ThrowAsync<InvalidOperationException>())
            .And.Message.Should().Contain("signature that does not verify");
    }

    // ---- The explicit re-anchor verb ---------------------------------------------------------

    [Fact]
    public async Task Reanchor_accepts_a_truncated_history_deliberately_and_writes_no_entry()
    {
        var led = Open();
        await Append(led, "s1", 0);
        await Append(led, "s2", 1);
        File.Delete(EntryFile(2));

        var result = await led.ReanchorAsync(_signer, Now.AddMinutes(5));

        result.Count.Should().Be(1);
        result.Head!.Seq.Should().Be(1);
        (await led.VerifyChainAsync()).Count.Should().Be(1, "the anchor now pins the history as it stands");
        Directory.EnumerateFiles(LedgerDir, "*.json").Should().HaveCount(1, "re-anchoring extends nothing");
    }

    [Fact]
    public async Task Reanchor_still_refuses_a_chain_whose_entries_do_not_verify()
    {
        var led = Open();
        await Append(led, "s1", 0);
        var node = JsonNode.Parse(await File.ReadAllTextAsync(EntryFile(1)))!;
        node["Subject"] = "tampered";
        await File.WriteAllTextAsync(EntryFile(1), node.ToJsonString());

        var act = async () => await led.ReanchorAsync(_signer, Now.AddMinutes(5));

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .And.Message.Should().Contain("signature that does not verify");
    }

    [Fact]
    public async Task Reanchor_refuses_an_empty_ledger_that_has_no_anchor_either()
    {
        // NOTE THE STATE THIS PINS, because the name it used to carry —
        // "refuses_to_pin_an_empty_ledger" — reads as though an empty ledger were refused in
        // general, and that reading is the defect. An empty ledger UNDER A LIVE ANCHOR is a
        // destroyed history, and it is accepted (see the two tests below); a refusal there would
        // strand every message that names this command as its fix. Empty with NO anchor is a
        // different thing entirely: a project that was never certified, where nothing was lost and
        // there is nothing an anchor could pin.
        var act = async () => await Open().ReanchorAsync(_signer, Now);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .And.Message.Should().Contain("Nothing to re-anchor");
    }

    [Fact]
    public async Task Accepting_a_destroyed_history_is_flagged_as_a_loss_not_counted_as_survivors()
    {
        // Count is 1 here and NOTHING survived to make it — the 1 is the marker this call just
        // wrote. Every earlier reader of this result treated Count as a survivor count, which is
        // how the CLI came to tell an operator whose ledger had been deleted that one entry of it
        // was still there. The distinction has to live on the result, not in the caller's guess.
        var led = Open();
        await Append(led, "s1", 0);
        await Append(led, "s2", 1);
        Directory.Delete(LedgerDir, recursive: true);

        var result = await led.ReanchorAsync(_signer, Now.AddMinutes(5));

        result.DestroyedHistoryAccepted.Should().BeTrue();
        result.Count.Should().Be(1, "the new history is one entry long: the record of the loss");
        result.Head!.Verified.Should().BeFalse();
        (await led.VerifyChainAsync()).Count.Should().Be(1, "and the ledger verifies afterwards");
    }

    [Fact]
    public async Task Accepting_a_truncation_is_not_flagged_as_a_destroyed_history()
    {
        // The control. Survivors are survivors, and the flag must not fire for them, or the honest
        // message for one case becomes a false message for the other.
        var led = Open();
        await Append(led, "s1", 0);
        await Append(led, "s2", 1);
        File.Delete(EntryFile(2));

        var result = await led.ReanchorAsync(_signer, Now.AddMinutes(5));

        result.DestroyedHistoryAccepted.Should().BeFalse();
        result.Count.Should().Be(1);
    }

    [Fact]
    public async Task A_verifying_chain_is_never_flagged_as_a_destroyed_history()
    {
        var led = Open();
        await Append(led, "s1", 0);

        (await led.VerifyChainAsync()).DestroyedHistoryAccepted.Should().BeFalse();
    }

    [Fact]
    public async Task The_record_of_a_loss_does_not_repeat_an_unsigned_anchors_claim_as_fact()
    {
        // The entries-gone state is refused BEFORE the anchor's signature is examined, and that
        // ordering is deliberate: checking the signature first would route an operator whose
        // entries are gone and whose anchor is also unsigned to `ashlar verify`, which refuses the
        // same state — a refusal naming a fix that cannot run. So the anchor's sequence and hash
        // reach the permanent record unattested, and the record has to say so rather than assert
        // someone else's number as the length of a history nobody signed for.
        var led = Open();
        await Append(led, "s1", 0);
        var node = JsonNode.Parse(await File.ReadAllTextAsync(AnchorFile))!;
        node["Seq"] = 99999;
        await File.WriteAllTextAsync(AnchorFile, node.ToJsonString());
        Directory.Delete(LedgerDir, recursive: true);

        var result = await led.ReanchorAsync(_signer, Now.AddMinutes(5));

        var detail = result.Head!.Courses.Single(c => c.Name == "ledger-anchor").Detail;
        detail.Should().Contain("claimed to pin entry 99999")
            .And.Contain("signature did NOT verify",
                "an unsigned anchor's length is what was found on disk, not something that was attested");
        detail.Should().Contain("the whole history had been deleted");
    }

    [Fact]
    public async Task A_record_of_a_loss_never_prints_a_dangling_empty_hash()
    {
        // An anchor with no head hash is only reachable by hand-editing, but the entry that
        // records the loss is permanent and signed — "hash )" with nothing in it would be there
        // for good.
        var led = Open();
        await Append(led, "s1", 0);
        var node = JsonNode.Parse(await File.ReadAllTextAsync(AnchorFile))!;
        node["Hash"] = "";
        await File.WriteAllTextAsync(AnchorFile, node.ToJsonString());
        Directory.Delete(LedgerDir, recursive: true);

        var result = await led.ReanchorAsync(_signer, Now.AddMinutes(5));

        result.Head!.Courses.Single(c => c.Name == "ledger-anchor").Detail
            .Should().Contain("hash none recorded").And.NotContain("(hash )");
    }

    // ---- The crash window itself -------------------------------------------------------------

    [Fact]
    public async Task An_append_interrupted_after_the_entry_landed_reads_clean()
    {
        // The anchor names BOTH positions an interrupted append can leave the chain in, in one
        // signed document, before either file moves. So the interrupted state is a state the
        // anchor described — not a length dispute to refuse.
        var led = Open();
        await Append(led, "s1", 0);
        var entry2 = await Append(led, "s2", 1);
        await File.WriteAllTextAsync(AnchorFile, InFlightAnchorJson(pinnedSeq: 1, pending: entry2));

        var result = await led.VerifyChainAsync();

        result.Count.Should().Be(2);
        result.Head!.Seq.Should().Be(2);
    }

    [Fact]
    public async Task An_append_interrupted_before_the_entry_landed_reads_clean()
    {
        var led = Open();
        var entry1 = await Append(led, "s1", 0);
        var entry2 = await Append(led, "s2", 1);
        File.Delete(EntryFile(2));
        await File.WriteAllTextAsync(AnchorFile, InFlightAnchorJson(pinnedSeq: 1, pending: entry2));

        var result = await led.VerifyChainAsync();

        result.Count.Should().Be(1);
        result.Head!.Seq.Should().Be(entry1.Seq);
    }

    [Fact]
    public async Task A_completed_append_leaves_no_in_flight_declaration_behind()
    {
        var led = Open();
        await Append(led, "s1", 0);
        await Append(led, "s2", 1);

        var anchor = JsonSerializer.Deserialize<LedgerHeadAnchor>(
            await File.ReadAllBytesAsync(AnchorFile),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        anchor.Seq.Should().Be(2);
        anchor.PendingSeq.Should().BeNull("phase two clears the declaration, so the window is only open mid-append");
        anchor.PendingHash.Should().BeNull();
    }

    [Fact]
    public async Task A_stale_anchor_with_no_in_flight_declaration_is_still_refused()
    {
        // This is what keeps the crash tolerance from becoming a blanket "head minus one is fine",
        // which anyone holding an old copy of an anchor could exploit to shed the newest entry.
        var led = Open();
        await Append(led, "s1", 0);
        var stale = await File.ReadAllTextAsync(AnchorFile);
        await Append(led, "s2", 1);
        await File.WriteAllTextAsync(AnchorFile, stale);

        var act = async () => await led.VerifyChainAsync();

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .And.Message.Should().Contain("older anchor was replayed");
    }

    /// <summary>
    /// The anchor an append writes between its two file moves: committed position unchanged, plus
    /// a declaration of the entry about to land. Built here with the same key the ledger uses, so
    /// it differs from a real one in nothing.
    /// </summary>
    private string InFlightAnchorJson(int pinnedSeq, LedgerEntry pending)
    {
        var unsigned = new LedgerHeadAnchor
        {
            Seq = pinnedSeq,
            Hash = HashOfEntry(pinnedSeq),
            At = Now,
            PendingSeq = pending.Seq,
            PendingHash = CanonicalHash(pending),
        };
        var signed = unsigned with
        {
            Sig = _signer.Sign(CanonicalJson.Bytes(unsigned)),
            Signer = _signer.PublicKeyBase64,
        };
        return JsonSerializer.Serialize(signed);
    }

    private string HashOfEntry(int seq)
    {
        var entry = JsonSerializer.Deserialize<LedgerEntry>(
            File.ReadAllBytes(EntryFile(seq)),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        return CanonicalHash(entry);
    }

    private static string CanonicalHash(LedgerEntry entry) =>
        Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(CanonicalJson.Bytes(entry))).ToLowerInvariant();
}
