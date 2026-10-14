using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Ashlar.CLI.Commands;

namespace Ashlar.CLI;

/// <summary>Program.</summary>
static partial class Program
{
    private static Command BuildTrustCommand(Option<bool> jsonOpt)
    {
        // ashlar trust - Audit and access boundary (Phase 4)
        var trustCmd = new Command("trust", "Trust & Information Architecture: audit log and access boundary");
        var trustAuditCmd = new Command("audit", "Show or export data decision audit log")
        {
            new Option<int>("--count", () => 50, "Max entries to show"),
            new Option<string?>("--since", "Filter by time (e.g. 1h, 30m, or ISO date)"),
            new Option<string?>("--until", "Filter until time (e.g. 1h, 30m, or ISO date)"),
            new Option<string?>("--type", "Filter by event type (Sanitization, BoundaryChange, Classification)"),
            new Option<bool>("--json", "Export as JSON (compliance format)"),
            new Option<bool>("--md", "Export as Markdown"),
            new Option<bool>("--csv", "Export as CSV (compliance)")
        };
        trustAuditCmd.SetHandler(
            /// <summary>Async.</summary>
            /// <param name="count">Count.</param>
            /// <param name="since">Since.</param>
            /// <param name="until">Until.</param>
            /// <param name="type">Type.</param>
            /// <param name="json">Json.</param>
            /// <param name="md">Md.</param>
            /// <param name="csv">Csv.</param>
            async (int count, string? since, string? until, string? type, bool json, bool md, bool csv) =>
            {
                var cmd = ServiceProvider.GetRequiredService<TrustCommand>();
                EgressCliInvocation.Exit(await cmd.AuditAsync(count, since, until, type, json, md, csv));
            },
            trustAuditCmd.Options[0] as Option<int> ?? throw new InvalidOperationException(),
            trustAuditCmd.Options[1] as Option<string?> ?? throw new InvalidOperationException(),
            trustAuditCmd.Options[2] as Option<string?> ?? throw new InvalidOperationException(),
            trustAuditCmd.Options[3] as Option<string?> ?? throw new InvalidOperationException(),
            trustAuditCmd.Options[4] as Option<bool> ?? throw new InvalidOperationException(),
            trustAuditCmd.Options[5] as Option<bool> ?? throw new InvalidOperationException(),
            trustAuditCmd.Options[6] as Option<bool> ?? throw new InvalidOperationException());
        trustCmd.AddCommand(trustAuditCmd);
        var trustPauseCmd = new Command("pause", "Pause observation (halt all data collection)");
        trustPauseCmd.AddOption(jsonOpt);
        trustPauseCmd.SetHandler(
            /// <summary>Async.</summary>
            /// <param name="formatJson">Format json.</param>
            async (bool formatJson) =>
            {
                var cmd = ServiceProvider.GetRequiredService<TrustCommand>();
                EgressCliInvocation.Exit(await cmd.PauseAsync(formatJson));
            },
            jsonOpt);
        trustCmd.AddCommand(trustPauseCmd);
        var trustResumeCmd = new Command("resume", "Resume observation");
        trustResumeCmd.AddOption(jsonOpt);
        trustResumeCmd.SetHandler(
            /// <summary>Async.</summary>
            /// <param name="formatJson">Format json.</param>
            async (bool formatJson) =>
            {
                var cmd = ServiceProvider.GetRequiredService<TrustCommand>();
                EgressCliInvocation.Exit(await cmd.ResumeAsync(formatJson));
            },
            jsonOpt);
        trustCmd.AddCommand(trustResumeCmd);
        var trustAllowCmd = new Command("allow", "Allow a category or source");
        trustAllowCmd.AddOption(new Option<string?>("--category", "Category to allow (e.g. file-paths, terminal-output)"));
        trustAllowCmd.AddOption(new Option<string?>("--source", "Source to allow (e.g. git, vscode)"));
        trustAllowCmd.AddOption(new Option<string?>("--project", "Project path for override (requires --source)"));
        trustAllowCmd.AddOption(jsonOpt);
        trustAllowCmd.SetHandler(
            /// <summary>Async.</summary>
            /// <param name="category">Category.</param>
            /// <param name="source">Source.</param>
            /// <param name="project">Project.</param>
            /// <param name="formatJson">Format json.</param>
            async (string? category, string? source, string? project, bool formatJson) =>
            {
                var cmd = ServiceProvider.GetRequiredService<TrustCommand>();
                EgressCliInvocation.Exit(await cmd.AllowAsync(category, source, project, formatJson));
            },
            trustAllowCmd.Options[0] as Option<string?> ?? throw new InvalidOperationException(),
            trustAllowCmd.Options[1] as Option<string?> ?? throw new InvalidOperationException(),
            trustAllowCmd.Options[2] as Option<string?> ?? throw new InvalidOperationException(),
            trustAllowCmd.Options[3] as Option<bool> ?? throw new InvalidOperationException());
        trustCmd.AddCommand(trustAllowCmd);
        var trustDenyCmd = new Command("deny", "Deny a category or source");
        trustDenyCmd.AddOption(new Option<string?>("--category", "Category to deny"));
        trustDenyCmd.AddOption(new Option<string?>("--source", "Source to deny"));
        trustDenyCmd.AddOption(new Option<string?>("--project", "Project path for override (requires --source)"));
        trustDenyCmd.AddOption(jsonOpt);
        trustDenyCmd.SetHandler(
            /// <summary>Async.</summary>
            /// <param name="category">Category.</param>
            /// <param name="source">Source.</param>
            /// <param name="project">Project.</param>
            /// <param name="formatJson">Format json.</param>
            async (string? category, string? source, string? project, bool formatJson) =>
            {
                var cmd = ServiceProvider.GetRequiredService<TrustCommand>();
                EgressCliInvocation.Exit(await cmd.DenyAsync(category, source, project, formatJson));
            },
            trustDenyCmd.Options[0] as Option<string?> ?? throw new InvalidOperationException(),
            trustDenyCmd.Options[1] as Option<string?> ?? throw new InvalidOperationException(),
            trustDenyCmd.Options[2] as Option<string?> ?? throw new InvalidOperationException(),
            trustDenyCmd.Options[3] as Option<bool> ?? throw new InvalidOperationException());
        trustCmd.AddCommand(trustDenyCmd);
        var trustBoundaryCmd = new Command("boundary", "Show access boundary status");
        trustBoundaryCmd.SetHandler(
            /// <summary>Async.</summary>
            /// <param name="formatJson">Format json.</param>
            async (bool formatJson) =>
            {
                var cmd = ServiceProvider.GetRequiredService<TrustCommand>();
                EgressCliInvocation.Exit(await cmd.BoundaryAsync(formatJson));
            },
            jsonOpt);
        trustCmd.AddCommand(trustBoundaryCmd);
        var trustDashboardCmd = new Command("dashboard", "Compliance dashboard: boundary status + audit summary");
        trustDashboardCmd.AddOption(new Option<int>("--count", () => 50, "Max audit entries to include"));
        trustDashboardCmd.AddOption(jsonOpt);
        trustDashboardCmd.SetHandler(
            /// <summary>Async.</summary>
            /// <param name="count">Count.</param>
            /// <param name="formatJson">Format json.</param>
            async (int count, bool formatJson) =>
            {
                var cmd = ServiceProvider.GetRequiredService<TrustCommand>();
                EgressCliInvocation.Exit(await cmd.DashboardAsync(count, formatJson));
            },
            trustDashboardCmd.Options[0] as Option<int> ?? throw new InvalidOperationException(),
            trustDashboardCmd.Options[1] as Option<bool> ?? throw new InvalidOperationException());
        trustCmd.AddCommand(trustDashboardCmd);

        var trustPackCmd = new Command("pack", "Manage regulated trust policy packs");
        var trustPackListCmd = new Command("list", "List available trust policy packs");
        trustPackListCmd.AddOption(jsonOpt);
        trustPackListCmd.SetHandler(
            /// <summary>Async.</summary>
            /// <param name="formatJson">Format json.</param>
            async (bool formatJson) =>
            {
                var cmd = ServiceProvider.GetRequiredService<TrustCommand>();
                EgressCliInvocation.Exit(await cmd.ListPolicyPacksAsync(formatJson));
            },
            jsonOpt);
        trustPackCmd.AddCommand(trustPackListCmd);

        var trustPackDescribeCmd = new Command("describe", "Show rules for a trust policy pack");
        var trustPackDescribeIdArg = new Argument<string>("packId", "Policy pack id");
        trustPackDescribeCmd.AddArgument(trustPackDescribeIdArg);
        trustPackDescribeCmd.AddOption(jsonOpt);
        trustPackDescribeCmd.SetHandler(
            /// <summary>Async.</summary>
            /// <param name="packId">Pack id.</param>
            /// <param name="formatJson">Format json.</param>
            async (string packId, bool formatJson) =>
            {
                var cmd = ServiceProvider.GetRequiredService<TrustCommand>();
                EgressCliInvocation.Exit(await cmd.DescribePolicyPackAsync(packId, formatJson));
            },
            trustPackDescribeIdArg,
            jsonOpt);
        trustPackCmd.AddCommand(trustPackDescribeCmd);

        var trustPackApplyCmd = new Command("apply", "Apply a trust policy pack by id");
        var trustPackIdOpt = new Option<string>("--id", "Policy pack id (strict-enterprise, internal-only, air-gapped)");
        trustPackIdOpt.IsRequired = true;
        trustPackApplyCmd.AddOption(trustPackIdOpt);
        trustPackApplyCmd.AddOption(jsonOpt);
        trustPackApplyCmd.SetHandler(
            /// <summary>Async.</summary>
            /// <param name="id">Id.</param>
            /// <param name="formatJson">Format json.</param>
            async (string id, bool formatJson) =>
            {
                var cmd = ServiceProvider.GetRequiredService<TrustCommand>();
                EgressCliInvocation.Exit(await cmd.ApplyPolicyPackAsync(id, formatJson));
            },
            trustPackIdOpt,
            jsonOpt);
        trustPackCmd.AddCommand(trustPackApplyCmd);
        trustCmd.AddCommand(trustPackCmd);
        return trustCmd;
    }
}
