using System.Text.Json;
using System.Text.Json.Serialization;
using Ashlar.Manifest.Signing;

namespace Ashlar.Manifest.Admission;

/// <summary>
/// What the operator declared about this gate store at the one instant they were present:
/// <c>{stateRoot}/gate-signing.json</c>. Two things, both inside the signed bytes — the instant
/// signing was activated, and the <see cref="Grandfathered"/> inventory naming every unsigned
/// record that existed then, by id and by canonical hash. A reader that honours the marker treats
/// an unsigned record as a STRIPPED signature (SPEC-006 rule S-6) unless it is in that inventory
/// with those bytes. Membership, not a date: <c>DecidedAt</c> on an unsigned record is the
/// attacker's own field, so a floor compared against it grandfathers anything they choose to
/// back-date.
///
/// <para><b>Never overwritten — narrowed.</b> The invariant used to be absolute. It now reads: a
/// marker this machine vouches for is never overwritten by the automatic write path, and never
/// silently by anything. Only the operator's explicit verb (<c>ashlar gates sign-activate</c>,
/// and <c>--repair</c>) replaces one, and only a marker signed by a key the caller cannot vouch
/// for, or one that predates the inventory. The alternative — throwing always — hands anyone who
/// can write the state root a permanent brick of every keyed write with no named exit.</para>
///
/// <para><b>Where it lives, and why not inside <c>gates/</c>.</b> <see cref="GateStore.ListAsync"/>
/// enumerates <c>gates/*.json</c> and <see cref="GateRecord"/> declares required members, so a
/// marker inside that directory would deserialize to a <see cref="JsonException"/> and brick every
/// listing — including <see cref="GateStore.AdmittedInWindowAsync"/>, the durable budget this
/// convention exists to protect. It is a sibling of <c>gates/</c>, and not a dotfile, so it is
/// seen by anyone inspecting the state root.</para>
///
/// <para><b>Signed over its own unsigned form</b>, with the canonical-bytes, strip-then-sign
/// convention records use. A marker that exists but carries no <c>Sig</c>, or whose <c>Sig</c>
/// does not verify against its embedded <c>Signer</c>, is CORRUPTION and <see cref="TryRead"/>
/// throws: honouring an unsigned marker as a stricter declaration would let anyone with
/// filesystem write access brick a legitimately keyless store by planting a far-past
/// <see cref="ActivatedAt"/>. Whether a VERIFYING marker is honoured is the store's decision —
/// <see cref="GateStore"/> resolves it against key material and corroborating records; this
/// type only says what is on disk.</para>
/// </summary>
public sealed record GateSigningActivation
{
    /// <summary>The marker's file name, a sibling of <c>gates/</c> under the state root.</summary>
    public const string FileName = "gate-signing.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>When signing was activated (UTC).</summary>
    public required DateTimeOffset ActivatedAt { get; init; }

    /// <summary>
    /// Every unsigned record the operator authorized at <see cref="ActivatedAt"/>, by id and
    /// canonical sha256, sorted ordinally by id. Inside the signed bytes, so it travels with the
    /// store: a keyless bundle consumer gets the protection with no out-of-band state.
    ///
    /// <para><b>Nullable, and deliberately not <c>required</c>.</b> A <c>required</c> member makes
    /// <c>System.Text.Json</c> throw a <c>JsonException</c> on a marker written before the
    /// inventory existed, which would make the named refusal in <see cref="TryRead"/> unreachable
    /// and hand the operator a parse error instead of the remedy. Null therefore means "a marker
    /// that predates the inventory", which is refused BY NAME; an empty list means "the operator
    /// authorized nothing", which is a different value and enters the canonical bytes as one
    /// (<c>CanonicalJson</c> omits nulls only).</para>
    /// </summary>
    public IReadOnlyList<GrandfatheredRecord>? Grandfathered { get; init; }

    /// <summary>Base64 Ed25519 signature over the canonical marker with both signature fields null.</summary>
    public string? Sig { get; init; }

    /// <summary>Base64 raw public key of the signer.</summary>
    public string? Signer { get; init; }

    /// <summary>The marker's path for a state root.</summary>
    public static string PathFor(string stateRoot) => Path.Combine(stateRoot, FileName);

    /// <summary>True when <see cref="Sig"/> verifies against <see cref="Signer"/> over the unsigned form.</summary>
    public bool Verifies() =>
        Sig is not null
        && Signer is not null
        && OperatorKey.Verify(Signer, CanonicalJson.Bytes(this with { Sig = null, Signer = null }), Sig);

    /// <summary>A marker for <paramref name="activatedAt"/> over
    /// <paramref name="grandfathered"/>, signed by <paramref name="signer"/>. The inventory is
    /// folded into the unsigned form BEFORE signing, so it is covered by the signature.</summary>
    public static GateSigningActivation Signed(
        SigningIdentity signer, DateTimeOffset activatedAt, IReadOnlyList<GrandfatheredRecord> grandfathered)
    {
        ArgumentNullException.ThrowIfNull(signer);
        ArgumentNullException.ThrowIfNull(grandfathered);
        var unsigned = new GateSigningActivation { ActivatedAt = activatedAt, Grandfathered = grandfathered };
        return unsigned with
        {
            Sig = signer.Sign(CanonicalJson.Bytes(unsigned)),
            Signer = signer.PublicKeyBase64,
        };
    }

    /// <summary>
    /// Reads the marker, or null when none exists. A marker that exists but cannot be parsed, is
    /// unsigned, does not verify, or predates the grandfather inventory is corruption and throws —
    /// never null, never honoured. The last of those is refused BY NAME with its remedy, because
    /// the alternative readings are both wrong: a bare parse error tells the operator nothing, and
    /// treating a missing inventory as "grandfather everything" is the forgery it exists to stop.
    /// </summary>
    public static GateSigningActivation? TryRead(string stateRoot)
    {
        var marker = ReadVerified(stateRoot);
        if (marker is not null && marker.Grandfathered is null)
        {
            throw new InvalidOperationException(
                $"Corrupt signing activation: {FileName} carries no grandfather inventory. It predates the signed "
                + "(id, sha256) inventory that replaced date-based grandfathering, and a marker without one cannot "
                + "say which unsigned records the operator authorized — reading it as 'grandfather everything' is "
                + "exactly the forgery the inventory exists to stop. Refusing to operate. Re-mint it with "
                + "`ashlar gates sign-activate --repair`, which prints how many unsigned records it grandfathers.");
        }
        return marker;
    }

    /// <summary>
    /// The marker as it is on disk, parsed and cryptographically verified, WITHOUT the inventory
    /// requirement. Only the repair path uses this: <c>--repair</c> exists precisely for a store
    /// whose marker a reader refuses, so it cannot itself read through the refusal.
    /// </summary>
    private static GateSigningActivation? ReadVerified(string stateRoot)
    {
        var path = PathFor(stateRoot);
        if (!File.Exists(path))
        {
            return null;
        }

        GateSigningActivation? marker;
        try
        {
            marker = JsonSerializer.Deserialize<GateSigningActivation>(File.ReadAllText(path), Json);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Corrupt signing activation: {FileName} is not valid JSON ({ex.Message}). "
                + "Refusing to operate on a store whose signing posture cannot be read — inspect the file.");
        }

        if (marker is null)
        {
            throw new InvalidOperationException(
                $"Corrupt signing activation: {FileName} contains no marker. "
                + "Refusing to operate on a store whose signing posture cannot be read — inspect the file.");
        }

        if (!marker.Verifies())
        {
            var what = marker.Sig is null ? "carries no signature" : "carries a signature that does not verify";
            throw new InvalidOperationException(
                $"Corrupt signing activation: {FileName} {what}. An unsigned or unverifiable activation marker is "
                + "never honoured — honouring one would let anyone who can write this directory brick a keyless "
                + "store by planting a far-past activation. Refusing to operate.");
        }

        return marker;
    }

    /// <summary>
    /// Activates signing, and says whether it was already active. The activation instant is stable
    /// across key rotation and repeated activation — a rotation must not be able to re-open the
    /// grace window — so a marker this caller vouches for keeps its own <see cref="ActivatedAt"/>
    /// in every path, including repair.
    ///
    /// <para>Three cases, and which of them this caller may act on is the caller's to declare. A
    /// marker whose signer this caller VOUCHES for and that carries an inventory is returned
    /// unchanged — that is the idempotent path, and it is why re-running activation or rotating a
    /// key cannot move the instant. A marker under a key nobody here vouches for is somebody
    /// else's declaration about this store. A marker predating the inventory cannot say what it
    /// authorized at all. <paramref name="replaceUnvouchedFor"/> admits the second,
    /// <paramref name="reMintVouchedFor"/> the third (and re-mints the inventory of a healthy
    /// marker); both are false on the kernel's automatic path, which refuses and names the
    /// operator's verb instead. The split matters: refusing always hands anyone who can write the
    /// state root a permanent brick of every keyed write with no named exit, and replacing always
    /// puts a new capability on a security artefact on the hot path.</para>
    ///
    /// <para>Written temp-then-move. The no-marker path never overwrites, so losing the race to
    /// another writer means reading what won; only a deliberate replacement overwrites.</para>
    /// </summary>
    public static (GateSigningActivation Marker, bool WasAlreadyActive, bool ReplacedUnvouchedMarker) Activate(
        string stateRoot,
        SigningIdentity signer,
        DateTimeOffset activatedAt,
        IReadOnlyList<string> vouchedFor,
        IReadOnlyList<GrandfatheredRecord> grandfathered,
        bool replaceUnvouchedFor,
        bool reMintVouchedFor)
    {
        ArgumentNullException.ThrowIfNull(signer);
        ArgumentNullException.ThrowIfNull(vouchedFor);
        ArgumentNullException.ThrowIfNull(grandfathered);

        var existing = ReadVerified(stateRoot);
        if (existing is not null)
        {
            var vouched = string.Equals(existing.Signer, signer.PublicKeyBase64, StringComparison.Ordinal)
                || vouchedFor.Contains(existing.Signer!, StringComparer.Ordinal);

            if (!vouched)
            {
                if (!replaceUnvouchedFor)
                {
                    throw new InvalidOperationException(
                        $"The signing activation at {PathFor(stateRoot)} is signed by a key this machine does not "
                        + "vouch for, so it is somebody else's declaration about this store and activating beside it "
                        + "would leave it in force. Refusing to activate over it. Run `ashlar gates sign-activate`, "
                        + "which replaces it with this operator's own marker and prints what it grandfathers.");
                }
                // A foreign marker's instant is not ours to keep. The caller is TOLD this happened:
                // it is the one outcome of this method an operator must not learn about by inference.
                return (Write(stateRoot, Signed(signer, activatedAt, grandfathered), overwrite: true), true, true);
            }

            if (existing.Grandfathered is not null && !reMintVouchedFor)
            {
                return (existing, true, false);
            }

            if (!reMintVouchedFor)
            {
                throw new InvalidOperationException(
                    $"The signing activation at {PathFor(stateRoot)} predates the grandfather inventory, so it cannot "
                    + "say which unsigned records this operator authorized. Refusing to activate over it. Re-mint it "
                    + "with `ashlar gates sign-activate --repair`.");
            }

            // The operator may re-mint WHAT was authorized here; never WHEN.
            return (Write(stateRoot, Signed(signer, existing.ActivatedAt, grandfathered), overwrite: true), true, false);
        }

        var marker = Signed(signer, activatedAt, grandfathered);
        var path = PathFor(stateRoot);
        Directory.CreateDirectory(stateRoot);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(marker, Json));
        try
        {
            File.Move(tmp, path, overwrite: false);
        }
        catch (IOException) when (File.Exists(path))
        {
            try
            {
                File.Delete(tmp);
            }
            catch (IOException)
            {
                // A stray temp beside the marker is harmless; nothing enumerates it.
            }
            var won = TryRead(stateRoot)
                ?? throw new InvalidOperationException(
                    $"Signing activation at {path} vanished between losing the write race and re-reading it.");
            return (won, true, false);
        }

        return (marker, false, false);
    }

    /// <summary>
    /// Re-signs the marker with the same <see cref="ActivatedAt"/> and a STRICTLY SMALLER
    /// inventory: the ids in <paramref name="removeIds"/> are dropped. The only mutator of an
    /// existing inventory, and it can only shrink one — a set that has grown is not an amendment,
    /// it is a re-blessing, and the operator's verb is where that belongs.
    ///
    /// <para>Called after a grandfathered record has been decided under a key: the record is now
    /// signed, so its entry pins bytes that are no longer on disk, and leaving it there would let
    /// an actor restore the pre-decision bytes and have them accepted.</para>
    /// </summary>
    public static GateSigningActivation Amend(string stateRoot, SigningIdentity signer, IReadOnlyList<string> removeIds)
    {
        ArgumentNullException.ThrowIfNull(signer);
        ArgumentNullException.ThrowIfNull(removeIds);

        var existing = TryRead(stateRoot)
            ?? throw new InvalidOperationException(
                $"Cannot amend the grandfather inventory: there is no {FileName} under {stateRoot}.");

        var inventory = existing.Grandfathered!;   // non-null: TryRead refuses a marker without one
        var kept = inventory
            .Where(g => !removeIds.Contains(g.Id, StringComparer.Ordinal))
            .ToList();

        if (kept.Count == inventory.Count)
        {
            return existing;
        }

        return Write(stateRoot, Signed(signer, existing.ActivatedAt, kept), overwrite: true);
    }

    /// <summary>Temp-then-move onto the marker path. Only callers that have already decided the
    /// replacement is legitimate pass <c>overwrite: true</c>.</summary>
    private static GateSigningActivation Write(string stateRoot, GateSigningActivation marker, bool overwrite)
    {
        var path = PathFor(stateRoot);
        Directory.CreateDirectory(stateRoot);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(marker, Json));
        File.Move(tmp, path, overwrite);
        return marker;
    }
}
