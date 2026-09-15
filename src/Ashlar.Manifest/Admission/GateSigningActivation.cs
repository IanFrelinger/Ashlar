using System.Text.Json;
using System.Text.Json.Serialization;
using Ashlar.Manifest.Signing;

namespace Ashlar.Manifest.Admission;

/// <summary>
/// The persisted instant a gate store started signing: <c>{stateRoot}/gate-signing.json</c>,
/// written by the first signed write and never overwritten. A reader that honours it treats an
/// unsigned record decided at or after <see cref="ActivatedAt"/> as a STRIPPED signature
/// (SPEC-006 rule S-6), while everything decided before it is grandfathered — the non-bricking
/// adoption path for a store that already holds honest unsigned records.
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

    /// <summary>When signing was activated (UTC). The store anchors this to the first signed
    /// record's <c>DecidedAt</c>, so everything already on disk is grandfathered.</summary>
    public required DateTimeOffset ActivatedAt { get; init; }

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

    /// <summary>A marker for <paramref name="activatedAt"/>, signed by <paramref name="signer"/>.</summary>
    public static GateSigningActivation Signed(SigningIdentity signer, DateTimeOffset activatedAt)
    {
        ArgumentNullException.ThrowIfNull(signer);
        var unsigned = new GateSigningActivation { ActivatedAt = activatedAt };
        return unsigned with
        {
            Sig = signer.Sign(CanonicalJson.Bytes(unsigned)),
            Signer = signer.PublicKeyBase64,
        };
    }

    /// <summary>
    /// Reads the marker, or null when none exists. A marker that exists but cannot be parsed, is
    /// unsigned, or does not verify is corruption and throws — never null, never honoured.
    /// </summary>
    public static GateSigningActivation? TryRead(string stateRoot)
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
    /// Writes the marker if none exists, and returns the marker on disk either way. An existing
    /// marker is verified and never overwritten, so the activation instant is stable across key
    /// rotation and repeated activation — a rotation cannot re-open the grace window. Written
    /// temp-then-move; the move never overwrites, and losing the race to another writer means
    /// reading what won.
    /// </summary>
    public static GateSigningActivation Activate(string stateRoot, SigningIdentity signer, DateTimeOffset activatedAt)
    {
        ArgumentNullException.ThrowIfNull(signer);

        if (TryRead(stateRoot) is { } existing)
        {
            return existing;
        }

        var marker = Signed(signer, activatedAt);
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
            return TryRead(stateRoot)
                ?? throw new InvalidOperationException(
                    $"Signing activation at {path} vanished between losing the write race and re-reading it.");
        }

        return marker;
    }
}
