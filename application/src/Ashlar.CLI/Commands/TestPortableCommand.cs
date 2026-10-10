using Ashlar.Abstractions.Security.Egress;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Ashlar.CLI.Output;

namespace Ashlar.CLI.Commands;

/// <summary>
/// Portable cross-platform test entry point. Replaces scripts/portable-test.sh.
/// Runs tests for every target possible on this host; works on Windows, macOS, Linux, and mobile (e.g. smoke-only).
/// </summary>
public class TestPortableCommand
{
    /// <summary>Creates a new CreateCommand instance.</summary>
    public static Command CreateCommand()
    {
        var listOpt = new Option<bool>("--list", "List targets that will run or be skipped on this host");
        var scopeOpt = new Option<string>("--scope", () => "persistence", "Scope: smoke (native only), persistence (multi-env), trust (multi-env), or all");

        var cmd = new Command("portable", "Run portable cross-platform tests (replaces portable-test.sh)")
        {
            listOpt,
            scopeOpt
        };

        cmd.SetHandler(async (InvocationContext ctx) =>
        {
            var list = ctx.ParseResult.GetValueForOption(listOpt);
            var scope = ctx.ParseResult.GetValueForOption(scopeOpt) ?? "persistence";
            var rootCommand = ctx.ParseResult.RootCommandResult.Command;
            var jsonOpt = rootCommand.Options.OfType<Option<bool>>().FirstOrDefault(o => o.Name == "--format-json");
            var verboseOpt = rootCommand.Options.OfType<Option<bool>>().FirstOrDefault(o => o.Name == "--verbose");
            var json = jsonOpt != null && ctx.ParseResult.GetValueForOption(jsonOpt);
            var verbose = verboseOpt != null && ctx.ParseResult.GetValueForOption(verboseOpt);

            var exitCode = await ExecuteAsync(list, scope, json, verbose);
            ctx.ExitCode = exitCode;
        });

        return cmd;
    }

    /// <summary>Executes the command handler and returns a process exit code.</summary>
    public static async Task<int> ExecuteAsync(bool list, string scope, bool json, bool verbose)
    {
        var console = json ? null : new CliConsole(verbose);
        var host = GetHostOs();

        if (list)
        {
            ListTargets(host, console, json);
            return 0;
        }

        if (!json && console != null)
        {
            console.WriteHeader($"Portable cross-platform tests (host: {host}, scope: {scope})");
            console.WriteLine();
        }

        if (scope.Equals("smoke", StringComparison.OrdinalIgnoreCase))
        {
            return await RunSmokeAsync(console, json, verbose);
        }

        if (scope.Equals("persistence", StringComparison.OrdinalIgnoreCase))
        {
            return await RunPersistenceMultiEnvAsync(console, json, verbose);
        }

        if (scope.Equals("trust", StringComparison.OrdinalIgnoreCase))
        {
            return await TestMultiEnvCommand.ExecuteAsync("trust", null, true, ephemeral: false, noNetwork: false, json, verbose);
        }

        if (scope.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            var persistenceExit = await RunPersistenceMultiEnvAsync(console, json, verbose);
            if (persistenceExit != 0) return persistenceExit;
            var trustExit = await TestMultiEnvCommand.ExecuteAsync("trust", null, true, ephemeral: false, noNetwork: false, json, verbose);
            if (trustExit != 0) return trustExit;
            return await RunSmokeAsync(console, json, verbose);
        }

        if (!json && console != null)
            console.WriteError($"Unknown scope: {scope} (use smoke|persistence|trust|all)");
        else if (json)
            Console.WriteLine(JsonSerializer.Serialize(new { error = $"Unknown scope: {scope}" }));
        return 1;
    }

    private static void ListTargets(string host, CliConsole? console, bool json)
    {
        if (json)
        {
            var run = new List<string>();
            var skip = new List<string>();
            if (host == "windows") { run.Add("windows"); run.Add("linux (Docker)"); run.Add("android (Docker)"); skip.Add("ios"); skip.Add("macos"); }
            else if (host == "macos") { run.Add("ios"); run.Add("macos"); run.Add("linux (Docker)"); run.Add("android (Docker)"); skip.Add("windows (use CI windows-latest)"); }
            else { run.Add("linux (Ubuntu, Alpine, Debian)"); run.Add("android (Docker)"); skip.Add("windows (use CI)"); skip.Add("ios"); skip.Add("macos"); }
            Console.WriteLine(JsonSerializer.Serialize(new { host, run, skip }));
            return;
        }

        if (console != null)
        {
            console.WriteLine($"Host: {host}");
            console.WriteLine("");
            console.WriteLine("Targets that WILL run on this host:");
            console.WriteLine("  - linux (Ubuntu, Alpine, Debian): Docker");
            console.WriteLine("  - android: Docker");
            if (host == "windows") console.WriteLine("  - windows: Native / Docker");
            if (host == "macos")
            {
                console.WriteLine("  - ios: Native (simulator)");
                console.WriteLine("  - macos: Native");
                console.WriteLine("  - unity: Native (if Unity installed)");
                console.WriteLine("  - windows: Skip (Windows containers require Windows host; use CI windows-latest)");
            }
            if (host == "linux") console.WriteLine("  - windows: Skip (use CI windows-latest or see docs)");
            console.WriteLine("");
            console.WriteLine("Targets that will be SKIPPED on this host:");
            if (host != "macos") console.WriteLine("  - ios: Requires macOS");
            if (host != "macos") console.WriteLine("  - macos: Requires Apple hardware");
            console.WriteLine("");
            console.WriteLine("To get full coverage (all platforms), use CI: gh workflow run \"Cross-Platform Tests\" --ref master");
        }
    }

    private static async Task<int> RunSmokeAsync(CliConsole? console, bool json, bool verbose)
    {
        var root = DiscoverProjectRoot();
        if (string.IsNullOrEmpty(root))
        {
            if (json) Console.WriteLine(JsonSerializer.Serialize(new { error = "Project root not found" }));
            else console?.WriteError("Project root not found");
            return 1;
        }

        var project = Path.Combine(root, "src", "Ashlar.Tests.Infrastructure", "Ashlar.Tests.Infrastructure.csproj");
        if (!File.Exists(project))
        {
            if (json) Console.WriteLine(JsonSerializer.Serialize(new { error = "Test project not found" }));
            else console?.WriteError("Test project not found");
            return 1;
        }

        if (!json && console != null) console.WriteLine("Running smoke tests (BaseFrameworkSmokeTests)...");

        EgressGuard.ProcessDefault.Evaluate(new EgressRequest(EgressFamilies.Process, "EG-PROC-09", "process:dotnet")).ThrowIfRefused();
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"test \"{project}\" --filter \"FullyQualifiedName~BaseFrameworkSmokeTests\" --logger \"console;verbosity=minimal\"",
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi);
        if (process == null) { if (json) Console.WriteLine(JsonSerializer.Serialize(new { error = "Failed to start dotnet test" })); else console?.WriteError("Failed to start dotnet test"); return 1; }

        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (!json && verbose && !string.IsNullOrEmpty(stdout)) console?.WriteLine(stdout);
        if (process.ExitCode != 0 && !string.IsNullOrEmpty(stderr)) console?.WriteError(stderr);

        if (json)
            Console.WriteLine(JsonSerializer.Serialize(new { exitCode = process.ExitCode, passed = process.ExitCode == 0 }));
        return process.ExitCode;
    }

    private static async Task<int> RunPersistenceMultiEnvAsync(CliConsole? console, bool json, bool verbose)
    {
        // Delegate to multi-env with suite persistence (same as test-persistence-multi-env.sh)
        var root = DiscoverProjectRoot();
        if (string.IsNullOrEmpty(root)) return 1;

        // Run via dotnet run to avoid circular ref: ashlar test multi-env --suite persistence --all
        EgressGuard.ProcessDefault.Evaluate(new EgressRequest(EgressFamilies.Process, "EG-PROC-09", "process:dotnet")).ThrowIfRefused();
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"run --project \"{Path.Combine(root, "src", "Ashlar.CLI", "Ashlar.CLI.csproj")}\" -- test multi-env --suite persistence --all",
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi);
        if (process == null) return 1;
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (!json && !string.IsNullOrEmpty(stdout)) console?.WriteLine(stdout);
        if (process.ExitCode != 0 && !string.IsNullOrEmpty(stderr)) console?.WriteError(stderr);
        return process.ExitCode;
    }

    private static string GetHostOs()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return "macos";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "windows";
        return "linux";
    }

    private static string? DiscoverProjectRoot()
    {
        var dir = new DirectoryInfo(Environment.CurrentDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Ashlar.sln"))) return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }
}
