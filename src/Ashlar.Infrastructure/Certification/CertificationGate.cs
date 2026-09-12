#pragma warning disable ASHLAREXP001 // The gate enforces the experimental autonomy contract (touch-set, lineage) by design; see docs/SdkCompatibilityPolicy.md.
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Ashlar.Certification.Contracts;
using Ashlar.Core.Application.Certification.Models;
using Ashlar.Core.Application.Certification.Ports;

namespace Ashlar.Infrastructure.Certification;

/// <summary>Certification gate: mutation testing, dependency checks, signing, and admit/reject decisions.</summary>
public sealed class CertificationGate : ICertificationGate
{
    private const string GateVersion = "1";

    private static readonly CertificationGatePass CorrectnessGatePass = new() { Name = "correctness-witness", Version = GateVersion };
    private static readonly CertificationGatePass MutationGatePass = new() { Name = "mutation-gate", Version = GateVersion, Configuration = "escapeRateThreshold=0" };
    private static readonly CertificationGatePass DeterminismGatePass = new() { Name = "determinism", Version = GateVersion };
    private static readonly CertificationGatePass DependencyGatePass = new() { Name = "dependency-graph", Version = GateVersion };

    private static readonly JsonSerializerOptions WitnessHashOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly BrickMutationEngine _mutationEngine = new();
    private readonly CertificationRecordSigner _signer;
    private readonly AnalyzerFenceGate _analyzerGate;
    private readonly ILogger<CertificationGate>? _logger;

    /// <summary>Initializes a new certification gate.</summary>
    public CertificationGate(
        CertificationRecordSigner signer,
        ILogger<CertificationGate>? logger = null,
        AnalyzerFenceGate? analyzerGate = null,
        IEnumerable<IDiagnosticProbe>? probes = null)
    {
        _signer = signer ?? throw new ArgumentNullException(nameof(signer));
        _logger = logger;
        // Defaulting the gate on (rather than null-meaning-skip) keeps the chain fail-closed:
        // no construction path exists that certifies without the analyzer fence.
        _analyzerGate = analyzerGate ?? new AnalyzerFenceGate();
        _probes = probes?.ToArray() ?? Probes.CertificationProbeCatalog.Default;
    }

    private readonly IReadOnlyList<IDiagnosticProbe> _probes;

    /// <summary>Certify asynchronously; on rejection, diagnostic probes attach structured findings (G4).</summary>
    public async Task<CertificationDecision> CertifyAsync(
        CertificationRequest request,
        CancellationToken cancellationToken = default)
    {
        var decision = await CertifyCoreAsync(request, cancellationToken).ConfigureAwait(false);
        return decision.Admitted ? decision : decision with { ProbeFindings = RunProbes(request, decision) };
    }

    private IReadOnlyList<DiagnosticProbeFinding> RunProbes(
        CertificationRequest request, CertificationDecision decision)
    {
        var findings = new List<DiagnosticProbeFinding>();
        foreach (var probe in _probes.Where(p => p.FailureCheck == decision.FailureCheck))
        {
            try
            {
                if (probe.Probe(request, decision) is { } finding)
                    findings.Add(finding);
            }
            catch (Exception ex)
            {
                // A probe holds no authority: it can neither change a verdict nor break
                // the decision path. Skipped and logged.
                _logger?.LogWarning(ex, "Diagnostic probe {Probe} threw; verdict unaffected", probe.GetType().Name);
            }
        }

        return findings;
    }

    private async Task<CertificationDecision> CertifyCoreAsync(
        CertificationRequest request,
        CancellationToken cancellationToken)
    {
        var brickId = request.Brick.Id;
        var timestamp = DateTimeOffset.UtcNow;

        var contentHash = BrickContentHasher.ComputeSha256(request.SourceCode);
        var inputs = BuildInputs(request);

        try
        {
            request = BindEmittedArtifact(request);
        }
        catch (InvalidOperationException ex)
        {
            return new CertificationDecision
            {
                Admitted = false,
                FailureCheck = "load",
                Record = BuildRecord(
                    admitted: false,
                    signed: false,
                    status: "FAIL",
                    brickId,
                    timestamp,
                    contentHash,
                    ex.Message,
                    mutation: null,
                    gatesPassed: Array.Empty<CertificationGatePass>(),
                    inputs)
            };
        }

        // Declared ahead of the recursion refusal because the shared Fail/GatesPassedBefore
        // closures reference it; it is re-assigned with the real per-run configuration once
        // the analyzer fence actually evaluates. A recursion refusal never reads it
        // (GatesPassedBefore("recursion") is the empty prefix).
        var analyzerGatePass = new CertificationGatePass { Name = "analyzer-gate", Version = GateVersion };

        // Execution-backend routing: when the request names one, every EXECUTION of
        // candidate/mutant code goes through it and the gate judges raw observations.
        // The per-run gate passes record where execution actually happened. Declared here
        // for the same closure reason as the analyzer pass above.
        var backend = request.ExecutionBackend;
        var correctnessPass = backend is null
            ? CorrectnessGatePass
            : CorrectnessGatePass with { Configuration = $"execution={backend.Describe()}" };
        var mutationPass = backend is null
            ? MutationGatePass
            : MutationGatePass with { Configuration = $"escapeRateThreshold=0;execution={backend.Describe()}" };
        var determinismPass = backend is null
            ? DeterminismGatePass
            : DeterminismGatePass with { Configuration = $"execution={backend.Describe()}" };

        // Autonomy spec R4.1/R4.2: the recursion check runs before EVERYTHING — an
        // incoherent depth claim (laundering) or a candidate past the ceiling must not
        // even be analyzed. Null lineage = human-authored context (depth 0), always
        // coherent and under the ceiling.
        var lineage = request.Lineage ?? Ashlar.Core.Application.Autonomy.GenerationLineage.HumanAuthored;
        var recursionViolations = Ashlar.Core.Application.Autonomy.RecursionDiscipline.FindViolations(lineage);
        if (recursionViolations.Count > 0)
        {
            var reason = "Recursion discipline failed: " + string.Join(" | ", recursionViolations);
            _logger?.LogWarning("Certification REJECT {BrickId}: {Reason}", brickId, reason);
            return new CertificationDecision
            {
                Admitted = false,
                FailureCheck = "recursion",
                Record = Fail("recursion", reason)
            };
        }

        // Spec A1.1/I-A: the analyzer fence runs first — a candidate carrying a defect a
        // deterministic analyzer can name never reaches the expensive witness/mutation gates.
        // Its gates_passed entry is per-run because A1.5 records the evaluated-diagnostic count.
        var analyzerOutcome = await _analyzerGate.EvaluateAsync(
            request.SourceCode,
            request.CompilationReferences,
            request.ConstraintManifest,
            request.TouchSet,
            cancellationToken).ConfigureAwait(false);
        analyzerGatePass = new CertificationGatePass
        {
            Name = "analyzer-gate",
            Version = GateVersion,
            Configuration = analyzerOutcome.GatePassConfiguration,
        };

        // R2.4 "furthest gate reached": the ordered prefix of gates already passed when the
        // named check failed. Local because the analyzer entry is per-run.
        IReadOnlyList<CertificationGatePass> GatesPassedBefore(string failedCheck) => failedCheck switch
        {
            "correctness" => new[] { analyzerGatePass },
            "mutation" => new[] { analyzerGatePass, correctnessPass },
            "determinism" => new[] { analyzerGatePass, correctnessPass, mutationPass },
            "dependency" => new[] { analyzerGatePass, correctnessPass, mutationPass, determinismPass },
            _ => Array.Empty<CertificationGatePass>()
        };

        CertificationRecord Fail(string check, string reason, MutationTestResult? mutation = null) =>
            BuildRecord(
                admitted: false,
                signed: false,
                status: "FAIL",
                brickId,
                timestamp,
                contentHash,
                reason,
                mutation,
                GatesPassedBefore(check),
                inputs);

        if (!analyzerOutcome.Passed)
        {
            var reason = analyzerOutcome.FormatProposerFeedback();
            _logger?.LogWarning("Certification REJECT {BrickId}: {Reason}", brickId, reason);
            return new CertificationDecision
            {
                Admitted = false,
                FailureCheck = "analyzer",
                Record = Fail("analyzer", reason)
            };
        }

        var brickTypeName = request.BrickTypeName ?? request.Brick.GetType().FullName ?? request.Brick.GetType().Name;

        // With a backend: one batched candidate execution (repeats=2) serves BOTH the
        // witness leg (repeat 0) and the determinism leg (repeat 0 vs 1 of case 0) —
        // observed before mutation so a mutant run can never poison the candidate's own
        // evidence. Backend infrastructure failures THROW (they are not candidate
        // evidence and must never become a signed verdict either way).
        IReadOnlyList<Ashlar.Core.Application.Certification.Ports.CandidateCaseObservation>? candidateObservations = null;
        if (backend is not null)
        {
            var report = await backend.ExecuteAsync(
                new Ashlar.Core.Application.Certification.Ports.CandidateExecutionJob(
                    new[] { new Ashlar.Core.Application.Certification.Ports.CandidateExecutionUnit("candidate", null, brickTypeName) },
                    request.Witness,
                    Repeats: 2),
                cancellationToken).ConfigureAwait(false);
            candidateObservations = report.Observations.Where(o => o.UnitId == "candidate").ToArray();
        }

        var witnessResult = backend is null
            ? await WitnessRunner.RunAsync(
                request.Brick,
                request.Witness,
                new AuditExecutionContext(),
                cancellationToken).ConfigureAwait(false)
            : WitnessRunner.JudgeObservations(request.Witness, candidateObservations!);

        if (!witnessResult.Passed)
        {
            var reason = $"Correctness check failed: {string.Join("; ", witnessResult.Failures)}";
            _logger?.LogWarning("Certification REJECT {BrickId}: {Reason}", brickId, reason);
            return new CertificationDecision
            {
                Admitted = false,
                FailureCheck = "correctness",
                Record = Fail("correctness", reason),
                WitnessFindings = witnessResult.Findings,
            };
        }

        var mutationResult = await _mutationEngine.RunAsync(
            request.SourceCode,
            brickTypeName,
            request.Witness,
            request.CompilationReferences,
            cancellationToken,
            backend,
            _analyzerGate).ConfigureAwait(false);

        if (mutationResult.TotalMutants == 0)
        {
            var reason = "Mutation escape check failed: no mutants were generated from catalog";
            return new CertificationDecision
            {
                Admitted = false,
                FailureCheck = "mutation",
                Record = Fail("mutation", reason, mutationResult)
            };
        }

        if (mutationResult.EscapeRate > 0)
        {
            var reason =
                $"Mutation escape check failed: escape_rate={mutationResult.EscapeRate:F2}, survivors=[{string.Join(", ", mutationResult.SurvivingMutantIds)}]"
                + DescribeSurvivors(mutationResult);
            _logger?.LogWarning("Certification REJECT {BrickId}: {Reason}", brickId, reason);
            return new CertificationDecision
            {
                Admitted = false,
                FailureCheck = "mutation",
                Record = Fail("mutation", reason, mutationResult)
            };
        }

        var determinism = backend is null
            ? await WitnessRunner.CheckDeterminismAsync(
                request.Brick,
                request.Witness,
                cancellationToken).ConfigureAwait(false)
            : JudgeObservationDeterminism(candidateObservations!);

        if (!determinism.Identical)
        {
            var reason = "Determinism check failed: outputs differ under AuditMode";
            _logger?.LogWarning("Certification REJECT {BrickId}: {Reason}", brickId, reason);
            return new CertificationDecision
            {
                Admitted = false,
                FailureCheck = "determinism",
                Record = Fail("determinism", reason, mutationResult)
            };
        }

        var dependency = BrickDependencyChecker.Check(request.ProjectPath, request.SourceCode);
        if (!dependency.Passed)
        {
            var reason = $"Dependency-cleanliness failed: {string.Join("; ", dependency.Violations)}";
            _logger?.LogWarning("Certification REJECT {BrickId}: {Reason}", brickId, reason);
            return new CertificationDecision
            {
                Admitted = false,
                FailureCheck = "dependency",
                Record = Fail("dependency", reason, mutationResult)
            };
        }

        var admittedRecord = BuildRecord(
            admitted: true,
            signed: true,
            status: "PASS",
            brickId,
            timestamp,
            contentHash,
            reason: null,
            mutation: mutationResult,
            gatesPassed: new[]
            {
                analyzerGatePass, correctnessPass, mutationPass, determinismPass, DependencyGatePass
            },
            inputs: inputs);
        admittedRecord = _signer.SignRecord(admittedRecord);

        _logger?.LogInformation("Certification ADMIT {BrickId} escape_rate=0", brickId);
        return new CertificationDecision
        {
            Admitted = true,
            Record = admittedRecord
        };
    }

    /// <summary>
    /// Spells out each surviving mutant: location, the edit, and the line it landed on.
    /// </summary>
    /// <remarks>
    /// A mutant id names an operator and a line, which is enough to locate a survivor and not
    /// enough to judge it. The two cases behind <c>escape_rate &gt; 0</c> need opposite
    /// responses — a weak witness wants more cases, an EQUIVALENT MUTANT (a rewrite that cannot
    /// change behaviour on any input) wants none, because no case can kill it and the candidate
    /// may be perfectly correct. Ledger S5 hit the second twice on <c>semver-parse</c>, and
    /// telling them apart meant decoding the recorded candidate by hand. The verdict is
    /// deliberately unchanged: equivalence is undecidable, so the gate still rejects and a human
    /// adjudicates — this only makes that adjudication a glance instead of an investigation.
    /// </remarks>
    private static string DescribeSurvivors(MutationTestResult mutation)
    {
        if (mutation.Survivors.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        foreach (var survivor in mutation.Survivors)
            sb.Append("; ").Append(survivor.Describe());

        return sb.ToString();
    }

    /// <summary>
    /// Determinism verdict over backend observations: case 0's two repeats must
    /// canonicalize identically (mirror of <see cref="WitnessRunner.CheckDeterminismAsync"/>).
    /// A missing repeat is nondeterminism-by-absence — fail-closed, never vacuously identical.
    /// </summary>
    private static (bool Identical, string? First, string? Second) JudgeObservationDeterminism(
        IReadOnlyList<Ashlar.Core.Application.Certification.Ports.CandidateCaseObservation> observations)
    {
        var first = observations.FirstOrDefault(o => o.CaseIndex == 0 && o.Repeat == 0);
        var second = observations.FirstOrDefault(o => o.CaseIndex == 0 && o.Repeat == 1);
        if (first is null && second is null)
            return (true, null, null); // No cases — same as the in-proc empty-witness path.
        if (first is null || second is null)
            return (false, first is null ? "<missing>" : "present", second is null ? "<missing>" : "present");

        var firstJson = WitnessRunner.CanonicalizeObservation(first);
        var secondJson = WitnessRunner.CanonicalizeObservation(second);
        return (firstJson == secondJson, firstJson, secondJson);
    }

    private IReadOnlyList<CertificationInput> BuildInputs(CertificationRequest request)
    {
        try
        {
            var witnessJson = JsonSerializer.Serialize(request.Witness, WitnessHashOptions);
            var inputs = new List<CertificationInput>
            {
                new CertificationInput
                {
                    Kind = "witness",
                    Id = request.Witness.BrickId,
                    Hash = BrickContentHasher.ComputeSha256(witnessJson)
                }
            };
            inputs.AddRange(request.AdditionalInputs);
            RecordCompileAuthority(request, inputs);
            // Where execution happened is certificate-relevant on PASS and FAIL alike:
            // a verdict minted over backend observations names the backend.
            if (request.ExecutionBackend is { } executionBackend)
            {
                inputs.Add(new CertificationInput
                {
                    Kind = "session-execution",
                    Id = executionBackend.Describe(),
                    Hash = BrickContentHasher.ComputeSha256(executionBackend.Describe()),
                });
            }
            // Autonomy R4.1: an explicitly declared lineage is recorded — depth bound to a
            // hash over the parent certificate chain — on PASS and FAIL records alike, so
            // even a refused laundering attempt leaves its claim in evidence. Requests
            // without a lineage (the human-authored default) record nothing extra.
            if (request.Lineage is { } lineage)
                inputs.Add(GenerationLineageInputs.From(lineage));
            return inputs;
        }
        catch (NotSupportedException ex)
        {
            // Was: log a warning and return request.AdditionalInputs -- i.e. mint the certificate
            // with NO witness input at all. The witness input is the record of WHAT WAS JUDGED,
            // and dropping it produces a structurally valid certificate that binds nothing about
            // the specification it was judged against. Neither Default nor Strict requires a
            // witness input (they require gate-emitted-artifact and certifier-identity), so such a
            // record verifies cleanly downstream and no consumer can tell.
            //
            // The trigger is not exotic. JsonSerializer.Serialize throws NotSupportedException
            // when reflection-based serialization is disabled, which is every trimmed and
            // ahead-of-time publish -- so the fail-open was reachable by publish mode alone, with
            // one warning in a log nobody reads at admission time.
            //
            // Refusing at mint time is the precedent this repository already set for bytes nobody
            // can vouch for (#584: signing propagates, verification refuses without throwing).
            throw new CanonicalOutputException(
                $"The witness spec for brick '{request.Witness.BrickId}' could not be serialized for "
                + "input hashing, so the certificate cannot record what it judged. Refusing to mint "
                + "a certificate with no witness input rather than minting one that binds nothing.",
                ex);
        }
    }

    /// <summary>
    /// When a PE is bound, the gate inspects and activates those bytes and witnesses
    /// the resulting instance. A caller-supplied brick object is not the judged program.
    /// </summary>
    private static CertificationRequest BindEmittedArtifact(CertificationRequest request)
    {
        if (request.EmittedArtifact is not { } artifact)
            return request;

        var bytesHash = BrickContentHasher.ComputeSha256(artifact.AssemblyBytes);
        if (!string.Equals(bytesHash, artifact.AssemblySha256, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "gate-emitted artifact hash does not match the supplied bytes.");

        var sourceHash = BrickContentHasher.ComputeSha256(request.SourceCode);
        if (!string.Equals(sourceHash, artifact.SourceSha256, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "gate-emitted artifact was not compiled from this request's source.");

        var brick = CertifiedBrickActivator.Activate(artifact);
        if (!string.Equals(brick.Id, request.Brick.Id, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"gate-emitted brick Id '{brick.Id}' does not match request '{request.Brick.Id}'.");

        return request with { Brick = brick, BrickTypeName = artifact.BrickTypeName };
    }

    private static void RecordCompileAuthority(CertificationRequest request, List<CertificationInput> inputs)
    {
        inputs.Add(CertifierIdentity.ToInput());
        inputs.Add(new CertificationInput
        {
            Kind = CertificationInputKinds.CompileOptions,
            Id = BrickCompileOptions.LanguageVersionName,
            Hash = BrickContentHasher.ComputeSha256(BrickCompileOptions.CanonicalBlob)
        });

        if (request.EmittedArtifact is { } artifact)
        {
            inputs.Add(new CertificationInput
            {
                Kind = CertificationInputKinds.GateEmittedArtifact,
                Id = artifact.BrickTypeName,
                Hash = artifact.AssemblySha256
            });
            inputs.Add(IlImportFence.ToInput());
            inputs.Add(new CertificationInput
            {
                Kind = CertificationInputKinds.ExecutionMode,
                Id = "gate-emitted",
                Hash = BrickContentHasher.ComputeSha256("gate-emitted")
            });
        }
        else
        {
            inputs.Add(new CertificationInput
            {
                Kind = CertificationInputKinds.ExecutionMode,
                Id = "in-process-fixture",
                Hash = BrickContentHasher.ComputeSha256("in-process-fixture")
            });
        }
    }

    private static CertificationRecord BuildRecord(
        bool admitted,
        bool signed,
        string status,
        string brickId,
        DateTimeOffset timestamp,
        string contentHash,
        string? reason,
        MutationTestResult? mutation,
        IReadOnlyList<CertificationGatePass> gatesPassed,
        IReadOnlyList<CertificationInput> inputs)
    {
        return new CertificationRecord
        {
            Status = status,
            Stage = "S0-S2",
            Admitted = admitted,
            Signed = signed,
            Timestamp = timestamp,
            BrickId = brickId,
            ContentHash = contentHash,
            EscapeRate = mutation?.EscapeRate ?? 0,
            TotalMutants = mutation?.TotalMutants ?? 0,
            SurvivingMutants = mutation?.SurvivingMutantIds.Count ?? 0,
            KilledMutants = mutation?.KilledMutantIds ?? Array.Empty<string>(),
            SurvivingMutantIds = mutation?.SurvivingMutantIds ?? Array.Empty<string>(),
            Reason = reason,
            Gate = "Ashlar.Infrastructure.Certification.CertificationGate",
            SchemaVersion = CertificationRecordData.TrustLoopSchemaVersion,
            GatesPassed = gatesPassed,
            Inputs = inputs
        };
    }
}
