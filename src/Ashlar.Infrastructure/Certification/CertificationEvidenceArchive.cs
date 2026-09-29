using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Ashlar.Certification.Contracts;
using Ashlar.Core.Application.Certification.Models;

namespace Ashlar.Infrastructure.Certification;

/// <summary>
/// One certification record's persist-and-re-verify verdict: what was written, where, and whether
/// the BYTES ON DISK still verify under the archive's configured strictness.
/// </summary>
/// <remarks>
/// <para><b>This shape is parsed by <c>scripts/dogfood-continuous-proof.sh</c></b> (its
/// <c>classify_sweep_evidence</c> / <c>sweep_evidence_citation</c> functions read the camelCase
/// JSON this serializes to). The field names are frozen by
/// <c>CertificationEvidenceArchiveTests.PersistAndReverify_WritesASidecarWithTheFieldNamesTheSweepScriptParses</c>;
/// renaming one without updating that script makes the script's verdict permanently GAP, which is
/// the safe direction but is still a silent break of the contract.</para>
///
/// <para><see cref="PinningEnabled"/> and <see cref="UsesDevHmacKey"/> are on the result so that a
/// ledger row built from it structurally cannot claim pinned or non-dev-key evidence it does not
/// have.</para>
/// </remarks>
/// <param name="Verified">
/// Whether the record RE-READ FROM DISK verified. False is a reported verdict, never an exception
/// and never an admission: see <see cref="CertificationEvidenceArchive"/>.
/// </param>
/// <param name="BrickId">The brick the record is about.</param>
/// <param name="RecordPath">Absolute path of the record file that was written and read back.</param>
/// <param name="RecordSha256">
/// Lowercase hex SHA-256 over the record FILE's bytes as they were read back, so a row's citation
/// names a specific artefact. Null when the file could not be read at all.
/// </param>
/// <param name="SignerFingerprint">
/// SPEC-006 §3 fingerprint of the Ed25519 public key the re-read record carries, or null when it
/// carries none (or one that is not decodable). Absent is never guessed at.
/// </param>
/// <param name="PinningEnabled">
/// Whether the strictness applied pinned a signer set at all. FALSE means the record was verified
/// against the key it itself carries, so a record signed with any other keypair would have
/// verified identically.
/// </param>
/// <param name="UsesDevHmacKey">
/// Whether the signer that minted and re-verified the HMAC half is on the COMMITTED development
/// key, i.e. whether anyone with the source can forge a record that verifies here.
/// </param>
/// <param name="FailureCode">The verifier's own failure code when <see cref="Verified"/> is false; null otherwise.</param>
/// <param name="FailureReason">The verifier's own reason when <see cref="Verified"/> is false; null otherwise.</param>
/// <param name="VerifiedAtUtc">When the re-verification ran.</param>
public sealed record CertificationEvidenceResult(
    bool Verified,
    string BrickId,
    string RecordPath,
    string? RecordSha256,
    string? SignerFingerprint,
    bool PinningEnabled,
    bool UsesDevHmacKey,
    string? FailureCode,
    string? FailureReason,
    DateTimeOffset VerifiedAtUtc);

/// <summary>
/// Writes a certification record to a run-scoped evidence directory, reads the FILE'S BYTES back,
/// and re-verifies the deserialized record under the operator's configured strictness — so a
/// ledger row can cite a record file and a signer fingerprint instead of a console line.
///
/// <para><b>This is NOT an admission store, and it must never be registered as one.</b> A
/// persisted record carries <c>Admitted: true</c>, <c>Signed: true</c>, <c>Status: PASS</c>, so
/// registering this directory's <see cref="FileCertificationRecordStore"/> as
/// <c>ICertificationRecordStore</c> would make <c>IsAdmitted</c> return true for a brick the
/// operator is deliberately HOLDING: persisting evidence would manufacture admission. That is the
/// most dangerous mis-wiring available here, it is the convenient one, and
/// <c>EvidenceArchiveCompositionConventionTests.TheEvidenceArchiveIsNeverTheAdmissionRecordStore</c>
/// is what notices if someone does it.</para>
///
/// <para><b>What this proves, stated narrowly.</b> That a record's bytes survived a round trip to
/// disk and still verify. It says nothing whatever about the ADMISSION path: the caller on the
/// autonomy loop invokes this ABOVE the operator hold precisely so that reaching a Strict
/// verification never requires hot-swapping model-proposed code into the host process. A verdict
/// from here is evidence about record persistence and about nothing else.</para>
///
/// <para><b>A failed re-verification is reported, never thrown and never escalated.</b> The result
/// carries the verifier's own failure code; the caller's outcome is unchanged. The one thing that
/// does throw is a root directory that cannot be created — a configured archive that cannot exist
/// is a boot fault, not a silent skip.</para>
/// </summary>
public sealed class CertificationEvidenceArchive
{
    /// <summary>Sidecar suffix. Deliberately NOT <c>.json</c> inside <c>records/</c>; see the constructor.</summary>
    private const string SidecarSuffix = ".evidence.json";

    private const string LaneDisagreementReason =
        "The two verification lanes disagree about this record's HMAC signature. The host's own "
        + "signer and the consumer verifier reach different keys — the usual cause is a host that "
        + "supplied an EXPLICIT HMAC key (the only way to hold a real one, per SPEC-006 S-4), which "
        + "the consumer's ladder (explicit key, then ASHLAR_CERT_DEV_HMAC_KEY, then the committed "
        + "constant) cannot reach, because nothing extracts a key from the signer. Refusing rather "
        + "than reporting a pass from a verifier that could not check the symmetric half, and "
        + "rather than blaming the signature for a key-routing fault.";

    private static readonly JsonSerializerOptions RecordJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly string _rootDirectory;
    private readonly string _recordsDirectory;
    private readonly FileCertificationRecordStore _store;
    private readonly CertificationRecordSigner _signer;
    private readonly CertificationVerifyOptions _verifyOptions;
    private readonly ILogger<CertificationEvidenceArchive>? _logger;

    /// <summary>Initializes an evidence archive rooted at <paramref name="rootDirectory"/>.</summary>
    /// <param name="rootDirectory">
    /// Directory the run's evidence lands in. Records go to <c>&lt;root&gt;/records/</c> and each
    /// verdict sidecar to <c>&lt;root&gt;/&lt;brickId&gt;.evidence.json</c> — the sidecar is
    /// deliberately OUTSIDE <c>records/</c>, because <see cref="FileCertificationRecordStore.All"/>
    /// enumerates <c>*.json</c> there and would otherwise try to read a verdict as a record.
    /// </param>
    /// <param name="signer">
    /// Signer that re-verifies the HMAC half. Defaults to the standard signer. Pass the container's
    /// singleton so the archive verifies under the same key that signed.
    /// </param>
    /// <param name="trustPolicy">
    /// Operator trust configuration, which supplies the pinned signer set re-verification applies.
    /// Defaults to <see cref="CertificationTrustPolicy.Ambient"/>, so a host that configured
    /// <c>ASHLAR_CERT_TRUSTED_ED25519_KEYS</c> pins this archive without threading anything
    /// through — the same seam <see cref="FileCertificationRecordStore"/> uses, and the reason
    /// turning pinning on later needs no second edit here. Resolved HERE rather than per call, so a
    /// host whose trust configuration is unusable fails when the archive is built instead of
    /// quietly verifying unpinned for the process's lifetime.
    /// </param>
    /// <param name="logger">Optional logger.</param>
    public CertificationEvidenceArchive(
        string rootDirectory,
        CertificationRecordSigner? signer = null,
        CertificationTrustPolicy? trustPolicy = null,
        ILogger<CertificationEvidenceArchive>? logger = null)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
            throw new ArgumentException("An evidence archive needs a root directory.", nameof(rootDirectory));

        _rootDirectory = rootDirectory;
        _signer = signer ?? new CertificationRecordSigner();
        // Strict PLUS whatever the operator pinned. Without the pinning set this asks only "is this
        // record self-consistent", which any keypair can satisfy — so the verdict says which of the
        // two it was, in UsesDevHmacKey and PinningEnabled, rather than letting a row imply the
        // stronger one.
        _verifyOptions = (trustPolicy ?? CertificationTrustPolicy.Ambient).Strict;
        _logger = logger;

        // An IOException propagates: a configured archive that cannot be created is a boot fault.
        Directory.CreateDirectory(_rootDirectory);
        _recordsDirectory = Path.Combine(_rootDirectory, "records");
        _store = new FileCertificationRecordStore(_recordsDirectory, _signer, trustPolicy);
    }

    /// <summary>Directory the record files land in.</summary>
    public string RecordsDirectory => _recordsDirectory;

    /// <summary>
    /// Persists <paramref name="record"/>, then re-verifies it by reading it back off disk.
    ///
    /// <para>This is a two-step composition and nothing else: <see cref="Persist"/> writes the
    /// file, <see cref="ReverifyFromDisk"/> produces the verdict. The split is the design.
    /// <see cref="ReverifyFromDisk"/> is handed a brick ID and no record, so it has no in-memory
    /// instance available to verify instead of the file — the implementation that would make the
    /// round-trip claim a lie is not representable, rather than merely tested against. (It is
    /// also not testable by tampering here: <see cref="Persist"/> immediately precedes the read,
    /// so the two are identical by construction and no assertion inside one call could tell them
    /// apart. The tamper facts therefore target <see cref="ReverifyFromDisk"/> directly.)</para>
    /// </summary>
    /// <param name="record">The record the gate minted and signed.</param>
    /// <param name="brickSource">The candidate's own source text, for the content binding.</param>
    /// <param name="gateEmittedAssembly">The gate-emitted assembly bytes, when the caller has them.</param>
    public CertificationEvidenceResult PersistAndReverify(
        CertificationRecord record,
        string brickSource,
        byte[]? gateEmittedAssembly)
    {
        ArgumentNullException.ThrowIfNull(record);

        Persist(record);
        return ReverifyFromDisk(record.BrickId, brickSource, gateEmittedAssembly);
    }

    /// <summary>
    /// Writes a record into the archive's <c>records/</c> directory and returns the path it landed
    /// at. Routed through <see cref="FileCertificationRecordStore"/>, so the archive inherits its
    /// stage-then-replace write rather than restating it.
    /// </summary>
    /// <param name="record">The record to persist.</param>
    public string Persist(CertificationRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        _store.Save(record);
        return FileCertificationRecordStore.RecordPathFor(_recordsDirectory, record.BrickId);
    }

    /// <summary>
    /// Reads the record file for <paramref name="brickId"/>, hashes those bytes, deserializes them,
    /// and verifies the RESULT of that deserialization under the archive's configured strictness.
    ///
    /// <para>There is no record parameter, on purpose. The verdict can only come from the file:
    /// a lossy round trip (a field dropped by serialization, a naming-policy mismatch between
    /// writer and reader), a write that landed at a path the read does not look at, or any later
    /// edit to the bytes all surface here as a named refusal instead of a pass.</para>
    ///
    /// <para>Two verifiers run, and both must agree.
    /// <see cref="CertificationRecordSigner.Verify(CertificationRecord, CertificationVerifyOptions?)"/>
    /// is authoritative for the HMAC half because it holds the key internally (nothing here
    /// extracts it), and <see cref="CertificationTrustVerifier"/> supplies the NAMED failure code
    /// plus the bindings a signature alone does not give: the certified content hash against the
    /// candidate's own source, and the gate-emitted assembly hash against the bytes that were
    /// emitted. A disagreement is reported as <c>hmac-key-lane-disagreement</c> rather than
    /// resolved in favour of either: reporting "verified" from a verifier that could not check the
    /// symmetric half is the shape this repository keeps rejecting.</para>
    /// </summary>
    /// <param name="brickId">The brick whose record file to read.</param>
    /// <param name="brickSource">
    /// The candidate's own source text, for the content binding. A record whose
    /// <c>ContentHash</c> does not cover this source is refused as <c>content-hash-mismatch</c>.
    /// </param>
    /// <param name="gateEmittedAssembly">
    /// The gate-emitted assembly bytes, when the caller has them: supplying them adds the
    /// judged-equals-shipped binding. Null or empty verifies source binding only — the
    /// <c>gate-emitted-artifact</c> INPUT is still required whenever the strictness requires it, so
    /// omitting the bytes weakens the binding without weakening the requirement.
    /// </param>
    public CertificationEvidenceResult ReverifyFromDisk(
        string brickId,
        string brickSource,
        byte[]? gateEmittedAssembly)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(brickId);

        var path = FileCertificationRecordStore.RecordPathFor(_recordsDirectory, brickId);

        if (!File.Exists(path))
        {
            return Complete(brickId, path, null, null, "record-file-missing",
                $"No record file exists at {path}. Either nothing was persisted for this brick, or "
                + "the write landed somewhere the read does not look (see "
                + "FileCertificationRecordStore.RecordPathFor, which is the one declaration both use).");
        }

        var bytes = File.ReadAllBytes(path);
        var recordSha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        CertificationRecord? reRead;
        try
        {
            reRead = JsonSerializer.Deserialize<CertificationRecord>(bytes, RecordJsonOptions);
        }
        catch (JsonException ex)
        {
            return Complete(brickId, path, recordSha256, null, "record-file-unparseable",
                $"The record file at {path} is not parseable as a certification record: {ex.Message}");
        }

        if (reRead is null)
        {
            return Complete(brickId, path, recordSha256, null, "record-file-unparseable",
                $"The record file at {path} deserialized to null.");
        }

        var fingerprint = CertificationSignerFingerprint.Of(reRead.Ed25519PublicKey);
        var signerAccepts = _signer.Verify(reRead, _verifyOptions);

        var data = CertificationRecordMapper.ToData(reRead);
        var consumer = gateEmittedAssembly is { Length: > 0 }
            ? CertificationTrustVerifier.Verify(data, brickSource, gateEmittedAssembly, hmacKey: null, _verifyOptions)
            : CertificationTrustVerifier.Verify(data, brickSource, hmacKey: null, options: _verifyOptions);

        // The two lanes must agree, and WHICH way they disagree decides what is reported.
        //
        // The consumer verifier checks bindings the signer does not evaluate at all (the content
        // hash against the candidate source, the gate-emitted artifact hash, the required inputs),
        // so the signer accepting while the consumer refuses one of those is expected, not a
        // disagreement — the consumer's named code is the answer.
        //
        // `signature-invalid` is the exception, because that is the one verdict both lanes compute
        // and they can only differ on it by holding different HMAC keys. A host that supplied an
        // EXPLICIT key (the only way to hold a real one, per SPEC-006 S-4) reaches exactly this:
        // the signer holds it and accepts, while CertificationTrustVerifier's own ladder — explicit
        // key, then ASHLAR_CERT_DEV_HMAC_KEY, then the committed constant — cannot reach it,
        // because nothing extracts a key from the signer and nothing here will add an accessor.
        // Reporting that as `signature-invalid` would be a lie about the signature; reporting it as
        // verified would be a pass from a verifier that could not check the symmetric half. So it
        // gets its own code.
        if (!consumer.Trusted)
        {
            if (signerAccepts && string.Equals(consumer.FailureCode, "signature-invalid", StringComparison.Ordinal))
                return Complete(brickId, path, recordSha256, fingerprint, "hmac-key-lane-disagreement", LaneDisagreementReason);

            return Complete(brickId, path, recordSha256, fingerprint, consumer.FailureCode, consumer.Reason);
        }

        if (!signerAccepts)
            return Complete(brickId, path, recordSha256, fingerprint, "hmac-key-lane-disagreement", LaneDisagreementReason);

        return Complete(brickId, path, recordSha256, fingerprint, null, null);
    }

    private CertificationEvidenceResult Complete(
        string brickId,
        string recordPath,
        string? recordSha256,
        string? signerFingerprint,
        string? failureCode,
        string? failureReason)
    {
        var result = new CertificationEvidenceResult(
            Verified: failureCode is null,
            BrickId: brickId,
            RecordPath: recordPath,
            RecordSha256: recordSha256,
            SignerFingerprint: signerFingerprint,
            PinningEnabled: _verifyOptions.PinningEnabled,
            UsesDevHmacKey: _signer.UsesDevKey,
            FailureCode: failureCode,
            FailureReason: failureReason,
            VerifiedAtUtc: DateTimeOffset.UtcNow);

        WriteSidecar(result);
        return result;
    }

    /// <summary>
    /// Writes the verdict beside the records directory. A plain write, not a stage-and-replace: a
    /// torn sidecar fails the ledger classifier's own checks and becomes a GAP, which is the safe
    /// direction, whereas the record file it points at is what a stage-and-replace exists to
    /// protect and already gets one.
    /// </summary>
    private void WriteSidecar(CertificationEvidenceResult result)
    {
        var sidecarPath = Path.Combine(_rootDirectory, $"{result.BrickId}{SidecarSuffix}");
        try
        {
            File.WriteAllText(sidecarPath, JsonSerializer.Serialize(result, RecordJsonOptions));
        }
        catch (IOException ex)
        {
            // The verdict is still returned to the caller, so nothing silently becomes a pass; the
            // ledger classifier sees an absent sidecar, which it reads as a GAP.
            _logger?.LogWarning(
                ex, "Could not write the certification evidence sidecar for {BrickId} to {SidecarPath}",
                result.BrickId, sidecarPath);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger?.LogWarning(
                ex, "Could not write the certification evidence sidecar for {BrickId} to {SidecarPath}",
                result.BrickId, sidecarPath);
        }
    }
}
