using System.Text.Json.Nodes;
using FluentAssertions;
using Ashlar.Manifest.Ledger;
using Ashlar.Manifest.Signing;
using Xunit;

namespace Ashlar.Tests.Kernel.Ledger;

/// <summary>
/// Tail truncation: the one shape a hash chain cannot see on its own.
///
/// <para>Altering, inserting or reordering an entry breaks a link, so the chain catches it. Delete
/// the NEWEST entries and there is no link left to break — the survivors chain and verify
/// perfectly, and verify used to report "chain intact" and happily re-certify on top of the
/// shortened history. Detection needs something outside the chain that pins its length, so these
/// facts exercise the signed head anchor: it must catch the cut, catch its own removal, and catch
/// a replay of an older pin.</para>
/// </summary>
public sealed class InstanceLedgerTruncationTests : IDisposable
{
    private readonly string _root;
    private readonly SigningIdentity _signer;

    public InstanceLedgerTruncationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ledger-trunc-" + Guid.NewGuid().ToString("N"));
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
    private string StateRoot => Path.Combine(_root, ".ashlar");
    private InstanceLedger Open() => new(StateRoot);
    private string LedgerDir => Path.Combine(StateRoot, "ledger");
    private string EntryFile(int seq) => Path.Combine(LedgerDir, seq.ToString("D6") + ".json");
    private string AnchorFile => Path.Combine(StateRoot, "ledger.head.json");

    private static IReadOnlyList<LedgerCourse> Courses() =>
        [new LedgerCourse { Name = "contract", Passed = true, Detail = "both documents load" }];

    private Task<LedgerEntry> Append(InstanceLedger led, string subject, int atMinutes = 0) =>
        led.AppendVerificationAsync(_signer, subject, verified: true, Courses(), Now.AddMinutes(atMinutes));

    [Fact]
    public async Task Deleting_the_newest_entries_is_detected()
    {
        var led = Open();
        await Append(led, "s1", 0);
        await Append(led, "s2", 1);
        await Append(led, "s3", 2);

        // The exact attack: drop the tail. Entries 1 and 2 are untouched, correctly signed, and
        // chain to each other — everything a chain can check still passes.
        File.Delete(EntryFile(3));

        var act = async () => await led.VerifyChainAsync();
        (await act.Should().ThrowAsync<InvalidOperationException>())
            .And.Message.Should().Contain("deleted from the end");
    }

    [Fact]
    public async Task Truncation_also_stops_the_ledger_being_re_certified_on_top()
    {
        // Reporting the truncation is only half of it. If append still extends the shortened
        // chain, the attacker gets a fresh, valid-looking head over the history they removed and
        // the anchor catches up with the lie.
        var led = Open();
        await Append(led, "s1", 0);
        await Append(led, "s2", 1);
        File.Delete(EntryFile(2));

        var act = async () => await Append(led, "s3", 2);
        (await act.Should().ThrowAsync<InvalidOperationException>())
            .And.Message.Should().Contain("deleted from the end");
    }

    [Fact]
    public async Task Removing_the_anchor_is_itself_refused()
    {
        // Otherwise truncation is a two-step: cut the tail, then delete the thing that noticed.
        var led = Open();
        await Append(led, "s1", 0);
        await Append(led, "s2", 1);
        File.Delete(EntryFile(2));
        File.Delete(AnchorFile);

        var act = async () => await led.VerifyChainAsync();
        (await act.Should().ThrowAsync<InvalidOperationException>())
            .And.Message.Should().Contain("no head anchor");
    }

    [Fact]
    public async Task Deleting_the_whole_history_is_refused_not_read_as_never_certified()
    {
        var led = Open();
        await Append(led, "s1", 0);
        Directory.Delete(LedgerDir, recursive: true);

        var act = async () => await led.VerifyChainAsync();
        (await act.Should().ThrowAsync<InvalidOperationException>())
            .And.Message.Should().Contain("no entries at all");
    }

    [Fact]
    public async Task Replaying_an_older_anchor_over_the_current_one_is_refused()
    {
        // The mirror image of truncation: keep the entries, roll the pin backwards. Left
        // unchecked it is how an attacker would prepare the ground for deleting the tail later.
        var led = Open();
        await Append(led, "s1", 0);
        var stale = await File.ReadAllTextAsync(AnchorFile);
        await Append(led, "s2", 1);
        await File.WriteAllTextAsync(AnchorFile, stale);

        var act = async () => await led.VerifyChainAsync();
        (await act.Should().ThrowAsync<InvalidOperationException>())
            .And.Message.Should().Contain("older anchor was replayed");
    }

    [Fact]
    public async Task An_edited_anchor_is_refused()
    {
        // A pin anyone can rewrite is not a pin: an attacker would simply edit Seq down to match
        // the chain they left behind.
        var led = Open();
        await Append(led, "s1", 0);
        await Append(led, "s2", 1);
        File.Delete(EntryFile(2));

        var node = JsonNode.Parse(await File.ReadAllTextAsync(AnchorFile))!;
        node["Seq"] = 1;
        await File.WriteAllTextAsync(AnchorFile, node.ToJsonString());

        var act = async () => await led.VerifyChainAsync();
        (await act.Should().ThrowAsync<InvalidOperationException>())
            .And.Message.Should().Contain("signature that does not verify");
    }

    [Fact]
    public async Task An_anchor_signed_by_a_stranger_does_not_vouch_for_the_chain()
    {
        // Forging a matching anchor must require the key that signed the head entry, or the pin
        // costs an attacker nothing.
        var led = Open();
        await Append(led, "s1", 0);
        await Append(led, "s2", 1);
        File.Delete(EntryFile(2));

        var attacker = OperatorKey.Generate(Path.Combine(_root, "attacker-keys"));
        var forged = new LedgerHeadAnchor { Seq = 1, Hash = HeadHashOfEntryOne(), At = Now };
        var signedForgery = forged with
        {
            Sig = attacker.Sign(CanonicalJson.Bytes(forged)),
            Signer = attacker.PublicKeyBase64,
        };
        await File.WriteAllTextAsync(
            AnchorFile, System.Text.Json.JsonSerializer.Serialize(signedForgery));

        var act = async () => await led.VerifyChainAsync();
        (await act.Should().ThrowAsync<InvalidOperationException>())
            .And.Message.Should().Contain("signed by a different key");
    }

    [Fact]
    public async Task An_intact_chain_still_verifies_and_the_anchor_tracks_the_head()
    {
        // The pin must not become a second thing that has to be repaired by hand: ordinary
        // appends keep it current, and a clean history reads clean.
        var led = Open();
        await Append(led, "s1", 0);
        await Append(led, "s2", 1);
        await Append(led, "s3", 2);

        var result = await led.VerifyChainAsync();
        result.Count.Should().Be(3);
        result.Head!.Seq.Should().Be(3);
        led.HasHeadAnchor.Should().BeTrue();

        // And a reader holding no key validates the whole thing, anchor included.
        var reader = new InstanceLedger(StateRoot);
        (await reader.VerifyChainAsync()).Count.Should().Be(3);
    }

    [Fact]
    public async Task An_absent_ledger_has_no_anchor_and_is_still_valid()
    {
        // Absence is not corruption: a project that was never certified must not be told its
        // history was destroyed.
        var result = await Open().VerifyChainAsync();
        result.Count.Should().Be(0);
        result.Head.Should().BeNull();
        Open().HasHeadAnchor.Should().BeFalse();
    }

    private string HeadHashOfEntryOne()
    {
        // Recompute the head hash the way the ledger does, so the forgery differs from a valid
        // anchor in exactly one respect: who signed it.
        var entry = System.Text.Json.JsonSerializer.Deserialize<LedgerEntry>(
            File.ReadAllBytes(EntryFile(1)),
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        return Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(CanonicalJson.Bytes(entry))).ToLowerInvariant();
    }
}
