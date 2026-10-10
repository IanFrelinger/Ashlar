using System.CommandLine;
using System.Xml.Linq;
using Ashlar.CLI.Commands;
using Ashlar.Manifest;
using Ashlar.Tests.Infrastructure.Helpers;
using FluentAssertions;
using Xunit;

namespace Ashlar.Tests.CLI.Tests.Commands;

[Collection("MeshIntegration")]
public sealed class EgressCliRuntimeExportTests
{
    [Theory]
    [InlineData("air-gapped")]
    [InlineData("secure-workstation")]
    public async Task Operator_export_does_not_authorize_runtime_publish_and_returns_77(string deployment)
    {
        using var mode = new EnvironmentVariableScope("ASHLAR_EGRESS_MODE", "enforce");
        using var profile = new EnvironmentVariableScope("ASHLAR_DEPLOYMENT_PROFILE", deployment);
        using var state = new EgressProcessStateScope(reset: true);
        var directory = Path.Combine(Path.GetTempPath(), "runtime-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var stderr = new StringWriter();
        var previous = Console.Error;
        try
        {
            ProjectScaffold.TryScaffold("triage", out var manifest, out var policy, out var reason).Should().BeTrue(reason);
            await File.WriteAllTextAsync(Path.Combine(directory, "ashlar.yaml"), manifest);
            await File.WriteAllTextAsync(Path.Combine(directory, "ashlar.policy.yaml"), policy);
            var project = Path.Combine(directory, "Runtime.csproj");
            new XDocument(new XElement("Project", new XElement("Target", new XAttribute("Name", "Publish"),
                new XElement("WriteLinesToFile", new XAttribute("File", Path.Combine(directory, "executed.txt")),
                    new XAttribute("Lines", "ran"))))).Save(project);
            var output = Path.Combine(directory, "output");
            Console.SetError(stderr);
            var root = new RootCommand { new ExportCommand() };
            var code = await root.InvokeAsync(["export", "native", "--path", directory, "--out", output,
                "--rid", "linux-x64", "--cli-project", project]);
            code.Should().Be(77, stderr.ToString());
            stderr.ToString().Should().Contain("Egress refused by policy").And.Contain("ref=").And.NotContain(directory);
            Directory.EnumerateFiles(output, "ashlar.yaml", SearchOption.AllDirectories).Should().ContainSingle();
            Directory.EnumerateFiles(directory, "executed.txt", SearchOption.AllDirectories).Should().BeEmpty();
            Directory.EnumerateDirectories(output, ".publish-tmp", SearchOption.AllDirectories).Should().BeEmpty();
        }
        finally
        {
            Console.SetError(previous);
            Directory.Delete(directory, recursive: true);
        }
    }
}
