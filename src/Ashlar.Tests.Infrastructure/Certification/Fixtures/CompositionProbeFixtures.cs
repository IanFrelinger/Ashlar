using Ashlar.Certification.Contracts;
using Ashlar.Core.Application.Certification.Models;
using Ashlar.Core.Application.Certification.Ports;
using Ashlar.Core.Domain.Bricks;
using Ashlar.Core.Domain.Execution;
using Ashlar.Infrastructure.Certification;
using Ashlar.Infrastructure.Certification.Composition;
using Ashlar.Tests.Infrastructure.Certification.Fixtures;

namespace Ashlar.Tests.Infrastructure.Certification.Fixtures;

/// <summary>Composition probe fixtures.</summary>
public static class CompositionProbeFixtures
{
    public const string CompositionId = "error-summary-pipeline";
    public const string ProbeBrickId = "mutation-probe-brick";
    public const string FormatterBrickId = "error-summary-formatter";
    public const string PassthroughBrickId = "error-count-passthrough";
    public const string UncertifiedBrickId = "uncertified-brick";

    public static readonly string ProbeLog =
        "2024-01-01 INFO Started\n2024-01-01 ERROR First failure: connection reset\n2024-01-01 WARN Retrying\n2024-01-01 ERROR Second failure: timeout";

    public static readonly string ExpectedSummary = "Errors=2; first=First failure: connection reset";

    /// <summary>Correct spec.</summary>
    public static CompositionSpec CorrectSpec() => new(
        CompositionId,
        [
            new CompositionNode("probe", ProbeBrickId),
            new CompositionNode("format", FormatterBrickId)
        ],
        CorrectEdges(),
        [new CompositionPort("logText", "string")],
        [
            new CompositionPort("errorCount", "int"),
            new CompositionPort("summary", "string")
        ]);

    public static CompositionSpec BrokenSeamSpec()
    {
        var edges = new List<CompositionEdge>(CorrectEdges())
        {
            new CompositionEdge("probe", "firstErrorMessage", "format", "errorCount")
        };
        edges.RemoveAll(e =>
            string.Equals(e.SourceNodeId, "probe", StringComparison.Ordinal)
            && string.Equals(e.SourcePort, "errorCount", StringComparison.Ordinal)
            && string.Equals(e.TargetNodeId, "format", StringComparison.Ordinal)
            && string.Equals(e.TargetPort, "errorCount", StringComparison.Ordinal));

        return CorrectSpec() with { Edges = edges };
    }

    /// <summary>Uncertified constituent spec.</summary>
    public static CompositionSpec UncertifiedConstituentSpec() =>
        CorrectSpec() with
        {
            Nodes =
            [
                new CompositionNode("probe", ProbeBrickId),
                new CompositionNode("format", UncertifiedBrickId)
            ]
        };

    /// <summary>Nondeterministic spec.</summary>
    public static CompositionSpec NondeterministicSpec() =>
        new(
            CompositionId + "-nondet",
            [
                new CompositionNode("probe", ProbeBrickId),
                new CompositionNode("format", "nondeterministic-formatter")
            ],
            NondeterministicEdges(),
            [new CompositionPort("logText", "string")],
            [
                new CompositionPort("errorCount", "int"),
                new CompositionPort("summary", "string"),
                new CompositionPort("nonce", "int")
            ]);

    /// <summary>Nondeterministic edges.</summary>
    private static IReadOnlyList<CompositionEdge> NondeterministicEdges() =>
    [
        new CompositionEdge(CompositionGraphConstants.InputNodeId, "logText", "probe", "logText"),
        new CompositionEdge("probe", "errorCount", "format", "errorCount"),
        new CompositionEdge("probe", "firstErrorMessage", "format", "firstErrorMessage"),
        new CompositionEdge("probe", "errorCount", CompositionGraphConstants.OutputNodeId, "errorCount"),
        new CompositionEdge("format", "summary", CompositionGraphConstants.OutputNodeId, "summary"),
        new CompositionEdge("format", "nonce", CompositionGraphConstants.OutputNodeId, "nonce")
    ];

    /// <summary>Strong witness.</summary>
    public static CompositionWitnessSpec StrongWitness() => new(
        CompositionId,
        [
            new WitnessCase(
                new Dictionary<string, object> { ["logText"] = ProbeLog },
                new Dictionary<string, object>
                {
                    ["errorCount"] = 2,
                    ["summary"] = ExpectedSummary
                })
        ]);

    /// <summary>Weak witness.</summary>
    public static CompositionWitnessSpec WeakWitness() => new(
        CompositionId,
        [
            new WitnessCase(
                new Dictionary<string, object> { ["logText"] = ProbeLog },
                new Dictionary<string, object> { ["errorCount"] = 2 })
        ]);

    public static async Task<CertifiedCompositionTestContext> CreateAdmittedContextAsync()
    {
        var brickStore = new InMemoryCertificationRecordStore();
        var (privateKey, _) = CreateEd25519Key();
        var brickSigner = new CertificationRecordSigner(ed25519PrivateKeyBase64: privateKey);
        var brickRegistry = new CertifiedBrickRegistry(brickStore, brickSigner);
        var brickGate = new CertificationGate(brickSigner);
        var brickAdmission = new CertifiedBrickAdmission(brickGate, brickRegistry);

        await AdmitBrickAsync(
            brickAdmission,
            new MutationProbeBrick(),
            MutationProbeBrickSource.Code,
            new WitnessSpec(
                ProbeBrickId,
                [
                    new WitnessCase(
                        new Dictionary<string, object> { ["logText"] = ProbeLog },
                        new Dictionary<string, object>
                        {
                            ["errorCount"] = 2,
                            ["firstErrorMessage"] = "First failure: connection reset"
                        }),
                    MutationProbeWitnesses.ZeroErrorCase
                ])).ConfigureAwait(false);

        await AdmitBrickAsync(
            brickAdmission,
            new ErrorSummaryFormatterBrick(),
            ErrorSummaryFormatterBrickSource.Code,
            new WitnessSpec(
                FormatterBrickId,
                [
                    new WitnessCase(
                        new Dictionary<string, object>
                        {
                            ["errorCount"] = 2,
                            ["firstErrorMessage"] = "First failure: connection reset"
                        },
                        new Dictionary<string, object> { ["summary"] = ExpectedSummary })
                ])).ConfigureAwait(false);

        await AdmitBrickAsync(
            brickAdmission,
            new ErrorCountPassthroughBrick(),
            ErrorCountPassthroughBrickSource.Code,
            new WitnessSpec(
                PassthroughBrickId,
                [
                    new WitnessCase(
                        new Dictionary<string, object>
                        {
                            ["errorCount"] = 2,
                            ["firstErrorMessage"] = "ignored"
                        },
                        new Dictionary<string, object> { ["summary"] = "Errors=2; first=WRONG" })
                ])).ConfigureAwait(false);

        var compositionStore = new InMemoryCompositionCertificationRecordStore();
        var compositionSigner = new CompositionCertificationRecordSigner(brickSigner);
        var compositionRegistry = new CertifiedCompositionRegistry(compositionStore, compositionSigner);
        var compositionGate = new CompositionCertificationGate(
            brickStore,
            brickSigner,
            brickRegistry,
            compositionSigner);
        var compositionAdmission = new CertifiedCompositionAdmission(compositionGate, compositionRegistry);

        return new CertifiedCompositionTestContext(
            brickStore,
            brickSigner,
            brickRegistry,
            brickAdmission,
            compositionStore,
            compositionSigner,
            compositionRegistry,
            compositionGate,
            compositionAdmission);
    }

    public static void SeedSyntheticConstituent(
        CertifiedCompositionTestContext ctx,
        DomainBrick brick,
        string brickId)
    {
        var record = new CertificationRecord
        {
            SchemaVersion = CertificationRecordData.TrustLoopSchemaVersion,
            Status = "PASS",
            Stage = "S0-S2",
            Admitted = true,
            Signed = true,
            Timestamp = DateTimeOffset.UtcNow,
            BrickId = brickId,
            ContentHash = $"sha256:synthetic-{brickId}",
            Gate = "CompositionProbeFixtures.SeedSyntheticConstituent",
            Inputs = new[]
            {
                new CertificationInput { Kind = CertificationInputKinds.GateEmittedArtifact, Id = brickId, Hash = $"sha256:artifact-{brickId}" },
                new CertificationInput { Kind = CertificationInputKinds.CertifierIdentity, Id = "composition-test-certifier", Hash = "sha256:certifier" }
            }
        };
        var signed = ctx.BrickSigner.SignRecord(record);
        ctx.BrickStore.Save(signed);
        ctx.BrickRegistry.TryAdmit(brick, signed);
    }

    private static async Task AdmitBrickAsync(
        CertifiedBrickAdmission admission,
        DomainBrick brick,
        string sourceCode,
        WitnessSpec witness)
    {
        var decision = await admission.CertifyAndAdmitAsync(new CertificationRequest
        {
            Brick = brick,
            Witness = witness,
            SourceCode = sourceCode,
            ProjectPath = CreateCleanProjectFile(),
            CompilationReferences =
            [
                typeof(DomainBrick).Assembly.Location,
                typeof(BrickInput).Assembly.Location,
                typeof(MutationProbeBrick).Assembly.Location
            ],
            BrickTypeName = brick.GetType().FullName
        }).ConfigureAwait(false);

        if (!decision.Admitted)
            throw new InvalidOperationException(
                $"Failed to admit fixture brick {brick.Id}: {decision.FailureCheck} {decision.Record.Reason}");
    }

    /// <summary>Correct edges.</summary>
    private static IReadOnlyList<CompositionEdge> CorrectEdges() =>
    [
        new CompositionEdge(CompositionGraphConstants.InputNodeId, "logText", "probe", "logText"),
        new CompositionEdge("probe", "errorCount", "format", "errorCount"),
        new CompositionEdge("probe", "firstErrorMessage", "format", "firstErrorMessage"),
        new CompositionEdge("probe", "errorCount", CompositionGraphConstants.OutputNodeId, "errorCount"),
        new CompositionEdge("format", "summary", CompositionGraphConstants.OutputNodeId, "summary")
    ];

    private static string CreateCleanProjectFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ashlar-comp-cert-{Guid.NewGuid():N}.csproj");
        File.WriteAllText(path, """
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <PackageReference Include="Ashlar.Brick.Contracts" Version="0.1.0" />
    <PackageReference Include="Ashlar.Authoring" Version="0.1.0" />
  </ItemGroup>
</Project>
""");
        return path;
    }

    private static (string PrivateKeyBase64, string PublicKeyBase64) CreateEd25519Key()
    {
        using var key = NSec.Cryptography.Key.Create(
            NSec.Cryptography.SignatureAlgorithm.Ed25519,
            new NSec.Cryptography.KeyCreationParameters { ExportPolicy = NSec.Cryptography.KeyExportPolicies.AllowPlaintextExport });
        return (
            Convert.ToBase64String(key.Export(NSec.Cryptography.KeyBlobFormat.RawPrivateKey)),
            Convert.ToBase64String(key.PublicKey.Export(NSec.Cryptography.KeyBlobFormat.RawPublicKey)));
    }
}
