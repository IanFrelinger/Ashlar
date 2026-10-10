using Ashlar.Abstractions.Security.Egress;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ashlar.Core.Application.Observation.Models;
using Ashlar.Core.Application.Observation.Ports;
using Ashlar.Core.Application.Paths;
using Ashlar.Infrastructure.Observation;

namespace Ashlar.CLI.Commands;

/// <summary>
/// Runs a dogfood block via dotnet test with the given filter.
/// </summary>
internal static class DogfoodTestCommand
{
    /// <summary>Creates a new Create instance.</summary>
    public static Command Create(string name, string description, string filter, Option<bool> jsonOpt)
    {
        var cmd = new Command(name, description);
        cmd.AddOption(jsonOpt);
        var verboseOpt = new Option<bool>("--verbose", () => false, "Stream test output to console");
        cmd.AddOption(verboseOpt);
        cmd.SetHandler(async (InvocationContext ctx) =>
        {
            var json = ctx.ParseResult.GetValueForOption(jsonOpt);
            var verbose = ctx.ParseResult.GetValueForOption(verboseOpt);
            Environment.Exit(await ExecuteAsync(name, filter, json, verbose));
        });
        return cmd;
    }

    /// <summary>Executes the command handler and returns a process exit code.</summary>
    public static async Task<int> ExecuteAsync(string blockName, string filter, bool json, bool verbose = false)
    {
        var repoRoot = RepoPathResolver.FindRepoRoot();
        var slnPath = Path.Combine(repoRoot, "Ashlar.sln");
        var testProj = Path.Combine(repoRoot, "src", "Ashlar.Tests.Infrastructure", "Ashlar.Tests.Infrastructure.csproj");

        if (!File.Exists(slnPath))
        {
            if (json)
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { block = blockName, passed = false, reason = "Not in Ashlar repo (Ashlar.sln not found)" }));
            else
                Console.Error.WriteLine($"{blockName} dogfood gate FAILED: Not in Ashlar repo. Run from Ashlar repository root.");
            return 1;
        }

        if (!File.Exists(testProj))
        {
            if (json)
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { block = blockName, passed = false, reason = "Ashlar.Tests.Infrastructure.csproj not found" }));
            else
                Console.Error.WriteLine($"{blockName} dogfood gate FAILED: Test project not found.");
            return 1;
        }

        var buildResult = await RunDotNetAsync(repoRoot, "build", verbose, testProj, "-v", "minimal");
        if (buildResult != 0)
        {
            if (json)
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { block = blockName, passed = false, reason = "Build failed" }));
            else
                Console.Error.WriteLine($"{blockName} dogfood gate FAILED: Build failed.");
            return 1;
        }

        var verbosity = verbose ? "normal" : "minimal";
        var testResult = await RunDotNetAsync(repoRoot, "test", verbose, testProj, "--filter", $"FullyQualifiedName~{filter}", "--no-build", "-v", verbosity);
        var passed = testResult == 0;

        if (json)
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { block = blockName, passed, reason = passed ? "Tests passed" : "Tests failed" }));
        else if (passed)
            Console.WriteLine($"{blockName} dogfood gate PASSED.");
        else
            Console.Error.WriteLine($"{blockName} dogfood gate FAILED.");

        return testResult;
    }

    private static async Task<int> RunDotNetAsync(string workingDir, string command, bool streamOutput, params string[] args)
    {
        EgressGuard.ProcessDefault.Evaluate(new EgressRequest(EgressFamilies.Process, "EG-PROC-09", "process:dotnet")).ThrowIfRefused();
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = workingDir,
            UseShellExecute = false,
        };
        if (!streamOutput)
        {
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
        }
        psi.ArgumentList.Add(command);
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi);
        if (proc == null) return 1;
        // When streamOutput: no redirect, child inherits console and output streams directly
        await proc.WaitForExitAsync();
        return proc.ExitCode;
    }
}
