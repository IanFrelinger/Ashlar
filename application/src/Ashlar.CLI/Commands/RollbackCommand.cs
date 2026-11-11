using System.CommandLine;
using System.CommandLine.Invocation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ashlar.Core.Application.Paths;
using Ashlar.Core.Application.Rollback.Ports;
using Ashlar.Infrastructure;

namespace Ashlar.CLI.Commands;

/// <summary>
/// Rollback adaptations or restore to a snapshot.
/// </summary>
public sealed class RollbackCommand : Command
{
    /// <summary>Creates a new RollbackCommand instance.</summary>
    public RollbackCommand() : base("rollback", "Roll back an adaptation or restore to a snapshot")
    {
        var adaptationIdArg = new Argument<string?>("adaptation-id", "Adaptation ID to roll back");
        var toSnapshotOpt = new Option<string?>("--to-snapshot", "Snapshot ID to restore to");
        var previewOpt = new Option<bool>("--preview", () => false, "Preview rollback impact without executing");
        var storePathOpt = new Option<string?>("--store-path", "Directory for ashlar dbs (default: ASHLAR_STATE_DIR, else <repo root>/.ashlar/state)");

        AddArgument(adaptationIdArg);
        AddOption(toSnapshotOpt);
        AddOption(previewOpt);
        AddOption(storePathOpt);

        this.SetHandler(async (InvocationContext ctx) =>
        {
            var adaptationId = ctx.ParseResult.GetValueForArgument(adaptationIdArg);
            var toSnapshot = ctx.ParseResult.GetValueForOption(toSnapshotOpt);
            var preview = ctx.ParseResult.GetValueForOption(previewOpt);
            var storePathOverride = ctx.ParseResult.GetValueForOption(storePathOpt);
            await ExecuteAsync(ctx, adaptationId, toSnapshot, preview, storePathOverride);
        });
    }

    private static async Task ExecuteAsync(InvocationContext ctx, string? adaptationId, string? toSnapshot, bool preview, string? storePathOverride)
    {
        var repoRoot = RepoPathResolver.FindRepoRoot();
        var storePath = !string.IsNullOrWhiteSpace(storePathOverride)
            ? Path.Combine(Path.GetFullPath(storePathOverride), "ashlar-patterns.db")
            : Path.Combine(RepoPathResolver.ResolveStateDirectory(repoRoot), "ashlar-patterns.db");

        using var services = new ServiceCollection()
            .AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning))
            .AddAdaptationInfrastructure(storePath)
            .BuildServiceProvider();

        var rollbackManager = services.GetRequiredService<IRollbackManager>();

        if (!string.IsNullOrWhiteSpace(toSnapshot))
        {
            if (preview)
            {
                Console.WriteLine($"Preview: Would restore to snapshot {toSnapshot}");
                return;
            }
            await rollbackManager.RollbackToSnapshotAsync(toSnapshot).ConfigureAwait(false);
            Console.WriteLine($"Restored to snapshot {toSnapshot}");
            ctx.ExitCode = 0;
            return;
        }

        if (string.IsNullOrWhiteSpace(adaptationId))
        {
            Console.Error.WriteLine("Specify adaptation-id or --to-snapshot <snapshotId>");
            ctx.ExitCode = 1;
            return;
        }

        if (preview)
        {
            var impact = await rollbackManager.PreviewRollbackAsync(adaptationId).ConfigureAwait(false);
            Console.WriteLine(impact.Summary);
            Console.WriteLine($"  Additional components affected: {impact.AdditionalComponentsAffected.Count}");
            if (impact.AdditionalComponentsAffected.Count > 0)
            {
                foreach (var c in impact.AdditionalComponentsAffected)
                    Console.WriteLine($"    - {c}");
            }
            ctx.ExitCode = 0;
            return;
        }

        try
        {
            await rollbackManager.RollbackAsync(adaptationId).ConfigureAwait(false);
            Console.WriteLine($"Rolled back adaptation {adaptationId}");
            ctx.ExitCode = 0;
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine(ex.Message);
            ctx.ExitCode = 1;
        }
    }
}
