using System.Diagnostics;
using FluentAssertions;
using Xunit;

namespace Ashlar.Tests.Kernel;

public sealed class YamlDepthProcessTests
{
    [Theory]
    [InlineData("guard", "mapping")]
    [InlineData("guard", "sequence")]
    [InlineData("manifest", "mapping")]
    [InlineData("manifest", "sequence")]
    [InlineData("policy", "mapping")]
    [InlineData("policy", "sequence")]
    public async Task Extreme_input_returns_a_reason_instead_of_terminating_the_process(string entrypoint, string shape)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        var probe = Path.Combine(AppContext.BaseDirectory, "InputSafetyProbe", "InputSafetyProbe.dll");
        File.Exists(probe).Should().BeTrue("the test build must include the child-process probe");
        foreach (var argument in new[] { probe, "yaml", entrypoint, shape, "10000" })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start YAML probe.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new TimeoutException("YAML probe did not exit within 30 seconds.");
        }
        var output = await stdout + await stderr;
        process.ExitCode.Should().Be(23, output);
        output.Should().Contain("nesting depth").And.Contain("64");
    }
}
