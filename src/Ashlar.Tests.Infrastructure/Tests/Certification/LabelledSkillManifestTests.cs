using System.Text.Json;
using Ashlar.Certification.Contracts;
using Ashlar.Core.Application.Certification.Models;
using Ashlar.Infrastructure.Certification;
using FluentAssertions;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

[Trait("Category", "Certification")]
public sealed class LabelledSkillManifestTests
{
    private static GateEmittedArtifact Artifact() => new()
    {
        AssemblyBytes = [1, 2, 3],
        AssemblySha256 = BrickContentHasher.ComputeSha256(new byte[] { 1, 2, 3 }),
        SourceSha256 = BrickContentHasher.ComputeSha256("source"),
        BrickTypeName = "Example.Skill",
        CompileOptionsBlob = "fixture",
        CompilerVersion = "fixture"
    };

    private static CertificationRecordData Record(GateEmittedArtifact artifact) => new()
    {
        Status = "PASS", Stage = "S0-S2", Admitted = true, Signed = false,
        Timestamp = DateTimeOffset.UnixEpoch,
        BrickId = "skill",
        ContentHash = artifact.SourceSha256,
        SchemaVersion = CertificationRecordData.LabelSchemaVersion,
        HighestDataLabel = "Secret",
        Inputs = [new CertificationInput
        {
            Kind = CertificationInputKinds.GateEmittedArtifact,
            Id = artifact.BrickTypeName,
            Hash = artifact.AssemblySha256
        }]
    };

    [Theory]
    [InlineData("format")]
    [InlineData("brick")]
    [InlineData("source")]
    [InlineData("artifact")]
    [InlineData("entry")]
    [InlineData("label")]
    public void Each_manifest_field_is_bound_to_the_certificate(string field)
    {
        var artifact = Artifact();
        var record = Record(artifact);
        var manifest = CertifiedArtifactExporter.CreateManifest(record, artifact);
        manifest.Matches(record).Should().BeTrue();
        var changed = field switch
        {
            "format" => manifest with { Format = "unknown" },
            "brick" => manifest with { BrickId = "other" },
            "source" => manifest with { SourceHash = "other" },
            "artifact" => manifest with { ArtifactHash = "other" },
            "entry" => manifest with { EntryPoint = "Other.Skill" },
            _ => manifest with { HighestDataLabel = "Public" }
        };
        changed.Matches(record).Should().BeFalse();
    }

    [Fact]
    public void Legacy_records_and_ambiguous_artifact_inputs_cannot_authorize_a_declaration()
    {
        var artifact = Artifact();
        var record = Record(artifact);
        var manifest = CertifiedArtifactExporter.CreateManifest(record, artifact);
        manifest.Matches(record with { SchemaVersion = 2 }).Should().BeFalse();
        manifest.Matches(record with { Inputs = [] }).Should().BeFalse();
        manifest.Matches(record with { Inputs = [record.Inputs[0], record.Inputs[0]] }).Should().BeFalse();
        manifest.Matches(record with { HighestDataLabel = null }).Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("secret")]
    [InlineData(" Secret ")]
    [InlineData("Unknown")]
    public void Malformed_declarations_are_refused(string? label)
    {
        var action = () => CertifiedSkillManifest.ValidateLabel(label);
        action.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Export_checks_actual_bytes_and_source_binding_before_writing()
    {
        var artifact = Artifact();
        var record = Record(artifact);
        var changedBytes = () => CertifiedArtifactExporter.CreateManifest(record, artifact with { AssemblyBytes = [4] });
        changedBytes.Should().Throw<InvalidOperationException>();
        var changedSource = () => CertifiedArtifactExporter.CreateManifest(record, artifact with { SourceSha256 = "other" });
        changedSource.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task Export_writes_the_bound_label_and_legacy_export_removes_stale_declarations()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ashlar-manifest-" + Guid.NewGuid().ToString("N"));
        var artifact = Artifact();
        var record = Record(artifact);
        try
        {
            var path = Path.Combine(directory, "record.json");
            await CertifiedArtifactExporter.WriteAsync(path, record, artifact);
            var manifestPath = Path.Combine(directory, CertifiedArtifactExporter.ManifestFileName);
            var manifest = JsonSerializer.Deserialize<CertifiedSkillManifest>(await File.ReadAllTextAsync(manifestPath),
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            manifest!.HighestDataLabel.Should().Be("Secret");
            manifest.Matches(record).Should().BeTrue();
            (await File.ReadAllBytesAsync(Path.Combine(directory, CertifiedArtifactExporter.ArtifactFileName)))
                .Should().Equal(artifact.AssemblyBytes);
            await CertifiedArtifactExporter.WriteAsync(path, record with { SchemaVersion = 2, HighestDataLabel = null }, artifact);
            File.Exists(manifestPath).Should().BeFalse();
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }
}
