using System.Security.Cryptography;
using NSec.Cryptography;

namespace Ashlar.Manifest.Signing;

/// <summary>
/// SPEC-006 v1: the single local operator Ed25519 keypair.
///
/// <para><c>Generate</c> writes <c>operator.key</c> (raw 32-byte seed, base64, owner-only on
/// POSIX) and <c>operator.pub</c> under the key directory — <c>ASHLAR_KEY_DIR</c> or
/// <c>~/.ashlar/keys</c>. <c>TryLoad</c> returns null when no key exists: signing is
/// presence-activated, and absence degrades to today's honest unsigned behaviour exactly
/// (rule S-2). The private key never appears inside a repository, bundle, or output.</para>
/// </summary>
public static class OperatorKey
{
    private static readonly SignatureAlgorithm Alg = SignatureAlgorithm.Ed25519;

    /// <summary>Resolves the key directory: <c>ASHLAR_KEY_DIR</c>, else <c>~/.ashlar/keys</c>.</summary>
    public static string ResolveKeyDir() =>
        Environment.GetEnvironmentVariable("ASHLAR_KEY_DIR") is { Length: > 0 } dir
            ? Path.GetFullPath(dir)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ashlar", "keys");

    /// <summary>
    /// Generates a new operator keypair. Refuses to overwrite an existing key unless
    /// <paramref name="rotate"/>; rotation moves the old PUBLIC key to <c>trusted/</c> so
    /// old records still verify (revocation is v2, and v1 says so).
    /// </summary>
    public static SigningIdentity Generate(string? keyDir = null, bool rotate = false)
    {
        var dir = keyDir ?? ResolveKeyDir();
        Directory.CreateDirectory(dir);
        var privPath = Path.Combine(dir, "operator.key");
        var pubPath = Path.Combine(dir, "operator.pub");

        if (File.Exists(privPath))
        {
            if (!rotate)
            {
                throw new InvalidOperationException(
                    $"An operator key already exists at {privPath}. Use --rotate to replace it; "
                    + "the old public key is kept in trusted/ so existing records still verify.");
            }
            var trustedDir = Path.Combine(dir, "trusted");
            Directory.CreateDirectory(trustedDir);
            if (File.Exists(pubPath))
            {
                var oldPub = File.ReadAllText(pubPath).Trim();
                var oldPrint = Fingerprint(Convert.FromBase64String(oldPub)).Replace(':', '-');
                File.WriteAllText(Path.Combine(trustedDir, oldPrint + ".pub"), oldPub);
            }
        }

        using var key = Key.Create(Alg, new KeyCreationParameters
        {
            ExportPolicy = KeyExportPolicies.AllowPlaintextExport,
        });
        var seed = key.Export(KeyBlobFormat.RawPrivateKey);
        var pub = key.PublicKey.Export(KeyBlobFormat.RawPublicKey);

        // Write each file crash-safely (temp then rename), and set owner-only permissions on
        // the seed's temp BEFORE it is moved into place, so the private key never exists at
        // its final path with default permissions even for an instant. The two files are not
        // moved as one atomic unit — a crash between the moves can still strand a new seed
        // beside an old pub — but that mismatch is caught loudly at load by the
        // SigningIdentity invariant below, rather than silently producing unverifiable records.
        WriteFileAtomic(privPath, Convert.ToBase64String(seed), ownerOnly: true);
        WriteFileAtomic(pubPath, Convert.ToBase64String(pub), ownerOnly: false);
        // Windows: the profile directory's ACL is the v1 protection; DPAPI is v2 (SPEC-006 §3).

        return new SigningIdentity(Convert.ToBase64String(pub), seed);
    }

    /// <summary>Loads the operator identity, or null when no key exists (S-2 degrade).</summary>
    public static SigningIdentity? TryLoad(string? keyDir = null)
    {
        var dir = keyDir ?? ResolveKeyDir();
        var privPath = Path.Combine(dir, "operator.key");
        var pubPath = Path.Combine(dir, "operator.pub");
        if (!File.Exists(privPath) || !File.Exists(pubPath))
        {
            return null;
        }
        // A key that is PRESENT but unreadable is corrupt, not absent: it must fail loud, never
        // degrade to unsigned (silently dropping signatures the moment a key file is mangled is
        // the worst outcome). Convert.FromBase64String throws FormatException on garbled text and
        // NSec's Key.Import throws on a wrong-length seed; normalise both to the same
        // InvalidOperationException the mismatch case (SigningIdentity's ctor) already raises, so
        // every caller — the CLI included — sees ONE corrupt-key contract, not a raw parse
        // exception that escapes its InvalidOperationException/ArgumentException guards as a stack
        // trace. (IOException from a locked file is a different, transient failure and is left to
        // propagate as itself.)
        try
        {
            return new SigningIdentity(
                File.ReadAllText(pubPath).Trim(),
                Convert.FromBase64String(File.ReadAllText(privPath).Trim()));
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            throw new InvalidOperationException(
                $"Corrupt operator key in {dir}: the files are not a readable Ed25519 keypair ({ex.Message}). "
                + "Re-run key generation with rotation to write a fresh pair; records signed by earlier keys keep "
                + "verifying via the public keys retained under trusted/.");
        }
    }

    /// <summary>SPEC-006 §3 fingerprint: <c>ed25519:</c> + first 16 lowercase hex chars of
    /// SHA-256 over the raw public key.</summary>
    public static string Fingerprint(byte[] rawPublicKey) =>
        "ed25519:" + Convert.ToHexString(SHA256.HashData(rawPublicKey))[..16].ToLowerInvariant();

    /// <summary>
    /// Every public key this operator's key material vouches for: <c>operator.pub</c> plus each
    /// <c>trusted/*.pub</c> that <see cref="Generate"/> retained on rotation — trimmed,
    /// deduplicated, and EMPTY when the directory holds none (the S-2 degrade, never a throw).
    /// This is the signer-pinning set a gate store judges its own records against, so it is
    /// derived from key material only, never from the records being judged: a pinning set read
    /// off the records would accept whatever key an attacker re-signed them all with.
    ///
    /// <para>A file that is PRESENT but is not a readable Ed25519 public key is corruption, not
    /// absence — the same <see cref="InvalidOperationException"/> <see cref="TryLoad"/> raises, for
    /// the same reason: quietly narrowing the trust set the moment a key file is mangled would turn
    /// a corrupt directory into a silently weaker reader. <c>peers/</c> is deliberately NOT read:
    /// that keychain is the package-import admission allowlist, and the comment above it forbids
    /// wiring it onto a different trust decision.</para>
    /// </summary>
    public static IReadOnlyList<string> TrustedPublicKeysBase64(string? keyDir = null)
    {
        var dir = keyDir ?? ResolveKeyDir();
        var keys = new List<string>();

        var pubPath = Path.Combine(dir, "operator.pub");
        if (File.Exists(pubPath))
        {
            keys.Add(ReadPublicKeyFile(dir, pubPath));
        }

        var trustedDir = Path.Combine(dir, "trusted");
        if (Directory.Exists(trustedDir))
        {
            // Filter on the exact extension: a three-character search pattern also matches longer
            // extensions on Windows ("*.pub" finds "x.pubx"), and a stray file must not be read as key material.
            foreach (var file in Directory.GetFiles(trustedDir, "*.pub")
                         .Where(f => string.Equals(Path.GetExtension(f), ".pub", StringComparison.Ordinal))
                         .OrderBy(f => f, StringComparer.Ordinal))
            {
                keys.Add(ReadPublicKeyFile(dir, file));
            }
        }

        return keys.Distinct(StringComparer.Ordinal).ToList();
    }

    private static string ReadPublicKeyFile(string dir, string path)
    {
        var text = File.ReadAllText(path).Trim();
        try
        {
            PublicKey.Import(Alg, Convert.FromBase64String(text), KeyBlobFormat.RawPublicKey);
            return text;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            throw new InvalidOperationException(
                $"Corrupt operator key in {dir}: {Path.GetFileName(path)} is not a readable Ed25519 public key ({ex.Message}). "
                + "Re-run key generation with rotation to write a fresh pair; records signed by earlier keys keep "
                + "verifying via the public keys retained under trusted/.");
        }
    }

    // ─────────────────────────── the peers trust keychain (Phase 3) ───────────────────────────
    // The set of signer fingerprints this operator trusts to admit imported packages, kept as
    // marker files under <keyDir>/peers/. Deliberately SEPARATE from trusted/ (the rotation
    // retention of the operator's OWN superseded public keys): an admission allowlist must never
    // be wired onto the rotation directory, or rotating after a theft would re-authorize the stolen
    // key. Fingerprints, not public keys, are enough — a package is intrinsically signed and
    // verifies against the key it carries; trust is only the decision to accept that fingerprint.

    private const string FpPrefix = "ed25519:";
    private const int FpHexLen = 16;

    /// <summary>The peers keychain directory: <c>&lt;keyDir&gt;/peers</c>.</summary>
    public static string PeersDir(string? keyDir = null) => Path.Combine(keyDir ?? ResolveKeyDir(), "peers");

    /// <summary>True for a well-formed <c>ed25519:</c> + 16-lowercase-hex fingerprint.</summary>
    public static bool IsValidFingerprint(string? fingerprint)
    {
        if (fingerprint is null || fingerprint.Length != FpPrefix.Length + FpHexLen) return false;
        if (!fingerprint.StartsWith(FpPrefix, StringComparison.Ordinal)) return false;
        foreach (var c in fingerprint.AsSpan(FpPrefix.Length))
        {
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
        }
        return true;
    }

    /// <summary>Adds a fingerprint to the peers keychain. Idempotent; validates the format.</summary>
    public static void Trust(string fingerprint, string? keyDir = null)
    {
        if (!IsValidFingerprint(fingerprint))
            throw new ArgumentException($"'{fingerprint}' is not a valid operator fingerprint (expected ed25519: + 16 hex).");
        var dir = PeersDir(keyDir);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, fingerprint.Replace(':', '-') + ".pub"), fingerprint);
    }

    /// <summary>Removes a fingerprint from the keychain. Returns false if it was not trusted.</summary>
    public static bool Untrust(string fingerprint, string? keyDir = null)
    {
        if (!IsValidFingerprint(fingerprint)) return false;
        var path = Path.Combine(PeersDir(keyDir), fingerprint.Replace(':', '-') + ".pub");
        if (!File.Exists(path)) return false;
        File.Delete(path);
        return true;
    }

    /// <summary>The trusted fingerprints in the keychain, sorted, deduplicated.</summary>
    public static IReadOnlyList<string> ListTrusted(string? keyDir = null)
    {
        var dir = PeersDir(keyDir);
        if (!Directory.Exists(dir)) return Array.Empty<string>();
        return Directory.GetFiles(dir, "*.pub")
            .Select(f => Path.GetFileNameWithoutExtension(f).Replace('-', ':'))
            .Where(IsValidFingerprint)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// A short digest of a trust set (sorted fingerprints), so two boxes — or one box before and
    /// after a revocation — can be compared at a glance. A box that was off during a revocation is
    /// the one still trusting the removed key, and nothing else makes that divergence visible.
    /// </summary>
    public static string TrustSetDigest(IEnumerable<string> fingerprints)
    {
        var joined = string.Join("\n", fingerprints.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.Ordinal));
        return "sha256:" + Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(joined)))[..16].ToLowerInvariant();
    }

    /// <summary>
    /// Whether an imported package's sealer is trusted to admit: its fingerprint is listed by the
    /// project's policy OR in the operator's local peers keychain. An unsigned package
    /// (<c>(unsigned)</c>) is never trusted — the fail-closed default once a key exists.
    /// </summary>
    public static bool IsSignerTrusted(string sealerFingerprint, IEnumerable<string> policyTrustedSigners, string? keyDir = null)
    {
        if (!IsValidFingerprint(sealerFingerprint)) return false;
        // Self-trust: a node always trusts its OWN operator key. Publishing and re-importing your
        // own package needs no ceremony, and trusting the key you sign with adds no attack surface
        // — the threat this gate exists for is a STRANGER's key, never your own.
        try
        {
            if (TryLoad(keyDir) is { } self && string.Equals(self.Fingerprint, sealerFingerprint, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        catch (InvalidOperationException) { /* a corrupt local key is not a trust source */ }
        foreach (var s in policyTrustedSigners)
        {
            if (string.Equals(s, sealerFingerprint, StringComparison.OrdinalIgnoreCase)) return true;
        }
        foreach (var p in ListTrusted(keyDir))
        {
            if (string.Equals(p, sealerFingerprint, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>Verifies a signature made by <see cref="SigningIdentity.Sign"/>.</summary>
    public static bool Verify(string publicKeyBase64, byte[] data, string signatureBase64)
    {
        try
        {
            var pub = PublicKey.Import(Alg, Convert.FromBase64String(publicKeyBase64), KeyBlobFormat.RawPublicKey);
            return Alg.Verify(pub, data, Convert.FromBase64String(signatureBase64));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>Writes a file crash-safely via a temp-then-rename, applying owner-only Unix
    /// permissions to the temp before the move so a private key is never briefly world-readable
    /// at its final path.</summary>
    private static void WriteFileAtomic(string path, string content, bool ownerOnly)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        if (ownerOnly && !OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        File.Move(tmp, path, overwrite: true);
    }
}

/// <summary>A loaded operator identity: the public half, its fingerprint, and the ability
/// to sign. Holds the seed in memory only for the process lifetime.</summary>
public sealed class SigningIdentity
{
    private readonly byte[] _seed;

    /// <summary>Base64 raw 32-byte public key.</summary>
    public string PublicKeyBase64 { get; }

    /// <summary>Display fingerprint, e.g. <c>ed25519:9f3c…</c> without the ellipsis.</summary>
    public string Fingerprint { get; }

    internal SigningIdentity(string publicKeyBase64, byte[] seed)
    {
        // Invariant: a SigningIdentity NEVER exists with a public half its seed does not
        // produce. TryLoad builds this from two independently-written files, and a crash
        // between those writes — or during --rotate, which destroys the old seed — can leave
        // operator.key and operator.pub disagreeing. Catch it HERE, loudly, rather than
        // signing every record with one key while advertising another, which would stamp
        // honest verdicts with signatures that can never verify and poison the store on the
        // next fail-closed read. Ed25519's public key is a deterministic function of the seed,
        // so this is an exact check; the public key is not secret, so an ordinal compare is fine.
        using (var check = Key.Import(SignatureAlgorithm.Ed25519, seed, KeyBlobFormat.RawPrivateKey))
        {
            var derived = Convert.ToBase64String(check.PublicKey.Export(KeyBlobFormat.RawPublicKey));
            if (!string.Equals(derived, publicKeyBase64, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "operator.key and operator.pub disagree: the private seed does not derive the stored public "
                    + "key. This is the fingerprint of a crash during key rotation. Re-run key generation with "
                    + "rotation to write a fresh, consistent keypair; records signed by earlier keys keep verifying "
                    + "via the public keys retained under trusted/.");
            }
        }

        PublicKeyBase64 = publicKeyBase64;
        _seed = seed;
        Fingerprint = OperatorKey.Fingerprint(Convert.FromBase64String(publicKeyBase64));
    }

    /// <summary>Signs canonical bytes; returns base64.</summary>
    public string Sign(byte[] data)
    {
        using var key = Key.Import(SignatureAlgorithm.Ed25519, _seed, KeyBlobFormat.RawPrivateKey);
        return Convert.ToBase64String(SignatureAlgorithm.Ed25519.Sign(key, data));
    }
}
