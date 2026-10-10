using System.CommandLine;
using Ashlar.CLI.Commands;
using Ashlar.Tests.Infrastructure.Helpers;
using FluentAssertions;
using Xunit;

namespace Ashlar.Tests.CLI.Tests.Commands;

/// <summary>
/// #484: void System.CommandLine handlers that set <c>Environment.ExitCode</c> exit 0, because
/// <c>Main</c> returns <c>InvokeAsync</c> and only <c>ctx.ExitCode</c> is honoured. These drive the
/// refusal paths through <c>InvokeAsync</c> and assert the code that comes back.
/// </summary>
public sealed class CliHandlerExitCodeTests
{
    private static async Task<(int rc, string stdout, string stderr)> InvokeAsync(Command command, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        Console.SetOut(stdout);
        Console.SetError(stderr);
        try
        {
            var root = new RootCommand();
            root.AddCommand(command);
            var rc = await root.InvokeAsync(args).ConfigureAwait(false);
            return (rc, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(ConsoleCapture.Out);
            Console.SetError(ConsoleCapture.Error);
        }
    }

    private static string TempDir(string prefix)
    {
        var dir = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void DeleteDir(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // LiteDB can keep the store file open after the command returns.
        }
    }

    [Fact]
    public async Task Rollback_with_no_target_exits_non_zero()
    {
        var store = TempDir("ashlar-rollback-usage-");
        try
        {
            var (rc, _, stderr) = await InvokeAsync(new RollbackCommand(), "rollback", "--store-path", store);
            rc.Should().NotBe(0, "a usage error must not look like a successful restore");
            stderr.Should().Contain("Specify adaptation-id");
            stderr.Should().NotContain("Required argument missing");
        }
        finally
        {
            DeleteDir(store);
        }
    }

    [Fact]
    public async Task Rollback_of_a_missing_adaptation_exits_non_zero()
    {
        var store = TempDir("ashlar-rollback-missing-");
        try
        {
            var (rc, _, stderr) = await InvokeAsync(
                new RollbackCommand(), "rollback", "no-such-adaptation-id", "--store-path", store);
            rc.Should().NotBe(0, "a restore that never happened must not exit 0");
            stderr.Should().Contain("No snapshot found");
        }
        finally
        {
            DeleteDir(store);
        }
    }

    [Fact]
    public async Task Adapt_of_an_unknown_brick_exits_non_zero()
    {
        var store = TempDir("ashlar-adapt-missing-");
        try
        {
            var (rc, _, _) = await InvokeAsync(
                new AdaptCommand(), "adapt", "--brick", "no-such-brick", "--store-path", store);
            rc.Should().NotBe(0);
        }
        finally
        {
            DeleteDir(store);
        }
    }

    [Fact]
    public async Task Analyze_bricks_with_no_violations_exits_zero()
    {
        var dir = TempDir("ashlar-analyze-clean-");
        var file = Path.Combine(dir, "Clean.cs");
        await File.WriteAllTextAsync(file, "class Clean { void M() { int x = 1; } }\n");
        try
        {
            var (rc, stdout, _) = await InvokeAsync(new AnalyzeBricksCommand(), "bricks", "--path", file);
            rc.Should().Be(0);
            stdout.Should().Contain("No violations found");
        }
        finally
        {
            DeleteDir(dir);
        }
    }

    [Fact]
    public async Task Analyze_bricks_with_an_empty_catch_exits_non_zero()
    {
        var dir = TempDir("ashlar-analyze-catch-");
        var file = Path.Combine(dir, "Swallow.cs");
        await File.WriteAllTextAsync(file, "class Swallow { void M() { try { } catch { } } }\n");
        try
        {
            var (rc, stdout, _) = await InvokeAsync(new AnalyzeBricksCommand(), "bricks", "--path", file);
            rc.Should().NotBe(0, "violations must fail the command, not only print");
            stdout.Should().Contain("EmptyCatch");
        }
        finally
        {
            DeleteDir(dir);
        }
    }

    [Fact]
    public async Task Compose_with_an_unknown_support_level_exits_non_zero()
    {
        var (rc, _, stderr) = await InvokeAsync(
            new ComposeCommand(), "compose", "--problem", "probe", "--support-level", "not-a-level");
        rc.Should().NotBe(0);
        stderr.Should().Contain("No valid support levels");
    }

    [Fact]
    public async Task Compose_with_no_remaining_capabilities_exits_non_zero()
    {
        var (rc, _, stderr) = await InvokeAsync(
            new ComposeCommand(),
            "compose", "--problem", "probe", "--capabilities", "not-a-capability", "--support-level", "stable");
        rc.Should().NotBe(0);
        stderr.Should().Contain("No capabilities remain");
    }

    [Fact]
    public async Task Improve_with_no_violations_exits_zero()
    {
        var dir = TempDir("ashlar-improve-clean-");
        var file = Path.Combine(dir, "Clean.cs");
        await File.WriteAllTextAsync(file, "class Clean { void M() { int x = 1; } }\n");
        try
        {
            var (rc, stdout, _) = await InvokeAsync(
                new ImproveCommand(), "improve", "--path", file, "--dry-run", "--yes", "--store-path", dir);
            rc.Should().Be(0);
            stdout.Should().Contain("No violations found");
        }
        finally
        {
            DeleteDir(dir);
        }
    }

    [Fact]
    public async Task Improve_dry_run_with_unmapped_violations_exits_non_zero()
    {
        var dir = TempDir("ashlar-improve-unmap-");
        var file = Path.Combine(dir, "NotABrick.cs");
        await File.WriteAllTextAsync(file, "class NotABrick { void M() { try { } catch { } } }\n");
        try
        {
            var (rc, stdout, _) = await InvokeAsync(
                new ImproveCommand(), "improve", "--path", file, "--dry-run", "--yes", "--store-path", dir);
            rc.Should().NotBe(0);
            stdout.Should().Contain("dry-run: skip source fixes");
        }
        finally
        {
            DeleteDir(dir);
        }
    }

    [Fact]
    public async Task Improve_dry_run_with_a_known_brick_violation_exits_non_zero()
    {
        var dir = TempDir("ashlar-improve-brick-");
        var file = Path.Combine(dir, "ObservationContextBrick.cs");
        await File.WriteAllTextAsync(file, "class ObservationContextBrick { void M() { try { } catch { } } }\n");
        try
        {
            var (rc, stdout, _) = await InvokeAsync(
                new ImproveCommand(), "improve", "--path", file, "--dry-run", "--yes", "--store-path", dir);
            rc.Should().NotBe(0);
            stdout.Should().Contain("dry-run: skip brick adaptation");
        }
        finally
        {
            DeleteDir(dir);
        }
    }

    [Fact]
    public async Task Ingest_failures_with_no_trx_files_exits_non_zero()
    {
        var dir = TempDir("ashlar-ingest-empty-");
        try
        {
            var (rc, stdout, _) = await InvokeAsync(
                new IngestFailuresCommand(), "ingest-failures", "--trx-path", dir, "--store-path", dir);
            rc.Should().NotBe(0);
            stdout.Should().Contain("No TRX files found");
        }
        finally
        {
            DeleteDir(dir);
        }
    }

    [Fact]
    public async Task Observe_with_no_watch_paths_exits_non_zero()
    {
        var dir = TempDir("ashlar-observe-empty-");
        using var _ = EnvironmentVariableScope.Unset("ASHLAR_STATE_DIR");
        try
        {
            var (rc, _, _) = await InvokeAsync(
                new ObserveCommand(), "observe", "--path", dir, "--duration", "1s");
            rc.Should().NotBe(0, "nothing to watch must not look like a completed observation");
        }
        finally
        {
            DeleteDir(dir);
        }
    }

    [Fact]
    public async Task Docker_build_of_an_unbuildable_file_exits_non_zero()
    {
        var dir = TempDir("ashlar-docker-build-");
        var dockerfile = Path.Combine(dir, "Dockerfile");
        await File.WriteAllTextAsync(dockerfile, "THIS IS NOT A DOCKERFILE\n");
        try
        {
            var (rc, _, _) = await InvokeAsync(
                new DockerCommand(),
                "docker", "build", "--dockerfile", dockerfile, "--tag", "ashlar-exit-code-test:none", "--context", dir);
            rc.Should().NotBe(0);
        }
        finally
        {
            DeleteDir(dir);
        }
    }

    [Fact]
    public async Task Docker_run_of_a_missing_image_exits_non_zero()
    {
        var (rc, _, _) = await InvokeAsync(
            new DockerCommand(),
            "docker", "run", "--image", "ashlar-exit-code-test:no-such-image");
        rc.Should().NotBe(0, "a container that did not run must not exit 0");
    }

    [Fact]
    public async Task Docker_ps_without_a_daemon_exits_non_zero()
    {
        if (File.Exists("/var/run/docker.sock"))
            return;

        var (rc, _, _) = await InvokeAsync(new DockerCommand(), "docker", "ps");
        rc.Should().NotBe(0);
    }
}
