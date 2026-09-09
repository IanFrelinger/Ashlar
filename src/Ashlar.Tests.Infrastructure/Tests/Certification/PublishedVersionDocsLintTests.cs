using System.Diagnostics;
using FluentAssertions;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>C6: docs published-version claims key off ci/published-version, never VERSION.</summary>
[Trait("Category", "Certification")]
public sealed class PublishedVersionDocsLintTests
{
    [Fact]
    public void PublishedVersionFile_IsNotTheRepoVersionBumpSource()
    {
        var root = FindRepoRoot();
        var published = File.ReadAllText(Path.Combine(root, "ci", "published-version")).Trim();
        published.Should().MatchRegex(@"^\d+\.\d+\.\d+$");
        File.Exists(Path.Combine(root, "scripts", "verify-docs-published-version.sh")).Should().BeTrue();
    }

    [Fact]
    public void DocsClaimLint_PassesAgainstPublishedVersion()
    {
        var root = FindRepoRoot();
        var script = Path.Combine(root, "scripts", "verify-docs-published-version.sh");
        var psi = new ProcessStartInfo(ResolveBash())
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        psi.ArgumentList.Add(script);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("failed to start C6 lint");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        process.ExitCode.Should().Be(0, stderr + stdout);
        stdout.Should().Contain("published-version lint ok");
    }

    /// <summary>
    /// Resolves the bash executable to run the lint script with.
    /// On Windows a bare "bash" must not be used: CreateProcess searches System32 before PATH,
    /// so it resolves to C:\Windows\System32\bash.exe (the WSL launcher), which fails with
    /// "Windows Subsystem for Linux has no installed distributions" on hosts without a distro
    /// (e.g. the windows-latest runner). Prefer Git for Windows' bash, located next to git.exe.
    /// </summary>
    private static string ResolveBash()
    {
        if (!OperatingSystem.IsWindows())
            return "bash";

        foreach (var candidate in GitBashCandidates())
        {
            if (File.Exists(candidate))
                return Path.GetFullPath(candidate);
        }

        return "bash";
    }

    private static IEnumerable<string> GitBashCandidates()
    {
        // 1. Relative to git.exe on PATH: <GitRoot>\cmd\git.exe -> <GitRoot>\bin\bash.exe.
        var pathDirs = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        foreach (var dir in pathDirs)
        {
            if (File.Exists(Path.Combine(dir, "git.exe")))
                yield return Path.Combine(dir, "..", "bin", "bash.exe");
        }

        // 2. Default Git for Windows install locations.
        foreach (var programFiles in new[]
                 {
                     Environment.GetEnvironmentVariable("ProgramW6432"),
                     Environment.GetEnvironmentVariable("ProgramFiles"),
                     Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
                 })
        {
            if (!string.IsNullOrEmpty(programFiles))
                yield return Path.Combine(programFiles, "Git", "bin", "bash.exe");
        }

        yield return @"C:\Program Files\Git\bin\bash.exe";
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Ashlar.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("repo root not found");
    }
}
