using Ashlar.Abstractions.Security.Egress;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Ashlar.Infrastructure.HostProcess;

namespace Ashlar.CLI.Commands;

/// <summary>Bootstrap runtime.</summary>
internal static class BootstrapRuntime
{
    private static readonly IReadOnlyList<BootstrapDependencySpec> LinuxDemoDependencies =
    [
        new(
            "git",
            "Git",
            "command -v git",
            "sudo apt-get update && sudo apt-get install -y git",
            true,
            false),
        new(
            "curl",
            "curl",
            "command -v curl",
            "sudo apt-get update && sudo apt-get install -y curl",
            true,
            false),
        // The SDK alone is not enough to RUN the CLI/API: they target net10.0 and need the
        // Microsoft.AspNetCore.App shared runtime, 10.x or (via RollForward=Major) newer.
        // A host with only `dotnet` on PATH but no usable ASP.NET Core runtime must go red here.
        new(
            "dotnet",
            ".NET SDK",
            """command -v dotnet >/dev/null && dotnet --list-runtimes 2>/dev/null | grep -Eq '^Microsoft\.AspNetCore\.App [1-9][0-9]\.'""",
            "sudo apt-get update && sudo apt-get install -y dotnet-sdk-10.0",
            true,
            false),
        new(
            "zstd",
            "zstd",
            "command -v zstd",
            "sudo apt-get update && sudo apt-get install -y zstd",
            false,
            true),
        new(
            "ollama",
            "Ollama",
            "command -v ollama",
            "curl -fsSL https://ollama.com/install.sh | sh",
            false,
            true),
        new(
            "docker",
            "Docker",
            "command -v docker",
            "sudo apt-get update && sudo apt-get install -y docker.io",
            false,
            true),
    ];

    private static readonly IReadOnlyList<BootstrapDependencySpec> MacDemoDependencies =
    [
        new(
            "brew",
            "Homebrew",
            "command -v brew",
            "/bin/bash -c \"$(curl -fsSL https://raw.githubusercontent.com/Homebrew/install/HEAD/install.sh)\"",
            true,
            false),
        new(
            "git",
            "Git",
            "command -v git",
            "brew install git",
            true,
            false),
        new(
            "dotnet",
            ".NET SDK",
            """command -v dotnet >/dev/null && dotnet --list-runtimes 2>/dev/null | grep -Eq '^Microsoft\.AspNetCore\.App [1-9][0-9]\.'""",
            "brew install --cask dotnet-sdk",
            true,
            false),
        new(
            "ollama",
            "Ollama",
            "command -v ollama",
            "brew install ollama",
            false,
            true),
        new(
            "docker",
            "Docker Desktop",
            "command -v docker",
            "brew install --cask docker",
            false,
            true),
    ];

    private static readonly IReadOnlyList<BootstrapDependencySpec> WindowsDemoDependencies =
    [
        new(
            "git",
            "Git",
            """if (Get-Command git -ErrorAction SilentlyContinue) { exit 0 } else { exit 1 }""",
            """winget install --id Git.Git --exact --accept-package-agreements --accept-source-agreements --silent""",
            true,
            false),
        new(
            "curl",
            "curl",
            """if (Get-Command curl.exe -ErrorAction SilentlyContinue) { exit 0 } elseif (Get-Command curl -ErrorAction SilentlyContinue) { exit 0 } else { exit 1 }""",
            """winget install -e --id cURL.cURL --accept-package-agreements --accept-source-agreements --silent""",
            true,
            false),
        new(
            "dotnet",
            ".NET SDK",
            """$v = dotnet --version 2>$null; if (-not $v) { exit 1 }; $major = [int](($v -split '\.')[0]); if ($major -lt 10) { exit 1 }; $rt = dotnet --list-runtimes 2>$null | Where-Object { $_ -match '^Microsoft\.AspNetCore\.App [1-9][0-9]\.' }; if ($rt) { exit 0 } else { exit 1 }""",
            """winget install --id Microsoft.DotNet.SDK.10 --exact --accept-package-agreements --accept-source-agreements --silent""",
            true,
            false),
        new(
            "docker",
            "Docker",
            """if (-not (Get-Command docker -ErrorAction SilentlyContinue)) { exit 1 }; docker info *> $null; if ($LASTEXITCODE -eq 0) { exit 0 } else { exit 1 }""",
            """winget install --id Docker.DockerDesktop --exact --accept-package-agreements --accept-source-agreements --silent""",
            false,
            true),
        new(
            "ollama",
            "Ollama",
            """if (Get-Command ollama -ErrorAction SilentlyContinue) { exit 0 } else { exit 1 }""",
            """winget install --id Ollama.Ollama --exact --accept-package-agreements --accept-source-agreements --silent""",
            false,
            true),
    ];

    /// <summary>Creates a new AssessDemoAsync instance.</summary>
    public static async Task<BootstrapAssessment> AssessDemoAsync(
        string profile,
        bool includeOptional,
        CancellationToken ct,
        bool relaxStrictVisualHostDeps = false)
    {
        var os = System.Runtime.InteropServices.RuntimeInformation.OSDescription;
        var (_, supported, reason, deps) = ResolveProfile();
        if (!supported)
        {
            return new BootstrapAssessment(
                NormalizeBootstrapProfile(profile),
                os,
                false,
                reason,
                Array.Empty<BootstrapDependencyStatus>());
        }

        var normalizedBootstrapProfile = NormalizeBootstrapProfile(profile);
        var statuses = new List<BootstrapDependencyStatus>();
        foreach (var spec in deps)
        {
            var (exitCode, _, stderr) = await RunShellCaptureAsync(spec.ProbeCommand, ct).ConfigureAwait(false);
            var required = IsRequiredForBootstrapProfile(normalizedBootstrapProfile, spec, includeOptional, relaxStrictVisualHostDeps);
            statuses.Add(new BootstrapDependencyStatus(
                spec.Id,
                spec.DisplayName,
                exitCode == 0,
                required,
                !required,
                spec.InstallCommand,
                exitCode == 0 ? null : stderr.Trim()));
        }

        return new BootstrapAssessment(
            normalizedBootstrapProfile,
            os,
            true,
            null,
            statuses);
    }

    private static (string Profile, bool Supported, string? Reason, IReadOnlyList<BootstrapDependencySpec> Deps) ResolveProfile()
    {
        if (OperatingSystem.IsLinux())
            return ("linux-demo", true, null, LinuxDemoDependencies);
        if (OperatingSystem.IsMacOS())
            return ("mac-demo", true, null, MacDemoDependencies);
        if (OperatingSystem.IsWindows())
            return ("windows-demo", true, null, WindowsDemoDependencies);
        return ("unsupported", false, "Bootstrap does not recognize this host OS.", Array.Empty<BootstrapDependencySpec>());
    }

    /// <summary>Creates a new BuildInstallPlan instance.</summary>
    public static IReadOnlyList<BootstrapDependencyStatus> BuildInstallPlan(BootstrapAssessment assessment)
    {
        return assessment.Dependencies
            .Where(d => !d.Installed && d.Required)
            .ToList();
    }

    /// <summary>Creates a new RenderAssessment instance.</summary>
    public static void RenderAssessment(BootstrapAssessment assessment, bool json)
    {
        if (json)
        {
            var payload = new
            {
                profile = assessment.Profile,
                os = assessment.OsDescription,
                supported = assessment.Supported,
                reason = assessment.Reason,
                dependencies = assessment.Dependencies.Select(d => new
                {
                    id = d.Id,
                    name = d.DisplayName,
                    installed = d.Installed,
                    required = d.Required,
                    optional = d.Optional,
                    install = d.InstallCommand,
                    probeError = d.ProbeError,
                }).ToArray(),
                missingRequired = assessment.MissingRequired.Select(d => d.Id).ToArray(),
                installPlan = BuildInstallPlan(assessment).Select(d => d.Id).ToArray(),
            };
            Console.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
            return;
        }

        Console.WriteLine($"Bootstrap profile: {assessment.Profile}");
        Console.WriteLine($"Host OS: {assessment.OsDescription}");
        if (!assessment.Supported)
        {
            Console.WriteLine(assessment.Reason ?? "Unsupported host.");
            return;
        }

        Console.WriteLine("Dependency status:");
        foreach (var dep in assessment.Dependencies)
        {
            var label = dep.Installed ? "OK" : "MISSING";
            var requiredLabel = dep.Required ? "required" : "optional";
            Console.WriteLine($"  - [{label}] {dep.DisplayName} ({requiredLabel})");
            if (!dep.Installed)
                Console.WriteLine($"      install: {dep.InstallCommand}");
        }

        var plan = BuildInstallPlan(assessment);
        if (plan.Count == 0)
            Console.WriteLine("Install plan: no action needed.");
        else
            Console.WriteLine($"Install plan: {string.Join(", ", plan.Select(p => p.DisplayName))}");
    }

    private static string NormalizeBootstrapProfile(string? profile)
    {
        var normalized = (profile ?? "demo").Trim().ToLowerInvariant();
        return normalized switch
        {
            "self-extend-functional" => "self-extend-functional",
            "self-extend-aesthetic" => "self-extend-aesthetic",
            "self-extend-visual" => "self-extend-visual",
            _ => "demo"
        };
    }

    private static bool IsRequiredForBootstrapProfile(
        string profile,
        BootstrapDependencySpec spec,
        bool includeOptional,
        bool relaxStrictVisualHostDeps)
    {
        if (profile == "self-extend-visual")
        {
            if (relaxStrictVisualHostDeps)
                return spec.Required;
            if (spec.Id is "docker" or "ollama" or "zstd")
                return true;
        }

        if (profile == "self-extend-functional")
        {
            return spec.Required;
        }

        if (profile == "self-extend-aesthetic")
        {
            // Aesthetic workflows use local dotnet/UI smoke paths; containerized visual QA is optional here.
            return spec.Required;
        }

        return spec.Required || (includeOptional && spec.Optional);
    }

    /// <summary>Creates a new RunShellStreamingAsync instance.</summary>
    public static async Task<int> RunShellStreamingAsync(string command, CancellationToken ct)
    {
        using var process = StartShellProcess(command, redirect: false);
        if (process == null)
            return 1;

        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch
            {
                // already gone
            }

            throw;
        }

        return process.ExitCode;
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunShellCaptureAsync(string command, CancellationToken ct)
    {
        var result = await TimedProcess.RunShellAsync(command, TimedProcess.DaemonProbeTimeout, ct)
            .ConfigureAwait(false);
        if (result.TimedOut)
        {
            var timeoutNote = $"Probe timed out after {TimedProcess.DaemonProbeTimeout.TotalSeconds:0}s.";
            var stderr = string.IsNullOrWhiteSpace(result.StdErr)
                ? timeoutNote
                : timeoutNote + " " + result.StdErr.Trim();
            return (TimedProcess.TimeoutExitCode, result.StdOut, stderr);
        }

        return (result.ExitCode, result.StdOut, result.StdErr);
    }

    private static Process? StartShellProcess(string command, bool redirect)
    {
        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        EgressGuard.ProcessDefault.Evaluate(new EgressRequest(EgressFamilies.Process, "EG-PROC-09", isWindows ? "process:powershell" : "process:bash")).ThrowIfRefused();
        var psi = new ProcessStartInfo
        {
            FileName = isWindows ? "powershell.exe" : "bash",
            UseShellExecute = false,
        };

        if (redirect)
        {
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
        }

        if (isWindows)
        {
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add(command);
        }
        else
        {
            psi.ArgumentList.Add("-lc");
            psi.ArgumentList.Add(command);
        }

        return Process.Start(psi);
    }
}
