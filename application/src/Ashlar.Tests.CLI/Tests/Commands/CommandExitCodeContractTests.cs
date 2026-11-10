using System.CommandLine;
using System.Diagnostics;
using Ashlar.CLI.Commands;
using Ashlar.Infrastructure.Testing.Docker;
using Ashlar.Tests.Infrastructure.Helpers;
using FluentAssertions;
using Xunit;

namespace Ashlar.Tests.CLI.Tests.Commands;

/// <summary>Refused commands must return their status through the parser, not process-global state.</summary>
[Collection("MeshIntegration")]
public sealed class CommandExitCodeContractTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ashlar-exit-contract-" + Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _scopes = [];

    public CommandExitCodeContractTests()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "Dockerfile"), "FROM scratch\n");
        _scopes.Add(new EgressProcessStateScope(reset: true));
        _scopes.Add(new EnvironmentVariableScope("ASHLAR_STATE_DIR", _directory));
        _scopes.Add(new EnvironmentVariableScope("ASHLAR_MESH_INSTANCES_PATH", Path.Combine(_directory, "instances.json")));
        _scopes.Add(new EnvironmentVariableScope("ASHLAR_ALLOW_MOCK", "1"));
        File.WriteAllText(Path.Combine(_directory, "instances.json"), "[]");
    }

    [Theory]
    [InlineData("ghost-peer:trusted", "Peer 'ghost-peer' not found")]
    [InlineData("invalid", "Invalid --set-trust-tier value")]
    public async Task Mesh_trust_change_refusals_return_failure(string value, string message)
    {
        var result = await Invoke(new MeshCommand(), "mesh", "--set-trust-tier", value);
        result.ExitCode.Should().Be(1);
        result.Output.Should().Contain(message);
    }

    [Fact]
    public async Task Rollback_missing_snapshot_returns_failure()
    {
        var result = await Invoke(new RollbackCommand(), "rollback", "no-such-adaptation", "--store-path", _directory);
        result.ExitCode.Should().Be(1);
        result.Output.Should().Contain("No snapshot found for adaptation");
    }

    [Fact]
    public async Task Adapt_missing_brick_returns_failure()
    {
        var result = await Invoke(new AdaptCommand(), "adapt", "--brick", "no-such-brick", "--store-path", _directory);
        result.ExitCode.Should().Be(1);
        result.Output.Should().Contain("not found");
    }

    [Theory]
    [InlineData("--support-level", "not-a-level", "No valid support levels")]
    [InlineData("--capabilities", "not-a-capability", "No capabilities remain")]
    public async Task Compose_invalid_selection_returns_failure(string option, string value, string message)
    {
        var result = await Invoke(new ComposeCommand(), "compose", "--problem", "test CLI", option, value);
        result.ExitCode.Should().Be(1);
        result.Output.Should().Contain(message);
    }

    [Fact]
    public async Task Ingest_without_files_returns_failure()
    {
        var result = await Invoke(new IngestFailuresCommand(), "ingest-failures", "--trx-path", _directory, "--store-path", _directory);
        result.ExitCode.Should().Be(1);
        result.Output.Should().Contain("No TRX files found");
    }

    [Fact]
    public async Task Observe_without_watch_paths_returns_failure()
    {
        var result = await Invoke(new ObserveCommand(), "observe", "--path", _directory, "--duration", "1s");
        result.ExitCode.Should().Be(1);
        result.Output.Should().Contain("No watch paths exist");
    }

    [Theory]
    [InlineData(false, false, 1)]
    [InlineData(false, true, 0)]
    [InlineData(true, false, 1)]
    [InlineData(true, true, 0)]
    public async Task Analyzer_and_improve_return_analysis_status(bool improve, bool clean, int expectedExitCode)
    {
        var path = Path.Combine(_directory, "Sample.cs");
        await File.WriteAllTextAsync(path, clean
            ? "class Sample { void Run() { try {} catch (System.Exception) { throw; } } }"
            : "class Sample { void Run() { try {} catch (System.Exception) {} } }");
        var command = improve ? (Command)new ImproveCommand() : new AnalyzeBricksCommand();
        var args = improve
            ? new[] { "improve", "--path", path, "--dry-run", "--store-path", _directory }
            : new[] { "bricks", "--path", path };
        var result = await Invoke(command, args);
        result.ExitCode.Should().Be(expectedExitCode, result.Output);
        result.Output.Should().Contain(clean ? "No violations found" : "EmptyCatch");
    }

    [Theory]
    [InlineData("build")]
    [InlineData("run")]
    [InlineData("clean")]
    public async Task Docker_unavailable_returns_failure(string operation)
    {
        var service = new FakeDockerService { Available = false };
        var result = await Invoke(new DockerCommand(_ => service), DockerArgs(operation));
        result.ExitCode.Should().Be(1);
        result.Output.Should().Contain("Docker is not available");
        service.Disposed.Should().BeTrue();
    }

    [Theory]
    [InlineData(true, 0, 0)]
    [InlineData(true, 42, 42)]
    [InlineData(false, 42, 42)]
    [InlineData(false, 0, 1)]
    public async Task Docker_run_propagates_container_status(bool success, int containerExitCode, int expectedExitCode)
    {
        var service = new FakeDockerService { RunResult = new(success, containerExitCode, "output", "failure detail", "container-id", TimeSpan.Zero) };
        var result = await Invoke(new DockerCommand(_ => service), DockerArgs("run"));
        result.ExitCode.Should().Be(expectedExitCode, result.Output);
        result.Output.Should().Contain("exit code: " + containerExitCode);
        service.Disposed.Should().BeTrue();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Docker_build_returns_build_status(bool success)
    {
        var service = new FakeDockerService { BuildResult = new(success, success ? null : "build failed fixture", TimeSpan.Zero) };
        var result = await Invoke(new DockerCommand(_ => service), DockerArgs("build"));
        result.ExitCode.Should().Be(success ? 0 : 1, result.Output);
        if (!success) result.Output.Should().Contain("build failed fixture");
        service.Disposed.Should().BeTrue();
    }

    [Theory]
    [InlineData("build")]
    [InlineData("run")]
    [InlineData("clean")]
    public async Task Docker_caught_errors_return_failure(string operation)
    {
        var service = new FakeDockerService { Throw = true };
        var result = await Invoke(new DockerCommand(_ => service), DockerArgs(operation));
        result.ExitCode.Should().Be(1, result.Output);
        result.Output.Should().Contain("injected Docker failure");
        service.Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task Chat_exit_does_not_overwrite_process_global_status()
    {
        var previousInput = Console.In;
        try
        {
            Console.SetIn(new StringReader("/exit\n"));
            var result = await Invoke(new ChatCommand(() => throw new InvalidOperationException("No turn expected")),
                "chat", "--skip-bootstrap-check", "--demo-local=false");
            result.ExitCode.Should().Be(0, result.Output);
        }
        finally { Console.SetIn(previousInput); }
    }

    [Fact]
    public async Task Parallel_result_does_not_overwrite_process_global_status()
    {
        var result = await Invoke(new TestParallelCommand(), "parallel", "--count", "0");
        result.ExitCode.Should().Be(0, result.Output);
        result.Output.Should().Contain("Results: 0/0 passed");
    }

    [Fact]
    public async Task Real_entry_point_exits_nonzero_for_trust_change_refusal()
    {
        using var process = new Process { StartInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = _directory, RedirectStandardOutput = true,
            RedirectStandardError = true, UseShellExecute = false
        }};
        var testBase = Path.Combine(AppContext.BaseDirectory, "Ashlar.Tests.CLI");
        foreach (var arg in new[] { "exec", "--runtimeconfig", testBase + ".runtimeconfig.json", "--depsfile", testBase + ".deps.json",
            typeof(MeshCommand).Assembly.Location, "mesh", "--set-trust-tier", "ghost-peer:trusted" })
            process.StartInfo.ArgumentList.Add(arg);
        process.Start().Should().BeTrue();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try { await process.WaitForExitAsync(timeout.Token); }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        process.ExitCode.Should().Be(1, await stdout + await stderr);
        (await stderr).Should().Contain("Peer 'ghost-peer' not found");
    }

    private string[] DockerArgs(string operation) => operation switch
    {
        "build" => ["docker", "build", "--dockerfile", Path.Combine(_directory, "Dockerfile"), "--tag", "fixture:test", "--context", _directory],
        "run" => ["docker", "run", "--image", "fixture:test"],
        _ => ["docker", "clean", "--image", "fixture:test"]
    };

    private static async Task<(int ExitCode, string Output)> Invoke(Command command, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var oldOut = Console.Out;
        var oldError = Console.Error;
        var previousExitCode = Environment.ExitCode;
        Console.SetOut(TextWriter.Synchronized(stdout));
        Console.SetError(TextWriter.Synchronized(stderr));
        try
        {
            Environment.ExitCode = 73;
            var root = new RootCommand { command };
            var exitCode = await root.InvokeAsync(args);
            Environment.ExitCode.Should().Be(73, "a command's status belongs to its invocation, not global process state");
            return (exitCode, stdout.ToString() + stderr.ToString());
        }
        finally
        {
            Environment.ExitCode = previousExitCode;
            Console.SetOut(oldOut);
            Console.SetError(oldError);
        }
    }

    public void Dispose()
    {
        foreach (var scope in _scopes.AsEnumerable().Reverse()) scope.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    private sealed class FakeDockerService : IDockerService, IDisposable
    {
        internal bool Available { get; init; } = true;
        internal bool Throw { get; init; }
        internal bool Disposed { get; private set; }
        internal DockerRunResult RunResult { get; init; } = new(true, 0, "", "", "fixture", TimeSpan.Zero);
        internal DockerBuildResult BuildResult { get; init; } = new(true, null, TimeSpan.Zero);
        public Task<bool> IsDockerAvailableAsync(CancellationToken cancellationToken = default) => Task.FromResult(Available);
        public Task<DockerBuildResult> BuildImageAsync(string dockerfilePath, string imageTag, string contextPath,
            Dictionary<string, string>? buildArgs = null, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
            => Throw ? throw new InvalidOperationException("injected Docker failure") : Task.FromResult(BuildResult);
        public Task<DockerRunResult> RunContainerAsync(string imageTag, string[] command, Dictionary<string, string>? environmentVariables = null,
            Dictionary<string, string>? volumeMounts = null, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
            => Throw ? throw new InvalidOperationException("injected Docker failure") : Task.FromResult(RunResult);
        public Task RemoveContainerAsync(string containerId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RemoveImageAsync(string imageTag, CancellationToken cancellationToken = default)
            => Throw ? throw new InvalidOperationException("injected Docker failure") : Task.CompletedTask;
        public void Dispose() => Disposed = true;
    }
}
