using Microsoft.Extensions.Logging;
using Ashlar.Certification.Contracts;
using Ashlar.Core.Application.Certification.Models;
using Ashlar.Core.Application.Certification.Ports;
using Ashlar.Core.Domain.Bricks;

namespace Ashlar.Infrastructure.Certification;

/// <summary>
/// DomainBrick registry that only exposes bricks admitted through the certification gate.
/// </summary>
public sealed class CertifiedBrickRegistry : Ashlar.Core.Domain.Execution.IBrickRegistry
{
    private readonly Dictionary<string, DomainBrick> _bricks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ICertificationRecordStore _store;
    private readonly CertificationRecordSigner _signer;
    private readonly ILogger<CertifiedBrickRegistry>? _logger;
    private readonly CertificationVerifyOptions _verifyOptions;

    /// <summary>Initializes a new certified brick registry.</summary>
    /// <param name="store">Record store admissions are persisted to.</param>
    /// <param name="signer">Signer used to verify a record before admitting its brick.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="trustPolicy">
    /// Operator trust configuration supplying the pinned signer set. Defaults to
    /// <see cref="CertificationTrustPolicy.Ambient"/>; with nothing configured this is the
    /// <c>Strict</c> preset exactly as before.
    /// </param>
    public CertifiedBrickRegistry(
        ICertificationRecordStore store,
        CertificationRecordSigner signer,
        ILogger<CertifiedBrickRegistry>? logger = null,
        CertificationTrustPolicy? trustPolicy = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _signer = signer ?? throw new ArgumentNullException(nameof(signer));
        _logger = logger;
        _verifyOptions = (trustPolicy ?? CertificationTrustPolicy.Ambient).Strict;
    }

    /// <summary>Gets brick.</summary>
    public DomainBrick? GetBrick(string id)
    {
        if (!_store.IsAdmitted(id))
            return null;
        return _bricks.TryGetValue(id, out var brick) ? brick : null;
    }

    /// <summary>Gets all bricks.</summary>
    public IReadOnlyList<DomainBrick> GetAllBricks() =>
        _bricks.Values.Where(b => _store.IsAdmitted(b.Id)).ToList();

    internal bool TryAdmit(DomainBrick brick, CertificationRecord record)
    {
        if (!record.Admitted || !record.Signed || !_signer.Verify(record, _verifyOptions))
        {
            _logger?.LogWarning("Rejected ungated brick admission attempt for {BrickId}", brick.Id);
            return false;
        }

        _store.Save(record);
        _bricks[brick.Id] = brick;
        _logger?.LogInformation("Admitted certified brick {BrickId}", brick.Id);
        return true;
    }

    internal bool ContainsUngated(string brickId) =>
        _bricks.ContainsKey(brickId) && !_store.IsAdmitted(brickId);
}
