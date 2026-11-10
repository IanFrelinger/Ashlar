using System.CommandLine;
using System.CommandLine.Invocation;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ashlar.Core.Application.Observation.Models;
using Ashlar.Core.Application.Observation.Ports;
using Ashlar.Core.Application.Paths;
using Ashlar.Infrastructure.Observation;

namespace Ashlar.CLI.Commands;

/// <summary>
/// Block 1 dogfood validation: Observation pipeline watches Ashlar repo and stores patterns from file events.
/// </summary>
internal static class DogfoodBlock1Command
{
    /// <summary>Executes the command handler and returns a process exit code.</summary>
    public static async Task<int> ExecuteAsync(bool json)
    {
        var repoRoot = RepoPathResolver.FindRepoRoot();
        var slnPath = Path.Combine(repoRoot, "Ashlar.sln");
        if (!File.Exists(slnPath))
        {
            if (json)
                Console.WriteLine("{\"block\":\"block1\",\"passed\":false,\"reason\":\"Not in Ashlar repo (Ashlar.sln not found)\"}");
            else
                Console.Error.WriteLine("Block 1 dogfood gate FAILED: Not in Ashlar repo (Ashlar.sln not found). Run from Ashlar repository root.");
            return 1;
        }

        var srcDir = Path.Combine(repoRoot, "src");
        if (!Directory.Exists(srcDir))
        {
            if (json)
                Console.WriteLine("{\"block\":\"block1\",\"passed\":false,\"reason\":\"src/ directory not found\"}");
            else
                Console.Error.WriteLine("Block 1 dogfood gate FAILED: src/ directory not found.");
            return 1;
        }

        var storePath = Path.Combine(Path.GetTempPath(), $"ashlar-dogfood-block1-{Guid.NewGuid():N}.db");
        var verifyFile = Path.Combine(srcDir, ".dogfood-block1-verify.tmp");

        try
        {
            var store = new LiteDbPatternStore(storePath);
            var patternDetector = new PatternDetector(
                TimeSpan.FromMinutes(5),
                3,
                store,
                null);

            using var loggerFactory = LoggerFactory.Create(b => b.AddConsole(o =>
            {
                if (json) o.LogToStandardErrorThreshold = LogLevel.Trace;
            }).SetMinimumLevel(LogLevel.Warning));
            var fileSource = new FileSystemEventSource(
                new[] { srcDir },
                repoRoot,
                new[] { "*" },
                loggerFactory.CreateLogger<FileSystemEventSource>());

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var eventCount = 0;
            var processTask = Task.Run(async () =>
            {
                await foreach (var evt in fileSource.SubscribeAsync(cts.Token).WithCancellation(cts.Token))
                {
                    eventCount++;
                    await patternDetector.ProcessAsync(evt, cts.Token);
                    if (eventCount >= 5)
                        break;
                }
            }, cts.Token);

            await File.WriteAllTextAsync(verifyFile, "// dogfood block1 v1", cts.Token);
            await Task.Delay(500, cts.Token);
            await File.WriteAllTextAsync(verifyFile, "// dogfood block1 v2", cts.Token);
            await Task.Delay(500, cts.Token);
            await File.WriteAllTextAsync(verifyFile, "// dogfood block1 v3", cts.Token);
            await Task.Delay(500, cts.Token);
            await File.WriteAllTextAsync(verifyFile, "// dogfood block1 v4", cts.Token);
            await Task.Delay(500, cts.Token);
            await File.WriteAllTextAsync(verifyFile, "// dogfood block1 v5", cts.Token);

            await processTask;

            // Allow pattern detector to process events (FileSystemWatcher can batch/delay on macOS)
            await Task.Delay(1500, cts.Token);

            using var queryCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var patterns = await store.QueryAsync(new PatternStoreQueryParams { MaxCount = 10 }, queryCts.Token);

            var hasRepeatedEdits = patterns.Any(p => p.EventType == "repeated-edits");
            var passed = hasRepeatedEdits;

            if (json)
            {
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
                {
                    block = "block1",
                    passed,
                    reason = passed ? "Observation pipeline stored patterns from Ashlar's own file events" : "No repeated-edits pattern stored",
                    patternCount = patterns.Count,
                    eventCount
                }));
            }
            else
            {
                if (passed)
                    Console.WriteLine("Block 1 dogfood gate PASSED: Observation pipeline stored patterns from Ashlar's own file events.");
                else
                    Console.Error.WriteLine($"Block 1 dogfood gate FAILED: No repeated-edits pattern stored (events: {eventCount}, patterns: {patterns.Count}).");
            }

            return passed ? 0 : 1;
        }
        finally
        {
            try
            {
                if (File.Exists(verifyFile))
                    File.Delete(verifyFile);
                if (File.Exists(storePath))
                    File.Delete(storePath);
            }
            catch
            {
                // Best-effort cleanup
            }
        }
    }
}
