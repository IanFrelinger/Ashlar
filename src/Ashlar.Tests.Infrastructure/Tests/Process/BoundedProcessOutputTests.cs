using System.Diagnostics;
using System.Text;
using Ashlar.Core.Application.Execution.Ports;
using Ashlar.Infrastructure.HostProcess;
using Ashlar.Infrastructure.Scaling;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.ProcessExecution;

/// <summary>Real child processes exercise byte accounting, pipe lifecycle and termination.</summary>
public sealed class BoundedProcessOutputTests
{
    [Fact]
    [Trait("Category", "ProdStyle")]
    public async Task Exact_combined_byte_limit_preserves_utf8_across_pipe_reads()
    {
        var start = Script(
            "printf '%4095s' '' | tr ' ' a; printf '\\342'; sleep 0.05; printf '\\202\\254'; printf err >&2",
            "$out = [Console]::OpenStandardOutput(); "
            + "$prefix = [Text.Encoding]::UTF8.GetBytes(('a' * 4095)); $out.Write($prefix,0,$prefix.Length); "
            + "$out.WriteByte(226); $out.Flush(); Start-Sleep -Milliseconds 50; "
            + "$out.WriteByte(130); $out.WriteByte(172); $out.Flush(); "
            + "$err = [Console]::OpenStandardError(); $suffix = [Text.Encoding]::UTF8.GetBytes('err'); "
            + "$err.Write($suffix,0,$suffix.Length); $err.Flush();");

        var result = await TimedProcess.RunBoundedAsync(start, TimeSpan.FromSeconds(10), 4101);

        Assert.Equal(0, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.Equal(new string('a', 4095) + "€", result.StdOut);
        Assert.Equal("err", result.StdErr);
    }

    [Theory]
    [InlineData("stdout")]
    [InlineData("stderr")]
    [InlineData("combined")]
    public async Task Exceeding_raw_byte_budget_is_failure_even_when_process_exits(string stream)
    {
        var start = stream switch
        {
            "stdout" => Script("printf 123456789", "[Console]::Write('123456789')"),
            "stderr" => Script("printf 123456789 >&2", "[Console]::Error.Write('123456789')"),
            _ => Script("printf 12345; printf 6789 >&2", "[Console]::Write('12345'); [Console]::Error.Write('6789')"),
        };

        var failure = await Assert.ThrowsAsync<ProcessOutputLimitExceededException>(() =>
            TimedProcess.RunBoundedAsync(start, TimeSpan.FromSeconds(10), 8));

        Assert.Equal(8, failure.MaxOutputBytes);
    }

    [Fact]
    public async Task Output_limit_counts_bytes_instead_of_decoded_characters()
    {
        var start = Script("printf '\\342\\202\\254'",
            "$out = [Console]::OpenStandardOutput(); $bytes = [byte[]]@(226,130,172); $out.Write($bytes,0,3); $out.Flush()");

        var failure = await Assert.ThrowsAsync<ProcessOutputLimitExceededException>(() =>
            TimedProcess.RunBoundedAsync(start, TimeSpan.FromSeconds(10), 2));

        Assert.Equal(2, failure.MaxOutputBytes); // One Unicode character is three UTF-8 bytes.
    }

    [Fact]
    public async Task Overflow_kills_the_producer_before_the_wall_clock_deadline()
    {
        var pidPath = Path.Combine(Path.GetTempPath(), "ashlar-output-pid-" + Guid.NewGuid().ToString("N"));
        var start = Script(
            "printf '%s' \"$$\" > \"$ASHLAR_BOUNDED_TEST_PID\"; "
            + "while :; do printf 'abcdefghijklmnopqrstuvwxyz0123456789abcdefghijklmnopqrstuvwxyz0123456789'; done",
            "[IO.File]::WriteAllText($env:ASHLAR_BOUNDED_TEST_PID, [string]$PID); "
            + "$out = [Console]::OpenStandardOutput(); $bytes = [Text.Encoding]::UTF8.GetBytes(('x' * 4096)); "
            + "while ($true) { $out.Write($bytes,0,$bytes.Length); $out.Flush() }");
        start.Environment["ASHLAR_BOUNDED_TEST_PID"] = pidPath;
        try
        {
            var elapsed = Stopwatch.StartNew();
            await Assert.ThrowsAsync<ProcessOutputLimitExceededException>(() =>
                TimedProcess.RunBoundedAsync(start, TimeSpan.FromSeconds(10), 1024));

            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(8), "Overflow must end execution before its ten-second deadline.");
            var pid = int.Parse(await File.ReadAllTextAsync(pidPath), System.Globalization.CultureInfo.InvariantCulture);
            Assert.False(IsRunning(pid), "The output-producing process survived overflow.");
        }
        finally
        {
            KillRecordedProcess(pidPath);
            File.Delete(pidPath);
        }
    }

    [Fact]
    public async Task Bounded_process_runner_preserves_caller_cancellation()
    {
        IBoundedOutputProcessCommandRunner runner = new ProcessCommandRunner();
        var start = Script("sleep 30", "Start-Sleep -Seconds 30");
        using var caller = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        var elapsed = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runner.RunBoundedAsync(start.FileName, start.ArgumentList.ToArray(), 64, caller.Token));

        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(8), "Cancellation must kill the waiting process promptly.");
    }

    [Fact]
    public async Task Bounded_capture_preserves_timeout_result_without_waiting_for_the_child()
    {
        var elapsed = Stopwatch.StartNew();
        var result = await TimedProcess.RunBoundedAsync(
            Script("exec sleep 30", "Start-Sleep -Seconds 30"), TimeSpan.FromMilliseconds(400), 64);

        Assert.True(result.TimedOut);
        Assert.Equal(TimedProcess.TimeoutExitCode, result.ExitCode);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(8), "Timeout must kill the waiting process promptly.");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(16 * 1024 * 1024 + 1)]
    public async Task Invalid_output_limits_do_not_start_a_process(int maxOutputBytes)
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => TimedProcess.RunBoundedAsync(
            new ProcessStartInfo("ashlar-no-such-executable"), TimeSpan.FromSeconds(1), maxOutputBytes));
    }

    [PosixOnlyFact]
    public async Task Inherited_open_pipe_cannot_turn_incomplete_capture_into_success()
    {
        // POSIX descendants can outlive their shell while retaining its redirected pipe.
        // Windows has different handle inheritance; do not claim this scenario ran there.
        var pidPath = Path.Combine(Path.GetTempPath(), "ashlar-pipe-pid-" + Guid.NewGuid().ToString("N"));
        var start = Script("sleep 30 & printf '%s' \"$!\" > \"$ASHLAR_BOUNDED_TEST_PID\"; printf done", "");
        start.Environment["ASHLAR_BOUNDED_TEST_PID"] = pidPath;
        try
        {
            var elapsed = Stopwatch.StartNew();
            var failure = await Assert.ThrowsAsync<IOException>(() =>
                TimedProcess.RunBoundedAsync(start, TimeSpan.FromSeconds(10), 64));

            Assert.Contains("pipes did not close", failure.Message, StringComparison.Ordinal);
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(8), "Inherited pipe must not hold the caller until its child exits.");
        }
        finally
        {
            KillRecordedProcess(pidPath);
            File.Delete(pidPath);
        }
    }

    private static ProcessStartInfo Script(string unix, string windows)
    {
        var start = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "powershell.exe" : "/bin/sh",
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        if (OperatingSystem.IsWindows())
        {
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-NonInteractive");
            start.ArgumentList.Add("-Command");
            start.ArgumentList.Add(windows);
        }
        else
        {
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add(unix);
        }
        return start;
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static void KillRecordedProcess(string path)
    {
        if (!File.Exists(path))
            return;
        if (!int.TryParse(File.ReadAllText(path), out var pid))
            return;
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (ArgumentException)
        {
            // The bounded runner already reaped the process.
        }
        catch (InvalidOperationException)
        {
            // Exit raced the cleanup check.
        }
    }

    private sealed class PosixOnlyFactAttribute : FactAttribute
    {
        public PosixOnlyFactAttribute()
        {
            if (OperatingSystem.IsWindows())
                Skip = "This scenario requires POSIX shell descriptor inheritance.";
        }
    }
}
