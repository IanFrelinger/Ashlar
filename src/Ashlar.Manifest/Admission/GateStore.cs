using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ashlar.Manifest.Admission;

/// <summary>A proposal with its current state and decision history, as persisted.</summary>
public sealed record GateRecord
{
    /// <summary>The proposal.</summary>
    public required ExtensionProposal Proposal { get; init; }

    /// <summary>Current state.</summary>
    public required ProposalState State { get; init; }

    /// <summary>Why it is in that state.</summary>
    public required string Reason { get; init; }

    /// <summary>Who put it there: <c>gate</c> for automatic outcomes, otherwise the human's id.</summary>
    public required string Actor { get; init; }

    /// <summary>When the state was last decided (UTC).</summary>
    public required DateTimeOffset DecidedAt { get; init; }

    /// <summary>Base64 Ed25519 signature over the canonical record with the two signature
    /// fields null (SPEC-006 §4). Null when the record was written without keys — and a
    /// renderer MUST NOT print a fingerprint for a null sig (rule S-3).
    ///
    /// <para>A null sig is NOT, by itself, "honestly unsigned". From the record's own bytes a
    /// removed signature and a record that never had one are indistinguishable, so a reader that
    /// has resolved a <see cref="GateSignatureExpectation"/> for the store — from its other
    /// verifying records and the activation marker, never from this record — treats a null sig
    /// on a record decided after signing was activated as corruption (rule S-6). The store
    /// decides; the record cannot testify about itself.</para></summary>
    public string? Sig { get; init; }

    /// <summary>Base64 raw public key of the signer; null when unsigned.</summary>
    public string? Signer { get; init; }
}

/// <summary>
/// Durable, file-backed store for gate records — one JSON file per proposal under
/// <c>{root}/gates/</c>.
///
/// <para>Two rules live here rather than in callers. First, DURABILITY: a held proposal
/// must survive process death, because the reviewer is asleep when the app proposes — this
/// codebase's audit found process-lifetime state at nearly every point a durable record was
/// needed, and this store is the convention that answers it. Second, TRANSITION AUTHORITY
/// (SPEC-004): <see cref="DecideAsync"/> moves a proposal out of Held and nothing else —
/// admitted and rejected records are immutable history, so there is no way to re-decide a
/// refusal or quietly edit an admission, including for the vendor. Third, SIGNATURE
/// EXPECTATION (SPEC-006 S-6): once this store is known to be signed — by its own verifying
/// records, or by the activation marker at <c>{root}/gate-signing.json</c> when the reader's
/// key material vouches for it — a record with NO signature decided after activation is
/// corruption, exactly as a record with a bad one is. A removed signature is the cheapest
/// forgery there is, and it is the one S-1 alone cannot see. See
/// <see cref="GateSignatureExpectation"/> and <see cref="GateSigningActivation"/>.</para>
/// </summary>
public sealed partial class GateStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _stateRoot;
    private readonly string _dir;
    private readonly Signing.SigningIdentity? _signer;
    private readonly IReadOnlyList<string> _trustedSigners;

    /// <summary>
    /// The signature posture the most recent pass through <see cref="ReadStoreAsync"/> — any read,
    /// and the keyless write guard — resolved and judged its records against; null before the
    /// first of those. One posture per store operation, resolved in one place. Renderers use it to say
    /// whether signatures are expected here and to warn when keys are present but the store has
    /// never been signed — without ever printing a fingerprint the store did not verify (S-3).
    /// </summary>
    public GateSignatureExpectation? SignatureTrust { get; private set; }

    /// <summary>Creates a store rooted at <paramref name="stateRoot"/>/gates. When a
    /// <paramref name="signer"/> is supplied, every record written is signed (SPEC-006);
    /// without one, records are written unsigned — presence-activated, never half-on — unless
    /// the store is already known to be signed, in which case an unsigned write is refused
    /// (see <see cref="WriteAsync"/>). Reads pin signers to the operator's key material
    /// (<see cref="Signing.OperatorKey.TrustedPublicKeysBase64"/>) whether or not a signer is
    /// supplied: a read needs public keys, not the private identity.</summary>
    public GateStore(string stateRoot, Signing.SigningIdentity? signer = null)
    {
        if (string.IsNullOrWhiteSpace(stateRoot))
        {
            throw new ArgumentException("A state root is required.", nameof(stateRoot));
        }
        _stateRoot = stateRoot;
        _dir = Path.Combine(stateRoot, "gates");
        _signer = signer;
        _trustedSigners = LoadTrustedSigners(signer);
        Directory.CreateDirectory(_dir);
    }

    /// <summary>
    /// The signer-pinning set: <c>operator.pub</c> and every <c>trusted/*.pub</c> from the key
    /// directory, plus the explicit <paramref name="signer"/> when one was supplied — it is key
    /// material the caller loaded, and a store must be able to read what it signs. NEVER derived
    /// from the records being judged. An unreadable or inaccessible key directory NARROWS the set
    /// rather than throwing from a constructor, so a locked-down home directory cannot break
    /// composition; a key file that is present but corrupt still throws, because that is
    /// corruption, not absence.
    /// </summary>
    private static IReadOnlyList<string> LoadTrustedSigners(Signing.SigningIdentity? signer)
    {
        var keys = new List<string>();
        try
        {
            keys.AddRange(Signing.OperatorKey.TrustedPublicKeysBase64());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            // Narrow, do not throw: with no readable key material this reader verifies intrinsically.
        }
        if (signer is not null && !keys.Contains(signer.PublicKeyBase64, StringComparer.Ordinal))
        {
            keys.Add(signer.PublicKeyBase64);
        }
        return keys;
    }

    /// <summary>
    /// Activates signing for this store at <paramref name="now"/> — the explicit path behind
    /// <c>ashlar keys init</c> and <c>ashlar gates sign-activate</c>. Requires a signer. A no-op
    /// returning the existing marker when one is already on disk, so repeated activation and key
    /// rotation never move the instant; taken under the store lock like every other write.
    /// </summary>
    public async Task<GateSigningActivation> ActivateSigningAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        if (_signer is null)
        {
            throw new InvalidOperationException(
                "Activating gate signing requires an operator key and none is loaded. Run `ashlar keys init` "
                + "(or point ASHLAR_KEY_DIR at the operator's key directory) and try again.");
        }
        using var _ = await AcquireLockAsync(ct).ConfigureAwait(false);
        return GateSigningActivation.Activate(_stateRoot, _signer, now);
    }

    /// <summary>
    /// Serializes every read-check-write against the store, ACROSS PROCESSES. The lock is a
    /// FileShare.None handle on a well-known file: the OS enforces exclusivity and releases
    /// it when the holder dies, so a crash cannot leave a stale lock. Without this, two
    /// humans could decide the same held proposal and both "win" — an admit silently
    /// erasing a refusal — and two racing self-extending proposals could both read a spent
    /// budget of zero and both admit. On an admission boundary those are security bugs.
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
                var handle = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                SweepStrayTmp();
                return handle;
            }
            catch (IOException) when (Environment.TickCount64 < deadline)
            {
                await Task.Delay(25, ct).ConfigureAwait(false);
            }
            catch (IOException)
            {
                throw new TimeoutException(
                    "Could not acquire the gate-store lock within 15s. Another process is holding it unusually long.");
            }
        }
    }

    /// <summary>
    /// A crash between write and move leaves a stray <c>.json.tmp</c>. It can never be
    /// mistaken for a record (listings enumerate <c>*.json</c>, and the extension is four
    /// characters so the Windows legacy three-char pattern quirk does not apply) — but it
    /// would sit there forever. Swept here, under the lock, where no writer can be mid-move.
    /// </summary>
    private void SweepStrayTmp()
    {
        foreach (var stray in Directory.EnumerateFiles(_dir, "*.json.tmp"))
        {
            try
            {
                File.Delete(stray);
            }
            catch (IOException)
            {
                // A stray we cannot delete right now is swept on a later acquisition.
            }
        }
    }

    /// <summary>
    /// Records the gate's automatic outcome for an evaluated proposal. Refuses to overwrite
    /// an existing record — a proposal is recorded once, and the check holds under
    /// concurrency because it runs inside the store lock.
    /// </summary>
    public async Task<GateRecord> RecordAsync(ExtensionProposal proposal, AdmissionOutcome outcome, DateTimeOffset now, CancellationToken ct = default)
    {
        using var _ = await AcquireLockAsync(ct).ConfigureAwait(false);
        return await RecordLockedAsync(proposal, outcome, now, ct).ConfigureAwait(false);
    }

    private async Task<GateRecord> RecordLockedAsync(ExtensionProposal proposal, AdmissionOutcome outcome, DateTimeOffset now, CancellationToken ct)
    {
        var path = PathFor(proposal.Id);
        if (File.Exists(path))
        {
            throw new InvalidOperationException($"Proposal '{proposal.Id}' is already recorded. Records are append-once; propose under a new id.");
        }

        var record = new GateRecord
        {
            Proposal = proposal,
            State = outcome.State,
            Reason = outcome.Reason,
            Actor = "gate",
            DecidedAt = now,
        };
        return await WriteAsync(path, record, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The full propose transaction — count admissions in the window, decide under the
    /// policy, record — as ONE atomic step under the store lock. This lives here rather
    /// than in callers precisely so the budget check and the recording cannot be separated
    /// by another process's admission: budget 1 admits one, under any concurrency.
    /// An unparseable budget window fails closed to Held — never an unlimited allowance.
    /// </summary>
    public async Task<GateRecord> ProposeAsync(AshlarPolicy policy, ExtensionProposal proposal, DateTimeOffset now, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(proposal);

        using var _ = await AcquireLockAsync(ct).ConfigureAwait(false);

        AdmissionOutcome outcome;
        if (policy.SelfExtend.Mode == SelfExtendMode.SelfExtending
            && !AdmissionGate.TryParseWindow(policy.SelfExtend.Budget.Window, out var window))
        {
            outcome = new AdmissionOutcome
            {
                State = ProposalState.Held,
                Reason = $"budget window '{policy.SelfExtend.Budget.Window}' is unparseable — failing closed to a "
                       + "human decision rather than treating it as unlimited.",
            };
        }
        else
        {
            var admittedInWindow = 0;
            if (AdmissionGate.TryParseWindow(policy.SelfExtend.Budget.Window, out var w))
            {
                admittedInWindow = await AdmittedInWindowAsync(w, now, ct).ConfigureAwait(false);
            }
            outcome = AdmissionGate.Decide(policy, proposal, admittedInWindow);
        }

        return await RecordLockedAsync(proposal, outcome, now, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// A human decides a HELD proposal. The only legal transitions are Held → Admitted and
    /// Held → Refused; anything else is refused with the rule spelled out. A refusal
    /// requires a reason — a refusal that does not teach produces the same proposal again.
    /// </summary>
    public async Task<GateRecord> DecideAsync(string proposalId, bool admit, string actor, string reason, DateTimeOffset now, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(actor))
        {
            throw new ArgumentException("A decision needs an actor: the record must say who seated or refused.", nameof(actor));
        }
        if (!admit && string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("A refusal requires a reason — it is recorded and fed back to the proposer.", nameof(reason));
        }

        using var _ = await AcquireLockAsync(ct).ConfigureAwait(false);

        var existing = await GetAsync(proposalId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"No proposal '{proposalId}' in the store.");

        if (existing.State != ProposalState.Held)
        {
            throw new InvalidOperationException(
                $"Proposal '{proposalId}' is {existing.State}, not Held. Only held proposals can be decided: "
                + "admitted and rejected records are immutable history (SPEC-004 — no administrative path).");
        }

        var decided = existing with
        {
            State = admit ? ProposalState.Admitted : ProposalState.Refused,
            Reason = string.IsNullOrWhiteSpace(reason) ? "seated by operator" : reason,
            Actor = actor,
            DecidedAt = now,
        };
        return await WriteAsync(PathFor(proposalId), decided, ct).ConfigureAwait(false);
    }

    /// <summary>Fetches one record, or null when absent. A file that exists but cannot be
    /// read as a record is an error, never a null.
    ///
    /// <para>This resolves the store's posture through <see cref="ReadStoreAsync"/>, exactly as
    /// <see cref="ListAsync"/> does, so <b>a single-record read is no longer a single-file read</b>:
    /// one corrupt, stripped, re-signed, renamed or duplicated record anywhere under
    /// <c>gates/</c> refuses this read too. That is deliberate and it is the contract. The two
    /// funnels cannot agree on weaker terms, and while they disagreed this path resolved from the
    /// marker alone — so with the marker deleted it returned a stripped, state-flipped record that
    /// <see cref="ListAsync"/> beside it refused, and since <see cref="DecideAsync"/> reads through
    /// here, `gates --admit` signed tampered content under the operator's key.</para></summary>
    public async Task<GateRecord?> GetAsync(string proposalId, CancellationToken ct = default)
    {
        var path = PathFor(proposalId);
        if (!File.Exists(path))
        {
            // An unknown id in a healthy store still reads as absent rather than as an error.
            return null;
        }

        var wanted = Path.GetFileName(path);
        var (records, _) = await ReadStoreAsync(ct).ConfigureAwait(false);
        foreach (var entry in records)
        {
            if (string.Equals(Path.GetFileName(entry.Path), wanted, StringComparison.Ordinal))
            {
                return entry.Record;
            }
        }
        // The file was there a moment ago and is not now: another process removed it under us.
        return null;
    }

    /// <summary>
    /// Lists records, newest first, optionally filtered by state. A filter and an ordering over
    /// <see cref="ReadStoreAsync"/> — a store this class cannot fully read is a store it refuses
    /// to summarize, including for the budget count.
    /// </summary>
    public async Task<IReadOnlyList<GateRecord>> ListAsync(ProposalState? state = null, CancellationToken ct = default)
    {
        var (records, _) = await ReadStoreAsync(ct).ConfigureAwait(false);
        return records
            .Where(e => state is null || e.Record.State == state)
            .Select(e => e.Record)
            .OrderByDescending(r => r.Proposal.ProposedAt)
            .ToList();
    }

    /// <summary>
    /// THE resolution point, and the only way a record leaves this store. Every read — and the
    /// keyless write guard — comes through here, and no production code outside this file
    /// deserializes a <see cref="GateRecord"/> (pinned by the read-funnel convention test).
    ///
    /// <para>Three steps over ONE parsed set, which is why this cannot be split: parse and hash;
    /// then judge only the signatures that are PRESENT (verify, then pin to key material) and
    /// collect the survivors as anchors — no anchor is needed to judge a present signature, and a
    /// record that fails here is refused before it can anchor anything; then resolve the posture
    /// from those anchors and the marker, and apply it to EVERY record. Resolving twice is how the
    /// funnels came to disagree, so the posture is set here and nowhere else.</para>
    ///
    /// <para>The hash is the canonical sha256 of the record, computed where the bytes are already
    /// in hand: it is what a grandfather inventory pins an unsigned record by.</para>
    /// </summary>
    private async Task<(IReadOnlyList<(string Path, GateRecord Record, string Sha256)> Records, GateSignatureExpectation Expectation)>
        ReadStoreAsync(CancellationToken ct)
    {
        var parsed = await ParseAllAsync(ct).ConfigureAwait(false);
        var entries = new List<(string Path, GateRecord Record, string Sha256)>(parsed.Count);
        foreach (var (path, record) in parsed)
        {
            entries.Add((path, record, CanonicalSha256(record)));
        }

        var unanchored = GateSignatureExpectation.None(_trustedSigners, "judging present signatures only");
        var anchors = new List<GateRecord>();
        foreach (var entry in entries)
        {
            if (entry.Record.Sig is null)
            {
                continue;
            }
            if (unanchored.Refuse(entry.Record, Path.GetFileName(entry.Path)) is { } refusal)
            {
                throw new InvalidOperationException(refusal);
            }
            anchors.Add(entry.Record);
        }

        var expectation = ResolveExpectation(anchors);
        SignatureTrust = expectation;

        foreach (var entry in entries)
        {
            if (expectation.Refuse(entry.Record, Path.GetFileName(entry.Path)) is { } refusal)
            {
                throw new InvalidOperationException(refusal);
            }
        }

        return (entries, expectation);
    }

    /// <summary>
    /// The sha256 of the record's CANONICAL bytes, lowercase hex — derived from the deserialized
    /// record, never from the file as it sits on disk. A trailing newline, a reformat or a
    /// <c>text=auto</c> CRLF normalisation must not move it, and neither must a future nullable
    /// member (<c>CanonicalJson</c> omits nulls, the same mechanism S-5 already depends on).
    /// </summary>
    private static string CanonicalSha256(GateRecord record) =>
        Convert.ToHexString(SHA256.HashData(Signing.CanonicalJson.Bytes(record))).ToLowerInvariant();

    /// <summary>
    /// Resolves the signature posture from two anchors, neither of which is the record being
    /// judged. (a) The DERIVED anchor: any verifying signed record proves the store is signed.
    /// (b) The MARKER: honoured when it verifies AND this reader can vouch for its signer —
    /// through key material when there is any, or, for a keyless reader, through a verifying
    /// record under the same key. A keyed reader therefore catches TOTAL stripping (the marker
    /// alone creates expectation) and ignores a marker planted under a foreign key; a keyless
    /// reader cannot be bricked by a plant, yet still catches partial stripping. The grace
    /// floor is the MIN of whichever anchors fired: a forward-dated marker cannot grandfather a
    /// record an earlier signed record already dates, and lowering the floor only ever makes
    /// reads stricter, so caller clock skew cannot become store corruption. The pinning set is
    /// key material only — deriving it from the records would accept whatever key an attacker
    /// re-signed them all with.
    /// </summary>
    private GateSignatureExpectation ResolveExpectation(IReadOnlyList<GateRecord> anchors)
    {
        var marker = GateSigningActivation.TryRead(_stateRoot);   // throws on a corrupt or unsigned marker
        var basis = new List<string>(3);
        DateTimeOffset? grace = null;
        var expected = false;

        if (anchors.Count > 0)
        {
            expected = true;
            grace = anchors.Min(r => r.DecidedAt);
            basis.Add($"{anchors.Count} verifying signed record(s), earliest decided {grace:u}");
        }

        if (marker is not null)
        {
            var honoured = _trustedSigners.Count > 0
                ? _trustedSigners.Contains(marker.Signer!, StringComparer.Ordinal)
                : anchors.Any(r => string.Equals(r.Signer, marker.Signer, StringComparison.Ordinal));
            if (honoured)
            {
                expected = true;
                if (grace is null || marker.ActivatedAt < grace.Value)
                {
                    grace = marker.ActivatedAt;
                }
                basis.Add($"signing activated {marker.ActivatedAt:u}"
                    + (_trustedSigners.Count > 0 ? " under a key this machine vouches for" : ", corroborated by a record under the same key"));
            }
            else
            {
                basis.Add(_trustedSigners.Count > 0
                    ? "activation marker ignored: signed by a key this machine does not vouch for"
                    : "activation marker ignored: no operator key here and no verifying record corroborates its signer");
            }
        }

        if (!expected)
        {
            basis.Add(_trustedSigners.Count > 0
                ? "operator key present but this store has never been signed"
                : "no operator key and no signed record — unsigned, as SPEC-006 S-2 allows");
        }

        return new GateSignatureExpectation(expected, grace, _trustedSigners, string.Join("; ", basis), marker?.ActivatedAt);
    }

    private async Task<List<(string Path, GateRecord Record)>> ParseAllAsync(CancellationToken ct)
    {
        var parsed = new List<(string, GateRecord)>();
        foreach (var file in Directory.EnumerateFiles(_dir, "*.json"))
        {
            parsed.Add((file, await ParseRecordAsync(file, ct).ConfigureAwait(false)));
        }
        return parsed;
    }

    /// <summary>
    /// Parses one record file, FAIL-CLOSED. This used to skip records that deserialized to
    /// null and let JsonException escape raw — and a corrupt HELD record silently vanishing
    /// from the queue is an invisible pending decision, the worst possible failure shape
    /// for an admission store. A store this class cannot fully read is a store it refuses
    /// to summarize.
    /// </summary>
    private static async Task<GateRecord> ParseRecordAsync(string path, CancellationToken ct)
    {
        try
        {
            await using var stream = File.OpenRead(path);
            var record = await JsonSerializer.DeserializeAsync<GateRecord>(stream, Json, ct).ConfigureAwait(false);
            if (record is null)
            {
                throw new InvalidOperationException(
                    $"Corrupt gate record: {Path.GetFileName(path)} contains no record. "
                    + "Refusing to operate on a store that cannot be fully read — inspect or remove the file.");
            }
            return record;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Corrupt gate record: {Path.GetFileName(path)} is not valid JSON ({ex.Message}). "
                + "Refusing to operate on a store that cannot be fully read — inspect or remove the file.");
        }
    }

    /// <summary>
    /// How many extensions were admitted inside the budget window ending now. Drives the
    /// self-extending budget check. Consumes <see cref="ReadStoreAsync"/> directly rather than
    /// <see cref="ListAsync"/>, so the count uses the very expectation instance that judged these
    /// records instead of re-reading the public <see cref="SignatureTrust"/> after an await.
    /// </summary>
    public async Task<int> AdmittedInWindowAsync(TimeSpan window, DateTimeOffset now, CancellationToken ct = default)
    {
        var (records, _) = await ReadStoreAsync(ct).ConfigureAwait(false);
        var cutoff = now - window;
        return records.Count(e => e.Record.State == ProposalState.Admitted && e.Record.DecidedAt >= cutoff);
    }

    private string PathFor(string proposalId)
    {
        // ALLOWLIST, not blocklist. The old check blocked '/', '\' and '.' — and admitted
        // the entire Windows hazard alphabet: reserved names (CON, NUL), trailing dots and
        // spaces (Win32 strips them silently, so 'ext' and 'ext ' collide and append-once
        // is bypassed), ':' (NTFS alternate data streams), and unicode confusables. Ids are
        // machine-generated in this system; there is no reason to accept anything beyond
        // this shape, so nothing beyond it is accepted.
        if (!IdShape().IsMatch(proposalId))
        {
            throw new ArgumentException(
                $"Illegal proposal id '{proposalId}'. Ids are 1-64 characters of [A-Za-z0-9_-], starting alphanumeric.",
                nameof(proposalId));
        }
        // Win32 reserved device names are perfectly alphanumeric, so the shape check passes
        // them — and they are denied on EVERY OS, not just Windows: a store written on Linux
        // with a CON.json breaks the moment it syncs to a Windows machine. Portability means
        // the same ids are legal everywhere.
        if (Win32Reserved.Contains(proposalId))
        {
            throw new ArgumentException(
                $"Illegal proposal id '{proposalId}': a Win32 reserved device name cannot be a store filename on any OS.",
                nameof(proposalId));
        }
        return Path.Combine(_dir, proposalId + ".json");
    }

    private static readonly HashSet<string> Win32Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    [System.Text.RegularExpressions.GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$")]
    private static partial System.Text.RegularExpressions.Regex IdShape();

    /// <summary>Persists the record and RETURNS EXACTLY WHAT WAS PERSISTED — signature included.
    /// Callers hand the result to their own callers, and a returned record that differed from the
    /// disk record (unsigned, or carrying a stale signature over pre-decision content) would hand
    /// consumers — the packaging exporter above all — a verdict that fails its own verification.</summary>
    private async Task<GateRecord> WriteAsync(string path, GateRecord record, CancellationToken ct)
    {
        // SPEC-006, applied SYMMETRICALLY: always start from the unsigned form, then sign
        // it iff a key is present. The stripping is not optional on the keyless path — a
        // record that was read, mutated, and is now being rewritten (DecideAsync: Held →
        // Admitted) still carries the signature that covered its PRE-mutation content. If a
        // keyless store wrote it back verbatim, that stale signature would cover the wrong
        // bytes, and the very next fail-closed read would reject a legitimate verdict as
        // forged — bricking not just that record but every ListAsync over the store. The
        // rule is absolute: never persist a signature we did not just compute over exactly
        // these bytes. No key ⇒ no signature (S-2), never a half-signed inheritance.
        var unsigned = record with { Sig = null, Signer = null };
        if (_signer is null)
        {
            // FAIL CLOSED on a keyless write into a signed store. Without this the invariant is
            // not total: a keyless DecideAsync would rewrite a signed record unsigned — a silent
            // self-downgrade performed by LEGITIMATE code — and under the S-6 read rule the store
            // would then refuse itself on the next read. Availability is traded for security here
            // deliberately: a store this class cannot sign must not authorize new extensions.
            // The write guard is stricter than the read rule on purpose — a VERIFYING marker
            // refuses a keyless write even when no record corroborates it, because a keyless
            // writer cannot vouch for the marker and must not gamble that a keyed reader will not
            // honour it. The caller already holds the store lock.
            var (_, expectation) = await ReadStoreAsync(ct).ConfigureAwait(false);
            if (expectation.Expected || expectation.MarkerPresent)
            {
                var why = expectation.Expected ? expectation.Basis : $"signing activated {expectation.MarkerActivatedAt!.Value:u}";
                throw new InvalidOperationException(
                    $"This gate store is signed ({why}), but no operator key is loaded here, so the verdict for "
                    + $"'{record.Proposal.Id}' would be written UNSIGNED — and the next read would refuse it as a "
                    + "stripped signature. Refusing to write it. The remedy is `ashlar keys init` (or ASHLAR_KEY_DIR "
                    + "pointing at the operator's key directory) so this machine can sign. Deleting or editing records "
                    + "under gates/ is NOT the remedy: removing records is exactly the budget forgery this refusal "
                    + "exists to stop, and each removed admission raises the remaining self-extension budget.");
            }
            record = unsigned;
        }
        else
        {
            // Activate BEFORE the temp-write, a no-op once the marker exists: ActivatedAt anchors
            // to the FIRST signed record's DecidedAt, so everything already on disk is
            // grandfathered — the non-bricking adoption path. A corrupt marker throws here, so a
            // keyed writer never signs into a store whose posture it cannot read.
            GateSigningActivation.Activate(_stateRoot, _signer, record.DecidedAt);
            record = unsigned with
            {
                Sig = _signer.Sign(Signing.CanonicalJson.Bytes(unsigned)),
                Signer = _signer.PublicKeyBase64,
            };
        }

        // Write-then-move so a crash mid-write never leaves a truncated record.
        var tmp = path + ".tmp";
        await using (var stream = File.Create(tmp))
        {
            await JsonSerializer.SerializeAsync(stream, record, Json, ct).ConfigureAwait(false);
        }
        File.Move(tmp, path, overwrite: true);
        return record;
    }
}
