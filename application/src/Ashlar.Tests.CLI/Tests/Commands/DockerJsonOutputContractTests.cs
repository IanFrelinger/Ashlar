using System.CommandLine;
using System.Text.Json;
using Ashlar.CLI.Commands;
using Ashlar.Infrastructure.Testing.Docker;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Ashlar.Tests.CLI.Tests.Commands;

/// <summary>Exercise the real Docker logger and renderer without contacting a Docker daemon.</summary>
[Trait("Category", "CLI")]
public sealed class DockerJsonOutputContractTests
{
    [Theory]
    [InlineData("--format-json", false)]
    [InlineData("--format-json=true", false)]
    [InlineData("--format-json:true", false)]
    [InlineData("--format-json=true", true)]
    public async Task Docker_json_alias_forms_emit_one_document_and_actual_logs_on_stderr(string flag, bool responseFile)
    {
        var directory = Path.Combine(Path.GetTempPath(), "ashlar-docker-json-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int exitCode;
        Console.SetOut(TextWriter.Synchronized(stdout));
        Console.SetError(TextWriter.Synchronized(stderr));
        try
        {
            var root = new RootCommand();
            root.AddGlobalOption(new Option<bool>("--format-json"));
            root.AddGlobalOption(new Option<bool>("--verbose"));
            root.AddCommand(new DockerCommand(logger => new LoggingUnavailableDockerService(logger)));
            CommandExecutionSupport.ApplyJsonOutputContract(root);
            var dockerfile = Path.Combine(directory, "Dockerfile");
            await File.WriteAllTextAsync(dockerfile, "FROM scratch\n");
            string[] args = ["docker", "build", "--dockerfile", dockerfile, "--tag", "fixture:test", flag, "--verbose"];
            if (responseFile)
            {
                var response = Path.Combine(directory, "args.rsp");
                await File.WriteAllLinesAsync(response, args.Select(arg => "\"" + arg + "\""));
                args = ["@" + response];
            }

            // Do not run assertions under redirected stdout: FluentAssertions can print a
            // first-use diagnostic when this method runs alone in a mutation baseline.
            exitCode = await root.InvokeAsync(args);
        }
        finally
        {
            Console.SetOut(ConsoleCapture.Out);
            Console.SetError(ConsoleCapture.Error);
            Directory.Delete(directory, recursive: true);
        }

        exitCode.Should().Be(1);
        // DockerCommand disposes its service provider before returning, draining the real console logger.
        using var document = JsonDocument.Parse(stdout.ToString());
        document.RootElement.GetProperty("error").GetString().Should().Contain("Docker is not available");
        stdout.ToString().Should().NotContain("docker-json-log-fixture");
        stderr.ToString().Should().Contain("docker-json-log-fixture");
    }

    private sealed class LoggingUnavailableDockerService(ILogger<DockerService> logger) : IDockerService
    {
        public Task<bool> IsDockerAvailableAsync(CancellationToken cancellationToken = default)
        {
            logger.LogInformation("docker-json-log-fixture");
            return Task.FromResult(false);
        }
        public Task<DockerBuildResult> BuildImageAsync(string dockerfilePath, string imageTag, string contextPath,
            Dictionary<string, string>? buildArgs = null, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Unavailable Docker must not build");
        public Task<DockerRunResult> RunContainerAsync(string imageTag, string[] command,
            Dictionary<string, string>? environmentVariables = null, Dictionary<string, string>? volumeMounts = null,
            IProgress<string>? progress = null, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Unavailable Docker must not run");
        public Task RemoveContainerAsync(string containerId, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Unavailable Docker must not remove containers");
        public Task RemoveImageAsync(string imageTag, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Unavailable Docker must not remove images");
    }
}
