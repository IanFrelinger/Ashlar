using System.Globalization;
using Ashlar.Core.Application.Execution.Ports;

namespace Ashlar.Infrastructure.Execution.Sandbox;

/// <summary>
/// One task per Linux Docker session, reusing Ashlar's hardened session backend. The
/// trusted image must contain /bin/sh and sleep. Containers share the host kernel and
/// are not, on their own, a secure sandbox for hostile code.
/// </summary>
public sealed class DockerIsolatedTaskRunner : IIsolatedTaskRunner
{
    private readonly ISandboxedSessionRunner _sessions;

    /// <summary>Creates a task runner over the host's sandbox session backend.</summary>
    public DockerIsolatedTaskRunner(ISandboxedSessionRunner sessions)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
    }

    /// <inheritdoc />
    public async Task<IsolatedTaskResult> RunAsync(
        IsolatedTaskRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        // Snapshot caller-owned argv before the first await; no mutable command can change
        // after validation while the environment is being allocated or inspected.
        var command = request.Command?.ToArray();
        Validate(request, command);
        cancellationToken.ThrowIfCancellationRequested();

        var limits = new ResourceLimits(
            request.MemoryBytes.ToString(CultureInfo.InvariantCulture), request.Pids,
            request.Cpus.ToString(CultureInfo.InvariantCulture), request.Timeout);
        var seconds = Math.Ceiling(request.Timeout.TotalSeconds).ToString(CultureInfo.InvariantCulture);
        var spec = new SandboxSpec(
            request.Image, Array.Empty<Mount>(), NetworkAccess.None,
            new[] { "-c", $"exec sleep {seconds}" }, limits, Entrypoint: "/bin/sh")
        {
            ScratchPaths = new[] { "/tmp" },
        };

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(request.Timeout);
        try
        {
            // The backend cleans up a start whose outcome is uncertain. Once it returns,
            // disposal owns every exit, including inspection refusal and cancellation.
            await using var session = await _sessions.StartAsync(spec, budget.Token).ConfigureAwait(false);
            if (session is not IBoundedOutputSandboxedSession boundedSession)
                throw new NotSupportedException("Isolated tasks require a session with bounded host output capture.");
            budget.Token.ThrowIfCancellationRequested();
            var attestation = await session.AttestAsync(budget.Token).ConfigureAwait(false);
            // Compare against this invocation's limits, not an untrusted backend's echo.
            attestation = attestation with { Requested = limits };
            attestation.ThrowIfBelowRequested();
            if (attestation.SessionId != session.SessionId
                || string.IsNullOrWhiteSpace(attestation.ImageDigest)
                || attestation.EffectiveNetworkMode != DockerSandboxedSessionRunner.NoNetworkMode
                || attestation.EffectiveReadOnlyRootFilesystem != true
                || !attestation.EffectiveDroppedCapabilities.Contains("ALL", StringComparer.OrdinalIgnoreCase)
                || !attestation.EffectiveSecurityOptions.Any(IsNoNewPrivileges))
            {
                throw new InvalidOperationException(
                    "Isolated task environment could not verify image identity and requested containment; refusing execution.");
            }

            budget.Token.ThrowIfCancellationRequested();
            var result = await boundedSession.ExecBoundedAsync(command!, request.MaxOutputBytes, budget.Token)
                .ConfigureAwait(false);
            budget.Token.ThrowIfCancellationRequested();
            return new IsolatedTaskResult(result, attestation);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && budget.IsCancellationRequested)
        {
            throw new TimeoutException($"Isolated task exceeded its {request.Timeout} startup and execution budget.");
        }
    }

    private static bool IsNoNewPrivileges(string option) =>
        option is "no-new-privileges" or "no-new-privileges=true" or "no-new-privileges:true";

    private static void Validate(IsolatedTaskRequest request, string[]? command)
    {
        if (string.IsNullOrWhiteSpace(request.Image) || request.Image.StartsWith('-')
            || request.Image.Any(char.IsWhiteSpace) || request.Image.Contains('\0'))
            throw new ArgumentException("An image reference without whitespace or option prefixes is required.", nameof(request));
        if (command is null || command.Length == 0 || string.IsNullOrWhiteSpace(command[0])
            || command.Any(arg => arg is null || arg.Contains('\0')))
            throw new ArgumentException("A non-empty program argv without null arguments is required.", nameof(request));
        if (request.MemoryBytes < 6L * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(request), "Memory must be at least 6 MiB.");
        if (request.Cpus < 0.01m || request.Cpus > 1024m)
            throw new ArgumentOutOfRangeException(nameof(request), "CPU quota must be between 0.01 and 1024.");
        if (request.Pids < 1)
            throw new ArgumentOutOfRangeException(nameof(request), "The process limit must be positive.");
        if (request.Timeout <= TimeSpan.Zero || request.Timeout > TimeSpan.FromDays(1))
            throw new ArgumentOutOfRangeException(nameof(request), "Timeout must be positive and at most one day.");
        if (request.MaxOutputBytes < 1 || request.MaxOutputBytes > 16 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(request), "Combined output must be between 1 byte and 16 MiB.");
    }
}
