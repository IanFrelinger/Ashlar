namespace Ashlar.Certification.Contracts;

/// <summary>
/// Portable skill declaration. Every field must agree with a verified v3 certificate;
/// this document alone confers no trust or clearance.
/// </summary>
public sealed record CertifiedSkillManifest
{
    public const string CurrentFormat = "ashlar-skill/v1";
    public string Format { get; init; } = CurrentFormat;
    public string BrickId { get; init; } = string.Empty;
    public string SourceHash { get; init; } = string.Empty;
    public string ArtifactHash { get; init; } = string.Empty;
    public string EntryPoint { get; init; } = string.Empty;
    public string HighestDataLabel { get; init; } = "SystemHigh";

    /// <summary>Rejects declarations outside the canonical, signed level vocabulary.</summary>
    public static void ValidateLabel(string? label)
    {
        if (!CertificationRecordSigning.IsCanonicalDataLevel(label))
            throw new ArgumentException("A skill requires a canonical data level.", nameof(label));
    }

    /// <summary>
    /// Checks manifest-to-certificate binding. The caller must additionally verify the
    /// certificate signature, operator trust and actual source and assembly bytes.
    /// </summary>
    public bool Matches(CertificationRecordData record)
    {
        if (record is null || Format != CurrentFormat
            || record.SchemaVersion != CertificationRecordData.LabelSchemaVersion
            || !CertificationRecordSigning.IsCanonicalDataLevel(HighestDataLabel)
            || !string.Equals(HighestDataLabel, record.HighestDataLabel, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(BrickId) || string.IsNullOrWhiteSpace(SourceHash)
            || string.IsNullOrWhiteSpace(ArtifactHash) || string.IsNullOrWhiteSpace(EntryPoint)
            || !string.Equals(BrickId, record.BrickId, StringComparison.Ordinal)
            || !string.Equals(SourceHash, record.ContentHash, StringComparison.Ordinal))
            return false;

        var artifacts = record.Inputs?.Where(input => input.Kind == CertificationInputKinds.GateEmittedArtifact).ToArray();
        return artifacts is { Length: 1 }
            && string.Equals(ArtifactHash, artifacts[0].Hash, StringComparison.Ordinal)
            && string.Equals(EntryPoint, artifacts[0].Id, StringComparison.Ordinal);
    }
}
