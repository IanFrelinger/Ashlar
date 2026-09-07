using System.Text.Json.Nodes;
using FluentAssertions;
using Ashlar.Manifest.Ledger;
using Ashlar.Manifest.Signing;
using Xunit;

namespace Ashlar.Tests.Kernel.Ledger;

/// <summary>
/// The instance ledger: a signed, hash-chained, append-only history. These tests pin the two
/// promise that makes it worth having — every entry is signed and chained, so tampering with the
/// past is detected and refused loudly (fail-closed) — by proving each detectable shape throws
/// while absence does not. Tail truncation is the shape the chain alone cannot see; it is pinned
/// separately in InstanceLedgerTruncationTests, against the signed head anchor.
/// </summary>
public sealed class InstanceLedgerTests : IDisposable
{
    private readonly string _root;
    private readonly SigningIdentity _signer;

    public InstanceLedgerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ledger-" + Guid.NewGuid().ToString("N"));
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

    private static readonly DateTimeOffset Now = new(2026, 8, 24, 3, 0, 0, TimeSpan.Zero);
    private InstanceLedger Open() => new(Path.Combine(_root, ".ashlar"));
    private string LedgerDir => Path.Combine(_root, ".ashlar", "ledger");
    private string EntryFile(int seq) => Path.Combine(LedgerDir, seq.ToString("D6") + ".json");

    private static IReadOnlyList<LedgerCourse> Courses(bool pass = true) =>
        [new LedgerCourse { Name = "contract", Passed = pass, Detail = "both documents load" }];

    private Task<LedgerEntry> Append(InstanceLedger led, string subject, int atMinutes = 0) =>
        led.AppendVerificationAsync(_signer, subject, verified: true, Courses(), Now.AddMinutes(atMinutes));

    // ─────────────────────────── absence and shape ───────────────────────────

    [Fact]
    public async Task An_absent_ledger_is_valid_not_corrupt()
    {
        var result = await Open().VerifyChainAsync();
        result.Count.Should().Be(0);
        result.Head.Should().BeNull("absence is not corruption");
    }

    [Fact]
    public async Task The_genesis_entry_has_seq_one_no_prev_and_a_signature()
    {
        var entry = await Append(Open(), "subject-a");

        entry.Seq.Should().Be(1);
        entry.Prev.Should().BeNull("genesis chains to nothing");
        entry.Sig.Should().NotBeNullOrEmpty();
        entry.Signer.Should().Be(_signer.PublicKeyBase64);
    }

    [Fact]
    public async Task Entries_are_contiguous_and_each_chains_to_its_predecessor()
    {
        var led = Open();
        var e1 = await Append(led, "s1", 0);
        var e2 = await Append(led, "s2", 1);
        var e3 = await Append(led, "s3", 2);

        new[] { e1.Seq, e2.Seq, e3.Seq }.Should().Equal(1, 2, 3);
        e1.Prev.Should().BeNull();
        e2.Prev.Should().NotBeNullOrEmpty();
        e3.Prev.Should().NotBe(e2.Prev, "each link is the hash of a different predecessor");

        var result = await led.VerifyChainAsync();
        result.Count.Should().Be(3);
        result.Head!.Seq.Should().Be(3);
    }

    [Fact]
    public void Subject_is_deterministic_and_boundary_safe()
    {
        InstanceLedger.Subject("a", "b").Should().Be(InstanceLedger.Subject("a", "b"));
        // Hashing each document independently then combining the fixed-length digests means
        // "a|b" and "ab|" cannot collide by shifting the split point — with no assumption about
        // what bytes the documents may contain.
        InstanceLedger.Subject("a", "b").Should().NotBe(InstanceLedger.Subject("ab", ""));
    }

    [Fact]
    public async Task A_foreign_file_in_the_ledger_directory_is_refused()
    {
        var led = Open();
        await Append(led, "s1", 0);

        // A stray, non-sequence .json file has no legitimate place in the ledger — reading it into
        // the chain at some guessed position would be exactly the kind of silent acceptance the
        // store exists to prevent. It fails closed.
        await File.WriteAllTextAsync(Path.Combine(LedgerDir, "notes.json"), "{}");

        var act = async () => await led.VerifyChainAsync();
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*unexpected file*");
    }

    // ─────────────────────────── fail-closed on tampering ───────────────────────────

    [Fact]
    public async Task A_tampered_entry_body_breaks_the_chain_and_is_refused()
    {
        var led = Open();
        await Append(led, "s1", 0);
        await Append(led, "s2", 1);

        // Rewrite the first entry's subject on disk. Its signature no longer covers it, AND its
        // hash changes so the second entry no longer chains — either alone is fatal.
        var file = EntryFile(1);
        var node = JsonNode.Parse(await File.ReadAllTextAsync(file))!;
        node["Subject"] = "tampered";
        await File.WriteAllTextAsync(file, node.ToJsonString());

        var act = async () => await led.VerifyChainAsync();
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Corrupt ledger*");
    }

    [Fact]
    public async Task A_tampered_signature_is_refused()
    {
        var led = Open();
        await Append(led, "s1", 0);

        var file = EntryFile(1);
        var node = JsonNode.Parse(await File.ReadAllTextAsync(file))!;
        var sig = node["Sig"]!.GetValue<string>();
        node["Sig"] = (sig[0] == 'A' ? 'B' : 'A') + sig[1..];
        await File.WriteAllTextAsync(file, node.ToJsonString());

        var act = async () => await led.VerifyChainAsync();
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*signature that does not verify*");
    }

    [Fact]
    public async Task A_gap_in_the_sequence_is_refused()
    {
        var led = Open();
        await Append(led, "s1", 0);
        await Append(led, "s2", 1);
        await Append(led, "s3", 2);

        // Remove the middle entry: the survivors are individually well-signed, but 1,3 is a gap.
        File.Delete(EntryFile(2));

        var act = async () => await led.VerifyChainAsync();
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Corrupt ledger*");
    }

    [Fact]
    public async Task An_inserted_or_reordered_entry_is_refused()
    {
        var led = Open();
        var e1 = await Append(led, "s1", 0);
        await Append(led, "s2", 1);

        // Overwrite entry 2 with a copy of entry 1's file contents (a replay/reorder). Entry 2's
        // slot now holds an entry claiming seq 1, so the sequence check (position 2 wants seq 2)
        // fires — a well-signed entry in the wrong place is still corruption.
        await File.WriteAllTextAsync(EntryFile(2), await File.ReadAllTextAsync(EntryFile(1)));

        var act = async () => await led.VerifyChainAsync();
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Corrupt ledger*");
    }

    [Fact]
    public async Task Appending_onto_a_corrupt_chain_is_refused()
    {
        var led = Open();
        await Append(led, "s1", 0);

        // Corrupt the head, then try to extend. The append must verify what is there first, so a
        // fresh valid-looking entry can never be used to bury a broken chain.
        var file = EntryFile(1);
        var node = JsonNode.Parse(await File.ReadAllTextAsync(file))!;
        node["Verified"] = false;   // a signed field; the signature no longer covers it
        await File.WriteAllTextAsync(file, node.ToJsonString());

        var act = async () => await Append(led, "s2", 1);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Corrupt ledger*");
    }

    [Fact]
    public async Task A_clean_chain_verifies_for_a_reader_that_holds_no_key()
    {
        var led = Open();
        await Append(led, "s1", 0);
        await Append(led, "s2", 1);

        // Verification is intrinsic (each entry carries its own public key), so a fresh process
        // with no operator key still validates the whole history.
        var reader = new InstanceLedger(Path.Combine(_root, ".ashlar"));
        var result = await reader.VerifyChainAsync();
        result.Count.Should().Be(2);
        result.Head!.Signer.Should().Be(_signer.PublicKeyBase64);
    }
}
