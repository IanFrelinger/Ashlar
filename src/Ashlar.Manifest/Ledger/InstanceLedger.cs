using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ashlar.Manifest.Signing;

namespace Ashlar.Manifest.Ledger;

/// <summary>A course result as snapshotted into a ledger entry — a frozen copy, so the record
/// of what was certified does not move when the verifier's wording later changes.</summary>
public sealed record LedgerCourse
{
    /// <summary>Course name, e.g. <c>contract</c>.</summary>
    public required string Name { get; init; }

    /// <summary>Whether the course passed.</summary>
    public required bool Passed { get; init; }

    /// <summary>What was checked or exactly what failed.</summary>
    public required string Detail { get; init; }
}

/// <summary>
/// One entry in a project's instance ledger: a signed, chained statement that a specific pair
/// of documents verified at a moment in time.
/// </summary>
public sealed record LedgerEntry
{
    /// <summary>1-based position in the chain; contiguous, no gaps.</summary>
    public required int Seq { get; init; }

    /// <summary>When the entry was written (UTC).</summary>
    public required DateTimeOffset At { get; init; }

    /// <summary>What the entry records. v1 writes only <c>verification</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>SHA-256 (hex) of the exact documents certified — the entry attests THESE bytes,
    /// so a later document edit is visible as a subject that no ledger entry covers.</summary>
    public required string Subject { get; init; }

    /// <summary>Whether the verification passed.</summary>
    public required bool Verified { get; init; }

    /// <summary>The courses as they stood, snapshotted.</summary>
    public required IReadOnlyList<LedgerCourse> Courses { get; init; }

    /// <summary>SHA-256 (hex) of the previous entry's canonical bytes — the chain link. Null for
    /// the genesis entry (<see cref="Seq"/> 1). Altering any past entry changes its hash and
    /// breaks the next entry's link, which is what makes the history tamper-evident.</summary>
    public string? Prev { get; init; }

    /// <summary>Base64 Ed25519 signature over the canonical entry with the two signature fields
    /// null. A ledger entry is always signed — an unsigned ledger is a contradiction.</summary>
    public string? Sig { get; init; }

    /// <summary>Base64 raw public key of the signer.</summary>
    public string? Signer { get; init; }
}

/// <summary>The result of verifying a whole chain: how many entries, and the head, or a throw.</summary>
public sealed record LedgerVerification
{
    /// <summary>Number of entries in the chain (0 when there is no ledger yet).</summary>
    public required int Count { get; init; }

    /// <summary>The most recent entry, or null when the ledger is empty.</summary>
    public LedgerEntry? Head { get; init; }

    /// <summary>
    /// True only when this result came from ACCEPTING A DESTROYED HISTORY: every entry was gone
    /// from under a live anchor, and <see cref="InstanceLedger.ReanchorAsync"/> started the history
    /// again with the loss recorded as its first signed entry.
    /// </summary>
    /// <remarks>
    /// It exists because <see cref="Count"/> means something different in that one case, and the
    /// difference is the kind a caller gets wrong silently. Everywhere else <see cref="Count"/> is
    /// how many entries SURVIVED. Here nothing survived: the count is 1, and that 1 is the marker
    /// the re-anchor just wrote. A caller that renders "N surviving entries" without consulting
    /// this tells an operator whose whole history was deleted that one entry of it came back —
    /// which is the reassurance this entire mechanism exists to refuse to give. The flag is on the
    /// result rather than left to the caller to infer, because inferring it means sniffing the
    /// refusal text, and a rewording would silently turn the message back into a lie.
    /// </remarks>
    public bool DestroyedHistoryAccepted { get; init; }
}

/// <summary>
/// The instance ledger: an append-only, hash-chained, signed history of a project's
/// verifications, under <c>{stateRoot}/ledger/</c> — one JSON file per entry, named by its
/// zero-padded sequence.
///
/// <para>INTEGRITY, stated honestly. Every entry is signed (SPEC-006 keys) and carries the hash
/// of its predecessor, so MODIFYING a past entry, INSERTING one, or REORDERING the chain is
/// detectable and refused loudly — the same fail-closed stance as the gate store, because a
/// forged history is worse than a missing one. TRUNCATION of the tail is detected too, but by a
/// different mechanism, because a chain cannot see its own end being cut: a signed
/// <see cref="LedgerHeadAnchor"/> beside the directory pins how long the history is, and every
/// read checks it (see <c>InstanceLedgerAnchor.cs</c>). The policy's <c>truncate_ledger</c>
/// never-entry remains the runtime-side guard.</para>
///
/// <para>What it still does NOT defend against is an attacker holding the operator's signing key,
/// who can rewrite history and re-anchor it. That needs a trust root outside the machine, and
/// this class does not pretend to be one.</para>
/// </summary>
public sealed partial class InstanceLedger
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _dir;

    /// <summary>Creates a ledger rooted at <paramref name="stateRoot"/>/ledger.</summary>
    public InstanceLedger(string stateRoot)
    {
        if (string.IsNullOrWhiteSpace(stateRoot))
        {
            throw new ArgumentException("A state root is required.", nameof(stateRoot));
        }
        _dir = Path.Combine(stateRoot, "ledger");
    }

    /// <summary>SHA-256 (hex) identifying the exact certified documents. Each document is hashed
    /// independently and the two fixed-length digests are hashed together, so no pair of distinct
    /// documents can collide by shifting where one ends and the next begins — and it relies on no
    /// "the text cannot contain byte X" assumption about the documents themselves.</summary>
    public static string Subject(string? manifestYaml, string? policyYaml)
    {
        var mh = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(manifestYaml ?? string.Empty));
        var ph = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(policyYaml ?? string.Empty));
        var combined = new byte[mh.Length + ph.Length];
        Buffer.BlockCopy(mh, 0, combined, 0, mh.Length);
        Buffer.BlockCopy(ph, 0, combined, mh.Length, ph.Length);
        return Convert.ToHexString(SHA256.HashData(combined)).ToLowerInvariant();
    }

    /// <summary>Canonical hash of a complete (signed) entry — the value the NEXT entry's
    /// <see cref="LedgerEntry.Prev"/> must equal.</summary>
    private static string HashOf(LedgerEntry entry) =>
        Convert.ToHexString(SHA256.HashData(CanonicalJson.Bytes(entry))).ToLowerInvariant();

    /// <summary>
    /// Appends a signed verification entry, chained to the current head, as ONE atomic step under
    /// a cross-process lock so two racing verifies cannot claim the same sequence number. The
    /// existing chain is verified BEFORE it is extended: a corrupt ledger is never silently grown.
    /// </summary>
    /// <remarks>
    /// <para>Append used to call the same anchor check the READ path uses, before writing
    /// anything. Every state that made a read refuse therefore made append refuse too — including
    /// the state an ordinary crash produces — so the fix all five refusals named ("re-certify with
    /// a signed <c>ashlar verify</c>") returned the byte-identical refusal and exit 65, and the
    /// only thing that actually worked was deleting the ledger by hand: the exact destructive act
    /// the anchor exists to detect. A project could be bricked for good by a power cut.</para>
    ///
    /// <para>So the two checks are now different checks, on purpose. The chain-level verification
    /// is unchanged and total: a bad signature, a broken link, a gap or a reordering still refuses
    /// here, so a re-pin never vouches for text that did not verify. The ANCHOR check
    /// (<see cref="InspectAnchorForAppendLocked"/>) repairs forward only — it refuses exactly the
    /// states truncation produces (chain shorter than the anchor, or entries gone entirely) and
    /// re-pins the ones truncation cannot produce. What it repairs, it records: a failed
    /// <c>ledger-anchor</c> course inside the entry it writes, so the repair is in the signed
    /// history rather than in place of it.</para>
    /// </remarks>
    public async Task<LedgerEntry> AppendVerificationAsync(
        SigningIdentity signer, string subject, bool verified, IReadOnlyList<LedgerCourse> courses,
        DateTimeOffset now, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(signer);
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(courses);

        Directory.CreateDirectory(_dir);
        using var _ = await AcquireLockAsync(ct).ConfigureAwait(false);
        SweepStrayTmp();

        // Verify what is already there, then extend it. This is the append-time half of
        // fail-closed: you cannot bury a broken chain under a fresh, valid-looking entry.
        var existing = ReadChain();
        VerifyChainLocked(existing);
        var anchorRepair = InspectAnchorForAppendLocked(existing);
        var head = existing.Count > 0 ? existing[^1] : null;

        var recorded = anchorRepair is null
            ? courses
            : [.. courses, new LedgerCourse
                {
                    Name = AnchorRepairCourseName,
                    Passed = false,
                    Detail = anchorRepair,
                }];

        var unsigned = new LedgerEntry
        {
            Seq = (head?.Seq ?? 0) + 1,
            At = now,
            Kind = "verification",
            Subject = subject,
            Verified = verified,
            Courses = recorded,
            Prev = head is null ? null : HashOf(head),
        };
        var signed = unsigned with
        {
            Sig = signer.Sign(CanonicalJson.Bytes(unsigned)),
            Signer = signer.PublicKeyBase64,
        };

        var path = Path.Combine(_dir, PathFor(signed.Seq));
        var tmp = path + ".tmp";
        await using (var stream = File.Create(tmp))
        {
            await JsonSerializer.SerializeAsync(stream, signed, Json, ct).ConfigureAwait(false);
        }

        // Phase one of the anchor move, BEFORE the entry lands: keep the committed position
        // exactly where it is and merely declare the entry about to appear. A crash, kill or
        // ENOSPC anywhere from here to the second write now leaves the chain on one of two
        // positions this one signed document already names, so the reader accepts it instead of
        // refusing a length it has no way to explain. There is no phase one for the genesis entry:
        // with nothing committed there is no position to keep, and an anchor pinning entry 0 would
        // read as "the history was deleted" on a project that was never certified.
        if (head is not null)
        {
            WriteAnchor(signer, head, now, pending: signed);
        }

        try
        {
            // No overwrite: a sequence file that already exists means the chain moved under us,
            // which the lock should have prevented — treat it as corruption rather than clobbering
            // history, and keep it inside the fail-closed contract (InvalidOperationException)
            // rather than leaking a raw IOException at a caller expecting only that shape.
            File.Move(tmp, path, overwrite: false);
        }
        catch (IOException ex)
        {
            TryDelete(tmp);
            throw new InvalidOperationException(
                $"Corrupt ledger: entry {signed.Seq} already exists on disk while holding the append lock. "
                + $"The history moved unexpectedly — refusing to overwrite it. ({ex.Message})");
        }

        // Phase two: the entry is durably on disk, so the anchor commits to it and the in-flight
        // declaration is cleared.
        WriteAnchor(signer, signed, now, pending: null);
        return signed;
    }

    /// <summary>
    /// Re-pins the head anchor over the chain as it stands, after verifying every entry in it.
    /// The explicit, signed re-anchor: it writes NO entry and extends nothing.
    /// </summary>
    /// <remarks>
    /// <para>This is the recovery path for BOTH states <see cref="AppendVerificationAsync"/>
    /// deliberately refuses — a chain SHORTER than its anchor, and an anchor with no entries left
    /// beneath it. It used to be the recovery path for only the first: the second was rejected
    /// here before anything happened, which left the refusal that names this command naming a
    /// command that refuses. The second is handled by
    /// <see cref="AcceptDestroyedHistoryLocked"/>, which writes the loss down rather than pinning
    /// nothing. Those two states are what truncation looks like, and letting an ordinary
    /// <c>ashlar verify</c> clear them would mean a fresh valid-looking head could always be
    /// written over a history someone deleted. Accepting that loss has to be a separate decision,
    /// taken deliberately, by someone holding the operator key — which is exactly what calling
    /// this is.</para>
    ///
    /// <para>It weakens nothing the chain guarantees: <see cref="VerifyChainLocked"/> runs first
    /// and in full, so a tampered, reordered or unsigned entry still refuses. What it asserts is
    /// only the one thing a chain cannot assert about itself — that this is how long the history
    /// is meant to be.</para>
    /// </remarks>
    /// <param name="signer">The operator key. The anchor it writes is pinned to this key, and a
    /// read then requires the chain's head entry to carry the same signer.</param>
    /// <param name="now">Timestamp for the anchor (UTC).</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The chain the anchor was re-pinned over.</returns>
    public async Task<LedgerVerification> ReanchorAsync(
        SigningIdentity signer, DateTimeOffset now, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(signer);

        Directory.CreateDirectory(_dir);
        using var _ = await AcquireLockAsync(ct).ConfigureAwait(false);
        SweepStrayTmp();

        var entries = ReadChain();
        VerifyChainLocked(entries);

        if (entries.Count == 0)
        {
            return await AcceptDestroyedHistoryLocked(signer, now, ct).ConfigureAwait(false);
        }

        WriteAnchor(signer, entries[^1], now, pending: null);
        return new LedgerVerification { Count = entries.Count, Head = entries[^1] };
    }

    /// <summary>
    /// The entries-gone half of a re-anchor: start the history again, with the destruction as its
    /// first signed entry.
    /// </summary>
    /// <remarks>
    /// <para>This used to be a refusal, and the refusal was the bug. Two states carry the "restore
    /// from backup, or accept the loss with <c>ashlar ledger reanchor</c>" recovery sentence: a
    /// chain shorter than its anchor, and entries gone from under a live anchor. For the second the
    /// named command could not run — <see cref="ReanchorAsync"/> rejected an empty ledger before
    /// doing anything — so status refused, the fix it named refused, verify refused, and the only
    /// escape was deleting <c>ledger.head.json</c> by hand: the act that makes the destruction
    /// invisible, which is precisely what the refusal exists to prevent. A refusal whose only
    /// working fix is the thing it warns against is not fail-closed, it is a dead end.</para>
    ///
    /// <para>An anchor genuinely cannot pin nothing, so the honest act is not to write one over an
    /// empty directory — it is to make the loss the first thing in the new history. The genesis
    /// entry records the destroyed anchor's position and hash as a failed
    /// <c>ledger-anchor</c> course, exactly as an ordinary append records the disagreements it
    /// repairs forward, and its <c>Subject</c> is the destroyed head's hash — the one piece of the
    /// old history that survived. So the loss is permanent and PERMANENTLY VISIBLE, signed by
    /// whoever accepted it, rather than erased by a file deletion nothing records.</para>
    ///
    /// <para>What this does not weaken: an ordinary <c>ashlar verify</c> still cannot reach here.
    /// <see cref="AppendVerificationAsync"/> refuses the same state, unchanged, so a fresh
    /// valid-looking head still cannot be written over a deleted history as a side effect of
    /// verifying. Accepting the loss stays a separate act, taken deliberately, by someone holding
    /// the operator key.</para>
    /// </remarks>
    private async Task<LedgerVerification> AcceptDestroyedHistoryLocked(
        SigningIdentity signer, DateTimeOffset now, CancellationToken ct)
    {
        var destroyed = ReadAnchor();
        if (destroyed is null)
        {
            throw new InvalidOperationException(
                "Nothing to re-anchor: there are no ledger entries and no head anchor either. That is not a "
                + "shortened history, it is a project that was never certified — there is no disagreement to "
                + "accept and nothing an anchor could pin. Fix: certify it with `ashlar keys init && ashlar "
                + "verify`. Refusing to write a pin that points at nothing.");
        }

        // Whether the destroyed anchor's own signature verified. This is recorded rather than
        // enforced: an anchor with no entries beneath it is refused for being an anchor with no
        // entries beneath it, BEFORE its signature is looked at, in the read path and in the append
        // path alike — deliberately, because checking the signature first would send an operator
        // whose entries are gone and whose anchor is also unsigned to `ashlar verify`, which
        // refuses that same state. That is the dead end this whole mechanism exists to remove. So
        // the state stays acceptable, and what changes is only the honesty of the record: the
        // sequence and hash below are the anchor's CLAIM about how much history there was, and
        // when nothing signed that claim the entry says so instead of repeating it as fact.
        var attested = destroyed.Sig is not null && destroyed.Signer is not null
            && OperatorKey.Verify(
                destroyed.Signer,
                CanonicalJson.Bytes(destroyed with { Sig = null, Signer = null }),
                destroyed.Sig);

        var found = attested
            ? $"the head anchor pinned entry {destroyed.Seq} (hash {HashOrNone(destroyed.Hash)}), and every entry "
              + "beneath it was gone: the whole history had been deleted."
            : $"the head anchor claimed to pin entry {destroyed.Seq} (hash {HashOrNone(destroyed.Hash)}), and every "
              + "entry beneath it was gone: the whole history had been deleted. That anchor's own signature did NOT "
              + "verify, so the length recorded above is what was found on disk rather than anything the operator "
              + "key ever attested.";

        var detail =
            found
            + " That loss was accepted deliberately, with the operator "
            + "key, by `ashlar ledger reanchor --yes`. Nothing was recovered — this entry is the record that "
            + "there was something to recover, so the deletion stays visible instead of looking like a project "
            + "that had never been certified.";

        var unsigned = new LedgerEntry
        {
            Seq = 1,
            At = now,
            Kind = "verification",
            // The destroyed head's hash: the only fragment of the old history still on disk, and
            // already the SHA-256 hex this field is defined to carry.
            Subject = destroyed.Hash,
            Verified = false,
            Courses = [new LedgerCourse { Name = AnchorRepairCourseName, Passed = false, Detail = detail }],
            Prev = null,
        };
        var signed = unsigned with
        {
            Sig = signer.Sign(CanonicalJson.Bytes(unsigned)),
            Signer = signer.PublicKeyBase64,
        };

        var path = Path.Combine(_dir, PathFor(signed.Seq));
        var tmp = path + ".tmp";
        await using (var stream = File.Create(tmp))
        {
            await JsonSerializer.SerializeAsync(stream, signed, Json, ct).ConfigureAwait(false);
        }

        try
        {
            File.Move(tmp, path, overwrite: false);
        }
        catch (IOException ex)
        {
            TryDelete(tmp);
            throw new InvalidOperationException(
                $"Corrupt ledger: entry {signed.Seq} already exists on disk while holding the append lock. "
                + $"The history moved unexpectedly — refusing to overwrite it. ({ex.Message})");
        }

        // A crash between the two writes leaves one entry under an anchor pinning the old, higher
        // sequence — the ordinary "chain shorter than its anchor" state, which this same command
        // clears on a second run. There is no phase-one pending anchor for a genesis entry: the
        // old anchor names a history none of whose entries exist, so there is no position to keep.
        WriteAnchor(signer, signed, now, pending: null);

        // Count 1, and NOTHING survived to make it. The flag is what stops a caller reporting the
        // marker as a recovered entry; see LedgerVerification.DestroyedHistoryAccepted.
        return new LedgerVerification { Count = 1, Head = signed, DestroyedHistoryAccepted = true };
    }

    /// <summary>The destroyed anchor's head hash, or a phrase saying it carried none — so a record
    /// of a loss never contains a dangling "hash " with nothing after it.</summary>
    private static string HashOrNone(string? hash) =>
        string.IsNullOrEmpty(hash) ? "none recorded" : hash;

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // A temp we cannot delete now is swept on the next append under the lock.
        }
    }

    /// <summary>Deletes stray <c>*.json.tmp</c> left by a crash mid-append. Runs under the lock,
    /// where no writer can be mid-rename, so it can never race a live append.</summary>
    private void SweepStrayTmp()
    {
        if (!Directory.Exists(_dir))
        {
            return;
        }
        foreach (var stray in Directory.EnumerateFiles(_dir, "*.json.tmp"))
        {
            TryDelete(stray);
        }
    }

    /// <summary>
    /// Verifies the entire chain, fail-closed, and returns its shape. A ledger that does not exist
    /// or is empty is VALID (count 0) — absence is not corruption. Any structural break — a bad
    /// signature, a broken hash link, a gap or duplicate in the sequence — throws
    /// <see cref="InvalidOperationException"/>, the same loud refusal a corrupt gate record gets.
    /// The signer is NOT pinned across entries: a key rotation legitimately changes which key
    /// signs later entries, and each entry verifies against its own embedded key. (Distinguishing
    /// the operator's key from an attacker's needs an external trust root — v2.)
    /// </summary>
    public Task<LedgerVerification> VerifyChainAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(VerifyChain());
    }

    /// <summary>The synchronous form of <see cref="VerifyChainAsync"/>. Reading and verifying a
    /// handful of tiny local JSON files needs no async machinery, and the verifier
    /// (<c>ProjectVerifier</c>) runs its courses synchronously — this is the shape it calls.</summary>
    public LedgerVerification VerifyChain()
    {
        var entries = ReadChain();
        VerifyChainLocked(entries);
        VerifyAnchorLocked(entries);
        return new LedgerVerification
        {
            Count = entries.Count,
            Head = entries.Count > 0 ? entries[^1] : null,
        };
    }

    private static void VerifyChainLocked(IReadOnlyList<LedgerEntry> entries)
    {
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var expectedSeq = i + 1;
            if (entry.Seq != expectedSeq)
            {
                throw new InvalidOperationException(
                    $"Corrupt ledger: entry at position {expectedSeq} claims seq {entry.Seq}. "
                    + "The history has a gap, a duplicate, or a reordering — refusing to operate on it.");
            }

            if (entry.Sig is null || entry.Signer is null
                || !OperatorKey.Verify(entry.Signer, CanonicalJson.Bytes(entry with { Sig = null, Signer = null }), entry.Sig))
            {
                throw new InvalidOperationException(
                    $"Corrupt ledger: entry {entry.Seq} carries a signature that does not verify. "
                    + "A forged history is worse than a missing one — refusing to operate.");
            }

            var expectedPrev = i == 0 ? null : HashOf(entries[i - 1]);
            if (!string.Equals(entry.Prev, expectedPrev, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Corrupt ledger: entry {entry.Seq} does not chain to its predecessor "
                    + "(the previous entry was altered, or an entry was inserted or removed). Refusing to operate.");
            }
        }
    }

    /// <summary>Reads every entry in sequence order. A file that exists but cannot be read as an
    /// entry is corruption, never a skipped record.</summary>
    private IReadOnlyList<LedgerEntry> ReadChain()
    {
        if (!Directory.Exists(_dir))
        {
            return [];
        }

        // Order by the NUMERIC sequence in the filename, not by filename string: a string sort
        // would misplace entry 1000000 before 999999 once the digit count grows. Any file here
        // whose stem is not a plain sequence number is foreign to the ledger and fail-closes,
        // rather than being silently read into some position in the chain.
        var ordered = new List<(int Seq, string Path)>();
        foreach (var f in Directory.EnumerateFiles(_dir, "*.json"))
        {
            if (f.EndsWith(".json.tmp", StringComparison.Ordinal))
            {
                continue;
            }
            var stem = Path.GetFileNameWithoutExtension(f);
            if (!int.TryParse(stem, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var seq))
            {
                throw new InvalidOperationException(
                    $"Corrupt ledger: unexpected file '{Path.GetFileName(f)}' in the ledger directory. Refusing to operate.");
            }
            ordered.Add((seq, f));
        }
        ordered.Sort((a, b) => a.Seq.CompareTo(b.Seq));

        var entries = new List<LedgerEntry>(ordered.Count);
        foreach (var (_, file) in ordered)
        {
            LedgerEntry? entry;
            try
            {
                entry = JsonSerializer.Deserialize<LedgerEntry>(File.ReadAllBytes(file), Json);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(
                    $"Corrupt ledger: {Path.GetFileName(file)} is not valid JSON ({ex.Message}). Refusing to operate.");
            }
            if (entry is null)
            {
                throw new InvalidOperationException(
                    $"Corrupt ledger: {Path.GetFileName(file)} contains no entry. Refusing to operate.");
            }
            entries.Add(entry);
        }
        return entries;
    }

    private static string PathFor(int seq) => seq.ToString("D6", System.Globalization.CultureInfo.InvariantCulture) + ".json";

    /// <summary>
    /// Serializes append against a FileShare.None handle the OS releases on process death, so a
    /// crash cannot strand the lock. Mirrors the gate store: an admission ledger and a verdict
    /// store carry the same "two writers must not race a sequence" hazard.
    /// </summary>
    private async Task<FileStream> AcquireLockAsync(CancellationToken ct)
    {
        var lockPath = Path.Combine(_dir, ".lock");
        var deadline = Environment.TickCount64 + 15_000;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (Environment.TickCount64 < deadline)
            {
                await Task.Delay(25, ct).ConfigureAwait(false);
            }
            catch (IOException)
            {
                throw new TimeoutException(
                    "Could not acquire the ledger lock within 15s. Another process is holding it unusually long.");
            }
        }
    }
}
