using System.CommandLine;
using System.CommandLine.Invocation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ashlar.Core.Application.Observation.Ports;
using Ashlar.Core.Application.Paths;
using Ashlar.Core.Application.SelfContext.Ports;
using Ashlar.BackgroundAgents.Observation;
using Ashlar.Infrastructure;
using Ashlar.Infrastructure.Adaptation;
using Ashlar.Infrastructure.Observation;
using Ashlar.Infrastructure.SelfContext;

namespace Ashlar.CLI.Commands;

/// <summary>
/// Observe development workflow: watch file system and processes, detect patterns.
/// Dogfood: when run in Ashlar repo, observes Ashlar's own development.
/// </summary>
public sealed class ObserveCommand : Command
{
    /// <summary>Creates a new ObserveCommand instance.</summary>
    public ObserveCommand() : base("observe", "Observe development workflow (file system, processes); detect patterns. Dogfood: in Ashlar repo, observes Ashlar itself.")
    {
        var pathOpt = new Option<string?>("--path", "Root path to observe (default: repo root from cwd)");
        var durationOpt = new Option<string>("--duration", () => "10m", "How long to observe (e.g. 5m, 30m, 1h). Use 0 for until Ctrl+C.");
        var dumpOpt = new Option<bool>("--dump", () => true, "Dump detected patterns at exit");
        var verboseOpt = new Option<bool>("--verbose", () => false, "Verbose output");

        AddOption(pathOpt);
        AddOption(durationOpt);
        AddOption(dumpOpt);
        AddOption(verboseOpt);

        this.SetHandler(async (InvocationContext ctx) =>
        {
            var path = ctx.ParseResult.GetValueForOption(pathOpt);
            var durationStr = ctx.ParseResult.GetValueForOption(durationOpt) ?? "10m";
            var dump = ctx.ParseResult.GetValueForOption(dumpOpt);
            var verbose = ctx.ParseResult.GetValueForOption(verboseOpt);
            var maxDuration = ParseDuration(durationStr);
            await ExecuteAsync(path, maxDuration, dump, verbose);
        });
    }

    private static TimeSpan ParseDuration(string s)
    {
        s = s.Trim().ToLowerInvariant();
        if (s == "0" || s == "infinite") return TimeSpan.MaxValue;
        if (s.EndsWith("m"))
        {
            var n = int.TryParse(s[..^1], out var v) ? v : 10;
            return TimeSpan.FromMinutes(n);
        }
        if (s.EndsWith("h"))
        {
            var n = int.TryParse(s[..^1], out var v) ? v : 1;
            return TimeSpan.FromHours(n);
        }
        if (s.EndsWith("s"))
        {
            var n = int.TryParse(s[..^1], out var v) ? v : 60;
            return TimeSpan.FromSeconds(n);
        }
        return int.TryParse(s, out var mins) ? TimeSpan.FromMinutes(mins) : TimeSpan.FromMinutes(10);
    }

    private static async Task ExecuteAsync(string? path, TimeSpan maxDuration, bool dump, bool verbose)
    {
        var repoRoot = path ?? RepoPathResolver.FindRepoRoot();
        var storePath = Path.Combine(RepoPathResolver.ResolveStateDirectory(repoRoot), "ashlar-patterns.db");

        var services = new ServiceCollection()
            .AddLogging(b =>
            {
                b.AddConsole();
                if (verbose) b.SetMinimumLevel(LogLevel.Debug);
            })
            .AddOptions()
            .AddAdaptationInfrastructure(storePath)
            .AddSelfContextInfrastructure(storePath)
            .Configure<ObservationPipelineOptions>(opts =>
            {
                opts.RepoRoot = repoRoot;
                opts.StorePath = Path.GetFileName(storePath);
                opts.WatchPaths = new[] { "src", ".github", "tools" };
                opts.ProcessFilters = new[] { "dotnet", "msbuild", "ashlar" };
            })
            .AddSingleton<IPatternStore>(_ => new LiteDbPatternStore(storePath))
            .AddSingleton<IContextAssembler, ContextAssembler>()
            .BuildServiceProvider();

        var loggerFactory = services.GetRequiredService<ILoggerFactory>();
        var logger = loggerFactory.CreateLogger<ObserveCommand>();
        var executionTracer = services.GetRequiredService<IExecutionTracer>();

        await executionTracer.TraceAsync("observe.start", new Dictionary<string, object> { ["path"] = repoRoot }, path: repoRoot).ConfigureAwait(false);

        var watchPaths = new[] { "src", ".github", "tools" }
            .Select(p => Path.Combine(repoRoot, p.TrimStart('/', '\\')))
            .Where(Directory.Exists)
            .ToList();

        if (watchPaths.Count == 0)
        {
            logger.LogError("No watch paths exist under {Root}. Create src/, .github/, or tools/ to observe.", repoRoot);
            await executionTracer.TraceAsync("observe.end", null, repoRoot, "no_watch_paths").ConfigureAwait(false);
            Environment.ExitCode = 1;
            return;
        }

        Console.WriteLine("Ashlar Observation Pipeline");
        Console.WriteLine($"  Root: {repoRoot}");
        Console.WriteLine($"  Watching: {string.Join(", ", watchPaths.Select(Path.GetFileName))}");
        Console.WriteLine($"  Duration: {(maxDuration == TimeSpan.MaxValue ? "until Ctrl+C" : maxDuration.ToString())}");
        Console.WriteLine();
        Console.WriteLine("Edit files and run builds to generate patterns. Press Ctrl+C to stop.");
        Console.WriteLine();

        var patternStore = services.GetRequiredService<IPatternStore>();
        var patternDetector = new PatternDetector(
            TimeSpan.FromMinutes(5),
            3,
            patternStore,
            loggerFactory.CreateLogger<PatternDetector>());

        var fileSource = new FileSystemEventSource(
            watchPaths,
            repoRoot,
            new[] { "*" },
            loggerFactory.CreateLogger<FileSystemEventSource>());
        var processSource = new ProcessEventSource(
            new[] { "dotnet", "msbuild", "ashlar" },
            repoRoot,
            TimeSpan.FromSeconds(2),
            loggerFactory.CreateLogger<ProcessEventSource>());
        var compositeSource = new CompositeEventSource(new IObservableEventSource[] { fileSource, processSource });

        using var cts = new CancellationTokenSource();
        if (maxDuration != TimeSpan.MaxValue)
            cts.CancelAfter(maxDuration);
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        var eventCount = 0;
        var startTime = DateTimeOffset.UtcNow;

        try
        {
            await foreach (var evt in compositeSource.SubscribeAsync(cts.Token).WithCancellation(cts.Token))
            {
                eventCount++;
                if (verbose)
                    logger.LogDebug("Event: {Source} {Category} {Id}", evt.SourceId, evt.Category, evt.EventId);
                await patternDetector.ProcessAsync(evt, cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }

        var elapsed = DateTimeOffset.UtcNow - startTime;
        Console.WriteLine();
        Console.WriteLine($"Observed {eventCount} events in {elapsed:hh\\:mm\\:ss}");

        if (dump)
        {
            var contextAssembler = services.GetRequiredService<IContextAssembler>();
            var context = await contextAssembler.AssembleContextAsync(
                projectPath: repoRoot,
                since: startTime,
                maxPatterns: 50,
                includeRecentEvents: false).ConfigureAwait(false);
            Console.WriteLine();
            Console.WriteLine(context.Summary);
            if (context.Patterns.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("Patterns:");
                foreach (var p in context.Patterns)
                {
                    Console.WriteLine($"  - {p.EventType} (freq={p.Frequency}, last={p.LastSeen:HH:mm:ss})");
                }
            }
        }

        await executionTracer.TraceAsync("observe.end", new Dictionary<string, object> { ["eventCount"] = eventCount }, repoRoot, "completed").ConfigureAwait(false);
        Environment.ExitCode = 0;
    }
}
