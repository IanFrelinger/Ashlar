using System.Text.Json;
using Ashlar.Core.Application.Certification.Models;
using Ashlar.Core.Application.Certification.Ports;
using Ashlar.Core.Domain.Bricks;
using Ashlar.Core.Domain.Execution;

namespace Ashlar.Infrastructure.Certification;

/// <summary>
/// Loads brick projects from disk into certification requests.
/// The certifier compiles the source itself (A8): author MSBuild is fenced, never executed,
/// type discovery is metadata-only, and the emitted bytes travel with the request.
/// </summary>
public static class BrickCertificationProjectLoader
{
    /// <summary>Load asynchronously.</summary>
    public static async Task<CertificationRequest> LoadAsync(
        string brickProjectDirectory,
        string witnessSpecPath,
        CancellationToken cancellationToken = default)
    {
        var projectDir = Path.GetFullPath(brickProjectDirectory);
        var csproj = Directory.GetFiles(projectDir, "*.csproj").FirstOrDefault()
            ?? throw new FileNotFoundException($"No .csproj in {projectDir}");
        var sourceFiles = Directory.GetFiles(projectDir, "*.cs")
            .Where(f => !f.EndsWith(".AssemblyInfo.cs", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (sourceFiles.Length == 0)
            throw new FileNotFoundException($"No .cs source in {projectDir}");
        if (sourceFiles.Length > 1)
        {
            throw new InvalidOperationException(
                "single-source fence: the certifier compiles exactly one author .cs file. "
                + $"Found {sourceFiles.Length} ({string.Join(", ", sourceFiles.Select(Path.GetFileName))}). "
                + "Extra files are neither judged nor shipped, so they are refused.");
        }

        var sourceFile = sourceFiles[0];

        BuildSurfaceFence.Inspect(projectDir, csproj);
        var sourceCode = await StrictUtf8SourceDecoder.ReadFileAsync(sourceFile, cancellationToken)
            .ConfigureAwait(false);
        var witnessJson = await StrictUtf8SourceDecoder.ReadFileAsync(witnessSpecPath, cancellationToken)
            .ConfigureAwait(false);
        var witnessDto = JsonSerializer.Deserialize<WitnessSpecDto>(witnessJson, JsonOptions)
            ?? throw new InvalidOperationException("Witness spec is empty");

        var references = DefaultCompilationReferences();
        var artifact = GateEmittedArtifactCompiler.Compile(sourceCode, references);
        IlImportFence.Inspect(artifact.AssemblyBytes);
        var brick = CertifiedBrickActivator.Activate(artifact);

        var witness = new WitnessSpec(
            witnessDto.BrickId ?? brick.Id,
            witnessDto.Cases.Select(c => new WitnessCase(
                NormalizeDictionary(c.Input),
                NormalizeDictionary(c.ExpectedOutput))).ToList());

        return new CertificationRequest
        {
            Brick = brick,
            Witness = witness,
            SourceCode = sourceCode,
            ProjectPath = csproj,
            CompilationReferences = references,
            BrickTypeName = artifact.BrickTypeName,
            EmittedArtifact = artifact
        };
    }

    /// <summary>
    /// The brick-authoring assemblies a candidate is compiled against. The anchor TYPES are declared
    /// once, in <see cref="CertifierReferenceSet.BrickAuthoringAnchors"/>, rather than in a private
    /// copy here — two copies of an anchor list drift.
    ///
    /// <para><b>This is NOT the same list the self-extend certifier uses, and that is deliberate.</b>
    /// A2 and A4 judge against <see cref="CertifierReferenceSet.SelfExtendAuthoringAnchors"/>, which
    /// is this list plus the rest of the <c>Ashlar.Authoring</c> package's compile surface
    /// (<c>Ashlar.Core.Domain</c>, <c>Ashlar.Core.Application</c> and the two
    /// <c>Microsoft.Extensions.*.Abstractions</c>). Widening what the CHAIN compiles against is a
    /// different risk: <see cref="GateEmittedArtifactCompiler"/>'s emitted bytes are hashed into the
    /// certificate and pinned by golden corpora, so it can move certificate bytes and is its own
    /// change. <c>CertifierReferenceSetTests</c> asserts the subset relation and asserts that this
    /// method still yields exactly the same assemblies it did before, so the difference cannot drift
    /// in either direction unnoticed.</para>
    /// </summary>
    internal static List<string> DefaultCompilationReferences()
    {
        var refs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var anchor in CertifierReferenceSet.BrickAuthoringAnchors)
        {
            var path = anchor.Assembly.Location;
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                refs.Add(path);
        }

        return refs.ToList();
    }

    private sealed class WitnessSpecDto
    {
        /// <summary>Brick id.</summary>
        public string? BrickId { get; set; }
        /// <summary>Cases.</summary>
        public List<WitnessCaseDto> Cases { get; set; } = [];
    }

    private sealed class WitnessCaseDto
    {
        /// <summary>Input.</summary>
        public Dictionary<string, object> Input { get; set; } = new();
        /// <summary>Expected output.</summary>
        public Dictionary<string, object> ExpectedOutput { get; set; } = new();
    }

    private static Dictionary<string, object> NormalizeDictionary(Dictionary<string, object> values)
    {
        var normalized = new Dictionary<string, object>(values.Count);
        foreach (var (key, value) in values)
            normalized[key] = NormalizeValue(value);
        return normalized;
    }

    private static object NormalizeValue(object value) => value switch
    {
        JsonElement element => FromJsonElement(element),
        _ => value
    };

    private static object FromJsonElement(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString()!,
        JsonValueKind.Number when element.TryGetInt32(out var int32) => int32,
        JsonValueKind.Number when element.TryGetInt64(out var number) => number,
        JsonValueKind.Number => element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => element.GetRawText()
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
}
