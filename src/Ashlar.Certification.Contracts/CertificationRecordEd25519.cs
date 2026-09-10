#if NET8_0_OR_GREATER
using System.Text;
using NSec.Cryptography;

namespace Ashlar.Certification.Contracts;

/// <summary>
/// Ed25519 signing for trust-loop certification records, dual-written alongside the
/// HMAC signature over the same canonical payload (<see cref="CertificationRecordSigning.BuildPayload"/>).
/// Asymmetric so verifiers do not hold minting capability, which lets certificates
/// compose across trust domains (spec R2.6). Unavailable on netstandard2.0 consumers,
/// which fall back to HMAC-only verification; the HMAC payload covers the Ed25519
/// public key, so these fields are still tamper-evident inside the symmetric domain.
/// There is deliberately no default development private key: without a configured key
/// the minter writes HMAC-only records, and verification enforces the Ed25519
/// signature whenever a record carries one.
/// </summary>
public static class CertificationRecordEd25519
{
    /// <summary>Environment variable holding the Base64 raw 32-byte Ed25519 private key.</summary>
    public const string PrivateKeyEnvVar = "ASHLAR_CERT_ED25519_KEY";

    private static readonly SignatureAlgorithm Algorithm = SignatureAlgorithm.Ed25519;

    /// <summary>
    /// Computes the Base64 Ed25519 signature over the record's canonical payload.
    /// Set <see cref="CertificationRecordData.Ed25519PublicKey"/> before signing so
    /// the payload covers it; both signature fields are structurally excluded.
    /// </summary>
    /// <param name="record">Record to sign.</param>
    /// <param name="privateKey">Raw 32-byte Ed25519 private key.</param>
    public static string Sign(CertificationRecordData record, ReadOnlySpan<byte> privateKey)
    {
        var payload = Encoding.UTF8.GetBytes(CertificationRecordSigning.BuildPayload(record));
        using var key = Key.Import(Algorithm, privateKey, KeyBlobFormat.RawPrivateKey);
        return Convert.ToBase64String(Algorithm.Sign(key, payload));
    }

    /// <summary>
    /// Verifies <see cref="CertificationRecordData.Ed25519Signature"/> against the
    /// canonical payload using the record's own <see cref="CertificationRecordData.Ed25519PublicKey"/>.
    /// Returns false when either field is missing or malformed, or the signature does not match.
    /// </summary>
    /// <param name="record">Record containing the signature and public key to verify.</param>
    public static bool VerifySignature(CertificationRecordData record)
    {
        if (string.IsNullOrWhiteSpace(record.Ed25519Signature) || string.IsNullOrWhiteSpace(record.Ed25519PublicKey))
            return false;

        byte[] signature;
        byte[] publicKeyBytes;
        try
        {
            signature = Convert.FromBase64String(record.Ed25519Signature!);
            publicKeyBytes = Convert.FromBase64String(record.Ed25519PublicKey!);
        }
        catch (FormatException)
        {
            return false;
        }

        PublicKey publicKey;
        try
        {
            publicKey = PublicKey.Import(Algorithm, publicKeyBytes, KeyBlobFormat.RawPublicKey);
        }
        catch (FormatException)
        {
            return false;
        }

        byte[] payload;
        try
        {
            payload = Encoding.UTF8.GetBytes(CertificationRecordSigning.BuildPayload(record));
        }
        catch (CanonicalPayloadException)
        {
            // Same reason as the HMAC path: the payload could not be established as its declared
            // shape, so there is nothing to verify against — and a verifier refuses rather than
            // throwing into its host.
            return false;
        }

        return Algorithm.Verify(publicKey, payload, signature);
    }

    /// <summary>Derives the Base64 raw public key for a raw 32-byte private key.</summary>
    /// <param name="privateKey">Raw 32-byte Ed25519 private key.</param>
    public static string DerivePublicKeyBase64(ReadOnlySpan<byte> privateKey)
    {
        using var key = Key.Import(Algorithm, privateKey, KeyBlobFormat.RawPrivateKey);
        return Convert.ToBase64String(key.PublicKey.Export(KeyBlobFormat.RawPublicKey));
    }

    /// <summary>
    /// Resolves the minting private key: explicit argument, then
    /// <see cref="PrivateKeyEnvVar"/>, then null (mint HMAC-only). A key that is
    /// configured but malformed throws rather than silently degrading to HMAC-only.
    /// </summary>
    /// <param name="privateKeyBase64">Optional explicit Base64 raw private key.</param>
    public static byte[]? ResolvePrivateKey(string? privateKeyBase64 = null)
    {
        var candidate = string.IsNullOrWhiteSpace(privateKeyBase64)
            ? Environment.GetEnvironmentVariable(PrivateKeyEnvVar)
            : privateKeyBase64;
        if (string.IsNullOrWhiteSpace(candidate))
            return null;

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(candidate!);
        }
        catch (FormatException ex)
        {
            throw new FormatException("Configured Ed25519 private key is not valid Base64.", ex);
        }

        if (bytes.Length != 32)
            throw new FormatException($"Configured Ed25519 private key must be 32 raw bytes, got {bytes.Length}.");
        return bytes;
    }
}
#endif
