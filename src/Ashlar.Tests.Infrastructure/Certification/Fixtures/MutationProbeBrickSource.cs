namespace Ashlar.Tests.Infrastructure.Certification.Fixtures;

/// <summary>Mutation probe brick source.</summary>
public static class MutationProbeBrickSource
{
    public const string Code = """
using Ashlar.Core.Domain.Bricks;
using Ashlar.Core.Domain.Execution;

namespace Ashlar.Tests.Infrastructure.Certification.Fixtures;

/// <summary>Mutation probe brick.</summary>
public sealed class MutationProbeBrick : DomainBrick
{
    public MutationProbeBrick()
    {
        Id = "mutation-probe-brick";
        Name = "Mutation Probe DomainBrick";
        Version = "1.0.0";
        Category = BrickCategory.Analysis;
        Description = "Log scanner for mutation gate tests.";
        Interface = new BrickInterface
        {
            Inputs = [new BrickInputDefinition("logText", "string", "log")],
            Outputs =
            [
                new BrickOutputDefinition("errorCount", "int", "count"),
                new BrickOutputDefinition("firstErrorMessage", "string", "first")
            ]
        };
    }

    public override Task<BrickOutput> ExecuteAsync(
        BrickInput input,
        ImplementationType implementation,
        IExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var logText = input.Get<string>("logText") ?? string.Empty;
        var errorLines = new List<string>();
        foreach (var line in logText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Contains("ERROR", StringComparison.Ordinal))
                errorLines.Add(line);
        }

        var errorCount = errorLines.Count;
        var firstErrorMessage = errorCount > 0 ? ExtractErrorMessage(errorLines[0]) : string.Empty;
        var output = new BrickOutput
        {
            Summary = $"Found {errorCount} ERROR line(s); first: {firstErrorMessage}"
        };
        output.Set("errorCount", errorCount);
        output.Set("firstErrorMessage", firstErrorMessage);
        return Task.FromResult(output);
    }

    private static string ExtractErrorMessage(string line)
    {
        // The caller only passes lines that contain the marker, so a guard for its absence would be a
        // branch no witness can reach, and every mutant of an unreachable branch is equivalent. 6 is
        // the marker plus the separator after it; no TrimStart, so an off-by-one here is observable.
        return line[(line.IndexOf("ERROR", StringComparison.Ordinal) + 6)..];
    }
}
""";
}
