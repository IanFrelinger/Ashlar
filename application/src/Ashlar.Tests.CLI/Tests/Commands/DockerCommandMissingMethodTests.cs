using System.CommandLine;
using Ashlar.CLI.Commands;
using FluentAssertions;
using Xunit;

namespace Ashlar.Tests.CLI.Tests.Commands;

/// <summary>
/// Issue #487: <c>ashlar docker build</c>, <c>run</c> and <c>clean</c> constructed a Docker client
/// before their own try/catch, so a <see cref="MissingMethodException"/> from an assembly mismatch
/// on <c>DockerClientConfiguration.CreateClient</c> escaped as an unhandled crash. These tests invoke
/// those three handlers far enough to pass that call. A daemon is not required: the section header is
/// written only after the client exists, and an unreachable daemon is then reported as unavailable.
/// Exit-code propagation is issue #484 and is not asserted here.
/// </summary>
public sealed class DockerCommandMissingMethodTests : IDisposable
{
    private readonly string _dir;
    private readonly int _previousExitCode;

    public DockerCommandMissingMethodTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ashlar-docker-487-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "Dockerfile"), "FROM scratch\n");
        _previousExitCode = Environment.ExitCode;
    }

    public void Dispose()
    {
        Environment.ExitCode = _previousExitCode;
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // best effort
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task Build_reaches_the_client_without_MissingMethodException()
    {
        await AssertPastCreateClient(
            "Building Docker Image",
            "docker", "build",
            "--dockerfile", Path.Combine(_dir, "Dockerfile"),
            "--tag", "ashlar-missing-method:test",
            "--context", _dir).ConfigureAwait(false);
    }

    [Fact(Timeout = 60_000)]
    public async Task Run_reaches_the_client_without_MissingMethodException()
    {
        await AssertPastCreateClient(
            "Running Docker Container",
            "docker", "run",
            "--image", "ashlar-missing-method:test",
            "--command", "echo", "hi").ConfigureAwait(false);
    }

    [Fact(Timeout = 60_000)]
    public async Task Clean_reaches_the_client_without_MissingMethodException()
    {
        await AssertPastCreateClient(
            "Cleaning Docker Resources",
            "docker", "clean").ConfigureAwait(false);
    }

    private static async Task AssertPastCreateClient(string header, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        Console.SetOut(stdout);
        Console.SetError(stderr);
        Exception? thrown = null;
        try
        {
            var root = new RootCommand();
            root.AddCommand(new DockerCommand());
            await root.InvokeAsync(args).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            thrown = ex;
        }
        finally
        {
            Console.SetOut(ConsoleCapture.Out);
            Console.SetError(ConsoleCapture.Error);
        }

        var observed = stdout.ToString() + stderr.ToString() + (thrown?.ToString() ?? string.Empty);
        observed.Should().NotContain("MissingMethodException");
        observed.Should().NotContain("Method not found:");
        stdout.ToString().Should().Contain(header, "the header is printed only after CreateClient returns");
        thrown.Should().BeNull("the handler must not escape once the Docker client has been constructed");
    }
}
