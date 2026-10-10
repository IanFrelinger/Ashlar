using System.CommandLine;
using System.Diagnostics;
using Ashlar.Core.Application.Paths;
using Ashlar.Infrastructure.HostProcess;

namespace Ashlar.CLI.Commands;

/// <summary>
/// Operator helpers for cutting releases (wraps scripts + gh).
/// </summary>
public sealed class ReleaseCommand : Command
{
    /// <summary>Creates a new ReleaseCommand instance.</summary>
    public ReleaseCommand() : base("release", "Release helpers: local NuGet preflight, trigger CI workflows")
    {
        var versionArg = new Argument<string>("version", "Semver to ship (e.g. 1.2.3), with or without v prefix");

        var preflightCmd = new Command("preflight", "Run local pack-graph alignment + NuGet consumer sample (same as scripts/release-preflight-local.sh).");
        preflightCmd.AddArgument(versionArg);
        var triggerGateOpt = new Option<bool>("--trigger-gate", "Also run: gh workflow run \"Runtime Release Gate\" (requires gh auth)");
        var gateRefOpt = new Option<string>("--gate-ref", () => "master", "Branch ref for Runtime Release Gate when --trigger-gate is set");
        preflightCmd.AddOption(triggerGateOpt);
        preflightCmd.AddOption(gateRefOpt);
        preflightCmd.SetHandler(
            async (string version, bool triggerGate, string gateRef) =>
            {
                var code = await RunPreflightAsync(version, triggerGate, gateRef).ConfigureAwait(false);
                Environment.Exit(code);
            },
            versionArg,
            triggerGateOpt,
            gateRefOpt);
        AddCommand(preflightCmd);

        var gateCmd = new Command("gate", "Trigger GitHub Action \"Runtime Release Gate\" (requires gh CLI + auth).");
        var gateRefOnly = new Option<string>("--ref", () => "master", "Branch or tag to run the workflow against");
        gateCmd.AddOption(gateRefOnly);
        gateCmd.SetHandler(
            async (string @ref) =>
            {
                var code = await RunGhWorkflowAsync("Runtime Release Gate", @ref).ConfigureAwait(false);
                Environment.Exit(code);
            },
            gateRefOnly);
        AddCommand(gateCmd);

        var dispatchCmd = new Command("dispatch", "Trigger GitHub Action \"Release\" (workflow_dispatch: GHCR + NuGet for a version). Requires gh auth.");
        dispatchCmd.AddArgument(versionArg);
        var refOpt = new Option<string>("--ref", () => "master", "Branch to run the workflow against");
        var skipMultiArchOpt = new Option<bool>("--skip-multi-arch", "Pass skip_multi_arch=true (ashlar-cli latest amd64 only on main)");
        dispatchCmd.AddOption(refOpt);
        dispatchCmd.AddOption(skipMultiArchOpt);
        dispatchCmd.SetHandler(
            async (string version, string @ref, bool skipMultiArch) =>
            {
                var ver = NormalizeVersion(version);
                var skip = skipMultiArch ? "true" : "false";
                var code = await RunProcessAsync(
                        "gh",
                        $"workflow run Release --ref {@ref} -f version={ver} -f skip_multi_arch={skip}",
                        RepoPathResolver.FindRepoRoot())
                    .ConfigureAwait(false);
                if (code != 0)
                    Console.Error.WriteLine("release dispatch: ensure gh is installed and authenticated (gh auth login).");
                Environment.Exit(code);
            },
            versionArg,
            refOpt,
            skipMultiArchOpt);
        AddCommand(dispatchCmd);
    }

    private static async Task<int> RunPreflightAsync(string version, bool triggerGate, string gateRef)
    {
        var repo = RepoPathResolver.FindRepoRoot();
        var sh = Path.Combine(repo, "scripts", "release-preflight-local.sh");
        var ps1 = Path.Combine(repo, "scripts", "release-preflight-local.ps1");
        if (!File.Exists(sh) && !File.Exists(ps1))
        {
            Console.Error.WriteLine("release preflight: scripts not found. Run from Ashlar repository root.");
            return 1;
        }

        var ver = NormalizeVersion(version);
        int exit;
        if (File.Exists(sh) && BashOnPath())
        {
            exit = await RunProcessAsync("bash", $"\"{sh}\" \"{ver}\"", repo).ConfigureAwait(false);
        }
        else if (File.Exists(ps1) && PwshOnPath())
        {
            exit = await RunProcessAsync(
                "pwsh",
                $"-NoProfile -File \"{ps1}\" -Version \"{ver}\"",
                repo).ConfigureAwait(false);
        }
        else
        {
            Console.Error.WriteLine("release preflight: install bash or PowerShell 7+ (pwsh) and run from repo root.");
            return 1;
        }

        if (exit != 0)
            return exit;

        if (triggerGate)
            return await RunGhWorkflowAsync("Runtime Release Gate", gateRef).ConfigureAwait(false);

        return 0;
    }

    private static string NormalizeVersion(string v)
    {
        v = v.Trim();
        return v.StartsWith('v') || v.StartsWith('V') ? v[1..] : v;
    }

    // Version probes never execute a command or load a PowerShell profile.
    internal static ProcessStartInfo VersionProbeStartInfo(string executable)
    {
        if (executable is not ("bash" or "pwsh"))
            throw new ArgumentOutOfRangeException(nameof(executable));
        return new ProcessStartInfo
        {
            FileName = executable,
            Arguments = "--version",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
    }

    private static bool BashOnPath()
    {
        try
        {
            using var p = Process.Start(VersionProbeStartInfo("bash"));
            if (p is null)
                return false;
            p.WaitForExit(5000);
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool PwshOnPath()
    {
        try
        {
            using var p = Process.Start(VersionProbeStartInfo("pwsh"));
            if (p is null)
                return false;
            p.WaitForExit(15000);
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<int> RunGhWorkflowAsync(string workflowName, string @ref)
    {
        var exit = await RunProcessAsync("gh", $"workflow run \"{workflowName}\" --ref {@ref}", RepoPathResolver.FindRepoRoot())
            .ConfigureAwait(false);
        if (exit != 0)
            Console.Error.WriteLine("release gate: ensure GitHub CLI is installed and authenticated: gh auth login");
        return exit;
    }

    private static async Task<int> RunProcessAsync(string fileName, string arguments, string workingDirectory)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
        };
        var run = await TimedProcess.RunAsync(psi, TimedProcess.OperatorCommandTimeout).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(run.StdOut))
            Console.Write(run.StdOut);
        if (!string.IsNullOrEmpty(run.StdErr))
            Console.Error.Write(run.StdErr);
        return run.ExitCode;
    }
}
