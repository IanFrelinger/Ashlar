using Ashlar.Core.Application.Certification.Models;
using Ashlar.Certification.Contracts;

namespace Ashlar.Infrastructure.Certification;

// DEPRECATED: CertificationRecordMapper has been moved to Ashlar.Core.Application.Certification
// This file provides backward compatibility and will be removed in a future release.

/// <summary>
/// Maps domain certification records to portable wire DTOs for external consumers.
/// DEPRECATED: Use Ashlar.Core.Application.Certification.CertificationRecordMapper instead.
/// </summary>
[Obsolete("CertificationRecordMapper has moved to Ashlar.Core.Application.Certification. Update your using statements.")]
public static class CertificationRecordMapper
{
    /// <summary>To data. DEPRECATED: Use Ashlar.Core.Application.Certification.CertificationRecordMapper.ToData instead.</summary>
    [Obsolete("Use Ashlar.Core.Application.Certification.CertificationRecordMapper.ToData instead.")]
    public static CertificationRecordData ToData(CertificationRecord record) =>
        Core.Application.Certification.CertificationRecordMapper.ToData(record);
}
