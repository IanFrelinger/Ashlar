using System.Text.Json;
using Ashlar.Manifest.Signing;

namespace Ashlar.Manifest.Ledger;

/// <summary>
/// The signed head anchor: the ledger's own statement of how long its history is and what its
/// last entry hashes to.
/// </summary>
/// <remarks>
/// A hash chain makes the PAST tamper-evident — altering, inserting or reordering an entry breaks
/// a link. It says nothing about the far end: delete the newest N entries and what remains is a
/// shorter chain that is still perfectly self-consistent, so verify reports "chain intact" and
/// re-certifies over it. Nothing inside a chain can detect that, because the evidence removed is
/// the only evidence it ever existed. Detection needs something OUTSIDE the chain that pins its
/// length, which is what this is.
/// </remarks>
public sealed record LedgerHeadAnchor
{
    /// <summary>Sequence number of the entry this anchor pins as the head.</summary>
    public required int Seq { get; init; }

    /// <summary>Canonical hash of that entry — the same value the next entry's
    /// <see cref="LedgerEntry.Prev"/> would carry.</summary>
    public required string Hash { get; init; }

    /// <summary>When the anchor was written (UTC).</summary>
    public required DateTimeOffset At { get; init; }

    /// <summary>
    /// Sequence number of an entry whose write was IN FLIGHT when this anchor was written, or null
    /// once that append completed. See <see cref="PendingHash"/>.
    /// </summary>
    public int? PendingSeq { get; init; }

    /// <summary>
    /// Canonical hash of the in-flight entry named by <see cref="PendingSeq"/>.
    /// </summary>
    /// <remarks>
    /// <para>An append moves two files: the entry lands, then the anchor follows. A crash, a kill
    /// or an ENOSPC in between used to leave a chain one entry longer than its anchor, which
    /// <see cref="InstanceLedger.VerifyChain"/> refuses as a length dispute — a project bricked by
    /// bad luck rather than by an attacker.</para>
    ///
    /// <para>So the anchor is written TWICE per append. The first write keeps the committed
    /// position (<see cref="Seq"/>/<see cref="Hash"/>) exactly as it was and merely DECLARES the
    /// entry about to land; the second replaces it, pending cleared. A reader therefore accepts
    /// either of the two positions the interrupted append could have left the chain in, and only
    /// those two: both are named, in one signed document, before either file moved.</para>
    ///
    /// <para>This is not the same as tolerating "anchor == head − 1" in general, which would let
    /// anyone holding a stale copy of an old anchor roll the pin back one entry and then delete
    /// that entry. The pending position is pinned by HASH to one specific entry that a real append
    /// was in the middle of writing, and it is cleared by the very next successful append.</para>
    ///
    /// <para>Both fields are optional, and <see cref="CanonicalJson"/> omits nulls, so anchors
    /// written before they existed still verify byte-identically.</para>
    /// </remarks>
    public string? PendingHash { get; init; }

    /// <summary>Base64 Ed25519 signature over the canonical anchor with the two signature fields
    /// null.</summary>
    public string? Sig { get; init; }

    /// <summary>Base64 raw public key of the signer.</summary>
    public string? Signer { get; init; }
}

/// <summary>The head-anchor half of the instance ledger.</summary>
public sealed partial class InstanceLedger
{
    /// <summary>
    /// The anchor lives BESIDE the ledger directory, not inside it: a file inside would be swept
    /// away by the same delete that truncates the chain, and an anchor that dies with its chain
    /// pins nothing. Living outside is also why <c>ReadChain</c>'s refusal of foreign files in the
    /// ledger directory does not fire on it.
    /// </summary>
    private string AnchorPath =>
        Path.Combine(Path.GetDirectoryName(_dir)!, "ledger.head.json");

    /// <summary>The course name under which an append records an anchor it had to re-pin.</summary>
    internal const string AnchorRepairCourseName = "ledger-anchor";

    /// <summary>
    /// The recovery sentence appended to every refusal whose cause a forward re-pin can clear.
    /// </summary>
    /// <remarks>
    /// Every one of these messages used to end with "re-certify with a signed 'ashlar verify',
    /// which rewrites the anchor over the real head". It did not: <c>AppendVerificationAsync</c>
    /// verified the anchor before writing anything, so every state that made a read refuse made
    /// the named fix refuse too, byte-identically. A refusal naming a fix that cannot be executed
    /// is worse than one naming none — it sends the operator to `rm -rf .ashlar/ledger`, the exact
    /// destructive act the anchor exists to detect. Append now re-pins FORWARD (see
    /// <see cref="InspectAnchorForAppendLocked"/>), so this sentence is true where it appears, and
    /// it appears only where it is true.
    /// </remarks>
    private const string ForwardRepairFix =
        "Fix: re-certify with a signed `ashlar verify` (the operator key must be present). Append "
        + "re-verifies every entry in the chain, re-pins the anchor over the real head, and RECORDS "
        + "the disagreement as a failed '" + AnchorRepairCourseName + "' course inside the entry it "
        + "writes — so the repair joins the signed history instead of erasing the evidence.";

    /// <summary>
    /// The recovery sentence for refusals a forward re-pin must NOT clear, because clearing them
    /// is indistinguishable from accepting an attacker's truncation.
    /// </summary>
    /// <remarks>
    /// It names a COMMAND, not only a method. This sentence used to end at
    /// <c>InstanceLedger.ReanchorAsync</c> — the kernel verb — because the CLI had no verb to name.
    /// An operator standing at a terminal cannot run a method, so the fix was still unrunnable for
    /// the person reading it, which is the same defect as naming no fix at all. The CLI half is
    /// <c>ashlar ledger reanchor</c>; the identifier stays beside it for anyone driving the kernel
    /// directly.
    ///
    /// <para>It has to describe what the command does in BOTH states it is attached to, which is
    /// the second way this sentence has been wrong. It said "re-verifies every surviving entry and
    /// then re-pins the anchor over them" — true of a truncated chain, meaningless when the entries
    /// are gone, and for a while the command matched the sentence and simply refused that case. So
    /// a refusal named a fix that could not run, and the only thing that DID clear it was deleting
    /// the anchor: the act this refusal exists to detect. Both halves are named now because both
    /// halves work.</para>
    /// </remarks>
    private const string RestoreOnlyFix =
        "Fix: restore .ashlar/ledger (and ledger.head.json) from backup — the only repair that keeps "
        + "the history. Accepting the loss instead is a deliberate, signed act and NOT a side effect "
        + "of verifying: run `ashlar ledger reanchor --path <project> --yes` (the kernel verb behind "
        + "it is InstanceLedger.ReanchorAsync). Where entries survive it re-verifies them and re-pins "
        + "the anchor over them; where the entries are gone entirely it starts the history again with "
        + "the destruction recorded as its first signed entry, so the loss stays visible instead of "
        + "looking like a project that was never certified. `ashlar ledger status` prints this same "
        + "message first, so you can see what you would be accepting. A signed `ashlar verify` "
        + "will not do it, and that is on purpose — burying a shortened history under a fresh entry "
        + "is exactly what this refusal exists to prevent. Do not delete .ashlar/ledger to make this "
        + "message go away; that is the act being detected.";

    /// <summary>
    /// True when this ledger has a head anchor on disk, whatever the entries look like.
    /// </summary>
    /// <remarks>
    /// A caller deciding whether a project has ever been certified must consult this as well as
    /// the entries. An anchor with no entries is a ledger that was DELETED, not one that never
    /// existed — and telling those apart is the whole point of having an anchor. Checking only for
    /// entries turns "the history was destroyed" into "this project is simply unsigned", which is
    /// the loudest failure quietly becoming the quietest.
    /// </remarks>
    public bool HasHeadAnchor => File.Exists(AnchorPath);

    /// <summary>
    /// Verifies the head anchor against the chain that was just read, fail-closed.
    /// </summary>
    /// <remarks>
    /// <para>Five refusals, one per way the pin can be broken, each naming what it means: the
    /// chain is SHORTER than the anchor (entries deleted from the tail — the case a hash chain
    /// alone cannot see); the chain is LONGER or its head hashes differently (a replayed anchor);
    /// the anchor is MISSING while entries exist (the pin itself was removed, which is what an
    /// attacker who truncates tries next); the entries are GONE while the anchor remains (the
    /// whole history was deleted); or the anchor does not verify.</para>
    ///
    /// <para>The anchor is signed, and pinned to the SIGNER OF THE HEAD ENTRY. That is what makes
    /// it more than a length written on a sticky note: forging an anchor to match a truncated
    /// chain needs the key that signed the entry the truncation left as head. An attacker holding
    /// that key can rewrite the whole history anyway — anti-rollback against a compromised
    /// signing key needs a trust root outside the machine, and this does not pretend to be one.
    /// </para>
    ///
    /// <para>What is NOT a refusal any more: the chain sitting on the in-flight position the
    /// anchor itself declared (<see cref="LedgerHeadAnchor.PendingHash"/>). That state is an
    /// append interrupted between its two writes, and the anchor named both acceptable positions
    /// before either file moved.</para>
    /// </remarks>
    private void VerifyAnchorLocked(IReadOnlyList<LedgerEntry> entries)
    {
        var anchor = ReadAnchor();

        if (anchor is null)
        {
            if (entries.Count == 0)
            {
                return; // No chain and no anchor: a project that was never certified.
            }

            throw new InvalidOperationException(
                $"Corrupt ledger: {entries.Count} signed entr{(entries.Count == 1 ? "y" : "ies")} on disk but no "
                + "head anchor beside them. The anchor is what pins how long this history is supposed to be, so "
                + "without it entries can be deleted from the end and every survivor still verifies. "
                + ForwardRepairFix
                + " Refusing to report an intact chain whose length nothing vouches for.");
        }

        if (entries.Count == 0)
        {
            throw new InvalidOperationException(
                $"Corrupt ledger: the head anchor pins entry {anchor.Seq}, but there are no entries at all. The "
                + "history was deleted and the anchor is what noticed. "
                + RestoreOnlyFix
                + " Refusing to operate as if this project had never been certified.");
        }

        if (anchor.Sig is null || anchor.Signer is null
            || !OperatorKey.Verify(anchor.Signer, CanonicalJson.Bytes(anchor with { Sig = null, Signer = null }), anchor.Sig))
        {
            throw new InvalidOperationException(
                "Corrupt ledger: the head anchor carries a signature that does not verify. An unsigned pin is no "
                + "pin — anyone could rewrite it to match a truncated chain. "
                + ForwardRepairFix
                + " Refusing to operate.");
        }

        var head = entries[^1];
        if (!string.Equals(anchor.Signer, head.Signer, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Corrupt ledger: the head anchor is signed by a different key than entry {head.Seq}, which it "
                + "pins. The anchor and its head entry are written in one signed step, so a mismatch means one "
                + "of them was replaced. "
                + ForwardRepairFix
                + " Refusing to operate.");
        }

        // The committed position, or the in-flight position the same signed anchor declared. Both
        // were named before either file moved, so accepting either is not tolerance — it is
        // reading the document.
        if (Pins(entries, anchor.Seq, anchor.Hash) || Pins(entries, anchor.PendingSeq, anchor.PendingHash))
        {
            return;
        }

        if (entries.Count < anchor.Seq)
        {
            var missing = anchor.Seq - entries.Count;
            throw new InvalidOperationException(
                $"Corrupt ledger: the head anchor pins entry {anchor.Seq}, but the chain ends at entry "
                + $"{entries.Count} — {missing} entr{(missing == 1 ? "y has" : "ies have")} been deleted from the "
                + "end. The survivors chain and verify perfectly, which is exactly why the anchor exists: a hash "
                + "chain cannot see its own tail being cut. "
                + RestoreOnlyFix
                + " Refusing to report an intact chain over a truncated history.");
        }

        throw new InvalidOperationException(
            $"Corrupt ledger: the head anchor pins entry {anchor.Seq}, but the chain's head is entry "
            + $"{head.Seq} with a different hash. An older anchor was replayed over the current one. (A crash "
            + "between writing an entry and updating the anchor does NOT land here: an append declares the entry "
            + "it is about to write in the anchor first, and both positions it could leave behind are accepted.) "
            + ForwardRepairFix
            + " Refusing to operate on a history whose length is in dispute.");
    }

    /// <summary>
    /// True when <paramref name="seq"/>/<paramref name="hash"/> name exactly the chain that is on
    /// disk: the same length, and the same head hash.
    /// </summary>
    private static bool Pins(IReadOnlyList<LedgerEntry> entries, int? seq, string? hash)
    {
        if (seq is not int pinned || hash is null || pinned <= 0 || entries.Count != pinned)
        {
            return false;
        }

        return string.Equals(hash, HashOf(entries[^1]), StringComparison.Ordinal);
    }

    /// <summary>
    /// The append-time view of the anchor: null when it agrees with the chain, otherwise the
    /// sentence describing the disagreement that the append is about to re-pin over — which the
    /// caller writes into the entry as a failed course.
    /// </summary>
    /// <remarks>
    /// <para>Repair is FORWARD ONLY, and the asymmetry is the whole safety argument. Truncation
    /// removes entries, so it always leaves the chain SHORTER than the anchor; every state where
    /// the chain is shorter (or where the entries are gone entirely) is still refused here, which
    /// is what keeps "you cannot bury a truncation under a fresh entry" true. The states this
    /// repairs are the ones truncation cannot produce: a chain LONGER than its anchor (an append
    /// interrupted after the entry landed, or an attacker rolling the pin BACKWARDS — re-pinning
    /// forward defeats that, it does not serve it), an anchor that is missing, unreadable or
    /// unsigned beside a chain that verifies end to end, and an anchor signed by a key other than
    /// the head entry's.</para>
    ///
    /// <para>Every entry in the chain has already been through <c>VerifyChainLocked</c> by the
    /// time this runs, so a re-pin never vouches for text that did not verify. And the repair is
    /// never silent: it is recorded, signed, at the sequence number where it happened.</para>
    /// </remarks>
    private string? InspectAnchorForAppendLocked(IReadOnlyList<LedgerEntry> entries)
    {
        LedgerHeadAnchor? anchor;
        try
        {
            anchor = ReadAnchor();
        }
        catch (InvalidOperationException ex)
        {
            return "the head anchor on disk was unreadable and has been re-pinned over the chain's real head: "
                + ex.Message;
        }

        if (anchor is null)
        {
            return entries.Count == 0
                ? null // Never certified: the genesis append writes the first anchor.
                : $"{entries.Count} signed entr{(entries.Count == 1 ? "y" : "ies")} were on disk with no head "
                  + "anchor beside them; the anchor has been re-pinned over the chain's real head. If entries "
                  + "were also deleted from the end, that loss is now inside this history.";
        }

        if (entries.Count == 0)
        {
            throw new InvalidOperationException(
                $"Corrupt ledger: the head anchor pins entry {anchor.Seq}, but there are no entries at all. "
                + "Refusing to start a fresh history on top of one that was destroyed — that would make the "
                + "destruction invisible. " + RestoreOnlyFix);
        }

        if (anchor.Sig is null || anchor.Signer is null
            || !OperatorKey.Verify(anchor.Signer, CanonicalJson.Bytes(anchor with { Sig = null, Signer = null }), anchor.Sig))
        {
            return "the head anchor carried a signature that did not verify; it has been re-pinned over the "
                + "chain's real head, whose entries all verify.";
        }

        if (Pins(entries, anchor.Seq, anchor.Hash) || Pins(entries, anchor.PendingSeq, anchor.PendingHash))
        {
            return string.Equals(anchor.Signer, entries[^1].Signer, StringComparison.Ordinal)
                ? null
                : $"the head anchor was signed by a different key than entry {entries[^1].Seq}, which it pinned; "
                  + "it has been re-pinned by the key signing this entry.";
        }

        if (entries.Count < anchor.Seq)
        {
            var missing = anchor.Seq - entries.Count;
            throw new InvalidOperationException(
                $"Corrupt ledger: the head anchor pins entry {anchor.Seq}, but the chain ends at entry "
                + $"{entries.Count} — {missing} entr{(missing == 1 ? "y has" : "ies have")} been deleted from the "
                + "end. Refusing to extend a truncated history: a fresh, valid-looking head written over it is "
                + "how the deletion would become permanent and invisible. " + RestoreOnlyFix);
        }

        return $"the head anchor pinned entry {anchor.Seq} while the chain's head was entry {entries[^1].Seq}; "
            + "the anchor has been re-pinned forward over the real head.";
    }

    /// <summary>
    /// Reads the anchor, or null when there is none.
    /// </summary>
    /// <remarks>
    /// A file that exists but cannot be read as an anchor is corruption, never an absent anchor.
    /// Absence is the one state an attacker can arrange with a delete, so it must not also be
    /// reachable by mangling the bytes.
    /// </remarks>
    private LedgerHeadAnchor? ReadAnchor()
    {
        if (!File.Exists(AnchorPath))
        {
            return null;
        }

        LedgerHeadAnchor? anchor;
        try
        {
            anchor = JsonSerializer.Deserialize<LedgerHeadAnchor>(File.ReadAllBytes(AnchorPath), Json);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Corrupt ledger: the head anchor is not valid JSON ({ex.Message}). "
                + ForwardRepairFix
                + " Refusing to operate.");
        }

        return anchor ?? throw new InvalidOperationException(
            "Corrupt ledger: the head anchor file contains no anchor. "
            + ForwardRepairFix
            + " Refusing to operate.");
    }

    /// <summary>
    /// Writes the anchor, atomically, over the given committed head and optional in-flight entry.
    /// Called under the append lock.
    /// </summary>
    /// <remarks>
    /// Overwriting is correct here and only here. Unlike an entry, the anchor is not history — it
    /// is the single current statement of where history ends, and every append moves it. It is
    /// written twice per append: once naming the entry about to land (<paramref name="pending"/>
    /// set, committed position unchanged), once after it lands (<paramref name="pending"/> null).
    /// </remarks>
    private void WriteAnchor(SigningIdentity signer, LedgerEntry? head, DateTimeOffset now, LedgerEntry? pending)
    {
        var unsigned = new LedgerHeadAnchor
        {
            Seq = head?.Seq ?? 0,
            Hash = head is null ? string.Empty : HashOf(head),
            At = now,
            PendingSeq = pending?.Seq,
            PendingHash = pending is null ? null : HashOf(pending),
        };
        var signed = unsigned with
        {
            Sig = signer.Sign(CanonicalJson.Bytes(unsigned)),
            Signer = signer.PublicKeyBase64,
        };

        var tmp = AnchorPath + ".tmp";
        File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(signed, Json));
        File.Move(tmp, AnchorPath, overwrite: true);
    }
}
