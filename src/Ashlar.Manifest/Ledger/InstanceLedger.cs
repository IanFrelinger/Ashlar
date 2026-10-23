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
}

/// <summary>
/// The instance ledger: an append-only, hash-chained, signed history of a project's
/// verifications, under <c>{stateRoot}/ledger/</c> — one JSON file per entry, named by its
/// zero-padded sequence.
///
/// <para>INTEGRITY, stated honestly. Every entry is signed (SPEC-006 keys) and carries the hash
/// of its predecessor, so MODIFYING a past entry, INSERTING one, or REORDERING the chain is
/// detectable and refused loudly — the same fail-closed stance as the gate store, because a
/// forged history is worse than a missing one. What v1 does NOT defend against is TRUNCATION of
/// the tail: without an external anchor, dropping the last N entries leaves a shorter but
/// internally-consistent chain. The policy's <c>truncate_ledger</c> never-entry is the v1
/// guard for that (the runtime may not rewrite the ledger); a persisted, anti-rollback head
/// anchor is v2. This class does not pretend otherwise.</para>
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
        Storage.FileLockingCapability.EnsureSupported();
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
        var head = existing.Count > 0 ? existing[^1] : null;

        var unsigned = new LedgerEntry
        {
            Seq = (head?.Seq ?? 0) + 1,
            At = now,
            Kind = "verification",
            Subject = subject,
            Verified = verified,
            Courses = courses,
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
        return signed;
    }

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
