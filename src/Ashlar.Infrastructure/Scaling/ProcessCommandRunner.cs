using System.Diagnostics;
using System.Text.Json;
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
        // SPEC-007 EG-PROC-03: working-tree commands need an actual network-off sandbox to be Host.
        var decision = EgressGuard.ProcessDefault.Evaluate(new EgressRequest(EgressFamilies.Process, "EG-PROC-03", Destination(
            fileName, Environment.GetEnvironmentVariable("DOCKER_HOST"), Environment.GetEnvironmentVariable("DOCKER_CONTEXT"),
            arguments, ReadPersistedDockerContext(fileName))));
        if (decision.Refuses) throw new EgressRefusedException(decision);
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
        };
        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);

        // Infinite wait still kill-on-cancel: sandbox timeouts CancelAfter and must
        // not leave a docker child attached to a wedged daemon.
        var result = await TimedProcess.RunEvaluatedAsync(psi, Timeout.InfiniteTimeSpan, decision, cancellationToken)
            .ConfigureAwait(false);
        return new ProcessCommandResult(result.ExitCode, result.StdOut, result.StdErr);
    }

    /// <summary>
    /// Classifies a command as NetworkExport unless it is a recognized network-off docker run against a
    /// local daemon. Compose, exec and unrecognized argv stay off-host. A named context (environment first,
    /// then persisted configuration) is not evidence of a local daemon. Global CLI options are not accepted
    /// as sandbox proof, so an argv host/context override cannot inherit the default daemon's classification.
    /// </summary>
    internal static string Destination(string? fileName, string? dockerHost, string? dockerContext,
        IReadOnlyList<string> arguments, string? persistedContext = null)
    {
        var name = string.IsNullOrWhiteSpace(fileName) ? string.Empty : Path.GetFileNameWithoutExtension(fileName.Trim());
        var ordinary = "process:" + (name.Length == 0 ? "unknown" : name);
        if (!name.Equals("docker", StringComparison.OrdinalIgnoreCase) || !HasNetworkOffRun(arguments))
        {
            return ordinary;
        }

        // Refuse named contexts. Without an explicit selection, inspect the persisted context too.
        var context = !string.IsNullOrWhiteSpace(dockerContext) ? dockerContext.Trim()
            : string.IsNullOrWhiteSpace(dockerHost) ? persistedContext?.Trim() : null;
        if (!string.IsNullOrWhiteSpace(context) && context != "default")
        {
            return "docker-context:" + context;
        }

        if (!string.IsNullOrWhiteSpace(dockerHost))
        {
            // A TCP/SSH daemon is off-host even if reached through a loopback relay.
            if (dockerHost.StartsWith("unix://", StringComparison.OrdinalIgnoreCase)
                || dockerHost.StartsWith("npipe://", StringComparison.OrdinalIgnoreCase))
                return "host:docker";
            return ordinary;
        }

        return "host:docker";
    }

    internal static bool HasNetworkOffRun(IReadOnlyList<string> arguments)
    {
        if (arguments.Count < 3 || arguments[0] != "run") return false;
        var networkOff = false;
        var pullNever = false;
        for (var i = 1; i < arguments.Count; i++)
        {
            var arg = arguments[i];
            if (arg == "--network=none")
            {
                if (networkOff) return false; // duplicate/conflicting selectors are not proof
                networkOff = true;
                continue;
            }
            if (arg is "--rm" or "-d" or "--detach" or "-i" or "-t" or "--read-only") continue;
            if (arg == "--pull")
            {
                if (pullNever || ++i >= arguments.Count || arguments[i] != "never") return false;
                pullNever = true;
                continue;
            }
            if (arg is "--name" or "--label" or "--cap-drop" or "--security-opt"
                or "--tmpfs" or "--memory" or "--cpus" or "--pids-limit" or "-v" or "--entrypoint")
            {
                if (++i >= arguments.Count || string.IsNullOrWhiteSpace(arguments[i]) || arguments[i].StartsWith('-')) return false;
                continue;
            }
            if (arg.StartsWith('-')) return false; // unknown flags include alternate --network/-H/--context forms
            // The first positional token is the image. Everything after it is the container's command,
            // so a string '--network=none' there must never authorize the launch.
            return networkOff && pullNever && !string.IsNullOrWhiteSpace(arg);
        }
        return false; // no image
    }

    private static string? ReadPersistedDockerContext(string fileName)
    {
        if (!Path.GetFileNameWithoutExtension(fileName.Trim()).Equals("docker", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            var config = Environment.GetEnvironmentVariable("DOCKER_CONFIG");
            var directory = string.IsNullOrWhiteSpace(config)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".docker") : config;
            var path = Path.Combine(directory, "config.json");
            if (!File.Exists(path)) return null;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.TryGetProperty("currentContext", out var context) ? context.GetString() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException)
        {
            return "unresolved"; // inability to resolve a daemon never grants Host
        }
    }
}
