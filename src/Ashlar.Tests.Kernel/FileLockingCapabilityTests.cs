using System.Diagnostics;
using FluentAssertions;
using Xunit;

namespace Ashlar.Tests.Kernel;

public sealed class FileLockingCapabilityTests
{
    [Theory]
    [InlineData("gate")]
    [InlineData("ledger")]
    public async Task Normal_runtime_constructs_both_stores(string store)
    {
        var result = await Probe(store, disabled: false);
        result.Code.Should().Be(0, result.Output);
        result.Output.Should().Contain("STORE_CONSTRUCTED");
    }

    [Theory]
    [InlineData("gate")]
    [InlineData("ledger")]
    public async Task Disabled_file_locking_refuses_store_construction(string store)
    {
        var result = await Probe(store, disabled: true);
        // Windows share denial is enforced by its kernel, independent of the Unix runtime knob.
        if (OperatingSystem.IsWindows())
        {
            result.Code.Should().Be(0, result.Output);
        }
        else
        {
            result.Code.Should().Be(42, result.Output);
            result.Output.Should().Contain("DOTNET_SYSTEM_IO_DISABLEFILELOCKING");
        }
    }

    private static async Task<(int Code, string Output)> Probe(string store, bool disabled)
    {
        var root = Path.Combine(Path.GetTempPath(), "ashlar-lock-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var assembly = typeof(FileLockingCapabilityTests).Assembly.Location;
            var info = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            info.ArgumentList.Add("exec");
            info.ArgumentList.Add("--runtimeconfig");
            info.ArgumentList.Add(Path.ChangeExtension(assembly, ".runtimeconfig.json"));
            info.ArgumentList.Add("--depsfile");
            info.ArgumentList.Add(Path.ChangeExtension(assembly, ".deps.json"));
            info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Ashlar.FileLockingProbe.dll"));
            info.ArgumentList.Add(store);
            info.ArgumentList.Add(root);
            info.Environment.Remove("DOTNET_SYSTEM_IO_DISABLEFILELOCKING");
            if (disabled) info.Environment["DOTNET_SYSTEM_IO_DISABLEFILELOCKING"] = "1";
            info.Environment["ASHLAR_KEY_DIR"] = Directory.CreateDirectory(Path.Combine(root, "keys")).FullName;
            using var process = Process.Start(info)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                throw new TimeoutException("File-lock capability child did not exit within 30 seconds.");
            }
            return (process.ExitCode, await stdout + await stderr);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
