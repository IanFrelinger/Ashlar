using System.CommandLine;
using Ashlar.CLI.Commands;
using Ashlar.Manifest;
using Ashlar.Tests.Infrastructure.Helpers;
using FluentAssertions;
using Xunit;

namespace Ashlar.Tests.CLI.Tests.Commands;

[Collection("MeshIntegration")]
public sealed class EgressCliExportPreservationTests
{
    [Theory]
    [InlineData("native", true)]
    [InlineData("aws", true)]
    [InlineData("azure", true)]
    [InlineData("native", false)]
    [InlineData("aws", false)]
    [InlineData("azure", false)]
    public async Task Refused_operator_export_preserves_existing_bundle_and_allowed_export_replaces_it(string target, bool fault)
    {
        using var mode = new EnvironmentVariableScope("ASHLAR_EGRESS_MODE", "enforce");
        using var profile = new EnvironmentVariableScope("ASHLAR_DEPLOYMENT_PROFILE", "secure-workstation");
        using var state = new EgressProcessStateScope(reset: true);
        var directory = Path.Combine(Path.GetTempPath(), "export-preserve-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var stderr = new StringWriter();
        var previous = Console.Error;
        try
        {
            ProjectScaffold.TryScaffold("triage", out var manifest, out var policy, out var reason).Should().BeTrue(reason);
            await File.WriteAllTextAsync(Path.Combine(directory, "ashlar.yaml"), manifest);
            await File.WriteAllTextAsync(Path.Combine(directory, "ashlar.policy.yaml"), policy);
            var output = Path.Combine(directory, "output");
            var args = new List<string> { "export", target, "--path", directory, "--out", output };
            if (target == "native") args.AddRange(["--no-runtime", "--rid", "linux-x64"]);
            Console.SetError(stderr);
            var root = new RootCommand { new ExportCommand() };
            (await root.InvokeAsync(args.ToArray())).Should().Be(0, stderr.ToString());
            var bundle = Directory.EnumerateDirectories(output).Should().ContainSingle().Which;
            var marker = Path.Combine(bundle, "old-bundle.txt");
            await File.WriteAllTextAsync(marker, "previous export");
            var original = await File.ReadAllTextAsync(Path.Combine(bundle, "app", "ashlar.yaml"));
            if (fault)
                EgressProcessStateScope.SetModeResolutionProbe(() => throw new InvalidOperationException("policy-fault-secret"));
            (await root.InvokeAsync(args.ToArray())).Should().Be(fault ? 77 : 0, stderr.ToString());
            if (fault)
            {
                File.Exists(marker).Should().BeTrue("a refused export must not delete the previous bundle");
                (await File.ReadAllTextAsync(marker)).Should().Be("previous export");
                stderr.ToString().Should().Contain("Egress refused by policy").And.NotContain("policy-fault-secret");
            }
            else
                File.Exists(marker).Should().BeFalse("an allowed replacement must remove stale bundle files");
            (await File.ReadAllTextAsync(Path.Combine(bundle, "app", "ashlar.yaml"))).Should().Be(original);
        }
        finally
        {
            Console.SetError(previous);
            Directory.Delete(directory, recursive: true);
        }
    }
}
