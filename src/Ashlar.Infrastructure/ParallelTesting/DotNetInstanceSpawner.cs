using System.Diagnostics;
using Ashlar.Core.Application.ParallelTesting.Models;
using Ashlar.Core.Application.ParallelTesting.Ports;

namespace Ashlar.Infrastructure.ParallelTesting;

/// <summary>
/// Spawns N dotnet test processes with different parameter sets.
/// </summary>
internal sealed class DotNetInstanceSpawner : IInstanceSpawner
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<TestInstance>> SpawnAsync(int count, IReadOnlyList<ParameterSet> paramSets, string solutionOrProjectPath, CancellationToken cancellationToken = default)
    {
        var results = new List<TestInstance>();
        var sets = paramSets.Count > 0 ? paramSets : new[] { new ParameterSet() };
        var toRun = Math.Min(count, sets.Count);
        for (var i = 0; i < toRun; i++)
        {
            var paramSet = sets[i];
            var filter = paramSet.Overrides.TryGetValue("filter", out var f) ? f : null;
            var sw = Stopwatch.StartNew();
            var (passed, output) = await RunDotNetTestAsync(solutionOrProjectPath, filter, cancellationToken).ConfigureAwait(false);
            sw.Stop();
            results.Add(new TestInstance
            {
                InstanceId = $"instance-{i}",
                ParameterSet = paramSet,
                Passed = passed,
                Output = output,
                Duration = sw.Elapsed,
            });
        }
        return results;
    }

    private static async Task<(bool Passed, string Output)> RunDotNetTestAsync(string path, string? filter, CancellationToken ct)
    {
        if (!File.Exists(path))
            return (false, $"Path not found: {path}");

        var args = $"test \"{path}\" --no-build --verbosity minimal";
        if (!string.IsNullOrWhiteSpace(filter))
            args += $" --filter \"{filter}\"";

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = args,
            WorkingDirectory = Path.GetDirectoryName(path) ?? Environment.CurrentDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        try
        {
            using var proc = Process.Start(psi);
            if (proc == null)
                return (false, "Failed to start dotnet test");
            var stdout = await proc.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            var stderr = await proc.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            var output = stdout + stderr;
            return (proc.ExitCode == 0, output);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
