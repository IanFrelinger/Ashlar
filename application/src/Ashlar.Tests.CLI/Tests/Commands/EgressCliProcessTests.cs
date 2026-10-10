using System.Reflection;
using System.Runtime.ExceptionServices;
using Ashlar.Abstractions.Security.Egress;
using Ashlar.CLI.Commands;
using Ashlar.Tests.Infrastructure.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Ashlar.Tests.CLI.Tests.Commands;

[Collection("MeshIntegration")]
public sealed class EgressCliProcessTests
{
    [Theory]
    [InlineData("bash")]
    [InlineData("pwsh")]
    public void Release_availability_probes_only_request_a_version(string executable)
    {
        var info = ReleaseCommand.VersionProbeStartInfo(executable);
        info.FileName.Should().Be(executable);
        info.Arguments.Should().Be("--version");
        info.ArgumentList.Should().BeEmpty();
        info.UseShellExecute.Should().BeFalse();
    }

    [Fact]
    public void Release_version_probe_rejects_an_arbitrary_executable()
    {
        Action create = () => ReleaseCommand.VersionProbeStartInfo("dotnet");
        create.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData("DogfoodTestCommand", "RunDotNetAsync")]
    [InlineData("ExportCommand", "PublishRuntimeAsync")]
    [InlineData("MultiPlatformTestCommand", "RunNativeTestsAsync")]
    [InlineData("TestPortableCommand", "RunSmokeAsync")]
    [InlineData("TestPortableCommand", "RunPersistenceMultiEnvAsync")]
    [InlineData("BootstrapRuntime", "StartShellProcess")]
    [InlineData("WorkflowCommand", "PullOllamaModelsAsync")]
    public async Task Direct_CLI_process_routes_refuse_before_launch(string typeName, string methodName)
    {
        using var mode = new EnvironmentVariableScope("ASHLAR_EGRESS_MODE", "enforce");
        using var profile = new EnvironmentVariableScope("ASHLAR_DEPLOYMENT_PROFILE", "secure-workstation");
        using var state = new EgressProcessStateScope(reset: true);
        var directory = Path.Combine(Path.GetTempPath(), "cli-process-egress-" + Guid.NewGuid().ToString("N"));
        var previous = Environment.CurrentDirectory;
        Directory.CreateDirectory(Path.Combine(directory, "src", "Ashlar.Tests.Infrastructure"));
        var project = Path.Combine(directory, "src", "Ashlar.Tests.Infrastructure", "Ashlar.Tests.Infrastructure.csproj");
        await File.WriteAllTextAsync(Path.Combine(directory, "Ashlar.sln"), "");
        await File.WriteAllTextAsync(project, "<Project />");
        Environment.CurrentDirectory = directory;
        try
        {
            object?[] arguments = methodName switch
            {
                "RunDotNetAsync" => [directory, "test", false, new[] { project, "--no-build" }],
                "PublishRuntimeAsync" => [new FileInfo(project), "linux-x64", directory, "ashlar", CancellationToken.None],
                "RunNativeTestsAsync" => ["linux", project, null, NullLogger.Instance],
                "RunSmokeAsync" or "RunPersistenceMultiEnvAsync" => [null, true, false],
                "StartShellProcess" => ["printf ran > executed.txt", false],
                "PullOllamaModelsAsync" => [new[] { "egress-test-model" }, CancellationToken.None],
                _ => throw new ArgumentException(methodName)
            };
            var type = typeof(ExportCommand).Assembly.GetType("Ashlar.CLI.Commands." + typeName, true)!;
            var method = type.GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static)!;
            var error = await Record.ExceptionAsync(async () =>
            {
                object? result;
                try { result = method.Invoke(null, arguments); }
                catch (TargetInvocationException ex) when (ex.InnerException is not null)
                {
                    ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                    throw;
                }
                if (result is Task task) await task;
            });
            var refusal = error.Should().BeOfType<EgressRefusedException>().Subject;
            refusal.Site.Should().Be("EG-PROC-09");
            refusal.Decision.DestinationClass.Should().Be(EgressDestinationClass.NetworkExport);
            File.Exists(Path.Combine(directory, "executed.txt")).Should().BeFalse();
        }
        finally
        {
            Environment.CurrentDirectory = previous;
            Directory.Delete(directory, recursive: true);
        }
    }
}
