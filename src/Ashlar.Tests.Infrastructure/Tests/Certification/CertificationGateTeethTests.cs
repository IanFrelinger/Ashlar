using FluentAssertions;
using Ashlar.Certification.Contracts;
using Ashlar.Core.Application.Certification.Models;
using Ashlar.Core.Application.Certification.Ports;
using Ashlar.Core.Domain.Bricks;
using Ashlar.Core.Domain.Execution;
using Ashlar.Infrastructure.Certification;
using Ashlar.Infrastructure.Certification.Sdk.Extensions;
using Ashlar.Tests.Infrastructure.Certification.Fixtures;
using NSec.Cryptography;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>Tests for certification gate teeth.</summary>
[Trait("Category", "Certification")]
public sealed class CertificationGateTeethTests
{
    private static readonly string ProbeLog =
        "2024-01-01 INFO Started\n2024-01-01 ERROR First failure: connection reset\n2024-01-01 WARN Retrying\n2024-01-01 ERROR Second failure: timeout";

    private static readonly WitnessSpec StrongWitness = new(
        "mutation-probe-brick",
        [
            new WitnessCase(
                new Dictionary<string, object> { ["logText"] = ProbeLog },
                new Dictionary<string, object>
                {
                    ["errorCount"] = 2,
                    ["firstErrorMessage"] = "First failure: connection reset"
                })
        ]);

    private static readonly WitnessSpec WeakWitness = new(
        "mutation-probe-brick",
        [
            new WitnessCase(
                new Dictionary<string, object> { ["logText"] = ProbeLog },
                new Dictionary<string, object> { ["errorCount"] = 2 })
        ]);

    /// <summary>Creates gate with Ed25519 dual-signing support.</summary>
    private static CertificationGate CreateGate()
    {
        var (privateKey, _) = CreateEd25519Key();
        return new CertificationGate(new CertificationRecordSigner(ed25519PrivateKeyBase64: privateKey));
    }

    private static List<string> CompilationReferences()
    {
        return
        [
            typeof(DomainBrick).Assembly.Location,
            typeof(BrickInput).Assembly.Location,
            typeof(MutationProbeBrick).Assembly.Location
        ];
    }

    [Theory]
    [InlineData("")]
    [InlineData("Secret ")]
    [InlineData("Unknown")]
    public async Task Invalid_skill_declaration_is_refused_before_candidate_execution(string label)
    {
        var request = new CertificationRequest
        {
            HighestDataLabel = label,
            Brick = new MutationProbeBrick(), Witness = StrongWitness,
            SourceCode = MutationProbeBrickSource.Code, ProjectPath = "fixture"
        };
        var action = () => CreateGate().CertifyAsync(request);
        await action.Should().ThrowAsync<ArgumentException>();
    }

    [Theory]
    [InlineData("Public")]
    [InlineData("Secret")]
    [InlineData("SystemHigh")]
    [InlineData(null)]
    public async Task GoodBrick_StrongWitness_Admits_WithZeroEscapeRate(string? label)
    {
        var gate = CreateGate();
        var brick = new MutationProbeBrick();
        var request = new CertificationRequest
        {
            Brick = brick,
            Witness = StrongWitness,
            SourceCode = MutationProbeBrickSource.Code,
            ProjectPath = CreateCleanProjectFile(),
            CompilationReferences = CompilationReferences(),
            BrickTypeName = typeof(MutationProbeBrick).FullName
        };

        if (label is not null)
            request = request with { HighestDataLabel = label };
        var decision = await gate.CertifyAsync(request);

        decision.Admitted.Should().BeTrue();
        decision.Record.SchemaVersion.Should().Be(CertificationRecordData.LabelSchemaVersion);
        decision.Record.HighestDataLabel.Should().Be(label ?? "SystemHigh");
        decision.Record.EscapeRate.Should().Be(0);
        decision.Record.Signed.Should().BeTrue();
        decision.Record.Signature.Should().NotBeNullOrWhiteSpace();
        decision.Record.ContentHash.Should().NotBeNullOrWhiteSpace();
        new CertificationRecordSigner().Verify(decision.Record, CertificationVerifyOptions.Strict).Should().BeTrue();
    }

    [Fact]
    public async Task Witness_CanPinTheSummary_UnderTheReservedKey()
    {
        // BrickOutput.ToDictionary() excludes Summary, so before the reserved key NO witness
        // could observe it — a mutated summary literal was unkillable by any witness in the
        // language, and the first model-proposed dogfood candidate was blocked at the
        // mutation leg by exactly that survivor. The reserved "$summary" key makes it
        // witnessable; this pins that a witness which states it is honoured on both legs.
        var gate = CreateGate();
        var withSummary = new WitnessSpec(
            "mutation-probe-brick",
            [
                new WitnessCase(
                    StrongWitness.Cases[0].Input,
                    new Dictionary<string, object>(StrongWitness.Cases[0].ExpectedOutput)
                    {
                        [WitnessObservableOutput.SummaryKey] = "Found 2 ERROR line(s); first: First failure: connection reset",
                    })
            ]);
        var wrongSummary = new WitnessSpec(
            "mutation-probe-brick",
            [
                new WitnessCase(
                    StrongWitness.Cases[0].Input,
                    new Dictionary<string, object>(StrongWitness.Cases[0].ExpectedOutput)
                    {
                        [WitnessObservableOutput.SummaryKey] = "not what the brick says",
                    })
            ]);

        var right = await gate.CertifyAsync(Request(withSummary));
        var wrong = await gate.CertifyAsync(Request(wrongSummary));

        right.Admitted.Should().BeTrue(right.Record.Reason);
        wrong.Admitted.Should().BeFalse("a summary the brick does not produce must fail the witness");
        wrong.FailureCheck.Should().Be("correctness");
        wrong.Record.Reason.Should().Contain("$summary");

        CertificationRequest Request(WitnessSpec witness) => new()
        {
            Brick = new MutationProbeBrick(),
            Witness = witness,
            SourceCode = MutationProbeBrickSource.Code,
            ProjectPath = CreateCleanProjectFile(),
            CompilationReferences = CompilationReferences(),
            BrickTypeName = typeof(MutationProbeBrick).FullName
        };
    }

    [Fact]
    public async Task RequestManifest_ReachesTheAnalyzerGate_AndItsInstructionReachesTheRecord()
    {
        // A2.3 end-to-end: the manifest instance on the request is the one whose rules reject
        // the candidate, and the failure reason restates that instance's instruction verbatim.
        var manifest = new Ashlar.Core.Domain.Bricks.Ports.BrickConstraintManifest
        {
            ForbiddenNamespaces = ["System.Collections"]
        };
        var gate = CreateGate();
        var request = new CertificationRequest
        {
            Brick = new MutationProbeBrick(),
            Witness = StrongWitness,
            SourceCode = MutationProbeBrickSource.Code,
            ProjectPath = CreateCleanProjectFile(),
            CompilationReferences = CompilationReferences(),
            BrickTypeName = typeof(MutationProbeBrick).FullName,
            ConstraintManifest = manifest
        };

        var decision = await gate.CertifyAsync(request);

        decision.Admitted.Should().BeFalse();
        decision.FailureCheck.Should().Be("analyzer");
        decision.Record.Reason.Should().Contain("ASHLAR0012")
            .And.Contain(manifest.ForbiddenNamespaceInstruction("System.Collections"));
        decision.Record.GatesPassed.Should().BeEmpty(
            "the analyzer fence is the first gate, so nothing precedes it in the furthest-gate prefix");
    }

    [Fact]
    public async Task DepthPastTheCeiling_RejectsBeforeAnyGateRuns()
    {
        var request = new CertificationRequest
        {
            Brick = new MutationProbeBrick(),
            Witness = StrongWitness,
            SourceCode = MutationProbeBrickSource.Code,
            ProjectPath = CreateCleanProjectFile(),
            CompilationReferences = CompilationReferences(),
            BrickTypeName = typeof(MutationProbeBrick).FullName,
            Lineage = new Ashlar.Core.Application.Autonomy.GenerationLineage
            {
                Depth = 3,
                ParentCertificateSignatures = ["sig-a", "sig-b", "sig-c"],
            }
        };

        var decision = await CreateGate().CertifyAsync(request);

        decision.Admitted.Should().BeFalse();
        decision.FailureCheck.Should().Be("recursion");
        decision.Record.Reason.Should().Contain("ceiling").And.Contain("Tier 2");
        decision.Record.GatesPassed.Should().BeEmpty("the recursion check precedes every gate");
        decision.Record.Inputs.Should().Contain(i => i.Kind == "generation-depth" && i.Id == "3",
            "even a refused depth claim leaves its evidence on the record");
    }

    [Fact]
    public async Task LaunderedDepthClaim_Rejects()
    {
        // §8 depth laundering: a fresh session claims depth 2 with no parent certificates.
        var request = new CertificationRequest
        {
            Brick = new MutationProbeBrick(),
            Witness = StrongWitness,
            SourceCode = MutationProbeBrickSource.Code,
            ProjectPath = CreateCleanProjectFile(),
            CompilationReferences = CompilationReferences(),
            BrickTypeName = typeof(MutationProbeBrick).FullName,
            Lineage = new Ashlar.Core.Application.Autonomy.GenerationLineage { Depth = 2 }
        };

        var decision = await CreateGate().CertifyAsync(request);

        decision.Admitted.Should().BeFalse();
        decision.FailureCheck.Should().Be("recursion");
        decision.Record.Reason.Should().Contain("laundering");
    }

    [Fact]
    public async Task CoherentDepthUnderTheCeiling_Admits_AndRecordsTheLineageInput()
    {
        var lineage = Ashlar.Core.Application.Autonomy.GenerationLineage.Child(
            Ashlar.Core.Application.Autonomy.GenerationLineage.HumanAuthored, "sig-parent");
        var request = new CertificationRequest
        {
            Brick = new MutationProbeBrick(),
            Witness = StrongWitness,
            SourceCode = MutationProbeBrickSource.Code,
            ProjectPath = CreateCleanProjectFile(),
            CompilationReferences = CompilationReferences(),
            BrickTypeName = typeof(MutationProbeBrick).FullName,
            Lineage = lineage
        };

        var decision = await CreateGate().CertifyAsync(request);

        decision.Admitted.Should().BeTrue(decision.Record.Reason);
        var input = decision.Record.Inputs.Should()
            .ContainSingle(i => i.Kind == "generation-depth").Subject;
        input.Id.Should().Be("1");
        input.Hash.Should().NotBeNullOrWhiteSpace(
            "the depth is bound to a hash over the parent certificate chain (anti-laundering)");
    }

    [Fact]
    public async Task BadWitnessBrick_Rejects_OnCorrectness()
    {
        var gate = CreateGate();
        var request = new CertificationRequest
        {
            Brick = new BadWitnessBrick(),
            Witness = StrongWitness with { BrickId = "bad-witness-brick" },
            SourceCode = "namespace X; class Y {}",
            ProjectPath = CreateCleanProjectFile(),
            CompilationReferences = CompilationReferences(),
            BrickTypeName = typeof(BadWitnessBrick).FullName
        };

        var decision = await gate.CertifyAsync(request);

        decision.Admitted.Should().BeFalse();
        decision.FailureCheck.Should().Be("correctness");
    }

    [Fact]
    public async Task WeakWitness_AllowsMutantEscapes_RejectsWithTeeth()
    {
        var gate = CreateGate();
        var brick = new MutationProbeBrick();
        var request = new CertificationRequest
        {
            Brick = brick,
            Witness = WeakWitness,
            SourceCode = MutationProbeBrickSource.Code,
            ProjectPath = CreateCleanProjectFile(),
            CompilationReferences = CompilationReferences(),
            BrickTypeName = typeof(MutationProbeBrick).FullName
        };

        var decision = await gate.CertifyAsync(request);

        decision.Admitted.Should().BeFalse("weak witness must not admit when mutants survive");
        decision.FailureCheck.Should().Be("mutation");
        decision.Record.EscapeRate.Should().BeGreaterThan(0);
        decision.Record.SurvivingMutantIds.Should().NotBeEmpty();
    }

    [Fact]
    public async Task NondeterministicBrick_Rejects_OnDeterminism()
    {
        var gate = CreateGate();
        var request = new CertificationRequest
        {
            Brick = new NondeterministicBrick(),
            Witness = new WitnessSpec(
                "nondeterministic-brick",
                [
                    new WitnessCase(
                        new Dictionary<string, object> { ["logText"] = ProbeLog },
                        new Dictionary<string, object>
                        {
                            ["errorCount"] = 2,
                            ["firstErrorMessage"] = "First failure: connection reset"
                        })
                ]),
            SourceCode = NondeterministicBrickSource.Code,
            ProjectPath = CreateCleanProjectFile(),
            CompilationReferences = CompilationReferences(),
            BrickTypeName = typeof(NondeterministicBrick).FullName
        };

        var decision = await gate.CertifyAsync(request);

        decision.Admitted.Should().BeFalse();
        decision.FailureCheck.Should().Be("determinism");
    }

    [Fact]
    public void UngatedBrick_IsRejectedByRegistryAdmissionPath()
    {
        var store = new InMemoryCertificationRecordStore();
        var (privateKey, _) = CreateEd25519Key();
        var signer = new CertificationRecordSigner(ed25519PrivateKeyBase64: privateKey);
        var registry = new CertifiedBrickRegistry(store, signer);
        var brick = new MutationProbeBrick();

        var admitted = registry.TryAdmit(brick, new CertificationRecord
        {
            Status = "PASS",
            Stage = "S0-S2",
            Admitted = true,
            Signed = true,
            Timestamp = DateTimeOffset.UtcNow,
            BrickId = brick.Id,
            Signature = "not-a-real-signature"
        });

        admitted.Should().BeFalse();
        registry.GetBrick(brick.Id).Should().BeNull();
    }

    [Fact]
    public async Task CertifiedAdmission_OnlyExposesAdmittedBricks()
    {
        var store = new InMemoryCertificationRecordStore();
        var (privateKey, _) = CreateEd25519Key();
        var signer = new CertificationRecordSigner(ed25519PrivateKeyBase64: privateKey);
        var registry = new CertifiedBrickRegistry(store, signer);
        var gate = new CertificationGate(signer);
        var admission = new CertifiedBrickAdmission(gate, registry);

        var request = new CertificationRequest
        {
            Brick = new MutationProbeBrick(),
            Witness = StrongWitness,
            SourceCode = MutationProbeBrickSource.Code,
            ProjectPath = CreateCleanProjectFile(),
            CompilationReferences = CompilationReferences(),
            BrickTypeName = typeof(MutationProbeBrick).FullName
        };

        var decision = await admission.CertifyAndAdmitAsync(request);
        decision.Admitted.Should().BeTrue();
        registry.GetBrick("mutation-probe-brick").Should().NotBeNull();

        var ungated = new BadWitnessBrick();
        registry.TryAdmit(ungated, decision.Record with { BrickId = ungated.Id, Signature = "bogus" }).Should().BeFalse();
        registry.GetBrick(ungated.Id).Should().BeNull();
    }

    private static string CreateCleanProjectFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ashlar-cert-clean-{Guid.NewGuid():N}.csproj");
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
        using var key = Key.Create(
            SignatureAlgorithm.Ed25519,
            new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
        return (
            Convert.ToBase64String(key.Export(KeyBlobFormat.RawPrivateKey)),
            Convert.ToBase64String(key.PublicKey.Export(KeyBlobFormat.RawPublicKey)));
    }
}
