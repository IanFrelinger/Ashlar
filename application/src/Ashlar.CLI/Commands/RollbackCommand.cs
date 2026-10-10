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
            ctx.ExitCode = await ExecuteAsync(adaptationId, toSnapshot, preview, storePathOverride);
        });
    }

    private static async Task<int> ExecuteAsync(string? adaptationId, string? toSnapshot, bool preview, string? storePathOverride)
    {
        var repoRoot = RepoPathResolver.FindRepoRoot();
        var storePath = !string.IsNullOrWhiteSpace(storePathOverride)
            ? Path.Combine(Path.GetFullPath(storePathOverride), "ashlar-patterns.db")
            : Path.Combine(RepoPathResolver.ResolveStateDirectory(repoRoot), "ashlar-patterns.db");

        var services = new ServiceCollection()
            .AddLogging(b => b.AddConsole().SetMinimumLevel(LogLevel.Warning))
            .AddAdaptationInfrastructure(storePath)
            .BuildServiceProvider();

        var rollbackManager = services.GetRequiredService<IRollbackManager>();

        if (!string.IsNullOrWhiteSpace(toSnapshot))
        {
            if (preview)
            {
                Console.WriteLine($"Preview: Would restore to snapshot {toSnapshot}");
                return 0;
            }
            await rollbackManager.RollbackToSnapshotAsync(toSnapshot).ConfigureAwait(false);
            Console.WriteLine($"Restored to snapshot {toSnapshot}");
            return 0;
        }

        if (string.IsNullOrWhiteSpace(adaptationId))
        {
            Console.Error.WriteLine("Specify adaptation-id or --to-snapshot <snapshotId>");
            return 1;
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
            return 0;
        }

        try
        {
            await rollbackManager.RollbackAsync(adaptationId).ConfigureAwait(false);
            Console.WriteLine($"Rolled back adaptation {adaptationId}");
            return 0;
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }
}
