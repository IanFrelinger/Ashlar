using System.Text.Json;
using Ashlar.Certification.Contracts;
using Ashlar.Core.Application.Certification.Models;

namespace Ashlar.Infrastructure.Certification;

/// <summary>
/// Writes the portable record and the gate-emitted assembly side by side. Consumers load
/// the assembly the certifier compiled; they do not recompile author source.
/// </summary>
public static class CertifiedArtifactExporter
{
    /// <summary>Default file name for the emitted assembly next to a record.</summary>
    public const string ArtifactFileName = "gate-emitted-brick.dll";

    /// <summary>File name of the certificate-bound skill declaration.</summary>
    public const string ManifestFileName = "skill-manifest.json";

    /// <summary>Builds a declaration bound to the certifier's exact source, assembly and entry point.</summary>
    public static CertifiedSkillManifest CreateManifest(CertificationRecordData record, GateEmittedArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(artifact);
        var manifest = new CertifiedSkillManifest
        {
            BrickId = record.BrickId ?? string.Empty,
            SourceHash = artifact.SourceSha256,
            ArtifactHash = artifact.AssemblySha256,
            EntryPoint = artifact.BrickTypeName,
            HighestDataLabel = record.HighestDataLabel ?? "SystemHigh"
        };
        if (!manifest.Matches(record)
            || !string.Equals(artifact.AssemblySha256, BrickContentHasher.ComputeSha256(artifact.AssemblyBytes), StringComparison.Ordinal))
            throw new InvalidOperationException("Skill manifest does not match the certified artifact.");
        return manifest;
    }

    /// <summary>Writes <paramref name="record"/> and <paramref name="artifact"/> under <paramref name="recordPath"/>.</summary>
    public static async Task WriteAsync(
        string recordPath,
        CertificationRecordData record,
        GateEmittedArtifact artifact,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(artifact);

        var manifest = record.SchemaVersion == CertificationRecordData.LabelSchemaVersion
            ? CreateManifest(record, artifact) : null;
        var directory = Path.GetDirectoryName(Path.GetFullPath(recordPath))
            ?? throw new InvalidOperationException("record path has no directory");
        Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(
            record,
            new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        await File.WriteAllTextAsync(recordPath, json, cancellationToken).ConfigureAwait(false);
        await File.WriteAllBytesAsync(
            Path.Combine(directory, ArtifactFileName),
            artifact.AssemblyBytes,
            cancellationToken).ConfigureAwait(false);
        var manifestPath = Path.Combine(directory, ManifestFileName);
        if (manifest is not null)
            await File.WriteAllTextAsync(manifestPath,
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions
                { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }),
                cancellationToken).ConfigureAwait(false);
        else if (File.Exists(manifestPath))
            File.Delete(manifestPath); // A legacy export must never retain a prior v3 declaration.
    }
}
