using System.Diagnostics;
using System.CommandLine;
using System.CommandLine.Invocation;
using Ashlar.Abstractions.Security.Egress;
using Ashlar.CLI.Commands;
using FluentAssertions;
using Xunit;

namespace Ashlar.Tests.CLI.Tests.Commands;

[Collection("MeshIntegration")]
public sealed class EgressCliExitCodeTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(10)]
    public async Task A_caught_refusal_cannot_be_converted_into_another_exit_code(int exitCode)
    {
        var root = new RootCommand();
        root.SetHandler((InvocationContext context) =>
        {
            try
            {
                new EgressGuard("secure-workstation", "enforce")
                    .Evaluate(new EgressRequest(EgressFamilies.Process, "EG-PROC-09", "process:dotnet")).ThrowIfRefused();
            }
            catch (EgressRefusedException) { context.ExitCode = exitCode; }
        });
        (await Ashlar.CLI.EgressCliInvocation.InvokeAsync(root, [])).Should().Be(77);
        var control = new RootCommand();
        control.SetHandler((InvocationContext context) => context.ExitCode = exitCode);
        (await Ashlar.CLI.EgressCliInvocation.InvokeAsync(control, [])).Should().Be(exitCode,
            "a refusal belongs only to its invocation, never to the next command");
    }

    [Fact]
    public async Task Concurrent_invocations_do_not_inherit_each_others_refusals()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clean = new RootCommand();
        clean.SetHandler(async (InvocationContext context) =>
        {
            entered.SetResult();
            await release.Task;
            context.ExitCode = 0;
        });
        var cleanRun = Ashlar.CLI.EgressCliInvocation.InvokeAsync(clean, []);
        await entered.Task;
        try
        {
            var refused = new RootCommand();
            refused.SetHandler(() => new EgressGuard("secure-workstation", "enforce")
                .Evaluate(new EgressRequest(EgressFamilies.Process, "EG-PROC-09", "process:dotnet")).ThrowIfRefused());
            (await Ashlar.CLI.EgressCliInvocation.InvokeAsync(refused, [])).Should().Be(77);
        }
        finally { release.SetResult(); }
        (await cleanRun).Should().Be(0);
    }

    [Theory]
    [InlineData("air-gapped", "portable")]
    [InlineData("secure-workstation", "portable")]
    [InlineData("secure-workstation", "validate")]
    public async Task A_real_process_refusal_exits_77_with_only_the_redacted_message(string profile, string command)
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
                typeof(ExportCommand).Assembly.Location })
                process.StartInfo.ArgumentList.Add(arg);
            foreach (var arg in command == "portable" ? new[] { "test", "portable", "--scope", "smoke" } : new[] { "validate" })
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
            output.Should().Contain("Egress refused by policy").And.Contain(command == "portable" ? "site=EG-PROC-09" : "site=EG-PROC-02").And.Contain("ref=")
                .And.NotContain(directory).And.NotContain(" at Ashlar.");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
