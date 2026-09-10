// CLI tool for brick certification workflows.
using Ashlar.Infrastructure.Certification;

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: Ashlar.CertifyBrick <brickProjectDir> <witnessSpec.json> [recordOutputPath]");
    return 2;
}

// The pipeline moved to BrickCertificationRun so that `ashlar certify brick` runs the SAME code —
// one gate, one refusal path, one set of files on disk. What stays here is this tool's published
// contract: the positional argument order, the two output lines, and the 0/1/2 exit map that
// scripts/certify-brick-gate.sh and its callers already read. The CLI verb mirrors that map for the
// same reason; if the two ever have to diverge, they diverge in their own callers, not in the seam.
var result = await BrickCertificationRun.ExecuteAsync(new BrickCertificationRunRequest
{
    BrickProjectDirectory = args[0],
    WitnessSpecPath = args[1],
    RecordPath = args.Length > 2 ? args[2] : null
}).ConfigureAwait(false);

switch (result.Outcome)
{
    case BrickCertificationOutcome.Admitted:
        Console.WriteLine($"ADMIT brick={result.Record.BrickId} escape_rate={result.Record.EscapeRate} mutants_killed={result.Record.KilledMutants.Count}");
        Console.WriteLine($"Record: {result.RecordPath}");
        return 0;

    case BrickCertificationOutcome.LoadRefused:
        // LoadRefusalRecord.Create stores the exception message verbatim as the reason, so this is
        // the same string the pre-extraction code printed — no exception has to cross the seam.
        Console.Error.WriteLine($"REJECT (load): {result.Record.Reason}");
        Console.Error.WriteLine($"Record: {result.RecordPath}");
        return 1;

    default:
        Console.Error.WriteLine($"REJECT ({result.FailureCheck}): {result.Record.Reason}");
        Console.Error.WriteLine($"Record: {result.RecordPath}");
        return 1;
}
