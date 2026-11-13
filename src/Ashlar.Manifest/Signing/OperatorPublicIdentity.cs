namespace Ashlar.Manifest.Signing;

/// <summary>
/// The configured operator public key and its fingerprint, without private signing material.
/// This describes public metadata only: it does not establish private-key possession,
/// keypair consistency, signing readiness, or an authenticated peer.
/// </summary>
public sealed class OperatorPublicIdentity
{
    internal OperatorPublicIdentity(string publicKeyBase64)
    {
        PublicKeyBase64 = publicKeyBase64;
        Fingerprint = OperatorKey.Fingerprint(Convert.FromBase64String(publicKeyBase64));
    }

    /// <summary>The validated Ed25519 public key, encoded as base64.</summary>
    public string PublicKeyBase64 { get; }

    /// <summary>The SPEC-006 fingerprint of the raw public key.</summary>
    public string Fingerprint { get; }
}
