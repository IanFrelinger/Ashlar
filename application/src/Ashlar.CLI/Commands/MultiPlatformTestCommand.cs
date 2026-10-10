using Ashlar.Abstractions.Security.Egress;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ashlar.CLI.Output;
using Ashlar.Infrastructure.Testing.ExecutionPlatform;
using Ashlar.Infrastructure.Testing;

namespace Ashlar.CLI.Commands;

/// <summary>
/// Command for running tests across multiple platforms using Ashlar's execution platform abstraction.
/// Replaces bash scripts like test-caching-multi-env.sh, test-framework-multi-env.sh, etc.
/// </summary>
public class MultiPlatformTestCommand : Command
{
    /// <summary>Creates the multi-platform test command with platform, project, and execution options.</summary>
    public MultiPlatformTestCommand() : base("test", "Run tests across multiple platforms using Ashlar's execution platform")
    {
        var platformsOption = new Option<string[]>(
            "--platforms",
            () => Array.Empty<string>(),
            "Platforms to test (ubuntu, alpine, debian, android, ios, windows, macos). Default: all")
        {
            AllowMultipleArgumentsPerToken = true
        };

        var testProjectOption = new Option<string?>(
            "--project",
            () => (string?)null,
            "Test project to run (e.g., Ashlar.Tests.Infrastructure)"
        );

        var filterOption = new Option<string?>(
            "--filter",
            () => (string?)null,
            "Test filter (xUnit filter syntax)"
        );

        var dotnetVersionOption = new Option<string>(
            "--dotnet-version",
            () => "8.0",
            ".NET version to use"
        );

        var executionPlatformOption = new Option<string>(
            "--execution-platform",
            () => "docker",
            "Execution platform (docker, rancher, kubernetes). Default: docker"
        );

        var outputDirOption = new Option<DirectoryInfo>(
            "--output-dir",
            () => new DirectoryInfo("test-results"),
            "Directory for test results"
        );

        var coverageOption = new Option<bool>(
            "--coverage",
            () => false,
            "Enable code coverage collection"
        );

        var stressOption = new Option<bool>(
            "--stress",
            () => false,
            "Run stress tests (multiple iterations)"
        );

        var visualOption = new Option<bool>(
            "--visual",
            () => false,
            "Run visual validation tests (requires Ollama for vision models)"
        );

        AddOption(platformsOption);
        AddOption(testProjectOption);
        AddOption(filterOption);
        AddOption(dotnetVersionOption);
        AddOption(executionPlatformOption);
        AddOption(outputDirOption);
        AddOption(coverageOption);
        AddOption(stressOption);
        AddOption(visualOption);
    }

    /// <summary>Runs tests on the requested platforms and returns a process exit code.</summary>
    public static async Task<int> ExecuteAsync(
        string[] platforms,
        string? testProject,
        string? filter,
        string dotnetVersion,
        string executionPlatform,
        DirectoryInfo outputDir,
        bool coverage,
        bool stress,
        bool visual,
        bool ephemeral,
        bool json,
        bool verbose,
        IServiceProvider? serviceProvider = null)
    {
        var services = serviceProvider ?? CreateDefaultServiceProvider();
        if (services == null) throw new InvalidOperationException("Service provider is required");
        var loggerFactory = services.GetRequiredService<ILoggerFactory>();
        var logger = loggerFactory.CreateLogger<MultiPlatformTestCommand>();
        var console = json ? null : new CliConsole(verbose);

        try
        {
            if (!json && console != null)
            {
                console.WriteHeader("🧪 Multi-Platform Test Execution");
                console.WritePair("Platforms", platforms.Length > 0 ? string.Join(", ", platforms) : "all");
                console.WritePair("Test Project", testProject ?? "all");
                console.WritePair(".NET Version", dotnetVersion);
                console.WritePair("Execution Platform", executionPlatform);
                if (coverage) console.WritePair("Coverage", "enabled");
                if (stress) console.WritePair("Stress", "enabled");
                if (visual) console.WritePair("Visual Validation", "enabled");
                if (ephemeral) console.WritePair("Ephemeral", "enabled");
                console.WriteLine();
            }

            // Handle visual validation tests
            if (visual)
            {
                testProject = testProject ?? "Ashlar.Tests.Infrastructure";
                if (platforms.Length == 0)
                {
                    platforms = new[] { "ubuntu", "alpine", "debian", "android", "ios" };
                }
            }

            // Create execution platform
            var platform = CreateExecutionPlatform(executionPlatform, loggerFactory, logger);
            if (platform == null)
            {
                if (json)
                {
                    Console.WriteLine(JsonSerializer.Serialize(new { ok = false, error = $"Execution platform '{executionPlatform}' not available" }));
                }
                else
                {
                    console?.WriteError($"Execution platform '{executionPlatform}' not available");
                }
                return 1;
            }

            // Check availability
            var isAvailable = await platform.IsAvailableAsync();
            if (!isAvailable)
            {
                if (json)
                {
                    Console.WriteLine(JsonSerializer.Serialize(new { ok = false, error = $"{executionPlatform} is not available" }));
                }
                else
                {
                    console?.WriteError($"{executionPlatform} is not available. Please ensure it's running.");
                }
                return 1;
            }

            // Determine platforms to test
            var platformsToTest = platforms.Length > 0 
                ? platforms 
                : new[] { "ubuntu", "alpine", "debian", "android", "ios", "windows", "macos" };

            var results = new List<PlatformTestResult>();
            var totalPassed = 0;
            var totalFailed = 0;

            foreach (var platformName in platformsToTest)
            {
            if (!json && console != null)
            {
                console.WriteLine($"Testing on {platformName}...");
            }

                var result = await RunTestsOnPlatformAsync(
                    platformName,
                    testProject,
                    filter,
                    dotnetVersion,
                    platform,
                    outputDir.FullName,
                    ephemeral,
                    loggerFactory,
                    logger);

                results.Add(result);
                if (result.Passed)
                {
                    totalPassed++;
                }
                else
                {
                    totalFailed++;
                }
            }

            // Output results
            if (json)
            {
                var jsonResult = new
                {
                    ok = totalFailed == 0,
                    platforms = results.Select(r => new
                    {
                        platform = r.PlatformName,
                        passed = r.Passed,
                        tests = r.TotalTests,
                        passedTests = r.PassedTests,
                        failedTests = r.FailedTests,
                        duration = r.Duration.TotalSeconds,
                        error = r.ErrorMessage
                    }),
                    summary = new
                    {
                        totalPlatforms = results.Count,
                        passed = totalPassed,
                        failed = totalFailed
                    }
                };
                Console.WriteLine(JsonSerializer.Serialize(jsonResult, new JsonSerializerOptions { WriteIndented = true }));
            }
            else
            {
                console?.WriteHeader("📊 Test Results Summary");
                foreach (var result in results)
                {
                    var status = result.Passed ? "✅" : "❌";
                    console?.WriteLine($"{status} {result.PlatformName}: {result.PassedTests}/{result.TotalTests} tests passed");
                }
                console?.WriteLine();
                console?.WriteSuccess($"Total: {totalPassed} platforms passed, {totalFailed} failed");
            }

            return totalFailed > 0 ? 1 : 0;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Multi-platform test execution failed");
            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(new { ok = false, error = ex.Message }));
            }
            else
            {
                console?.WriteError($"Test execution failed: {ex.Message}");
            }
            return 1;
        }
    }

    private static IExecutionPlatform? CreateExecutionPlatform(
        string platformName,
        ILoggerFactory loggerFactory,
        ILogger logger)
    {
        try
        {
            return platformName.ToLowerInvariant() switch
            {
                "docker" => (IExecutionPlatform)new DockerExecutionPlatform(loggerFactory.CreateLogger<DockerExecutionPlatform>()),
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    private static async Task<PlatformTestResult> RunTestsOnPlatformAsync(
        string platformName,
        string? testProject,
        string? filter,
        string dotnetVersion,
        IExecutionPlatform executionPlatform,
        string outputDir,
        bool ephemeral,
        ILoggerFactory loggerFactory,
        ILogger logger)
    {
        var startTime = DateTime.UtcNow;
        var projectRoot = DiscoverProjectRoot();
        string? imageTagToRemove = null;

        try
        {
            // Determine Dockerfile based on platform
            var dockerfile = GetDockerfileForPlatform(platformName, dotnetVersion);
            if (dockerfile == null && PlatformCapabilityDetector.RequiresNativeExecution(platformName))
            {
                // Native execution for iOS/macOS
                return await RunNativeTestsAsync(platformName, testProject, filter, logger);
            }

            if (dockerfile == null)
            {
                return new PlatformTestResult
                {
                    PlatformName = platformName,
                    Passed = false,
                    ErrorMessage = $"No Dockerfile found for platform {platformName}"
                };
            }

            // Build image
            var imageTag = $"ashlar-test:{platformName}-{dotnetVersion}";
            imageTagToRemove = imageTag;
            var buildResult = await executionPlatform.BuildImageAsync(
                dockerfile,
                imageTag,
                projectRoot,
                new Dictionary<string, string> { ["DOTNET_VERSION"] = dotnetVersion },
                null,
                CancellationToken.None);

            if (!buildResult.Success)
            {
                imageTagToRemove = null;
                return new PlatformTestResult
                {
                    PlatformName = platformName,
                    Passed = false,
                    ErrorMessage = $"Failed to build image: {buildResult.ErrorMessage}"
                };
            }

            // Run tests
            var testCommand = BuildTestCommand(testProject, filter);
            Dictionary<string, string>? volumeMounts = ephemeral
                ? null
                : new Dictionary<string, string>
                {
                    [Path.Combine(projectRoot, outputDir)] = "/workspace/test-results"
                };

            var runResult = await executionPlatform.RunContainerAsync(
                imageTag,
                new[] { "bash", "-c", testCommand },
                null,
                volumeMounts,
                "/workspace",
                CancellationToken.None);

            var duration = DateTime.UtcNow - startTime;

            // Remove image after use so it does not accumulate
            if (imageTagToRemove != null)
            {
                try
                {
                    await executionPlatform.RemoveImageAsync(imageTagToRemove, CancellationToken.None);
                    logger.LogDebug("Removed image after use: {ImageTag}", imageTagToRemove);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to remove image after use: {ImageTag}", imageTagToRemove);
                }
                imageTagToRemove = null;
            }

            // Parse test results
            var (passed, failed, total) = ParseTestResults(runResult.StandardOutput);

            return new PlatformTestResult
            {
                PlatformName = platformName,
                Passed = runResult.Success && failed == 0,
                TotalTests = total,
                PassedTests = passed,
                FailedTests = failed,
                Duration = duration,
                ErrorMessage = runResult.Success ? null : runResult.StandardError
            };
        }
        catch (Exception ex)
        {
            if (imageTagToRemove != null)
            {
                try
                {
                    await executionPlatform.RemoveImageAsync(imageTagToRemove, CancellationToken.None);
                    logger.LogDebug("Removed image after failure: {ImageTag}", imageTagToRemove);
                }
                catch (Exception removeEx)
                {
                    logger.LogWarning(removeEx, "Failed to remove image after failure: {ImageTag}", imageTagToRemove);
                }
            }
            logger.LogError(ex, "Failed to run tests on {Platform}", platformName);
            return new PlatformTestResult
            {
                PlatformName = platformName,
                Passed = false,
                ErrorMessage = ex.Message,
                Duration = DateTime.UtcNow - startTime
            };
        }
    }

    private static async Task<PlatformTestResult> RunNativeTestsAsync(
        string platformName,
        string? testProject,
        string? filter,
        ILogger logger)
    {
        var startTime = DateTime.UtcNow;
        var projectRoot = DiscoverProjectRoot();
        EgressGuard.ProcessDefault.Evaluate(new EgressRequest(EgressFamilies.Process, "EG-PROC-09", "process:dotnet")).ThrowIfRefused();

        try
        {
            var testCommand = BuildTestCommand(testProject, filter);
            var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = testCommand,
                    WorkingDirectory = projectRoot,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                }
            };

            process.Start();
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            var exitTask = process.WaitForExitAsync(timeoutCts.Token);
            try
            {
                await exitTask;
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return new PlatformTestResult
                {
                    PlatformName = platformName,
                    Passed = false,
                    TotalTests = 0,
                    PassedTests = 0,
                    FailedTests = 0,
                    Duration = DateTime.UtcNow - startTime,
                    ErrorMessage = "Test run timed out after 5 minutes"
                };
            }
            var output = await outputTask;
            var error = await errorTask;

            var (passed, failed, total) = ParseTestResults(output);

            return new PlatformTestResult
            {
                PlatformName = platformName,
                Passed = process.ExitCode == 0 && failed == 0,
                TotalTests = total,
                PassedTests = passed,
                FailedTests = failed,
                Duration = DateTime.UtcNow - startTime,
                ErrorMessage = process.ExitCode != 0 ? error : null
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to run native tests on {Platform}", platformName);
            return new PlatformTestResult
            {
                PlatformName = platformName,
                Passed = false,
                ErrorMessage = ex.Message,
                Duration = DateTime.UtcNow - startTime
            };
        }
    }

    private static string? GetDockerfileForPlatform(string platformName, string dotnetVersion)
    {
        var projectRoot = DiscoverProjectRoot();
        var platformLower = platformName.ToLowerInvariant();

        var dockerfileMap = new Dictionary<string, string>
        {
            ["ubuntu"] = ".docker/Dockerfile.test-caching",
            ["alpine"] = ".docker/Dockerfile.test-caching-alpine",
            ["debian"] = ".docker/Dockerfile.test-caching-debian",
            ["android"] = ".docker/Dockerfile.test-caching-android",
            ["windows"] = ".docker/Dockerfile.test-caching-windows",
        };

        if (dockerfileMap.TryGetValue(platformLower, out var dockerfile))
        {
            var fullPath = Path.Combine(projectRoot, dockerfile);
            return File.Exists(fullPath) ? fullPath : null;
        }

        return null;
    }

    private static string BuildTestCommand(string? testProject, string? filter)
    {
        var parts = new List<string> { "dotnet", "test" };

        if (!string.IsNullOrEmpty(testProject))
        {
            parts.Add($"src/{testProject}/{testProject}.csproj");
        }

        if (!string.IsNullOrEmpty(filter))
        {
            parts.Add("--filter");
            parts.Add($"'{filter}'");
        }

        parts.Add("--logger");
        parts.Add("'console;verbosity=minimal'");

        return string.Join(" ", parts);
    }

    private static (int passed, int failed, int total) ParseTestResults(string output)
    {
        // Parse dotnet test output
        var passedMatch = System.Text.RegularExpressions.Regex.Match(output, @"(\d+)\s+Passed!");
        var failedMatch = System.Text.RegularExpressions.Regex.Match(output, @"(\d+)\s+Failed!");
        var totalMatch = System.Text.RegularExpressions.Regex.Match(output, @"Total:\s*(\d+)");

        var passed = passedMatch.Success ? int.Parse(passedMatch.Groups[1].Value) : 0;
        var failed = failedMatch.Success ? int.Parse(failedMatch.Groups[1].Value) : 0;
        var total = totalMatch.Success ? int.Parse(totalMatch.Groups[1].Value) : (passed + failed);

        return (passed, failed, total);
    }

    private static IServiceProvider CreateDefaultServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddConsole());
        var provider = services.BuildServiceProvider();
        return provider ?? throw new InvalidOperationException("Failed to create service provider");
    }

    private static string DiscoverProjectRoot()
    {
        var current = Directory.GetCurrentDirectory();
        var dir = new DirectoryInfo(current);

        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "src", "Ashlar.CLI", "Ashlar.CLI.csproj")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }

        return current;
    }

    /// <summary>Per-platform test execution outcome collected during a multi-platform run.</summary>
    private class PlatformTestResult
    {
        /// <summary>Platform name under test.</summary>
        public string PlatformName { get; set; } = "";

        /// <summary>When true, the platform run completed without failures.</summary>
        public bool Passed { get; set; }

        /// <summary>Total tests discovered for the platform run.</summary>
        public int TotalTests { get; set; }

        /// <summary>Tests that passed on the platform.</summary>
        public int PassedTests { get; set; }

        /// <summary>Tests that failed on the platform.</summary>
        public int FailedTests { get; set; }

        /// <summary>Wall-clock duration of the platform run.</summary>
        public TimeSpan Duration { get; set; }

        /// <summary>Optional error message when the platform run failed.</summary>
        public string? ErrorMessage { get; set; }
    }
}
