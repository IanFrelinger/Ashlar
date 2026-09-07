using Ashlar.Core.Domain.Bricks;
using Ashlar.Core.Domain.Execution;

namespace Ashlar.Tests.Infrastructure.Certification.Fixtures;

/// <summary>Uses Guid.NewGuid — fails the determinism check under AuditMode while staying invisible to the static analyzer catalog (the honesty discipline leaves Guid.NewGuid alone), so this fixture exercises the runtime gate, not the analyzer fence.</summary>
public sealed class NondeterministicBrick : DomainBrick
{
    public NondeterministicBrick()
    {
        Id = "nondeterministic-brick";
        Name = "Nondeterministic DomainBrick";
        Version = "1.0.0";
        Category = BrickCategory.Analysis;
        Description = "Nondeterministic output.";
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
        foreach (var line in logText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Contains("ERROR", StringComparison.Ordinal))
                errorLines.Add(line);
        }

        var errorCount = errorLines.Count;
        var firstErrorMessage = errorCount > 0 ? ExtractErrorMessage(errorLines[0]) : string.Empty;
        var output = new BrickOutput
        {
            Summary = $"Found {errorCount} noise={Guid.NewGuid():N}"
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
