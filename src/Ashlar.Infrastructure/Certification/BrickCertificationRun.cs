using System.Text.Json;
using Ashlar.Core.Application.Certification.Models;
using Ashlar.Core.Application.Certification.Ports;

namespace Ashlar.Infrastructure.Certification;

/// <summary>Terminal state of one certification run.</summary>
public enum BrickCertificationOutcome
{
    /// <summary>The gate certified the brick and the registry admitted it.</summary>
    Admitted,

    /// <summary>The gate ran and refused. The record on disk is that verdict, signed.</summary>
    Rejected,

    /// <summary>
    /// The loader or a fence threw before the gate ever ran, so there is no gate verdict —
    /// only a signed <see cref="LoadRefusalRecord"/>. Distinct from <see cref="Rejected"/>
    /// because the two say different things about the candidate: one was judged, one never
    /// reached the judge.
    /// </summary>
    LoadRefused
}

/// <summary>Everything one certification run needs. Paths are resolved by the run, not by the caller.</summary>
public sealed record BrickCertificationRunRequest
{
    /// <summary>Directory holding the brick's single author <c>.cs</c> file and its <c>.csproj</c>.</summary>
    public required string BrickProjectDirectory { get; init; }

    /// <summary>Witness spec JSON the gate judges the candidate against.</summary>
    public required string WitnessSpecPath { get; init; }

    /// <summary>
    /// Where the record is written. Null takes the historical default,
    /// <c>Path.Combine(brickDir, "..", "certification-record.json")</c> — deliberately NOT
    /// normalized, because that literal <c>..</c> is what both callers echo as <c>Record: …</c>
    /// and what scripted consumers of <c>tools/Ashlar.CertifyBrick</c> already parse.
    /// </summary>
    public string? RecordPath { get; init; }

    /// <summary>
    /// Signer for the gate and for a load refusal. Null mints the standard one. Present so a test
    /// can pin an explicit key; neither shipped caller passes it, so both keep signing exactly as
    /// the tool always has.
    /// </summary>
    public CertificationRecordSigner? Signer { get; init; }
}

/// <summary>
/// What one run produced. Deliberately carries no exit code and no prose: the exit-code map and the
/// wording are each caller's published contract.
/// </summary>
public sealed record BrickCertificationRunResult
{
    /// <summary>Which terminal state the run reached.</summary>
    public required BrickCertificationOutcome Outcome { get; init; }

    /// <summary>Whether the brick was admitted.</summary>
    public bool Admitted => Outcome == BrickCertificationOutcome.Admitted;

    /// <summary>The gate's record, or the signed load refusal. Never null — a refusal is evidence too.</summary>
    public required CertificationRecord Record { get; init; }

    /// <summary>Which check refused, or <see cref="LoadRefusalRecord.Stage"/> on the load path.</summary>
    public string? FailureCheck { get; init; }

    /// <summary>The exact record path string a caller should echo, un-normalized default included.</summary>
    public required string RecordPath { get; init; }

    /// <summary>Directory the record, the brick-id copy and the emitted assembly were written to.</summary>
    public required string RecordDirectory { get; init; }

    /// <summary>Path of the gate-emitted assembly when one was written, else null.</summary>
    public string? ArtifactPath { get; init; }

    /// <summary>Path of the <c>&lt;brickId&gt;.json</c> copy when one was written, else null.</summary>
    public string? BrickIdRecordPath { get; init; }

    /// <summary>
    /// False only when persisting a load refusal threw and was swallowed. The run still fails closed
    /// — the caller's non-zero exit is the refusal — but a caller that promises "a refuse leaves
    /// evidence" can now tell when it could not keep that promise.
    /// </summary>
    public bool RecordPersisted { get; init; } = true;

    /// <summary>Structured witness failures behind a correctness refusal.</summary>
    public IReadOnlyList<WitnessFinding> WitnessFindings { get; init; } = Array.Empty<WitnessFinding>();

    /// <summary>Structured probe findings attached to a refusal (G4).</summary>
    public IReadOnlyList<DiagnosticProbeFinding> ProbeFindings { get; init; } = Array.Empty<DiagnosticProbeFinding>();
}

/// <summary>
/// The one certification pipeline: load the brick project, run it through the gate, and leave signed
/// evidence on disk. <c>tools/Ashlar.CertifyBrick</c> and <c>ashlar certify brick</c> both call this,
/// so the loop a consumer runs from outside the repo is the loop CI runs inside it — not a second
/// implementation that drifts.
///
/// <para>Self-constructing rather than DI-resolved on purpose. The record store is bound to a
/// directory derived per invocation from the caller's record path, which a composition-time
/// registration cannot know; and <c>AddCertificationGate</c>'s <c>TryAddSingleton</c> would let a
/// host-supplied signer displace the one the tool uses, so the two callers would sign differently
/// while looking identical.</para>
///
/// <para>The store is built with the single-argument constructor, which mints its own signer, while
/// the gate and any load refusal use a second one. Two signer instances is what the tool has always
/// done; collapsing them is a behaviour change wearing a cleanup's clothes.</para>
/// </summary>
public static class BrickCertificationRun
{
    private static readonly JsonSerializerOptions RecordJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>Runs the pipeline. Writes files; writes no console output and maps no exit codes.</summary>
    public static async Task<BrickCertificationRunResult> ExecuteAsync(
        BrickCertificationRunRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var brickDir = Path.GetFullPath(request.BrickProjectDirectory);
        var witnessPath = Path.GetFullPath(request.WitnessSpecPath);
        // Explicit paths are normalized; the default is not. That asymmetry is the tool's and it is
        // load-bearing: normalizing the default would change the `Record: …` line every existing
        // caller already reads.
        var recordPath = request.RecordPath is { } explicitPath
            ? Path.GetFullPath(explicitPath)
            : Path.Combine(brickDir, "..", "certification-record.json");

        var recordDir = Path.GetDirectoryName(recordPath)!;
        Directory.CreateDirectory(recordDir);

        var store = new FileCertificationRecordStore(recordDir);
        var signer = request.Signer ?? new CertificationRecordSigner();
        var gate = new CertificationGate(signer);
        var registry = new CertifiedBrickRegistry(store, signer);
        var admission = new CertifiedBrickAdmission(gate, registry);

        try
        {
            var certificationRequest = await BrickCertificationProjectLoader
                .LoadAsync(brickDir, witnessPath, cancellationToken).ConfigureAwait(false);
            var decision = await admission
                .CertifyAndAdmitAsync(certificationRequest, cancellationToken).ConfigureAwait(false);

            await File.WriteAllTextAsync(
                recordPath,
                JsonSerializer.Serialize(decision.Record, RecordJsonOptions),
                cancellationToken).ConfigureAwait(false);

            // Written on a REJECT too. The bytes the gate judged are the evidence behind the verdict
            // and the export/reuse scripts downstream read them either way; moving this inside an
            // `if (admitted)` looks like tidying and is a behaviour change.
            string? artifactPath = null;
            if (certificationRequest.EmittedArtifact is { } artifact)
            {
                artifactPath = Path.Combine(recordDir, CertifiedArtifactExporter.ArtifactFileName);
                await File.WriteAllBytesAsync(artifactPath, artifact.AssemblyBytes, cancellationToken).ConfigureAwait(false);
            }

            // On ADMIT the registry has already saved <brickId>.json through the store; this second
            // write is what puts the record there on a REJECT as well. The comparison stays
            // OrdinalIgnoreCase because it is what decides, today, whether the copy happens at all.
            var brickIdRecordPath = Path.Combine(recordDir, $"{decision.Record.BrickId}.json");
            var wroteBrickIdRecord = !string.Equals(recordPath, brickIdRecordPath, StringComparison.OrdinalIgnoreCase);
            if (wroteBrickIdRecord)
            {
                await File.WriteAllTextAsync(
                    brickIdRecordPath,
                    JsonSerializer.Serialize(decision.Record, RecordJsonOptions),
                    cancellationToken).ConfigureAwait(false);
            }

            return new BrickCertificationRunResult
            {
                Outcome = decision.Admitted ? BrickCertificationOutcome.Admitted : BrickCertificationOutcome.Rejected,
                Record = decision.Record,
                FailureCheck = decision.FailureCheck,
                RecordPath = recordPath,
                RecordDirectory = recordDir,
                ArtifactPath = artifactPath,
                BrickIdRecordPath = wroteBrickIdRecord ? brickIdRecordPath : null,
                WitnessFindings = decision.WitnessFindings,
                ProbeFindings = decision.ProbeFindings
            };
        }
        // A cancellation is not a verdict. Everything else escaping the loader or the fence is
        // recorded as a refusal, but signing "the operator pressed Ctrl+C" as a FAIL would put a lie
        // in the ledger. The tool passes no token, so its behaviour is untouched by this filter.
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // A load/fence refusal used to print the exception and exit with no file.
            // "No record" is what Get() returns for an unsigned or missing file, so the
            // refuse was indistinguishable from "never certified." Persist a signed FAIL.
            var brickId = TryWitnessBrickId(witnessPath) ?? new DirectoryInfo(brickDir).Name;
            var refusal = LoadRefusalRecord.Create(signer, brickId, ex.Message);
            var persisted = true;
            try
            {
                store.Save(refusal);
                File.WriteAllText(recordPath, JsonSerializer.Serialize(refusal, RecordJsonOptions));
            }
            catch
            {
                /* still fail closed — the caller's non-zero exit is the refusal */
                persisted = false;
            }

            return new BrickCertificationRunResult
            {
                Outcome = BrickCertificationOutcome.LoadRefused,
                Record = refusal,
                FailureCheck = LoadRefusalRecord.Stage,
                RecordPath = recordPath,
                RecordDirectory = recordDir,
                RecordPersisted = persisted
            };
        }
    }

    private static string? TryWitnessBrickId(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.TryGetProperty("brickId", out var id) ? id.GetString() : null;
        }
        catch
        {
            return null;
        }
    }
}
