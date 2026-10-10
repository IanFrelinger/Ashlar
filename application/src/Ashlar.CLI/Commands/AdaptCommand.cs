using System.CommandLine;
using System.CommandLine.Invocation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ashlar.Core.Application.Adaptation.Models;
using Ashlar.Core.Application.Adaptation.Ports;
using Ashlar.Core.Application.Observation.Ports;
using Ashlar.Core.Application.Paths;
using Ashlar.Core.Application.SelfContext.Ports;
using Ashlar.Core.Domain.Execution;
using Ashlar.Bricks.Owasp.Security;
using Ashlar.Infrastructure;
using Ashlar.Infrastructure.Adaptation;
using Ashlar.Infrastructure.Observation;
using Ashlar.Infrastructure.SelfContext;

namespace Ashlar.CLI.Commands;

/// <summary>
/// Dogfood command: decompose a brick to manifest, optionally apply fixes, recompile.
/// Block 3 adaptation engine.
/// </summary>
public sealed class AdaptCommand : Command
{
    /// <summary>Creates a new AdaptCommand instance.</summary>
    public AdaptCommand() : base("adapt", "Decompose brick to manifest, optionally apply fixes and recompile (Block 3 dogfood).")
    {
        var brickOpt = new Option<string>("--brick", () => "observation.context", "Brick ID to adapt");
        var fixOpt = new Option<string?>("--fix", "Apply fix for failure type (e.g. EmptyCatch, MissingOutput)");
        var dryRunOpt = new Option<bool>("--dry-run", () => false, "Only decompose; do not recompile");
        var storePathOpt = new Option<string?>("--store-path", "Directory for ashlar-patterns.db and ashlar-execution.db (default: ASHLAR_STATE_DIR, else <repo root>/.ashlar/state)");

        AddOption(brickOpt);
        AddOption(fixOpt);
        AddOption(dryRunOpt);
        AddOption(storePathOpt);

        this.SetHandler(async (InvocationContext ctx) =>
        {
            var brickId = ctx.ParseResult.GetValueForOption(brickOpt) ?? "observation.context";
            var fixType = ctx.ParseResult.GetValueForOption(fixOpt);
            var dryRun = ctx.ParseResult.GetValueForOption(dryRunOpt);
            var storePathOverride = ctx.ParseResult.GetValueForOption(storePathOpt);
            ctx.ExitCode = await ExecuteAsync(brickId, fixType, dryRun, storePathOverride);
        });
    }

    private static async Task<int> ExecuteAsync(string brickId, string? fixType, bool dryRun, string? storePathOverride = null)
    {
        var repoRoot = RepoPathResolver.FindRepoRoot();
        var storePath = !string.IsNullOrWhiteSpace(storePathOverride)
            ? Path.Combine(Path.GetFullPath(storePathOverride), "ashlar-patterns.db")
            : Path.Combine(RepoPathResolver.ResolveStateDirectory(repoRoot), "ashlar-patterns.db");

        var services = new ServiceCollection()
            .AddLogging(b => b.AddConsole())
            .AddSingleton<Ashlar.Infrastructure.Execution.IProviderFactory, Ashlar.Infrastructure.Execution.ProviderFactory>()
            .AddAdaptationInfrastructure(storePath)
            .AddAdaptationBricks(typeof(OWASPScannerBrick))
            .AddSelfContextInfrastructure(storePath)
            .BuildServiceProvider();

        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger<AdaptCommand>();
        var executionTracer = services.GetRequiredService<IExecutionTracer>();

        var decomposer = services.GetRequiredService<IBrickDecomposer>();
        var fixGenerator = services.GetRequiredService<IFixGenerator>();
        var recompiler = services.GetRequiredService<IBrickRecompiler>();
        var brickRegistry = services.GetRequiredService<IBrickRegistry>();

        var brick = brickRegistry.GetBrick(brickId);

        if (brick == null)
        {
            var available = brickRegistry.GetAllBricks();
            var ids = available.Count > 0 ? string.Join(", ", available.Select(b => b.Id)) : "observation.context (when store-path provided)";
            logger.LogError("Brick {Id} not found. Available for adapt: {Available}", brickId, ids);
            await executionTracer.TraceAsync("adapt.end", null, null, "brick_not_found").ConfigureAwait(false);
            return 1;
        }

        await executionTracer.TraceAsync("adapt.start", new Dictionary<string, object> { ["brickId"] = brickId, ["fixType"] = fixType ?? "" }, null).ConfigureAwait(false);

        var manifest = await decomposer.DecomposeAsync(brick).ConfigureAwait(false);
        Console.WriteLine($"Decomposed {brickId} -> manifest {manifest.Id} v{manifest.Version}");
        Console.WriteLine($"  Interface: {manifest.Interface.Inputs.Count} inputs, {manifest.Interface.Outputs.Count} outputs");
        Console.WriteLine($"  ImplementationTypeName: {manifest.ImplementationTypeName ?? "(none)"}");

        BrickManifest toRecompile = manifest;
        if (!string.IsNullOrEmpty(fixType))
        {
            var context = new FailureContext
            {
                FailureType = fixType,
                TargetId = brickId,
                Message = $"Applied fix for {fixType}",
            };
            var fixes = await fixGenerator.GenerateFixesAsync(context, manifest).ConfigureAwait(false);
            if (fixes.Count > 0)
            {
                toRecompile = fixes[0];
                Console.WriteLine($"  Applied fix: {fixType} -> v{toRecompile.Version}");
            }
        }

        if (dryRun)
        {
            Console.WriteLine("(dry-run: skip recompile)");
            return 0;
        }

        var recompiled = await recompiler.RecompileAsync(toRecompile).ConfigureAwait(false);
        var outcome = recompiled != null ? "recompiled" : "recompile_null";
        await executionTracer.TraceAsync("adapt.end", null, null, outcome).ConfigureAwait(false);
        if (recompiled != null)
        {
            Console.WriteLine($"  Recompiled OK: {recompiled.Id}");
        }
        else
        {
            logger.LogWarning("Recompile returned null (known type may need DI).");
        }

        return 0;
    }
}
