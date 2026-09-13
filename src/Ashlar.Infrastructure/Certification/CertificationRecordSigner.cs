using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Ashlar.Core.Application.Certification.Models;
using Ashlar.Certification.Contracts;

namespace Ashlar.Infrastructure.Certification;

/// <summary>
/// Dev HMAC signer for certification records, dual-writing an Ed25519 signature
/// when a private key is configured (explicitly or via
/// <see cref="CertificationRecordEd25519.PrivateKeyEnvVar"/>).
///
/// <para><b>Key resolution is loud.</b> With no explicit key and no
/// <c>ASHLAR_CERT_DEV_HMAC_KEY</c>, every record is signed and verified with the COMMITTED
/// <see cref="CertificationRecordSigning.DefaultDevKey"/>, which anyone with the source can
/// reproduce — a certificate under it proves integrity against accident, not against an
/// adversary. The constructor logs a warning in that state (when given a logger) and
/// exposes it as <see cref="UsesDevKey"/>, so a host cannot run production admissions on
/// the dev key without the fact being on the record.</para>
/// </summary>
public sealed class CertificationRecordSigner
{
    /// <summary>default dev key constant.</summary>
    public const string DefaultDevKey = CertificationRecordSigning.DefaultDevKey;

    private readonly string? _hmacKey;
    private readonly byte[]? _ed25519PrivateKey;

    /// <summary>Initializes a new certification record signer.</summary>
    /// <param name="hmacKey">Explicit HMAC key; null falls back to <c>ASHLAR_CERT_DEV_HMAC_KEY</c>, then the committed dev key.</param>
    /// <param name="ed25519PrivateKeyBase64">Optional Ed25519 private key for the dual-write signature.</param>
    /// <param name="logger">Optional logger; receives the dev-key warning when the committed key is in effect.</param>
    public CertificationRecordSigner(
        string? hmacKey = null,
        string? ed25519PrivateKeyBase64 = null,
        ILogger<CertificationRecordSigner>? logger = null)
    {
        _hmacKey = string.IsNullOrWhiteSpace(hmacKey)
            ? Environment.GetEnvironmentVariable(CertificationRecordSigning.HmacKeyEnvVar)
            : hmacKey;
        _ed25519PrivateKey = CertificationRecordEd25519.ResolvePrivateKey(ed25519PrivateKeyBase64);
        UsesDevKey = CertificationRecordSigning.UsesDevKey(_hmacKey);
        if (UsesDevKey)
            WarnDevKey(logger, nameof(CertificationRecordSigner));
    }

    /// <summary>
    /// True when records are signed with the committed development key (no explicit key and
    /// no <c>ASHLAR_CERT_DEV_HMAC_KEY</c>): every signature this instance mints or accepts is
    /// forgeable by anyone with the source.
    /// </summary>
    public bool UsesDevKey { get; }

    /// <summary>Sign.</summary>
    public string Sign(CertificationRecord record) =>
        CertificationRecordSigning.Sign(CertificationRecordMapper.ToData(record), _hmacKey);

    /// <summary>
    /// Attaches signatures to a record: always the HMAC signature, plus the Ed25519
    /// signature and public key when a private key is configured. Both signatures
    /// cover the same canonical payload, including the public key.
    /// </summary>
    public CertificationRecord SignRecord(CertificationRecord record)
    {
        if (_ed25519PrivateKey is not null)
            record = record with { Ed25519PublicKey = CertificationRecordEd25519.DerivePublicKeyBase64(_ed25519PrivateKey) };

        var data = CertificationRecordMapper.ToData(record);
        var hmacSignature = CertificationRecordSigning.Sign(data, _hmacKey);
        var ed25519Signature = _ed25519PrivateKey is not null
            ? CertificationRecordEd25519.Sign(data, _ed25519PrivateKey)
            : null;
        return record with { Signature = hmacSignature, Ed25519Signature = ed25519Signature };
    }

    /// <summary>
    /// Verify. Enforces the Ed25519 signature whenever the record carries one, and applies
    /// any additional strictness in <paramref name="options"/> (SPEC-006 S-1 and S-5).
    /// </summary>
    /// <param name="record">Record to verify.</param>
    /// <param name="options">
    /// Strictness to apply. Null uses <see cref="CertificationVerifyOptions.Default"/>, which
    /// is now fail-closed (Ed25519 required, trust-loop schema floor). Use
    /// <see cref="CertificationVerifyOptions.Legacy"/> for pre-trust-loop records.
    /// </param>
    /// <remarks>
    /// This is the SECOND verification tier, and the busier one: it gates
    /// <see cref="FileCertificationRecordStore"/>, <c>CertifiedBrickRegistry</c>,
    /// <c>CertifiedCompositionRegistry</c>, <c>CompositionConstituentChecker</c> and the
    /// adaptation wiring. Strictness has to be available here, not only on
    /// <see cref="CertificationTrustVerifier"/>, or the store whose whole security claim is
    /// re-verification on load would keep admitting records a strict host would refuse.
    /// </remarks>
    public bool Verify(CertificationRecord record, CertificationVerifyOptions? options = null)
    {
        var strictness = options ?? CertificationVerifyOptions.Default;
        var data = CertificationRecordMapper.ToData(record);

        // Floor first: the schema version selects which canonical payload the signature
        // covers, so a downgraded record can carry a valid HMAC over a payload that omits
        // Gate, GatesPassed, Inputs, Proposer, Attempts and Ed25519PublicKey.
        if ((data.SchemaVersion ?? 0) < strictness.MinimumSchemaVersion)
            return false;

        // A version that selects no payload lane cannot be verified. VerifySignature would refuse
        // it anyway (BuildPayload throws, which it turns into false), but the reason is stated
        // here so this tier reads the same as CertificationTrustVerifier, which reports it as
        // schema-version-unknown. This tier returns bool, so that code is only available there.
        if (!CertificationRecordSigning.IsKnownSchemaVersion(data.SchemaVersion))
            return false;

        if (!CertificationRecordSigning.VerifySignature(data, _hmacKey))
            return false;

        if (string.IsNullOrWhiteSpace(data.Ed25519Signature))
        {
            // Absent is only acceptable when nothing stricter was asked for. Presence is
            // controlled by the record's own bytes, so an attacker strips rather than forges.
            return !strictness.RequireEd25519Signature && !strictness.PinningEnabled;
        }

        if (!CertificationRecordEd25519.VerifySignature(data))
            return false;

        // The signature is checked against the key the RECORD carries, so without pinning a
        // record signed with an attacker's own keypair is self-consistent and passes.
        return !strictness.PinningEnabled
            || strictness.TrustedEd25519PublicKeys!.Contains(data.Ed25519PublicKey!, StringComparer.Ordinal);
    }

    /// <summary>
    /// Computes this signer's Base64 HMAC-SHA256 over an ALREADY-CANONICAL payload, for the
    /// composition lane.
    ///
    /// <para><b>Internal, and capability-shaped on purpose.</b> The composition signer needs this
    /// signer's key; the only safe way to give it one is never to give it the key. Nothing here
    /// returns, logs or retains key material — the byte array is a local and dies with the call —
    /// so there is no accessor for a failure message, a debugger view or a destructuring logger to
    /// print. That matters concretely: this assembly's internals are visible to
    /// <c>Ashlar.Tests.Infrastructure</c> (<c>Ashlar.Infrastructure.csproj</c>), whose
    /// certification suite alone is dozens of files. Do not add a key accessor "for symmetry".</para>
    ///
    /// <para>The key is resolved PER CALL, by the same ladder <see cref="Sign"/> uses: explicit
    /// key, then <c>ASHLAR_CERT_DEV_HMAC_KEY</c>, then the committed constant. That is the brick
    /// lane's own late-binding behaviour, so the two lanes cannot end up under different keys. The
    /// ladder is spelled here rather than called because the contracts helper that owns it is
    /// private to a packed, multi-target NuGet assembly, and widening a method that RETURNS the key
    /// onto that surface is the one change this design exists to avoid.</para>
    ///
    /// <para><c>CompositionSignerKeyPathConventionTests</c> freezes this member to one declaration
    /// and one call site. That freeze is a tripwire, not a proof.</para>
    /// </summary>
    /// <param name="canonicalPayload">
    /// The exact text to be MACed, already canonicalised by the caller. This type does not know
    /// the composition payload's shape and must not learn it.
    /// </param>
    internal string ComputeCanonicalHmac(string canonicalPayload)
    {
        var key = string.IsNullOrWhiteSpace(_hmacKey)
            ? Environment.GetEnvironmentVariable(CertificationRecordSigning.HmacKeyEnvVar) ?? DefaultDevKey
            : _hmacKey!;

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(key));
        return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(canonicalPayload)));
    }

    /// <summary>
    /// The one dev-key warning, shared with the composition signer so both surfaces say the
    /// same thing. Nothing about the key itself is logged.
    /// </summary>
    internal static void WarnDevKey(ILogger? logger, string signerName) =>
        logger?.LogWarning(
            "{Signer} is signing and verifying certification records with the COMMITTED development HMAC key "
            + "(no explicit key, {EnvVar} unset). Anyone with the source can forge a record that verifies here; "
            + "these certificates prove integrity against accident, not against an adversary. Set {EnvVar} to a "
            + "secret before admitting anything you would not admit unsigned.",
            signerName, CertificationRecordSigning.HmacKeyEnvVar, CertificationRecordSigning.HmacKeyEnvVar);
}
