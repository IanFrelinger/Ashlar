using System.CommandLine;
using System.CommandLine.Invocation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ashlar.Core.Application.Paths;
using Ashlar.Infrastructure;
using Ashlar.Infrastructure.Adaptation;
using Ashlar.Infrastructure.SelfContext;
using Ashlar.Infrastructure.SelfImprovement;
using Ashlar.Infrastructure.Validation.Parsers;

namespace Ashlar.CLI.Commands;

/// <summary>
/// CLI command that parses TRX test result files and ingests failures into
/// the ITestFailureStore, closing the loop for self-improvement.
/// Usage: ashlar ingest-failures --trx-path ./TestResults
/// </summary>
public sealed class IngestFailuresCommand : Command
{
    /// <summary>Creates a new IngestFailuresCommand instance.</summary>
    public IngestFailuresCommand() : base("ingest-failures", "Parse TRX test result files and ingest failures into the self-improvement store.")
    {
        var trxPathOpt = new Option<string>("--trx-path", "Path to a TRX file or directory containing TRX files") { IsRequired = true };
        var storePathOpt = new Option<string?>("--store-path", "Directory for ashlar dbs (default: ASHLAR_STATE_DIR, else <repo root>/.ashlar/state)");

        AddOption(trxPathOpt);
        AddOption(storePathOpt);

        this.SetHandler(async (InvocationContext ctx) =>
        {
            var trxPath = ctx.ParseResult.GetValueForOption(trxPathOpt)!;
            var storePathOverride = ctx.ParseResult.GetValueForOption(storePathOpt);
            ctx.ExitCode = await ExecuteAsync(trxPath, storePathOverride);
        });
    }

    private static async Task<int> ExecuteAsync(string trxPath, string? storePathOverride)
    {
        var repoRoot = RepoPathResolver.FindRepoRoot();
        var storePath = !string.IsNullOrWhiteSpace(storePathOverride)
            ? Path.Combine(Path.GetFullPath(storePathOverride), "ashlar-patterns.db")
            : Path.Combine(RepoPathResolver.ResolveStateDirectory(repoRoot), "ashlar-patterns.db");

        var services = new ServiceCollection()
            .AddLogging(b => b.AddConsole())
            .AddAdaptationInfrastructure(storePath)
            .AddSelfContextInfrastructure(storePath)
            .BuildServiceProvider();

        var loggerFactory = services.GetRequiredService<ILoggerFactory>();
        var parser = new TrxTestResultParser(loggerFactory.CreateLogger<TrxTestResultParser>());
        var store = services.GetRequiredService<Ashlar.Core.Application.SelfContext.Ports.ITestFailureStore>();
        var bridge = new TestFailureIngestionBridge(store);

        var trxFiles = ResolveTrxFiles(trxPath);
        if (trxFiles.Count == 0)
        {
            Console.WriteLine($"No TRX files found at: {trxPath}");
            return 1;
        }

        Console.WriteLine($"Found {trxFiles.Count} TRX file(s).");
        var totalIngested = 0;

        foreach (var file in trxFiles)
        {
            var results = await parser.ParseAsync(file).ConfigureAwait(false);
            var ingested = await bridge.IngestAsync(results).ConfigureAwait(false);
            totalIngested += ingested;
            Console.WriteLine($"  {file.Name}: {results.Count} result(s), {ingested} failure(s) ingested");
        }

        Console.WriteLine($"Total: {totalIngested} failure(s) ingested into store.");
        return 0;
    }

    private static List<FileInfo> ResolveTrxFiles(string path)
    {
        if (File.Exists(path) && path.EndsWith(".trx", StringComparison.OrdinalIgnoreCase))
            return new List<FileInfo> { new FileInfo(path) };

        if (Directory.Exists(path))
        {
            return Directory.GetFiles(path, "*.trx", SearchOption.AllDirectories)
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ToList();
        }

        return new List<FileInfo>();
    }
}
