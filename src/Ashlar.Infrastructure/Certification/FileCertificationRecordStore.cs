using System.Text.Json;
using Ashlar.Certification.Contracts;
using Ashlar.Core.Application.Certification.Models;
using Ashlar.Core.Application.Certification.Ports;

namespace Ashlar.Infrastructure.Certification;

/// <summary>
/// File-backed certification record store for CLI / spike workflows.
/// </summary>
public sealed class FileCertificationRecordStore : ICertificationRecordStore
{
    private readonly string _directory;
    private readonly CertificationRecordSigner _signer;
    private readonly CertificationVerifyOptions _verifyOptions;

    /// <summary>Initializes a new file certification record store.</summary>
    /// <param name="directory">Directory holding one JSON record per brick.</param>
    /// <param name="signer">
    /// Signer used to RE-VERIFY every record on load. Defaults to the standard signer.
    /// </param>
    /// <param name="trustPolicy">
    /// Operator trust configuration, which supplies the pinned signer set re-verification applies.
    /// Defaults to <see cref="CertificationTrustPolicy.Ambient"/>, so a host that configured
    /// <c>ASHLAR_CERT_TRUSTED_ED25519_KEYS</c> pins this store without threading anything through.
    /// Resolved HERE rather than per load, so a host whose trust configuration is unusable fails
    /// when the store is built instead of quietly verifying unpinned for the process's lifetime.
    /// </param>
    /// <remarks>
    /// Verification on load is the whole reason this store can be trusted. Records live
    /// as plain JSON on disk, so anything that can write the directory can set
    /// <c>Admitted: true</c>. The flags on a persisted record are therefore a claim,
    /// not evidence — only the HMAC signature is evidence, and it covers the record
    /// including its <c>ContentHash</c>, so editing either the admission flags or the
    /// certified content hash invalidates it.
    /// </remarks>
    public FileCertificationRecordStore(
        string directory,
        CertificationRecordSigner? signer = null,
        CertificationTrustPolicy? trustPolicy = null)
    {
        _directory = directory ?? throw new ArgumentNullException(nameof(directory));
        _signer = signer ?? new CertificationRecordSigner();
        _verifyOptions = (trustPolicy ?? CertificationTrustPolicy.Ambient).Strict;
        Directory.CreateDirectory(_directory);
    }

    /// <summary>
    /// Writes a record by staging it to a private sibling file and moving that into place.
    ///
    /// <para>A direct write truncates first, so a failure partway — full disk, I/O error, process
    /// death — would leave a half-written file where a signed record used to be, DESTROYING the
    /// previous verdict rather than preserving it. Staging then moving means a reader sees either
    /// the old record or the new one, never a shredded one; a crash leaves at most a stray
    /// staging file, which no reader enumerates (<see cref="All"/> matches <c>*.json</c>, and a
    /// <c>.tmp</c> suffix is not that).</para>
    ///
    /// <para>The staging name carries a GUID because this store is a singleton and two callers may
    /// certify the same brick at once. A fixed <c>{brick}.json.tmp</c> would put both writers on
    /// ONE file: the second truncates the first's staged bytes mid-flight and the first then moves
    /// that partial file over the good record — reintroducing exactly the shredding this method
    /// exists to prevent — while a failing writer's cleanup deletes a healthy writer's staged file
    /// out from under it. Each call stages to, and cleans up, only its own file.</para>
    ///
    /// <para>Two limits worth stating plainly. Neither the staged bytes nor the directory entry is
    /// flushed, so this survives PROCESS death, not machine power loss — after a crash the rename
    /// may be durable while the contents are not. On Windows a concurrent replace of the same
    /// destination can throw <see cref="UnauthorizedAccessException"/> (MoveFileEx with
    /// MOVEFILE_REPLACE_EXISTING while another handle still holds the file). The store retries
    /// that replace; the previous verdict stays on disk until a retry lands. A crash or a
    /// persistent ACL denial still leaves the previous verdict intact or absent, never forged,
    /// which is the property that matters here.</para>
    /// </summary>
    public void Save(CertificationRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var path = Path.Combine(_directory, $"{record.BrickId}.json");
        var tmp = $"{path}.{Guid.NewGuid():N}.tmp";
        var json = JsonSerializer.Serialize(record, JsonOptions);
        try
        {
            File.WriteAllText(tmp, json);
            AtomicRecordReplace.IntoPlace(tmp, path);
        }
        catch
        {
            // Only ever this call's own staging file — never a name another writer could hold.
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* a stray staging file is inert */ }
            throw;
        }
    }

    /// <summary>
    /// Loads a record, returning null unless its signature verifies.
    /// </summary>
    /// <remarks>
    /// A record that fails verification is reported as ABSENT rather than as an
    /// untrusted record. Callers treat "no record" as uncertified and refuse the brick,
    /// so a tampered or unsigned file fails closed. Returning it and hoping every caller
    /// re-checks would fail open on the first caller that forgot.
    /// </remarks>
    public CertificationRecord? Get(string brickId)
    {
        var path = Path.Combine(_directory, $"{brickId}.json");
        if (!File.Exists(path))
            return null;

        CertificationRecord? record;
        try
        {
            record = JsonSerializer.Deserialize<CertificationRecord>(File.ReadAllText(path), JsonOptions);
        }
        catch (JsonException)
        {
            // Unparseable on disk is indistinguishable from absent, and both mean
            // "no admission evidence".
            return null;
        }

        if (record is null)
            return null;

        // Strict PLUS whatever the operator pinned. Without the pinning set this check asks only
        // "is this record self-consistent", which any keypair can satisfy.
        return _signer.Verify(record, _verifyOptions) ? record : null;
    }

    /// <summary>Whether admitted.</summary>
    /// <remarks>
    /// Get already rejects anything whose signature does not verify, so the checks here
    /// run only against records whose contents are cryptographically vouched for.
    /// </remarks>
    public bool IsAdmitted(string brickId)
    {
        var record = Get(brickId);
        return record is not null &&
               record.Admitted &&
               record.Signed &&
               string.Equals(record.Status, "PASS", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Every verifiable record in the directory. Routed through <see cref="Get"/> per
    /// file, so tampered or unsigned records are excluded exactly as they are for point
    /// lookups — a ledger scan must never treat unverified JSON as evidence.
    /// </summary>
    public IReadOnlyList<CertificationRecord> All() =>
        Directory.EnumerateFiles(_directory, "*.json")
            .Select(path => Get(Path.GetFileNameWithoutExtension(path)))
            .Where(record => record is not null)
            .Select(record => record!)
            .ToArray();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
}
