using System.Diagnostics;
using FluentAssertions;
using Xunit;

namespace Ashlar.Tests.Kernel.Storage;

public sealed class FileLockSafetyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ashlar-lock-tests-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("gate")]
    [InlineData("ledger")]
    public async Task Disabled_runtime_locking_fails_closed_in_a_fresh_process(string store)
    {
        // This runtime switch disables Unix locking. Windows continues to enforce sharing.
        var result = await RunProbe(store, "1", "write");
        if (OperatingSystem.IsWindows())
        {
            result.ExitCode.Should().Be(0, result.Output);
            result.Output.Should().Contain("written");
            Records().Should().ContainSingle("the Unix runtime switch does not disable Windows sharing");
            return;
        }
        result.ExitCode.Should().Be(23, result.Output);
        result.Output.Should().Contain("DOTNET_SYSTEM_IO_DISABLEFILELOCKING").And.Contain("cached failure");
        Records().Should().BeEmpty("neither store may persist a record after a failed self-test");
        Directory.GetFiles(_root, ".lock", SearchOption.AllDirectories).Should().BeEmpty(
            "the safety check must fail before attempting the real store lock");
    }

    [Theory]
    [InlineData("gate", "0")]
    [InlineData("ledger", "0")]
    [InlineData("gate", "1")]
    [InlineData("ledger", "1")]
    public async Task Construction_verifies_runtime_locking_before_creating_store_directories(string store, string disabled)
    {
        var result = await RunProbe(store, disabled, "construct");
        if (disabled == "0" || OperatingSystem.IsWindows())
        {
            result.ExitCode.Should().Be(0, result.Output);
            result.Output.Should().Contain("constructed");
        }
        else
        {
            result.ExitCode.Should().Be(23, result.Output);
            result.Output.Should().Contain("DOTNET_SYSTEM_IO_DISABLEFILELOCKING").And.Contain("cached failure");
            Directory.GetDirectories(_root).Should().BeEmpty(
                "both constructors must reject disabled locking before any state directory is created");
        }
        Records().Should().BeEmpty("construction alone never persists a record");
    }

    [Theory]
    [InlineData("gate")]
    [InlineData("ledger")]
    public async Task An_unusable_probe_directory_fails_closed_during_construction(string store)
    {
        var result = await RunProbe(store, "0", "construct", unavailableTemp: true);
        result.ExitCode.Should().Be(23, result.Output);
        result.Output.Should().Contain("Could not verify").And.Contain("cached failure");
        Directory.GetDirectories(_root).Should().BeEmpty(
            "failure to create the probe must not create either store's state directory");
        Records().Should().BeEmpty();
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    public async Task Concurrent_first_construction_shares_the_runtime_verification(string disabled)
    {
        var result = await RunProbe("gate", disabled, "concurrent-construct");
        if (disabled == "0" || OperatingSystem.IsWindows())
        {
            result.ExitCode.Should().Be(0, result.Output);
            result.Output.Should().Contain("16 concurrent constructors succeeded");
        }
        else
        {
            result.ExitCode.Should().Be(23, result.Output);
            result.Output.Should().Contain("16 concurrent constructors shared one cached failure")
                .And.Contain("DOTNET_SYSTEM_IO_DISABLEFILELOCKING");
            Directory.GetDirectories(_root).Should().BeEmpty();
        }
        Records().Should().BeEmpty("concurrent construction alone must not persist records");
    }

    [Theory]
    [InlineData("gate", "gates")]
    [InlineData("ledger", "ledger")]
    public async Task Enabled_runtime_locking_allows_writes_and_keeps_the_lock_inode(string store, string directory)
    {
        var result = await RunProbe(store, "0", "write");
        result.ExitCode.Should().Be(0, result.Output);
        result.Output.Should().Contain("written");
        Records().Should().ContainSingle();
        File.Exists(Path.Combine(_root, directory, ".lock")).Should().BeTrue(
            "unlinking the shared lock on release could let later writers lock a different inode");
    }

    [Theory]
    [InlineData("gate", "gates")]
    [InlineData("ledger", "ledger")]
    public async Task Another_process_cannot_write_while_the_store_lock_is_held(string store, string directory)
    {
        Directory.CreateDirectory(Path.Combine(_root, directory));
        using (var held = new FileStream(Path.Combine(_root, directory, ".lock"),
                   FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            var blocked = await RunProbe(store, "0", "blocked");
            blocked.ExitCode.Should().Be(24, blocked.Output);
            blocked.Output.Should().Contain("contended");
            Records().Should().BeEmpty();
        }
        var released = await RunProbe(store, "0", "write");
        released.ExitCode.Should().Be(0, released.Output);
        Records().Should().ContainSingle("closing the handle releases the lock without deleting its file");
    }

    private string[] Records() => Directory.GetFiles(_root, "*.json", SearchOption.AllDirectories);

    private async Task<(int ExitCode, string Output)> RunProbe(string store, string disabled, string mode, bool unavailableTemp = false)
    {
        Directory.CreateDirectory(_root);
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        var probe = Path.Combine(AppContext.BaseDirectory, "InputSafetyProbe", "InputSafetyProbe.dll");
        File.Exists(probe).Should().BeTrue("the test build must include the child-process probe");
        start.ArgumentList.Add(probe);
        start.ArgumentList.Add(store);
        start.ArgumentList.Add(_root);
        start.ArgumentList.Add(mode);
        start.Environment["DOTNET_SYSTEM_IO_DISABLEFILELOCKING"] = disabled;
        if (unavailableTemp)
        {
            var missing = Path.Combine(_root, "nonexistent-temp");
            foreach (var variable in new[] { "TMPDIR", "TMP", "TEMP", "SYSTEMTEMP" })
                start.Environment[variable] = missing;
        }
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start lock probe.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new TimeoutException("File lock probe did not exit within 30 seconds.");
        }
        return (process.ExitCode, await stdout + await stderr);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
