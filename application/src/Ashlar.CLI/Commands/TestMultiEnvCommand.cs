using System.CommandLine;
using System.CommandLine.Invocation;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ashlar.CLI.Output;
using Ashlar.Infrastructure.HostProcess;

namespace Ashlar.CLI.Commands;

/// <summary>
/// Runs framework/caching/persistence tests across multiple Docker environments.
/// Replaces test-framework-multi-env.sh, test-caching-multi-env.sh, test-persistence-multi-env.sh.
/// Cross-platform: uses Process to run docker/dotnet so it works on Windows, macOS, Linux.
/// </summary>
public class TestMultiEnvCommand
{
    private static readonly (string EnvName, string Dockerfile, string DotnetVersion, string Description)[] FrameworkEnvs =
    {
        ("ubuntu-8.0", ".docker/Dockerfile.test-framework", "8.0", "Ubuntu 22.04"),
        ("ubuntu-7.0", ".docker/Dockerfile.test-framework", "7.0", "Ubuntu 22.04"),
        ("alpine-8.0", ".docker/Dockerfile.test-framework-alpine", "8.0", "Alpine Linux"),
        ("debian-8.0", ".docker/Dockerfile.test-framework-debian", "8.0", "Debian 12"),
        ("android-8.0", ".docker/Dockerfile.test-framework-android", "8.0", "Android"),
        ("windows-8.0", ".docker/Dockerfile.test-framework-windows", "8.0", "Windows")
    };

    private static readonly (string Filter, string LogSuffix)[] FrameworkPhases =
    {
        ("FullyQualifiedName~BaseFrameworkSmokeTests", "base-framework"),
        ("FullyQualifiedName~StressTests", "infrastructure-stress"),
        ("FullyQualifiedName~BrickValueSerializerTests", "execution-brick")
    };

    /// <summary>Creates a new CreateCommand instance.</summary>
    public static Command CreateCommand()
    {
        var suiteOpt = new Option<string>("--suite", () => "framework", "Suite: framework, caching, persistence, trust, or adaptation");
        var envOpt = new Option<string?>("--env", "Run only this environment (e.g. ubuntu-8.0)");
        var allOpt = new Option<bool>("--all", "Run all environments for the suite");
        var ephemeralOpt = new Option<bool>("--ephemeral", () => false, "Run in ephemeral containers; no volume mounts, results discarded when container is removed");
        var noNetworkOpt = new Option<bool>("--no-network", () => false, "Air-gapped mode: run containers with --network none (no network access)");

        var cmd = new Command("multi-env", "Run tests across Docker environments (replaces test-*-multi-env.sh)")
        {
            suiteOpt,
            envOpt,
            allOpt,
            ephemeralOpt,
            noNetworkOpt
        };

        cmd.SetHandler(async (InvocationContext ctx) =>
        {
            var suite = ctx.ParseResult.GetValueForOption(suiteOpt) ?? "framework";
            var env = ctx.ParseResult.GetValueForOption(envOpt);
            var all = ctx.ParseResult.GetValueForOption(allOpt);
            var ephemeral = ctx.ParseResult.GetValueForOption(ephemeralOpt) || string.Equals(Environment.GetEnvironmentVariable("ASHLAR_TEST_EPHEMERAL"), "1", StringComparison.OrdinalIgnoreCase);
            var noNetwork = ctx.ParseResult.GetValueForOption(noNetworkOpt) || string.Equals(Environment.GetEnvironmentVariable("ASHLAR_TEST_NO_NETWORK"), "1", StringComparison.OrdinalIgnoreCase);
            var rootCommand = ctx.ParseResult.RootCommandResult.Command;
            var jsonOpt = rootCommand.Options.OfType<Option<bool>>().FirstOrDefault(o => o.Name == "--format-json");
            var verboseOpt = rootCommand.Options.OfType<Option<bool>>().FirstOrDefault(o => o.Name == "--verbose");
            var json = jsonOpt != null && ctx.ParseResult.GetValueForOption(jsonOpt);
            var verbose = verboseOpt != null && ctx.ParseResult.GetValueForOption(verboseOpt);

            var exitCode = await ExecuteAsync(suite, env, all, ephemeral, noNetwork, json, verbose);
            ctx.ExitCode = exitCode;
        });

        return cmd;
    }

    /// <summary>Executes the command handler and returns a process exit code.</summary>
    public static async Task<int> ExecuteAsync(string suite, string? envName, bool all, bool ephemeral, bool noNetwork, bool json, bool verbose)
    {
        var console = json ? null : new CliConsole(verbose);
        var root = DiscoverProjectRoot();
        if (string.IsNullOrEmpty(root))
        {
            if (json) Console.WriteLine(JsonSerializer.Serialize(new { error = "Project root not found" }));
            else console?.WriteError("Project root not found");
            return 1;
        }

        if (suite.Equals("caching", StringComparison.OrdinalIgnoreCase) || suite.Equals("persistence", StringComparison.OrdinalIgnoreCase))
        {
            return await RunCachingOrPersistenceAsync(root, suite, envName, all, ephemeral, noNetwork, console, json, verbose);
        }

        if (suite.Equals("trust", StringComparison.OrdinalIgnoreCase))
        {
            return await RunTrustSuiteAsync(root, envName, all, ephemeral, noNetwork, console, json, verbose);
        }

        if (suite.Equals("adaptation", StringComparison.OrdinalIgnoreCase))
        {
            return await RunAdaptationSuiteAsync(root, envName, all, ephemeral, noNetwork, console, json, verbose);
        }

        var envs = all || string.IsNullOrEmpty(envName)
            ? FrameworkEnvs
            : FrameworkEnvs.Where(e => e.EnvName.Equals(envName, StringComparison.OrdinalIgnoreCase)).ToArray();

        if (envs.Length == 0)
        {
            if (json) Console.WriteLine(JsonSerializer.Serialize(new { error = $"Unknown environment: {envName}" }));
            else console?.WriteError($"Unknown environment: {envName}");
            return 1;
        }

        var resultsDir = Path.Combine(root, "test-results", "framework");
        if (!ephemeral) Directory.CreateDirectory(resultsDir);

        var failed = 0;
        foreach (var (env, dockerfile, dotnetVersion, description) in envs)
        {
            var dockerfilePath = Path.GetFullPath(Path.Combine(root, dockerfile.TrimStart('/', '\\').Replace('/', Path.DirectorySeparatorChar)));
            if (!File.Exists(dockerfilePath))
            {
                if (!json) console?.WriteWarning($"Dockerfile not found: {dockerfile}");
                continue;
            }

            if (!json && console != null) { console.WriteLine($"Testing {env} ({description})..."); }

            var (phaseFailed, baseOk, execOk) = await RunFrameworkEnvAsync(root, env, dockerfilePath, dotnetVersion, resultsDir, ephemeral, noNetwork, console, json, verbose);
            if (phaseFailed) failed++;
        }

        if (!json && console != null)
            console.WriteSuccess(failed == 0 ? "All framework multi-env runs completed." : $"{failed} environment(s) failed.");
        if (json)
            Console.WriteLine(JsonSerializer.Serialize(new { failed, total = envs.Length, ok = failed == 0 }));
        return failed > 0 ? 1 : 0;
    }

    private static async Task<(bool PhaseFailed, bool BaseOk, bool ExecOk)> RunFrameworkEnvAsync(
        string root,
        string envName,
        string dockerfilePath,
        string dotnetVersion,
        string resultsDir,
        bool ephemeral,
        bool noNetwork,
        CliConsole? console,
        bool json,
        bool verbose)
    {
        var imageTag = $"ashlar-framework-test:{envName}";
        var isWindows = dockerfilePath.Contains("windows", StringComparison.OrdinalIgnoreCase);
        var logBaseDir = ephemeral ? Path.GetTempPath() : resultsDir;

        // Build
        if (!json && console != null) console.WriteLine($"  Building {envName}...");
        var sdkVersion = string.Equals(dotnetVersion, "8.0", StringComparison.Ordinal) ? "10.0" : dotnetVersion;
        var buildExit = await RunProcessAsync("docker", $"build -f \"{dockerfilePath}\" -t {imageTag} --build-arg DOTNET_SDK_VERSION={sdkVersion} \"{root}\"", root, verbose ? console : null);
        if (buildExit != 0)
        {
            if (!json) console?.WriteError($"  Failed to build {envName}");
            return (true, false, false);
        }

        var baseOk = true;
        var execOk = true;

        foreach (var (filter, logSuffix) in FrameworkPhases)
        {
            var logFile = Path.Combine(logBaseDir, $"{envName}-{logSuffix}.log");
            var projectPath = "src/Ashlar.Tests.Infrastructure/Ashlar.Tests.Infrastructure.csproj";
            var testArgs = $"test \"{projectPath}\" /p:TargetFramework=net8.0 --no-build --filter \"{filter}\" --logger \"console;verbosity=minimal\" --logger \"trx;LogFileName={envName}-{logSuffix}.trx\" --results-directory /workspace/test-results";
            string runCmd;
            var volMount = ephemeral ? "" : (isWindows ? $" -v \"{resultsDir}\":C:\\workspace\\test-results" : $" -v \"{resultsDir}\":/workspace/test-results");
            var networkOpt = noNetwork ? " --network none" : "";
            if (isWindows)
                runCmd = $"run --rm{volMount}{networkOpt} -e DOTNET_VERSION={dotnetVersion} {imageTag} cmd /c \"cd C:\\workspace && dotnet {testArgs.Replace("/workspace/test-results", "C:\\\\workspace\\\\test-results")}\"";
            else
                runCmd = $"run --rm{volMount}{networkOpt} -e DOTNET_VERSION={dotnetVersion} {imageTag} bash -c \"cd /workspace && dotnet {testArgs}\"";

            var runExit = await RunProcessAsync("docker", runCmd, root, null, logFile);
            var (passed, failed, total) = ParseTestOutput(File.Exists(logFile) ? await File.ReadAllTextAsync(logFile) : "");
            if (failed > 0 || (passed == 0 && total == 0 && runExit != 0))
            {
                if (logSuffix == "base-framework") baseOk = false;
                if (logSuffix == "execution-brick") execOk = false;
            }
            if (!json && console != null) console.WriteLine($"  {logSuffix}: {passed} passed, {failed} failed");
        }

        try { await RunProcessAsync("docker", $"rmi {imageTag}", root, null); } catch { /* best effort */ }
        return (!baseOk || !execOk, baseOk, execOk);
    }

    private static async Task<int> RunAdaptationSuiteAsync(string root, string? envName, bool all, bool ephemeral, bool noNetwork, CliConsole? console, bool json, bool verbose)
    {
        var envs = all || string.IsNullOrEmpty(envName)
            ? FrameworkEnvs
            : FrameworkEnvs.Where(e => e.EnvName.Equals(envName, StringComparison.OrdinalIgnoreCase)).ToArray();

        if (envs.Length == 0)
        {
            if (json) Console.WriteLine(JsonSerializer.Serialize(new { error = $"Unknown environment: {envName}" }));
            else console?.WriteError($"Unknown environment: {envName}");
            return 1;
        }

        var resultsDir = Path.Combine(root, "test-results", "adaptation");
        var logBaseDir = ephemeral ? Path.GetTempPath() : resultsDir;
        if (!ephemeral) Directory.CreateDirectory(resultsDir);
        var failed = 0;
        const string filter = "Category=Adaptation";

        foreach (var (env, dockerfile, dotnetVersion, description) in envs)
        {
            var dockerfilePath = Path.GetFullPath(Path.Combine(root, dockerfile.TrimStart('/', '\\').Replace('/', Path.DirectorySeparatorChar)));
            if (!File.Exists(dockerfilePath))
            {
                if (!json) console?.WriteWarning($"Dockerfile not found: {dockerfile}");
                continue;
            }

            if (!json && console != null) console.WriteLine($"Testing adaptation on {env} ({description})...");

            var imageTag = $"ashlar-adaptation-test:{env}";
            var isWindows = dockerfilePath.Contains("windows", StringComparison.OrdinalIgnoreCase);

            var sdkVersion = string.Equals(dotnetVersion, "8.0", StringComparison.Ordinal) ? "10.0" : dotnetVersion;
        var buildExit = await RunProcessAsync("docker", $"build -f \"{dockerfilePath}\" -t {imageTag} --build-arg DOTNET_SDK_VERSION={sdkVersion} \"{root}\"", root, verbose ? console : null);
            if (buildExit != 0)
            {
                if (!json) console?.WriteError($"  Failed to build {env}");
                failed++;
                continue;
            }

            var logFile = Path.Combine(logBaseDir, $"{env}-adaptation.log");
            var projectPath = "src/Ashlar.Tests.Infrastructure/Ashlar.Tests.Infrastructure.csproj";
            var testArgs = $"test \"{projectPath}\" --filter \"{filter}\" --blame-hang-timeout 900s --blame-hang-dump-type none --logger \"console;verbosity=minimal\" --logger \"trx;LogFileName={env}-adaptation.trx\" --results-directory /workspace/test-results";
            var volMount = ephemeral ? "" : (isWindows ? $" -v \"{resultsDir}\":C:\\workspace\\test-results" : $" -v \"{resultsDir}\":/workspace/test-results");
            var networkOpt = noNetwork ? " --network none" : "";
            string runCmd;
            if (isWindows)
                runCmd = $"run --rm{volMount}{networkOpt} -e DOTNET_VERSION={dotnetVersion} {imageTag} cmd /c \"cd C:\\workspace && dotnet {testArgs.Replace("/workspace/test-results", "C:\\\\workspace\\\\test-results")}\"";
            else
                runCmd = $"run --rm{volMount}{networkOpt} -e DOTNET_VERSION={dotnetVersion} {imageTag} bash -c \"cd /workspace && dotnet {testArgs}\"";

            var runExit = await RunProcessAsync("docker", runCmd, root, null, logFile);
            var (passed, testFailed, total) = ParseTestOutput(File.Exists(logFile) ? await File.ReadAllTextAsync(logFile) : "");

            if (runExit != 0 || testFailed > 0)
            {
                failed++;
                if (!json) console?.WriteError($"  {env}: {testFailed} failed");
            }
            else if (!json && console != null)
            {
                console.WriteLine($"  {env}: {passed} passed");
            }

            try { await RunProcessAsync("docker", $"rmi {imageTag}", root, null); } catch { /* best effort */ }
        }

        if (!json && console != null)
            console.WriteSuccess(failed == 0 ? "All adaptation multi-env runs completed." : $"{failed} environment(s) failed.");
        if (json)
            Console.WriteLine(JsonSerializer.Serialize(new { failed, total = envs.Length, ok = failed == 0 }));
        return failed > 0 ? 1 : 0;
    }

    private static async Task<int> RunTrustSuiteAsync(string root, string? envName, bool all, bool ephemeral, bool noNetwork, CliConsole? console, bool json, bool verbose)
    {
        var dockerfiles = new[]
        {
            ("ubuntu-8.0", ".docker/Dockerfile.test-caching"),
            ("alpine-8.0", ".docker/Dockerfile.test-caching-alpine"),
            ("debian-8.0", ".docker/Dockerfile.test-caching-debian")
        };
        var resultsDir = Path.Combine(root, "test-results", "trust");
        var logBaseDir = ephemeral ? Path.GetTempPath() : resultsDir;
        if (!ephemeral) Directory.CreateDirectory(resultsDir);
        var failed = 0;
        var trustFilter = "FullyQualifiedName~Trust";

        foreach (var (env, df) in dockerfiles)
        {
            if (!all && !string.IsNullOrEmpty(envName) && !env.Equals(envName, StringComparison.OrdinalIgnoreCase)) continue;
            var path = Path.GetFullPath(Path.Combine(root, df.TrimStart('/', '\\').Replace('/', Path.DirectorySeparatorChar)));
            if (!File.Exists(path)) continue;

            var tag = $"ashlar-trust-test:{env}";
            if (!json && console != null) console.WriteLine($"Testing Trust on {env}...");

            var dotnetVer = "10.0";
            var sdkVer = string.Equals(dotnetVer, "8.0", StringComparison.Ordinal) ? "10.0" : dotnetVer;
            var buildExit = await RunProcessAsync("docker", $"build -f \"{path}\" -t {tag} --build-arg DOTNET_SDK_VERSION={sdkVer} \"{root}\"", root, verbose ? console : null);
            if (buildExit != 0)
            {
                if (!json) console?.WriteError($"  Failed to build {env}");
                failed++;
                continue;
            }

            var logFile = Path.Combine(logBaseDir, $"{env}-trust.log");
            // Use -f net10.0: SDK 10 images lack net8.0 runtime; single-quote logger to avoid nested shell quoting
            var testArgsInfra = $"test src/Ashlar.Tests.Infrastructure/Ashlar.Tests.Infrastructure.csproj -f net10.0 --filter {trustFilter} --logger 'console;verbosity=minimal' --logger 'trx;LogFileName={env}-trust-infra.trx' --results-directory /workspace/test-results";
            var testArgsBg = $"test src/Ashlar.Tests.BackgroundAgents/Ashlar.Tests.BackgroundAgents.csproj -f net10.0 --filter {trustFilter} --logger 'console;verbosity=minimal' --logger 'trx;LogFileName={env}-trust-bg.trx' --results-directory /workspace/test-results";
            var volMount = ephemeral ? "" : $" -v \"{resultsDir}\":/workspace/test-results";
            var networkOpt = noNetwork ? " --network none" : "";
            var runCmd = $"run --rm{volMount}{networkOpt} {tag} bash -c \"cd /workspace && dotnet {testArgsInfra} && dotnet {testArgsBg}\"";

            var runExit = await RunProcessAsync("docker", runCmd, root, null, logFile);
            var (passed, testFailed, total) = ParseTestOutput(File.Exists(logFile) ? await File.ReadAllTextAsync(logFile) : "");

            if (runExit != 0 || testFailed > 0)
            {
                failed++;
                if (!json) console?.WriteError($"  {env}: {testFailed} failed");
            }
            else if (!json && console != null)
            {
                console.WriteLine($"  {env}: {passed} passed");
            }

            try { await RunProcessAsync("docker", $"rmi {tag}", root, null); } catch { /* best effort */ }
        }

        if (!json && console != null)
            console.WriteSuccess(failed == 0 ? "All Trust multi-env runs completed." : $"{failed} environment(s) failed.");
        if (json)
            Console.WriteLine(JsonSerializer.Serialize(new { failed, total = dockerfiles.Length, ok = failed == 0 }));
        return failed > 0 ? 1 : 0;
    }

    private static async Task<int> RunCachingOrPersistenceAsync(string root, string suite, string? envName, bool all, bool ephemeral, bool noNetwork, CliConsole? console, bool json, bool verbose)
    {
        var dockerfiles = new[] { ("ubuntu-8.0", ".docker/Dockerfile.test-caching"), ("alpine-8.0", ".docker/Dockerfile.test-caching-alpine"), ("debian-8.0", ".docker/Dockerfile.test-caching-debian") };
        var filter = "FullyQualifiedName~BaseFrameworkSmokeTests";
        if (suite.Equals("persistence", StringComparison.OrdinalIgnoreCase)) filter = "FullyQualifiedName~InMemoryPersistenceTests";
        var resultsDir = Path.Combine(root, "test-results", "caching");
        if (!ephemeral) Directory.CreateDirectory(resultsDir);
        var failed = 0;
        var volMount = ephemeral ? "" : $" -v \"{resultsDir}\":/workspace/test-results";
        foreach (var (env, df) in dockerfiles)
        {
            if (!all && !string.IsNullOrEmpty(envName) && !env.Equals(envName, StringComparison.OrdinalIgnoreCase)) continue;
            var path = Path.GetFullPath(Path.Combine(root, df.TrimStart('/', '\\').Replace('/', Path.DirectorySeparatorChar)));
            if (!File.Exists(path)) continue;
            var tag = $"ashlar-caching-test:{env}";
            var dotnetVer = "10.0";
            var sdkVer = string.Equals(dotnetVer, "8.0", StringComparison.Ordinal) ? "10.0" : dotnetVer;
            var buildExit = await RunProcessAsync("docker", $"build -f \"{path}\" -t {tag} --build-arg DOTNET_SDK_VERSION={sdkVer} \"{root}\"", root, verbose ? console : null);
            if (buildExit != 0) { failed++; continue; }
            var testArgs = $"test src/Ashlar.Tests.Infrastructure/Ashlar.Tests.Infrastructure.csproj -f net10.0 --filter {filter} --logger 'console;verbosity=minimal'";
            var networkOpt = noNetwork ? " --network none" : "";
            var runExit = await RunProcessAsync("docker", $"run --rm{volMount}{networkOpt} {tag} bash -c \"cd /workspace && dotnet {testArgs}\"", root);
            if (runExit != 0) failed++;
        }
        if (json) Console.WriteLine(JsonSerializer.Serialize(new { failed, ok = failed == 0 }));
        return failed > 0 ? 1 : 0;
    }

    private static async Task<int> RunProcessAsync(string fileName, string arguments, string workingDir, CliConsole? console = null, string? captureToFile = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDir,
        };
        var run = await TimedProcess.RunAsync(psi, TimedProcess.OperatorCommandTimeout).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(captureToFile))
            await File.WriteAllTextAsync(captureToFile, run.StdOut + "\n" + run.StdErr);
        if (console != null && !string.IsNullOrEmpty(run.StdErr))
            console.WriteError(run.StdErr);
        return run.ExitCode;
    }

    private static (int passed, int failed, int total) ParseTestOutput(string output)
    {
        var passedMatch = Regex.Match(output, @"Passed:\s*(\d+)");
        var failedMatch = Regex.Match(output, @"Failed:\s*(\d+)");
        var totalMatch = Regex.Match(output, @"Total:\s*(\d+)");
        var passed = passedMatch.Success ? int.Parse(passedMatch.Groups[1].Value) : 0;
        var failed = failedMatch.Success ? int.Parse(failedMatch.Groups[1].Value) : 0;
        var total = totalMatch.Success ? int.Parse(totalMatch.Groups[1].Value) : passed + failed;
        return (passed, failed, total);
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
