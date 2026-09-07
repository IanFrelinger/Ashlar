using Ashlar.Core.Application.Certification.Models;
using Ashlar.Certification.Contracts;

namespace Ashlar.Core.Application.Certification;

/// <summary>Maps domain certification records to portable wire DTOs for external consumers.</summary>
public static class CertificationRecordMapper
{
    /// <summary>To data.</summary>
    public static CertificationRecordData ToData(CertificationRecord record) => new()
    {
        Status = record.Status,
        Stage = record.Stage,
        Admitted = record.Admitted,
        Signed = record.Signed,
        Timestamp = record.Timestamp,
        BrickId = record.BrickId,
        ContentHash = record.ContentHash,
        EscapeRate = record.EscapeRate,
        TotalMutants = record.TotalMutants,
        SurvivingMutants = record.SurvivingMutants,
        KilledMutants = record.KilledMutants,
        SurvivingMutantIds = record.SurvivingMutantIds,
        Signature = record.Signature,
        Reason = record.Reason,
        Gate = record.Gate,
        SchemaVersion = record.SchemaVersion,
        GatesPassed = record.GatesPassed,
        Inputs = record.Inputs,
        Proposer = record.Proposer,
        Attempts = record.Attempts,
        Ed25519Signature = record.Ed25519Signature,
        Ed25519PublicKey = record.Ed25519PublicKey
    };
}
