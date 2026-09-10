using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text.Json;
using Ashlar.Infrastructure.Certification;

namespace Ashlar.CLI.Commands;

/// <summary>
/// <c>ashlar certify brick</c> — put a brick project through the certification gate and leave a
/// signed record behind.
///
/// <para>The gate itself was only ever reachable as <c>dotnet run --project
/// tools/Ashlar.CertifyBrick</c>, which means it needed a clone of this repository. That made the
/// certified-brick loop unusable from a consumer's own project, where the brick actually lives.
/// This verb is the same pipeline (<see cref="BrickCertificationRun"/>) behind the installed CLI;
/// the tool stays exactly where the gate script and three docs cite it.</para>
///
/// <para><b>Exit codes deliberately mirror the tool's, not the CLI's usual vocabulary.</b> 0 admit,
/// 1 refuse, 2 usage — so a script can swap <c>dotnet run --project tools/Ashlar.CertifyBrick</c>
/// for <c>ashlar certify brick</c> without re-reading its exit codes, which is the whole point of
/// the verb. That is why a refusal here is 1 and not the 65 that <c>verify</c> and <c>gates</c> use
/// for a course that did not pass: 65 would silently break every existing caller the moment they
/// moved to the CLI. Do not "unify" the two.</para>
///
/// <para>What that map costs: System.CommandLine answers a parse error with 1 as well, so a
/// mistyped invocation (<c>--witness</c> with no value, an unknown flag) is indistinguishable by
/// exit code from a brick that was judged and refused. Both handlers here claim back what they
/// can — a bare <c>certify</c> and a <c>certify brick</c> missing an argument exit 2 — but the
/// parser's own 1 cannot be reclaimed without giving up the map. The record is the tiebreak: a
/// refusal always leaves one, a parse error never does.</para>
/// </summary>
public sealed class CertifyCommand : Command
{
    /// <summary>The gate admitted the brick.</summary>
    private const int ExitAdmitted = 0;

    /// <summary>The gate refused, or the loader/fence did. Either way a signed record was written.</summary>
    private const int ExitRefused = 1;

    /// <summary>Nothing ran: the invocation was missing an argument the tool also requires.</summary>
    private const int ExitUsage = 2;

    /// <summary>Printed by both usage paths, so the two cannot drift into two dialects.</summary>
    private const string UsageLine =
        "Usage: ashlar certify brick <brickProjectDir> --witness <witnessSpec.json> [--record <path>]";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>Creates a new CertifyCommand instance.</summary>
    public CertifyCommand() : base("certify", "Run a brick through the certification gate and write a signed record.")
    {
        AddCommand(BuildBrick());

        // `keys`, `pkg` and `policy` leave their bare form to System.CommandLine, which reports a
        // missing subcommand as exit 1. For them 1 is only "failed"; here it is REFUSE, and REFUSE
        // promises a signed record on disk that a bare `ashlar certify` never wrote. Claiming the
        // bare verb keeps 2 meaning usage the whole way down, as it does in the tool.
        this.SetHandler((InvocationContext ctx) =>
        {
            Console.Error.WriteLine(UsageLine);
            ctx.ExitCode = ExitUsage;
        });
    }

    private static Command BuildBrick()
    {
        // Arity and requiredness are enforced in the handler rather than by the parser, because
        // System.CommandLine answers a parse error with exit 1 — which here is the REFUSE code. A
        // caller distinguishing "the brick failed" from "I typed the command wrong" would read the
        // second as the first. Validating by hand keeps 2 meaning usage, as it does in the tool.
        var brickDirArg = new Argument<string?>(
            name: "brickProjectDir",
            getDefaultValue: () => null,
            description: "Directory holding the brick's single author .cs file and its .csproj.")
        {
            Arity = ArgumentArity.ZeroOrOne
        };
        var witnessOpt = new Option<string?>(
            name: "--witness",
            description: "Witness spec JSON the gate judges the brick against (required).");
        var recordOpt = new Option<string?>(
            name: "--record",
            description: "Where to write the certification record (default: <brickProjectDir>/../certification-record.json).");

        var cmd = new Command("brick", "Certify one brick project and write its signed record.")
        {
            brickDirArg, witnessOpt, recordOpt
        };

        cmd.SetHandler(async (InvocationContext ctx) =>
        {
            var brickDir = ctx.ParseResult.GetValueForArgument(brickDirArg);
            var witness = ctx.ParseResult.GetValueForOption(witnessOpt);
            if (string.IsNullOrWhiteSpace(brickDir) || string.IsNullOrWhiteSpace(witness))
            {
                Console.Error.WriteLine(UsageLine);
                ctx.ExitCode = ExitUsage;
                return;
            }

            // No existence check on either path. The loader and the IL fence are what refuse a
            // missing or malformed candidate, and their refusal is a SIGNED FAIL record — evidence
            // that this brick was offered and turned away. Pre-empting them with a usage error would
            // exit 2 and write nothing, and "no record" reads as "never certified".
            var result = await BrickCertificationRun.ExecuteAsync(
                new BrickCertificationRunRequest
                {
                    BrickProjectDirectory = brickDir,
                    WitnessSpecPath = witness,
                    RecordPath = ctx.ParseResult.GetValueForOption(recordOpt)
                },
                ctx.GetCancellationToken()).ConfigureAwait(false);

            ctx.ExitCode = CommandExecutionSupport.WantsJson(ctx.ParseResult)
                ? WriteJson(result, Console.Out)
                : WriteProse(result, Console.Out, Console.Error);
        });

        return cmd;
    }

    /// <summary>
    /// The machine-readable verdict: exactly one JSON document on stdout and nothing else, on every
    /// outcome. <c>--format-json</c> is not refused here the way <c>verify</c> and <c>policy</c>
    /// refuse it, because this command HAS a rendering — the verdict is data, not a wall.
    /// </summary>
    internal static int WriteJson(BrickCertificationRunResult result, TextWriter stdout)
    {
        stdout.WriteLine(JsonSerializer.Serialize(
            new
            {
                brickId = result.Record.BrickId,
                admitted = result.Admitted,
                outcome = Label(result.Outcome),
                status = result.Record.Status,
                stage = result.Record.Stage,
                signed = result.Record.Signed,
                escapeRate = result.Record.EscapeRate,
                totalMutants = result.Record.TotalMutants,
                killedMutants = result.Record.KilledMutants.Count,
                survivingMutants = result.Record.SurvivingMutants,
                failureCheck = result.FailureCheck,
                reason = result.Record.Reason,
                recordPath = result.RecordPath,
                recordPersisted = result.RecordPersisted,
                artifactPath = result.ArtifactPath
            },
            JsonOptions));

        return ExitCodeFor(result.Outcome);
    }

    /// <summary>
    /// The human verdict, word for word what <c>tools/Ashlar.CertifyBrick</c> prints — same two
    /// lines, same streams — so the two entry points are not two dialects of the same result.
    /// </summary>
    internal static int WriteProse(BrickCertificationRunResult result, TextWriter stdout, TextWriter stderr)
    {
        switch (result.Outcome)
        {
            case BrickCertificationOutcome.Admitted:
                stdout.WriteLine($"ADMIT brick={result.Record.BrickId} escape_rate={result.Record.EscapeRate} mutants_killed={result.Record.KilledMutants.Count}");
                stdout.WriteLine($"Record: {result.RecordPath}");
                return ExitAdmitted;

            case BrickCertificationOutcome.LoadRefused:
                stderr.WriteLine($"REJECT (load): {result.Record.Reason}");
                stderr.WriteLine($"Record: {result.RecordPath}");
                return ExitRefused;

            default:
                stderr.WriteLine($"REJECT ({result.FailureCheck}): {result.Record.Reason}");
                stderr.WriteLine($"Record: {result.RecordPath}");
                return ExitRefused;
        }
    }

    private static int ExitCodeFor(BrickCertificationOutcome outcome) =>
        outcome == BrickCertificationOutcome.Admitted ? ExitAdmitted : ExitRefused;

    private static string Label(BrickCertificationOutcome outcome) => outcome switch
    {
        BrickCertificationOutcome.Admitted => "admitted",
        BrickCertificationOutcome.Rejected => "rejected",
        _ => "load-refused"
    };
}
