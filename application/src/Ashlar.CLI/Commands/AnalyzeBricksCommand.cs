using System.CommandLine;
using System.CommandLine.Invocation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ashlar.Core.Application.Analysis.Models;
using Ashlar.Core.Application.Analysis.Ports;
using Ashlar.Core.Application.Paths;
using Ashlar.Infrastructure.Analysis;

namespace Ashlar.CLI.Commands;

/// <summary>
/// Analyzes brick/generated code for schema, safety, and performance.
/// Dogfood: run against Block 1 (Observation) code first.
/// </summary>
public sealed class AnalyzeBricksCommand : Command
{
    /// <summary>Creates a new AnalyzeBricksCommand instance.</summary>
    public AnalyzeBricksCommand() : base("bricks", "Analyze brick code (schema, safety, performance). Dogfood: defaults to Block 1 Observation path.")
    {
        var pathOpt = new Option<string?>("--path", "Path to analyze (dir or .cs file). Default: Ashlar Observation folders.");
        var recursiveOpt = new Option<bool>("--recursive", () => false, "Also analyze analyzer code (Analysis, Adaptation folders).");
        AddOption(pathOpt);
        AddOption(recursiveOpt);
        this.SetHandler(async (InvocationContext ctx) =>
        {
            var path = ctx.ParseResult.GetValueForOption(pathOpt);
            var recursive = ctx.ParseResult.GetValueForOption(recursiveOpt);
            ctx.ExitCode = await ExecuteAsync(path, recursive);
        });
    }

    private static async Task<int> ExecuteAsync(string? path, bool recursive = false)
    {
        var services = new ServiceCollection()
            .AddLogging(b => b.AddConsole())
            .AddCodeAnalyzers()
            .BuildServiceProvider();

        var analyzer = services.GetRequiredService<IBrickStaticAnalyzer>();
        var targetPath = path ?? RepoPathResolver.FindBlock1ObservationPath();

        Console.WriteLine($"Brick static analyzer (Block 2)");
        Console.WriteLine($"  Target: {targetPath}");
        Console.WriteLine($"  Recursive: {recursive}");
        Console.WriteLine();

        var result = await analyzer.AnalyzeSourceAsync(targetPath, recursive).ConfigureAwait(false);

        if (result.Passed)
        {
            Console.WriteLine("No violations found.");
            return 0;
        }

        Console.WriteLine($"Found {result.TotalViolations} violation(s):");
        foreach (var v in result.Violations)
        {
            var loc = v.LineNumber.HasValue ? $"{v.FilePath}:{v.LineNumber}" : v.FilePath;
            Console.WriteLine($"  [{v.Rule}] {loc}");
            Console.WriteLine($"    {v.Message}");
        }

        return 1;
    }
}
