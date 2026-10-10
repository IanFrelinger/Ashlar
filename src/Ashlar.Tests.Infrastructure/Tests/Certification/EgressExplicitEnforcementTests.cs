using System.Reflection;
using System.Xml.Linq;
using Ashlar.Abstractions.Security.Egress;
using Ashlar.Core.Application.ParallelTesting.Models;
using Ashlar.Infrastructure.Analysis.BrickAnalyzer;
using Ashlar.Infrastructure.ParallelTesting;
using Ashlar.Tests.Infrastructure.Helpers;
using Ashlar.Tools.Dev;
using Ashlar.Infrastructure.Scaling;
using Ashlar.Infrastructure.Execution.Sandbox;
using Ashlar.Core.Application.Execution.Ports;
using Ashlar.Infrastructure.HostProcess;
using System.Diagnostics;
using FluentAssertions;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>Working-tree process calls must refuse before MSBuild or test code can run.</summary>
[Collection("EnvironmentVariables")]
[Trait("Category", "Certification")]
public sealed class EgressExplicitEnforcementTests : IDisposable
{
    private readonly EnvironmentVariableScope _mode = new("ASHLAR_EGRESS_MODE", "enforce");
    private readonly EnvironmentVariableScope _profile = new("ASHLAR_DEPLOYMENT_PROFILE", "secure-workstation");
    private readonly EgressProcessStateScope _state = new(reset: true);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "egress-process-" + Guid.NewGuid().ToString("N"));

    public EgressExplicitEnforcementTests() => Directory.CreateDirectory(_directory);

    [Theory]
    [InlineData("service-build", "EG-PROC-10")]
    [InlineData("service-run", "EG-PROC-10")]
    [InlineData("platform-build", "EG-PROC-10")]
    [InlineData("platform-run", "EG-PROC-10")]
    [InlineData("ollama", "EG-PROC-11")]
    [InlineData("postgres", "EG-PROC-11")]
    public async Task Docker_api_execution_refuses_before_transport_and_report_mode_reaches_transport(string operation, string site)
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        using var config = new Docker.DotNet.DockerClientConfiguration(new Uri($"http://127.0.0.1:{port}"));
        using var client = config.CreateClient();
        var dockerfile = Path.Combine(_directory, "Dockerfile");
        await File.WriteAllTextAsync(dockerfile, "FROM scratch\n");
        using var service = new Ashlar.Infrastructure.Testing.Docker.DockerService(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Ashlar.Infrastructure.Testing.Docker.DockerService>.Instance, client);
        using var platform = new Ashlar.Infrastructure.Testing.ExecutionPlatform.DockerExecutionPlatform(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Ashlar.Infrastructure.Testing.ExecutionPlatform.DockerExecutionPlatform>.Instance, client);
        var ollama = new Ashlar.Infrastructure.Execution.Ephemeral.OllamaEphemeralLifecycle(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Ashlar.Infrastructure.Execution.Ephemeral.OllamaEphemeralLifecycle>.Instance, client);
        var postgres = new Ashlar.Infrastructure.Persistence.Ephemeral.PostgresEphemeralLifecycle(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Ashlar.Infrastructure.Persistence.Ephemeral.PostgresEphemeralLifecycle>.Instance, client);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task Run() => operation switch
        {
            "service-build" => service.BuildImageAsync(dockerfile, "egress-test", _directory, cancellationToken: timeout.Token),
            "service-run" => service.RunContainerAsync("egress-test", ["dotnet", "test"], cancellationToken: timeout.Token),
            "platform-build" => platform.BuildImageAsync(dockerfile, "egress-test", _directory, cancellationToken: timeout.Token),
            "platform-run" => platform.RunContainerAsync("egress-test", ["dotnet", "test"], cancellationToken: timeout.Token),
            "ollama" => ollama.StartSessionAsync(cancellationToken: timeout.Token),
            "postgres" => postgres.StartAsync(new("postgres"), timeout.Token),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };
        var refused = await Record.ExceptionAsync(Run);
        refused.Should().BeOfType<EgressRefusedException>().Which.Site.Should().Be(site);
        listener.Pending().Should().BeFalse("refusal must happen before contacting the daemon");
        using var report = new EnvironmentVariableScope("ASHLAR_EGRESS_MODE", "report");
        EgressProcessStateScope.Reset();
        var run = Record.ExceptionAsync(Run);
        using var connection = await listener.AcceptTcpClientAsync(timeout.Token);
        await using var stream = connection.GetStream();
        var bytes = new byte[4096];
        (await stream.ReadAsync(bytes, timeout.Token)).Should().BeGreaterThan(0);
        await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(
            "HTTP/1.1 500 Internal Server Error\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), timeout.Token);
        connection.Close();
        var error = await run.WaitAsync(timeout.Token);
        if (operation is "ollama" or "postgres")
            error.Should().BeOfType<Docker.DotNet.DockerApiException>("report mode must reach Docker's error response");
        else
            error.Should().BeNull("build/run preserve their ordinary error-result behavior");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Timed_process_and_shell_refuse_before_launch_and_report_mode_reaches_the_marker(bool shell)
    {
        var marker = Path.Combine(_directory, "executed.txt");
        var script = Path.Combine(_directory, "marker.sh");
        await File.WriteAllTextAsync(script, "printf ran > executed.txt\n");
        Task<TimedProcessResult> Run()
        {
            if (shell) return TimedProcess.RunShellAsync("bash marker.sh", TimeSpan.FromSeconds(5), workingDirectory: _directory);
            var info = new ProcessStartInfo("bash") { WorkingDirectory = _directory };
            info.ArgumentList.Add(script);
            return TimedProcess.RunAsync(info, TimeSpan.FromSeconds(5));
        }
        var error = await Record.ExceptionAsync(async () => await Run());
        var refusal = error.Should().BeOfType<EgressRefusedException>().Subject;
        refusal.Site.Should().Be("EG-PROC-08");
        refusal.Decision.Destination.Should().Be("process:bash");
        File.Exists(marker).Should().BeFalse();
        using var report = new EnvironmentVariableScope("ASHLAR_EGRESS_MODE", "report");
        EgressProcessStateScope.Reset();
        (await Run()).ExitCode.Should().Be(0);
        (await File.ReadAllTextAsync(marker)).Should().Be("ran");
    }

    [Fact]
    public async Task Internal_evaluated_process_entry_still_rejects_a_refusing_decision()
    {
        var decision = EgressGuard.ProcessDefault.Evaluate(new EgressRequest(EgressFamilies.Process, "EG-PROC-03", "process:bash"));
        var info = new ProcessStartInfo("bash") { WorkingDirectory = _directory };
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add("printf ran > executed.txt");
        var error = await Record.ExceptionAsync(() => TimedProcess.RunEvaluatedAsync(info, TimeSpan.FromSeconds(5), decision, CancellationToken.None));
        error.Should().BeOfType<EgressRefusedException>().Which.Decision.Should().BeSameAs(decision);
        File.Exists(Path.Combine(_directory, "executed.txt")).Should().BeFalse();
    }

    [Theory]
    [InlineData("EG-MESH-02")]
    [InlineData("EG-MESH-08")]
    [InlineData("EG-FILE-01")]
    [InlineData("EG-FILE-02")]
    public void Only_an_internal_explicit_operator_initiator_reports_a_named_file_export(string site)
    {
        var request = new EgressRequest(EgressFamilies.FileExport, site, "file:/private/export");
        using var fakeInitiator = new EnvironmentVariableScope("ASHLAR_EGRESS_INITIATOR", "operator-verb");
        EgressGuard.ProcessDefault.Evaluate(request).Refuses.Should().BeTrue("neither no-subject nor an environment value authorizes an operator export");
        SetOperatorInitiator(request);
        var decision = EgressGuard.ProcessDefault.Evaluate(request);
        decision.Mode.Should().Be("report");
        decision.ModeBasis.Should().Be("operator-verb");
        decision.Refuses.Should().BeFalse();
        decision.DestinationClass.Should().Be(EgressDestinationClass.NetworkExport);
        EgressGuard.ProcessDefault.Evaluate(new EgressRequest(EgressFamilies.FileExport, site, "file:/another/export"))
            .Refuses.Should().BeTrue("the initiator never flows to a later request");

        EgressProcessStateScope.SetModeResolutionProbe(() => throw new InvalidOperationException("mode fault"));
        var fault = EgressGuard.ProcessDefault.Evaluate(request);
        fault.ModeBasis.Should().Be("fault");
        fault.Refuses.Should().BeTrue("operator exceptions do not hide mode-resolution faults");
    }

    [Theory]
    [InlineData(EgressFamilies.MeshPublish, "EG-MESH-01")]
    [InlineData(EgressFamilies.Process, "EG-PROC-01")]
    [InlineData(EgressFamilies.Http, "EG-FILE-01")]
    [InlineData(EgressFamilies.FileExport, "unrecognized-site")]
    public void Operator_initiator_never_bypasses_another_family_or_site(string family, string site)
    {
        var request = new EgressRequest(family, site, "process:private");
        SetOperatorInitiator(request);
        EgressGuard.ProcessDefault.Evaluate(request).Refuses.Should().BeTrue();
    }

    private static void SetOperatorInitiator(EgressRequest request)
    {
        typeof(EgressRequest).GetProperty("Initiator", BindingFlags.Instance | BindingFlags.Public).Should().BeNull();
        var property = typeof(EgressRequest).GetProperty("Initiator", BindingFlags.Instance | BindingFlags.NonPublic)!;
        property.SetValue(request, Enum.Parse(property.PropertyType, "OperatorFileExport"));
    }

    [Theory]
    [InlineData("run --network=none --pull never image", true)]
    [InlineData("run --rm --network=none --pull never --read-only image echo --network=host", true)]
    [InlineData("run image --network=none", false)]
    [InlineData("run --entrypoint --network=none image", false)]
    [InlineData("run --network=none --network=host --pull never image", false)]
    [InlineData("run --network=host --network=none --pull never image", false)]
    [InlineData("run --network=none --network=none --pull never image", false)]
    [InlineData("run --network=none image", false)]
    [InlineData("run --network=none --pull always image", false)]
    [InlineData("run --network=none --pull never --privileged image", false)]
    [InlineData("run --network=none --pull never", false)]
    [InlineData("--host tcp://remote:2376 run --network=none --pull never image", false)]
    [InlineData("exec image --network=none", false)]
    public void Docker_host_classification_requires_real_network_off_argv_and_no_image_pull(string command, bool host)
    {
        var destination = ProcessCommandRunner.Destination("docker", null, null, command.Split(' '));
        destination.Should().Be(host ? "host:docker" : "process:docker");
        EgressGuard.ProcessDefault.Evaluate(new EgressRequest(EgressFamilies.Process, "EG-PROC-03", destination))
            .Refuses.Should().Be(!host);
    }

    [Theory]
    [InlineData(null, null, "remote", "docker-context:remote")]
    [InlineData("tcp://127.0.0.1:2376", null, null, "process:docker")]
    [InlineData("ssh://remote", null, null, "process:docker")]
    [InlineData("unix:///var/run/docker.sock", "remote", null, "docker-context:remote")]
    [InlineData(null, null, "unresolved", "docker-context:unresolved")]
    public void A_remote_or_unresolved_daemon_is_not_made_Host_by_network_none(string? host, string? context, string? persisted, string expected)
    {
        ProcessCommandRunner.Destination("docker", host, context, ["run", "--network=none", "--pull", "never", "image"], persisted)
            .Should().Be(expected);
    }

    [Fact]
    public void Actual_network_off_sandbox_argv_qualifies_for_the_local_daemon_exception()
    {
        var spec = new SandboxSpec("sdk", [], NetworkAccess.None, ["dotnet", "test", "--no-build"]);
        foreach (var argv in new[]
        {
            DockerSandboxedCommandRunner.BuildDockerArguments(spec),
            DockerSandboxedSessionRunner.BuildStartArguments(spec, "session", 123456)
        })
            ProcessCommandRunner.Destination("docker", null, null, argv).Should().Be("host:docker");
    }

    [Fact]
    public async Task Docker_without_a_network_off_sandbox_refuses_before_process_launch()
    {
        var error = await Record.ExceptionAsync(() => new ProcessCommandRunner().RunAsync("docker", ["--version"]));
        var refusal = error.Should().BeOfType<EgressRefusedException>().Subject;
        refusal.Site.Should().Be("EG-PROC-03");
        refusal.Decision.Destination.Should().Be("process:docker");
        refusal.Decision.Refuses.Should().BeTrue();
    }

    [Theory]
    [InlineData("{\"currentContext\":\"remote-builder\"}", "docker-context:remote-builder")]
    [InlineData("{broken", "docker-context:unresolved")]
    public async Task Actual_docker_funnel_reads_persisted_context_and_fails_closed_when_unreadable(string config, string expected)
    {
        using var configDirectory = new EnvironmentVariableScope("DOCKER_CONFIG", _directory);
        using var dockerHost = EnvironmentVariableScope.Unset("DOCKER_HOST");
        using var dockerContext = EnvironmentVariableScope.Unset("DOCKER_CONTEXT");
        await File.WriteAllTextAsync(Path.Combine(_directory, "config.json"), config);
        var error = await Record.ExceptionAsync(() => new ProcessCommandRunner().RunAsync("docker",
            ["run", "--network=none", "--pull", "never", "image"]));
        var refusal = error.Should().BeOfType<EgressRefusedException>().Subject;
        refusal.Site.Should().Be("EG-PROC-03");
        refusal.Decision.Destination.Should().Be(expected);
    }

    public void Dispose()
    {
        _state.Dispose();
        _profile.Dispose();
        _mode.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    [Theory]
    [InlineData("build --no-restore")]
    [InlineData("test --no-build")]
    [InlineData("run --no-build")]
    [InlineData("pack --no-build")]
    [InlineData("publish --no-build")]
    public async Task Dotnet_runner_refuses_working_tree_verbs_before_launch(string arguments)
    {
        var (_, marker) = WriteProject();
        var method = typeof(DotnetTestTool).Assembly.GetType("Ashlar.Tools.Dev.DotnetRunner", true)!
            .GetMethod("RunAsync", BindingFlags.Public | BindingFlags.Static)!;
        var error = await Record.ExceptionAsync(async () =>
        {
            await (Task<(int, string, string, bool)>)method.Invoke(null,
                [_directory, arguments, TimeSpan.FromSeconds(20), CancellationToken.None])!;
        });
        AssertRefusal(error, "EG-PROC-01");
        File.Exists(marker).Should().BeFalse();
    }

    [Fact]
    public async Task Dotnet_test_no_build_executes_project_targets_in_report_but_not_enforce()
    {
        var (_, marker) = WriteProject();
        var error = await Record.ExceptionAsync(() => DotnetTestTool.RunTrxTestsNoBuildAsync(_directory));
        AssertRefusal(error, "EG-PROC-01");
        File.Exists(marker).Should().BeFalse();

        using var report = new EnvironmentVariableScope("ASHLAR_EGRESS_MODE", "report");
        EgressProcessStateScope.Reset();
        await DotnetTestTool.RunTrxTestsNoBuildAsync(_directory);
        File.Exists(marker).Should().BeTrue("--no-build must not be treated as a no-code-execution boundary");
        (await File.ReadAllTextAsync(marker)).Trim().Should().Be("target-ran");
    }

    [Fact]
    public async Task Regression_runner_preserves_refusal_before_launch()
    {
        var (project, marker) = WriteProject();
        var error = await Record.ExceptionAsync(() => new DotNetRegressionTestRunner().RunAsync(project));
        AssertRefusal(error, "EG-PROC-06");
        File.Exists(marker).Should().BeFalse();
    }

    [Fact]
    public async Task Instance_spawner_preserves_refusal_before_launch()
    {
        var (project, marker) = WriteProject();
        var error = await Record.ExceptionAsync(() => new DotNetInstanceSpawner()
            .SpawnAsync(1, [new ParameterSet()], project));
        AssertRefusal(error, "EG-PROC-07");
        File.Exists(marker).Should().BeFalse();
    }

    private static void AssertRefusal(Exception? error, string site)
    {
        var refusal = error.Should().BeOfType<EgressRefusedException>().Subject;
        refusal.Site.Should().Be(site);
        refusal.Decision.Destination.Should().Be("process:dotnet");
        refusal.Decision.DestinationClass.Should().Be(EgressDestinationClass.NetworkExport);
        refusal.Decision.Refuses.Should().BeTrue();
    }

    private (string Project, string Marker) WriteProject()
    {
        var project = Path.Combine(_directory, "Marker.csproj");
        var marker = Path.Combine(_directory, "executed.txt");
        new XDocument(new XElement("Project", new XAttribute("Sdk", "Microsoft.NET.Sdk"),
            new XElement("PropertyGroup", new XElement("TargetFramework", "net8.0"),
                new XElement("IsTestProject", "true")),
            new XElement("Target", new XAttribute("Name", "BeforeVSTestMarker"),
                new XAttribute("BeforeTargets", "VSTest"),
                new XElement("WriteLinesToFile", new XAttribute("File", marker),
                    new XAttribute("Lines", "target-ran"), new XAttribute("Overwrite", "true")))))
            .Save(project);
        return (project, marker);
    }
}
