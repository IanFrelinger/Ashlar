using System.Diagnostics;
using Ashlar.Abstractions.Security.Egress;
using Ashlar.Core.Application.Execution.Ports;
using Ashlar.Infrastructure.HostProcess;

namespace Ashlar.Infrastructure.Scaling;

/// <summary>Default <see cref="IProcessCommandRunner"/> using <see cref="System.Diagnostics.Process"/>.</summary>
public sealed class ProcessCommandRunner : IProcessCommandRunner
{
    /// <inheritdoc />
    public async Task<ProcessCommandResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        // SPEC-007 EG-PROC-03, report-only: every docker, compose and kubectl argv leaves through this funnel.
        _ = EgressGuard.ProcessDefault.Evaluate(new EgressRequest(EgressFamilies.Process, "EG-PROC-03", Destination(
            fileName, Environment.GetEnvironmentVariable("DOCKER_HOST"), Environment.GetEnvironmentVariable("DOCKER_CONTEXT"))));
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
        };
        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);

        // Infinite wait still kill-on-cancel: sandbox timeouts CancelAfter and must
        // not leave a docker child attached to a wedged daemon.
        var result = await TimedProcess.RunAsync(psi, Timeout.InfiniteTimeSpan, cancellationToken)
            .ConfigureAwait(false);
        return new ProcessCommandResult(result.ExitCode, result.StdOut, result.StdErr);
    }

    /// <summary>
    /// Where the argv goes, for the decision record. An executable other than <c>docker</c> or
    /// <c>docker-compose</c> (kubectl and its API server included) is <c>process:&lt;name&gt;</c> and is classified
    /// by its family. Docker talks to <paramref name="dockerHost"/> when it is set (returned as is, so a
    /// <c>unix://</c> or <c>npipe://</c> socket reads as inside the host and <c>tcp://</c> or <c>ssh://</c> as
    /// outside it); otherwise to the context named by <paramref name="dockerContext"/>, as
    /// <c>docker-context:&lt;name&gt;</c>, unless it is <c>default</c>; otherwise to the local daemon,
    /// <c>host:docker</c>. A context persisted by <c>docker context use</c> is not read. Never throws.
    /// </summary>
    internal static string Destination(string? fileName, string? dockerHost, string? dockerContext)
    {
        var name = string.IsNullOrWhiteSpace(fileName) ? string.Empty : Path.GetFileNameWithoutExtension(fileName.Trim());
        if (!name.Equals("docker", StringComparison.OrdinalIgnoreCase) && !name.Equals("docker-compose", StringComparison.OrdinalIgnoreCase))
        {
            return "process:" + (name.Length == 0 ? "unknown" : name);
        }

        if (!string.IsNullOrWhiteSpace(dockerHost))
        {
            return dockerHost;
        }

        if (!string.IsNullOrWhiteSpace(dockerContext) && !dockerContext.Trim().Equals("default", StringComparison.Ordinal))
        {
            return "docker-context:" + dockerContext.Trim();
        }

        return "host:docker";
    }
}
