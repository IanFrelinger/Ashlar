using System.Diagnostics;
using Ashlar.CLI.Commands;
using FluentAssertions;
using Xunit;

namespace Ashlar.Tests.CLI.Tests.Commands;

[Collection("MeshIntegration")]
public sealed class EgressCliExitCodeTests
{
    [Theory]
    [InlineData("air-gapped")]
    [InlineData("secure-workstation")]
    public async Task A_real_process_refusal_exits_77_with_only_the_redacted_message(string profile)
    {
        var directory = Path.Combine(Path.GetTempPath(), "egress-private-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "src", "Ashlar.Tests.Infrastructure"));
        await File.WriteAllTextAsync(Path.Combine(directory, "Ashlar.sln"), "");
        await File.WriteAllTextAsync(Path.Combine(directory, "src", "Ashlar.Tests.Infrastructure", "Ashlar.Tests.Infrastructure.csproj"), "<Project />");
        try
        {
            using var process = new Process { StartInfo = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = directory, RedirectStandardOutput = true,
                RedirectStandardError = true, UseShellExecute = false
            }};
            var testBase = Path.Combine(AppContext.BaseDirectory, "Ashlar.Tests.CLI");
            foreach (var arg in new[] { "exec", "--runtimeconfig", testBase + ".runtimeconfig.json", "--depsfile", testBase + ".deps.json",
                typeof(ExportCommand).Assembly.Location, "test", "portable", "--scope", "smoke" })
                process.StartInfo.ArgumentList.Add(arg);
            process.StartInfo.Environment["ASHLAR_DEPLOYMENT_PROFILE"] = profile;
            process.StartInfo.Environment.Remove("ASHLAR_EGRESS_MODE");
            process.Start().Should().BeTrue();
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try { await process.WaitForExitAsync(timeout.Token); }
            finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            var output = await stdout + await stderr;
            process.ExitCode.Should().Be(77, output);
            output.Should().Contain("Egress refused by policy").And.Contain("site=EG-PROC-09").And.Contain("ref=")
                .And.NotContain(directory).And.NotContain(" at Ashlar.");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
