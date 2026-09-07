namespace Ashlar.BackgroundAgents.Campaign;

/// <summary>
/// Specialist that guards regressions. Fast mode verifies the cert-gate and
/// dogfood surfaces still exist and runs a cheap counted convention slice.
/// Full mode invokes <c>scripts/run-cert-gate.sh --fast</c>.
/// </summary>
public sealed class RegressionLaneRunner : ICampaignLaneRunner
{
    private readonly ICampaignProcessInvoker? _invoker;

    /// <summary>Create a runner. <paramref name="invoker"/> is required to execute tests.</summary>
    public RegressionLaneRunner(ICampaignProcessInvoker? invoker = null)
    {
        _invoker = invoker;
    }

    /// <inheritdoc />
    public CampaignLane Lane => CampaignLane.Regression;

    /// <inheritdoc />
    public async Task<CampaignAgentReport> RunAsync(CampaignRunContext context, CancellationToken cancellationToken = default)
    {
        var started = DateTimeOffset.UtcNow;
        var findings = new List<CampaignFinding>();
        var repoRoot = context.RepoRoot;

        if (string.IsNullOrWhiteSpace(repoRoot) || !Directory.Exists(repoRoot))
        {
            return Error(context, started, "Repo root is missing.", "missing-repo");
        }

        RequireFile(repoRoot, "scripts/run-cert-gate.sh", "missing-cert-gate", findings);
        RequireFile(repoRoot, "src/Ashlar.Tests.Infrastructure/Tests/Dogfood/DogfoodBlock1Tests.cs", "missing-dogfood-block1", findings);
        RequireFile(repoRoot, "src/Ashlar.Tests.BackgroundAgents/Campaign/CampaignAgentSetConventionTests.cs", "missing-campaign-convention", findings);

        var makefile = Path.Combine(repoRoot, "Makefile");
        if (File.Exists(makefile) && !File.ReadAllText(makefile).Contains("dogfood-campaign", StringComparison.Ordinal))
        {
            findings.Add(new CampaignFinding(
                "missing-makefile-target",
                "Makefile has no dogfood-campaign target.",
                "Makefile"));
        }

        var facts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["mode"] = context.Full ? "full" : "fast"
        };

        if (!context.SkipProcessLanes && _invoker is not null && findings.Count == 0)
        {
            var (fileName, arguments) = ResolveCommand(context);
            facts["command"] = fileName + " " + string.Join(' ', arguments);
            CampaignProcessResult result;
            try
            {
                result = await _invoker.RunAsync(fileName, arguments, repoRoot, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Error(context, started, $"Regression process failed to start: {ex.Message}", "process-start");
            }

            facts["exit_code"] = result.ExitCode.ToString();
            if (result.ExitCode != 0)
            {
                var tail = TrimTail(string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut : result.StdErr);
                findings.Add(new CampaignFinding(
                    "regression-command-failed",
                    $"Regression command exited {result.ExitCode}: {tail}"));
            }
        }

        var blockers = findings.Count(f => string.Equals(f.Severity, "error", StringComparison.OrdinalIgnoreCase));
        var verdict = blockers == 0 ? CampaignVerdictKind.Pass : CampaignVerdictKind.Fail;
        var summary = blockers == 0
            ? (context.Full
                ? "Full regression command passed."
                : "Regression surface is present and the fast slice passed.")
            : $"{blockers} regression blocker(s).";

        return new CampaignAgentReport(
            context.AgentId,
            context.Role,
            Lane,
            verdict,
            summary,
            findings,
            started,
            DateTimeOffset.UtcNow,
            facts);
    }

    private static (string FileName, IReadOnlyList<string> Arguments) ResolveCommand(CampaignRunContext context)
    {
        if (context.Parameters is not null &&
            context.Parameters.TryGetValue("Command", out var command) &&
            !string.IsNullOrWhiteSpace(command))
        {
            var parts = SplitCommand(command);
            return (parts[0], parts.Skip(1).ToArray());
        }

        if (context.Full)
        {
            return ("bash", new[] { "scripts/run-cert-gate.sh", "--fast" });
        }

        return ("dotnet", new[]
        {
            "test",
            "src/Ashlar.Tests.BackgroundAgents/Ashlar.Tests.BackgroundAgents.csproj",
            "-f",
            "net8.0",
            "--filter",
            "FullyQualifiedName~CampaignAgentSetConventionTests",
            "--nologo",
            "/p:UseSharedCompilation=false",
            "/nodereuse:false"
        });
    }

    private static IReadOnlyList<string> SplitCommand(string command)
    {
        var parts = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        foreach (var ch in command)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (char.IsWhiteSpace(ch) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    parts.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(ch);
        }

        if (current.Length > 0)
            parts.Add(current.ToString());

        return parts;
    }

    private static void RequireFile(string repoRoot, string relative, string code, List<CampaignFinding> findings)
    {
        var full = Path.Combine(repoRoot, relative.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(full))
            findings.Add(new CampaignFinding(code, $"Required regression surface is missing: {relative}", relative));
    }

    private static string TrimTail(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length <= 400)
            return trimmed;
        return trimmed[^400..];
    }

    private static CampaignAgentReport Error(CampaignRunContext context, DateTimeOffset started, string message, string code)
    {
        return new CampaignAgentReport(
            context.AgentId,
            context.Role,
            CampaignLane.Regression,
            CampaignVerdictKind.Error,
            message,
            new[] { new CampaignFinding(code, message) },
            started,
            DateTimeOffset.UtcNow);
    }
}
