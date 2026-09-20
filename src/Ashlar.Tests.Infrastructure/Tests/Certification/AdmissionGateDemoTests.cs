using FluentAssertions;
using Ashlar.Certification.Contracts;
using Ashlar.Core.Application.Certification.Models;
using Ashlar.Core.Domain.Bricks;
using Ashlar.Core.Domain.Execution;
using Ashlar.Infrastructure.Certification;
using Ashlar.Tests.Infrastructure.Certification.Fixtures;
using NSec.Cryptography;
using Xunit;
using Xunit.Abstractions;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>
/// ADMISSION GATE DEMO — proves artifacts can be REJECTED or ADMITTED based on witness strength.
/// 
/// <para>This test demonstrates Ashlar's certification gate in action:</para>
/// <list type="bullet">
///   <item><description>Weak witness (incomplete expectations) → REJECTED with escape rate > 0</description></item>
///   <item><description>Strong witness (complete expectations) → ADMITTED with escape rate = 0</description></item>
///   <item><description>Both produce signed records with correlated IDs for audit trail</description></item>
/// </list>
/// 
/// <para><b>HONEST DISCLAIMER:</b></para>
/// <para>This proves artifact admission gates exist and enforce mutation + witness testing.
/// Copilot chat tasks currently record AFTER execution. Wiring pre-admission into the
/// /api/copilot/task path is a separate product change (see CLOSING-PLAN.md Phase 3-4).</para>
/// 
/// <para><b>Run this demo:</b></para>
/// <code>
/// dotnet test src/Ashlar.Tests.Infrastructure --filter FullyQualifiedName~AdmissionGateDemoTests
/// </code>
/// 
/// <para>Or via the shell script (standalone, no test runner):</para>
/// <code>
/// bash scripts/demo-admit-reject.sh
/// </code>
/// </summary>
[Trait("Category", "Certification")]
[Trait("Demo", "AdmissionGate")]
public sealed class AdmissionGateDemoTests
{
    private readonly ITestOutputHelper _output;

    public AdmissionGateDemoTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static readonly string ProbeLog =
        "2024-01-01 INFO Started\n2024-01-01 ERROR First failure: connection reset\n2024-01-01 WARN Retrying\n2024-01-01 ERROR Second failure: timeout";

    [Fact]
    public async Task Demo_WeakWitness_GetsRejected_DueToMutationSurvivors()
    {
        _output.WriteLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
        _output.WriteLine("ADMISSION GATE DEMO — PHASE 1: WEAK WITNESS → REJECT");
        _output.WriteLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
        _output.WriteLine("");
        _output.WriteLine("Artifact: MutationProbeBrick (log scanner)");
        _output.WriteLine("Witness:  Only checks 'errorCount' (incomplete)");
        _output.WriteLine("Expected: Gate REJECTS because mutations to 'firstErrorMessage'");
        _output.WriteLine("          logic cannot be detected → survivors escape");
        _output.WriteLine("");

        var weakWitness = new WitnessSpec(
            "mutation-probe-brick",
            [
                new WitnessCase(
                    new Dictionary<string, object> { ["logText"] = ProbeLog },
                    new Dictionary<string, object>
                    {
                        ["errorCount"] = 2
                    })
            ]);

        var gate = CreateGate();
        var brick = new MutationProbeBrick();
        var request = new CertificationRequest
        {
            Brick = brick,
            Witness = weakWitness,
            SourceCode = MutationProbeBrickSource.Code,
            ProjectPath = CreateCleanProjectFile(),
            CompilationReferences = CompilationReferences(),
            BrickTypeName = typeof(MutationProbeBrick).FullName
        };

        var decision = await gate.CertifyAsync(request);

        _output.WriteLine("RESULT: ✗ REJECTED");
        _output.WriteLine($"  Brick ID:        {decision.Record.BrickId}");
        _output.WriteLine($"  Content Hash:    {decision.Record.ContentHash![..16]}...");
        _output.WriteLine($"  Escape Rate:     {decision.Record.EscapeRate} (threshold: 0)");
        _output.WriteLine($"  Total Mutants:   {decision.Record.TotalMutants}");
        _output.WriteLine($"  Killed Mutants:  {decision.Record.KilledMutants.Count}");
        _output.WriteLine($"  Survivors:       {decision.Record.SurvivingMutants}");
        _output.WriteLine($"  Record Signed:   {decision.Record.Signed}");
        _output.WriteLine($"  Signature:       {decision.Record.Signature![..16]}...");
        _output.WriteLine("");
        _output.WriteLine("WHY IT FAILED:");
        _output.WriteLine("  Weak witness cannot detect mutations to 'firstErrorMessage'");
        _output.WriteLine("  extraction logic. Mutants that break that output still pass");
        _output.WriteLine("  the witness check because it only observes 'errorCount'.");
        _output.WriteLine("  Result: Escape rate > 0 → gate REJECTS the artifact.");
        _output.WriteLine("");

        decision.Admitted.Should().BeFalse("weak witness cannot kill all mutants");
        decision.FailureCheck.Should().Be("mutation");
        decision.Record.EscapeRate.Should().BeGreaterThan(0);
        decision.Record.Signed.Should().BeTrue();
        decision.Record.ContentHash.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Demo_StrongWitness_GetsAdmitted_AllMutantsKilled()
    {
        _output.WriteLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
        _output.WriteLine("ADMISSION GATE DEMO — PHASE 2: STRONG WITNESS → ADMIT");
        _output.WriteLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
        _output.WriteLine("");
        _output.WriteLine("Artifact: MutationProbeBrick (same as Phase 1)");
        _output.WriteLine("Witness:  Checks 'errorCount' AND 'firstErrorMessage'");
        _output.WriteLine("Expected: Gate ADMITS because ALL mutations are detected");
        _output.WriteLine("");

        var strongWitness = new WitnessSpec(
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

        var gate = CreateGate();
        var brick = new MutationProbeBrick();
        var request = new CertificationRequest
        {
            Brick = brick,
            Witness = strongWitness,
            SourceCode = MutationProbeBrickSource.Code,
            ProjectPath = CreateCleanProjectFile(),
            CompilationReferences = CompilationReferences(),
            BrickTypeName = typeof(MutationProbeBrick).FullName
        };

        var decision = await gate.CertifyAsync(request);

        _output.WriteLine("RESULT: ✓ ADMITTED");
        _output.WriteLine($"  Brick ID:        {decision.Record.BrickId}");
        _output.WriteLine($"  Content Hash:    {decision.Record.ContentHash![..16]}...");
        _output.WriteLine($"  Escape Rate:     {decision.Record.EscapeRate}");
        _output.WriteLine($"  Total Mutants:   {decision.Record.TotalMutants}");
        _output.WriteLine($"  Killed Mutants:  {decision.Record.KilledMutants.Count}");
        _output.WriteLine($"  Gates Passed:    {decision.Record.GatesPassed.Count}");
        _output.WriteLine($"  Record Signed:   {decision.Record.Signed}");
        _output.WriteLine($"  Signature:       {decision.Record.Signature![..16]}...");
        _output.WriteLine("");
        _output.WriteLine("WHY IT PASSED:");
        _output.WriteLine("  Strong witness checks BOTH outputs ('errorCount' and");
        _output.WriteLine("  'firstErrorMessage'). Any mutation that breaks either");
        _output.WriteLine("  output is detected by the witness check.");
        _output.WriteLine("  Result: Escape rate = 0 → gate ADMITS the artifact.");
        _output.WriteLine("");

        decision.Admitted.Should().BeTrue();
        decision.Record.EscapeRate.Should().Be(0);
        decision.Record.Signed.Should().BeTrue();
        decision.Record.ContentHash.Should().NotBeNullOrWhiteSpace();
        decision.Record.GatesPassed.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Demo_BothRecords_HaveCorrelatedIds_ForAuditTrail()
    {
        _output.WriteLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
        _output.WriteLine("ADMISSION GATE DEMO — PHASE 3: PROOF OF CORRELATION");
        _output.WriteLine("━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
        _output.WriteLine("");
        _output.WriteLine("Running both weak and strong witness against same artifact...");
        _output.WriteLine("");

        var weakWitness = new WitnessSpec("mutation-probe-brick",
            [new WitnessCase(
                new Dictionary<string, object> { ["logText"] = ProbeLog },
                new Dictionary<string, object> { ["errorCount"] = 2 })]);

        var strongWitness = new WitnessSpec("mutation-probe-brick",
            [new WitnessCase(
                new Dictionary<string, object> { ["logText"] = ProbeLog },
                new Dictionary<string, object>
                {
                    ["errorCount"] = 2,
                    ["firstErrorMessage"] = "First failure: connection reset"
                })]);

        var gate = CreateGate();
        var brick = new MutationProbeBrick();
        var sourceCode = MutationProbeBrickSource.Code;
        var projectPath = CreateCleanProjectFile();
        var refs = CompilationReferences();

        var weakDecision = await gate.CertifyAsync(new CertificationRequest
        {
            Brick = brick,
            Witness = weakWitness,
            SourceCode = sourceCode,
            ProjectPath = projectPath,
            CompilationReferences = refs,
            BrickTypeName = typeof(MutationProbeBrick).FullName
        });

        var strongDecision = await gate.CertifyAsync(new CertificationRequest
        {
            Brick = brick,
            Witness = strongWitness,
            SourceCode = sourceCode,
            ProjectPath = projectPath,
            CompilationReferences = refs,
            BrickTypeName = typeof(MutationProbeBrick).FullName
        });

        _output.WriteLine("CORRELATION PROOF:");
        _output.WriteLine($"  Brick ID (weak):    {weakDecision.Record.BrickId}");
        _output.WriteLine($"  Brick ID (strong):  {strongDecision.Record.BrickId}");
        _output.WriteLine($"  → SAME ARTIFACT ID");
        _output.WriteLine("");
        _output.WriteLine($"  Content Hash (weak):    {weakDecision.Record.ContentHash![..16]}...");
        _output.WriteLine($"  Content Hash (strong):  {strongDecision.Record.ContentHash![..16]}...");
        _output.WriteLine($"  → SAME SOURCE CODE");
        _output.WriteLine("");
        _output.WriteLine("DIFFERENT OUTCOMES FROM WITNESS STRENGTH:");
        _output.WriteLine($"  Weak witness:    REJECT (escape rate: {weakDecision.Record.EscapeRate})");
        _output.WriteLine($"  Strong witness:  ADMIT  (escape rate: {strongDecision.Record.EscapeRate})");
        _output.WriteLine("");
        _output.WriteLine("WHAT THIS PROVES:");
        _output.WriteLine("  ✓ Both records certify the SAME artifact (correlated by ID + hash)");
        _output.WriteLine("  ✓ Weak tests let mutations escape → REJECTED");
        _output.WriteLine("  ✓ Strong tests kill all mutations → ADMITTED");
        _output.WriteLine("  ✓ Signed records provide verifiable audit trail");
        _output.WriteLine("");
        _output.WriteLine("HONEST DISCLAIMER:");
        _output.WriteLine("  - This proves artifact admission gates work as designed");
        _output.WriteLine("  - Copilot chat task path currently records AFTER execution");
        _output.WriteLine("  - Wiring admission into /api/copilot/task requires separate");
        _output.WriteLine("    product integration (see CLOSING-PLAN.md Phase 3-4)");
        _output.WriteLine("");

        weakDecision.Record.BrickId.Should().Be(strongDecision.Record.BrickId,
            "both records certify the same artifact");
        weakDecision.Record.ContentHash.Should().Be(strongDecision.Record.ContentHash,
            "both records hash the same source code");

        weakDecision.Admitted.Should().BeFalse();
        strongDecision.Admitted.Should().BeTrue();

        weakDecision.Record.EscapeRate.Should().BeGreaterThan(0);
        strongDecision.Record.EscapeRate.Should().Be(0);

        weakDecision.Record.Signed.Should().BeTrue();
        strongDecision.Record.Signed.Should().BeTrue();
    }

    private static CertificationGate CreateGate()
    {
        var (privateKey, _) = CreateEd25519Key();
        return new CertificationGate(new CertificationRecordSigner(ed25519PrivateKeyBase64: privateKey));
    }

    private static (string privateKey, string publicKey) CreateEd25519Key()
    {
        var algorithm = SignatureAlgorithm.Ed25519;
        using var key = Key.Create(algorithm);
        var privateKeyBytes = key.Export(KeyBlobFormat.RawPrivateKey);
        var publicKeyBytes = key.PublicKey.Export(KeyBlobFormat.RawPublicKey);
        return (Convert.ToBase64String(privateKeyBytes), Convert.ToBase64String(publicKeyBytes));
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

    private static string CreateCleanProjectFile()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ashlar-gate-demo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var projectPath = Path.Combine(tempDir, "Demo.csproj");
        File.WriteAllText(projectPath,
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);
        return projectPath;
    }
}
