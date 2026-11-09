using Microsoft.Extensions.Logging;
using Ashlar.Core.Application.Execution.Ports;
using Ashlar.Infrastructure.Scaling;

namespace Ashlar.Infrastructure.Execution.Sandbox;

/// <summary>
/// Container-engine backend for <see cref="ISandboxedSessionRunner"/> (extension spec
/// Part B): <c>docker run -d</c> starts a named keepalive container, work is submitted
/// with <c>docker exec</c>, and teardown is <c>docker rm -f</c>.
///
/// Every session container carries <see cref="SessionLabel"/> plus a
/// <see cref="DeadlineLabelKey"/> label (unix seconds) computed from the spec's wall-clock
/// timeout — the labels are what <see cref="DockerSandboxSessionReaper"/> sweeps on, so
/// a session leaked by a crashed host process still dies at its deadline.
/// </summary>
public sealed class DockerSandboxedSessionRunner : ISandboxedSessionRunner
{
    /// <summary>Label marking a container as a Ashlar sandbox session (reaper filter key).</summary>
    public const string SessionLabel = "ashlar.session=1";

    /// <summary>Label key carrying the session's hard deadline in unix seconds.</summary>
    public const string DeadlineLabelKey = "ashlar.session.deadline";

    /// <summary>Container-name prefix for session containers.</summary>
    public const string NamePrefix = "ashlar-session-";

    /// <summary>
    /// Session lifetime applied when the spec declares no wall-clock timeout. Sessions are
    /// never immortal: an unlabeled or unbounded session is indistinguishable from a leak.
    /// </summary>
    public static readonly TimeSpan DefaultSessionLifetime = TimeSpan.FromMinutes(30);

    /// <summary>Maximum time spent removing a session before the reaper becomes the cleanup backstop.</summary>
    public static readonly TimeSpan TeardownTimeout = TimeSpan.FromSeconds(10);

    private readonly IProcessCommandRunner _processRunner;
    private readonly TimeProvider _clock;
    private readonly ISessionProvenanceSink? _provenance;
    private readonly ILogger<DockerSandboxedSessionRunner>? _logger;
    private readonly string? _expectedImageDigest;

    /// <summary>
    /// Creates a Docker-backed session runner. <paramref name="expectedImageDigest"/>, when
    /// set, PINS the session image: the image identity the spec's image reference must
    /// resolve to (the engine's image ID, <c>sha256:…</c> — the same value attestation
    /// records as the digest and the certificate carries as its <c>image-digest</c> input).
    /// A session whose image resolves to anything else refuses to start. Null keeps
    /// today's capture-only behaviour: the resolved identity is recorded, not checked.
    /// </summary>
    public DockerSandboxedSessionRunner(
        IProcessCommandRunner processRunner,
        TimeProvider? clock = null,
        ILogger<DockerSandboxedSessionRunner>? logger = null,
        ISessionProvenanceSink? provenance = null,
        string? expectedImageDigest = null)
    {
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _clock = clock ?? TimeProvider.System;
        _logger = logger;
        _provenance = provenance;
        _expectedImageDigest = string.IsNullOrWhiteSpace(expectedImageDigest) ? null : expectedImageDigest.Trim();
    }

    /// <summary>The pinned image identity, or null when the runner only captures it.</summary>
    public string? ExpectedImageDigest => _expectedImageDigest;

    /// <inheritdoc />
    public async Task<ISandboxedSession> StartAsync(SandboxSpec spec, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);

        if (_expectedImageDigest is not null)
            await RefuseUnlessImageMatchesPinAsync(spec, cancellationToken).ConfigureAwait(false);

        var sessionId = $"{NamePrefix}{Guid.NewGuid():N}";
        var deadline = _clock.GetUtcNow() + (spec.Limits?.Timeout is { } t && t > TimeSpan.Zero ? t : DefaultSessionLifetime);
        var args = BuildStartArguments(spec, sessionId, deadline.ToUnixTimeSeconds());

        // Own the name before launch: Docker can create the container even when the CLI
        // fails or cancellation interrupts its response. There is then no returned handle
        // for the caller to dispose, so startup itself must attempt bounded cleanup.
        var session = new DockerSandboxedSession(
            sessionId, spec, _processRunner, _clock, RecordProvenance, _logger, _expectedImageDigest);
        try
        {
            _logger?.LogDebug("Sandbox session start via docker: {Args}", string.Join(' ', args));
            var result = await _processRunner.RunAsync("docker", args, cancellationToken).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                // Fail-closed: no session means no work happens — never fall back to the host.
                throw new InvalidOperationException(
                    $"Sandbox session '{sessionId}' failed to start (exit {result.ExitCode}): {result.StdErr}");
            }

            cancellationToken.ThrowIfCancellationRequested();
        }
        catch
        {
            // Do not inherit the cancelled launch token; teardown has its own deadline.
            await session.StopAsync().ConfigureAwait(false);
            throw;
        }

        RecordProvenance(new SessionProvenanceEvent
        {
            SessionId = sessionId,
            Outcome = SessionProvenanceOutcomes.Started,
            Timestamp = _clock.GetUtcNow(),
            Image = spec.Image,
        });

        return session;
    }

    private void RecordProvenance(SessionProvenanceEvent provenanceEvent)
    {
        if (_provenance is null)
            return;

        try
        {
            _provenance.Record(provenanceEvent);
        }
        catch (Exception ex)
        {
            // Contract mirror of the hot-swap sinks: a provenance sink failure must never
            // fail a session operation.
            _logger?.LogWarning(ex, "Session provenance sink threw; the session operation proceeds");
        }
    }

    /// <summary>
    /// The <c>docker image inspect</c> format string the digest pin resolves through: the
    /// image ID, which is exactly what a running container's <c>{{.Image}}</c> reports.
    /// </summary>
    public const string ImageIdentityFormat = "{{.Id}}";

    /// <summary>
    /// Digest pin, checked BEFORE <c>docker run</c>: resolves the spec's image reference on
    /// the engine and refuses when it is absent (nothing to compare — and <c>--pull never</c>
    /// would refuse the start anyway) or resolves to an identity other than the pin.
    /// Attestation re-checks the started container's own image against the pin, so a tag
    /// retargeted between this probe and the start is still caught before any work runs.
    /// </summary>
    private async Task RefuseUnlessImageMatchesPinAsync(SandboxSpec spec, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(spec.Image))
            throw new ArgumentException("SandboxSpec.Image is required for the Docker backend.", nameof(spec));

        var resolve = await _processRunner
            .RunAsync("docker", new[] { "image", "inspect", spec.Image!, "--format", ImageIdentityFormat }, cancellationToken)
            .ConfigureAwait(false);
        if (!resolve.Succeeded)
        {
            throw new InvalidOperationException(
                $"Session image '{spec.Image}' is pinned to '{_expectedImageDigest}' but cannot be resolved on the "
                + $"engine (image inspect exit {resolve.ExitCode}): {resolve.StdErr}. Refusing to start: an "
                + "unresolvable image cannot be shown to be the pinned one.");
        }

        var resolved = resolve.StdOut.Trim();
        if (!string.Equals(resolved, _expectedImageDigest, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Session image '{spec.Image}' resolves to '{resolved}' but is pinned to '{_expectedImageDigest}'; "
                + "refusing to start a session in an image other than the one the operator pinned.");
        }
    }

    /// <summary>
    /// Builds the <c>docker run -d …</c> argv for a session container. Exposed for unit
    /// tests; not a public contracts surface. <c>--rm</c> is belt-and-braces: if the
    /// keepalive process exits on its own, the engine removes the container without
    /// waiting for teardown or the reaper.
    /// </summary>
    public static IReadOnlyList<string> BuildStartArguments(SandboxSpec spec, string sessionId, long deadlineUnixSeconds)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (string.IsNullOrWhiteSpace(spec.Image))
            throw new ArgumentException("SandboxSpec.Image is required for the Docker backend.", nameof(spec));
        if (spec.Command is null || spec.Command.Count == 0)
            throw new ArgumentException(
                "SandboxSpec.Command must be non-empty: sessions need a keepalive process.", nameof(spec));

        var args = new List<string>
        {
            "run",
            "-d",
            "--rm",
            "--name",
            sessionId,
            "--label",
            SessionLabel,
            "--label",
            $"{DeadlineLabelKey}={deadlineUnixSeconds}"
        };

        DockerSandboxedCommandRunner.AppendSpecArguments(args, spec);

        args.Add(spec.Image!);
        args.AddRange(spec.Command);
        return args;
    }

    /// <summary>Builds the <c>docker exec …</c> argv for one command inside a session.</summary>
    public static IReadOnlyList<string> BuildExecArguments(string sessionId, IReadOnlyList<string> command)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new ArgumentException("Session id is required.", nameof(sessionId));
        if (command is null || command.Count == 0)
            throw new ArgumentException("Command must be non-empty.", nameof(command));

        var args = new List<string> { "exec", sessionId };
        args.AddRange(command);
        return args;
    }

    /// <summary>
    /// The <c>docker inspect</c> format string attestation reads, tab-separated: image
    /// identity, the effective memory/pids/cpu caps, then the containment actually applied
    /// — network mode, read-only rootfs, dropped capabilities, security options (the last
    /// two rendered as Go's default "[a b]"). The containment fields are what the certificate
    /// needs to say "this is how the session was contained" rather than "this is what was asked for".
    /// </summary>
    /// <remarks>
    /// The list fields are deliberately NOT read with the template's <c>join</c> function.
    /// <c>join</c> requires <c>[]string</c> and hard-fails the whole inspect when the CLI
    /// decodes the field as <c>[]interface{}</c> instead — which is exactly what happens to a
    /// container created by an older CLI than the daemon it talks to (reproduced with CLI
    /// 27.5.1 against daemon 29.7.2: "wrong type for value; expected []string; got
    /// []interface {}"). Attestation is the one place that must not be brittle about how the
    /// engine happened to type a field: a template error throws before
    /// <see cref="ParseInspectLine"/> can apply its fail-closed reading, so an unattestable
    /// session became a crashed process rather than a refused iteration. Plain rendering
    /// prints identically for both shapes.
    /// </remarks>
    public const string InspectFormat =
        "{{.Image}}\t{{.HostConfig.Memory}}\t{{.HostConfig.PidsLimit}}\t{{.HostConfig.NanoCpus}}"
        + "\t{{.HostConfig.NetworkMode}}\t{{.HostConfig.ReadonlyRootfs}}"
        + "\t{{.HostConfig.CapDrop}}\t{{.HostConfig.SecurityOpt}}";

    /// <summary>
    /// The engine's network-mode name for "no network" — what <c>--network=none</c> reports
    /// back through inspect.
    /// </summary>
    public const string NoNetworkMode = "none";

    /// <summary>
    /// Parses the <see cref="InspectFormat"/> output line. Unparsable numeric fields
    /// (docker renders unset values as <c>&lt;nil&gt;</c>) come back null — which the
    /// attestation's shortfall check treats as unverified, fail-closed. Missing trailing
    /// fields (an older format, a truncated line) come back null/empty for the same reason:
    /// absent evidence is never read as "contained".
    /// </summary>
    public static SessionInspection ParseInspectLine(string line)
    {
        var parts = (line ?? "").Trim().Split('\t');
        var digest = parts.Length > 0 && !string.IsNullOrWhiteSpace(parts[0]) ? parts[0].Trim() : null;
        return new SessionInspection(
            digest,
            parts.Length > 1 && long.TryParse(parts[1].Trim(), out var mem) ? mem : null,
            parts.Length > 2 && long.TryParse(parts[2].Trim(), out var pids) ? pids : null,
            parts.Length > 3 && long.TryParse(parts[3].Trim(), out var nano) ? nano : null,
            parts.Length > 4 && !string.IsNullOrWhiteSpace(parts[4]) ? parts[4].Trim() : null,
            parts.Length > 5 && bool.TryParse(parts[5].Trim(), out var readOnly) ? readOnly : null,
            parts.Length > 6 ? SplitList(parts[6]) : Array.Empty<string>(),
            parts.Length > 7 ? SplitList(parts[7]) : Array.Empty<string>());

        // Docker renders a string slice as Go's default "[a b]" (and "[]" when empty), whether the CLI
        // decoded it into []string or fell back to []interface{}. Both shapes print identically, which
        // is the point: this parse cannot be broken by how the engine happened to type the field.
        static IReadOnlyList<string> SplitList(string field)
        {
            var trimmed = (field ?? "").Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
                trimmed = trimmed[1..^1];

            return trimmed.Split(
                new[] { ' ', ',' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
    }

    /// <summary>
    /// One parsed <see cref="InspectFormat"/> line: what the engine says the container is.
    /// </summary>
    /// <param name="ImageDigest">Resolved image identity, or null when unreported.</param>
    /// <param name="MemoryBytes">Effective memory cap in bytes; null when unparsable.</param>
    /// <param name="PidsLimit">Effective pids cap; null when unparsable.</param>
    /// <param name="NanoCpus">Effective cpu cap in nano-cpus; null when unparsable.</param>
    /// <param name="NetworkMode">Effective network mode name; null when unreported.</param>
    /// <param name="ReadOnlyRootFilesystem">Whether the rootfs is read-only; null when unreported.</param>
    /// <param name="DroppedCapabilities">Capabilities dropped; empty when none or unreported.</param>
    /// <param name="SecurityOptions">Security options applied; empty when none or unreported.</param>
    public sealed record SessionInspection(
        string? ImageDigest,
        long? MemoryBytes,
        long? PidsLimit,
        long? NanoCpus,
        string? NetworkMode,
        bool? ReadOnlyRootFilesystem,
        IReadOnlyList<string> DroppedCapabilities,
        IReadOnlyList<string> SecurityOptions);

    private sealed class DockerSandboxedSession : IBoundedOutputSandboxedSession
    {
        private readonly SandboxSpec _spec;
        private readonly IProcessCommandRunner _processRunner;
        private readonly TimeProvider _clock;
        private readonly Action<SessionProvenanceEvent> _recordProvenance;
        private readonly ILogger? _logger;
        private readonly string? _expectedImageDigest;
        private int _stopped;

        public DockerSandboxedSession(
            string sessionId,
            SandboxSpec spec,
            IProcessCommandRunner processRunner,
            TimeProvider clock,
            Action<SessionProvenanceEvent> recordProvenance,
            ILogger? logger,
            string? expectedImageDigest)
        {
            SessionId = sessionId;
            _spec = spec;
            _processRunner = processRunner;
            _clock = clock;
            _recordProvenance = recordProvenance;
            _logger = logger;
            _expectedImageDigest = expectedImageDigest;
        }

        public string SessionId { get; }

        public async Task<SessionAttestation> AttestAsync(CancellationToken cancellationToken = default)
        {
            var inspect = await _processRunner
                .RunAsync("docker", new[] { "inspect", SessionId, "--format", InspectFormat }, cancellationToken)
                .ConfigureAwait(false);
            if (!inspect.Succeeded)
            {
                // Fail-closed: certification must never record an unverified environment.
                throw new InvalidOperationException(
                    $"Sandbox session '{SessionId}' cannot be attested (inspect exit {inspect.ExitCode}): {inspect.StdErr}");
            }

            var version = await _processRunner
                .RunAsync("docker", new[] { "version", "--format", "{{.Server.Version}}" }, cancellationToken)
                .ConfigureAwait(false);

            var inspection = ParseInspectLine(inspect.StdOut);

            if (_expectedImageDigest is not null
                && !string.Equals(inspection.ImageDigest, _expectedImageDigest, StringComparison.Ordinal))
            {
                // The pin was checked before start; this closes the window in which the tag
                // could have been retargeted between that probe and the container's start.
                throw new InvalidOperationException(
                    $"Sandbox session '{SessionId}' is running image '{inspection.ImageDigest ?? "<unresolved>"}' "
                    + $"but the session image is pinned to '{_expectedImageDigest}'; refusing to attest a "
                    + "session in an image other than the pinned one.");
            }

            if (_spec.Network == NetworkAccess.None
                && !string.Equals(inspection.NetworkMode, NoNetworkMode, StringComparison.Ordinal))
            {
                // Same fail-closed shape as the resource-cap shortfalls: the spec asked for
                // no network, and an engine that reports anything else (or nothing) has
                // not demonstrably honored it.
                throw new InvalidOperationException(
                    $"Sandbox session '{SessionId}' requested no network but the engine reports network mode "
                    + $"'{inspection.NetworkMode ?? "<unknown>"}'; refusing to attest an environment weaker than requested.");
            }

            var attestation = new SessionAttestation
            {
                SessionId = SessionId,
                Image = _spec.Image ?? "",
                ImageDigest = inspection.ImageDigest,
                EngineVersion = version.Succeeded ? version.StdOut.Trim() : null,
                Requested = _spec.Limits ?? new ResourceLimits(),
                EffectiveMemoryBytes = inspection.MemoryBytes,
                EffectivePidsLimit = inspection.PidsLimit,
                EffectiveNanoCpus = inspection.NanoCpus,
                EffectiveNetworkMode = inspection.NetworkMode,
                EffectiveReadOnlyRootFilesystem = inspection.ReadOnlyRootFilesystem,
                EffectiveDroppedCapabilities = inspection.DroppedCapabilities,
                EffectiveSecurityOptions = inspection.SecurityOptions,
                AttestedAt = _clock.GetUtcNow(),
            };

            _recordProvenance(new SessionProvenanceEvent
            {
                SessionId = SessionId,
                Outcome = SessionProvenanceOutcomes.Attested,
                Timestamp = attestation.AttestedAt,
                Image = attestation.Image,
                ImageDigest = attestation.ImageDigest,
            });

            return attestation;
        }

        public async Task<ProcessCommandResult> ExecAsync(
            IReadOnlyList<string> command,
            CancellationToken cancellationToken = default)
        {
            ThrowIfStopped();
            var args = BuildExecArguments(SessionId, command);
            return await _processRunner.RunAsync("docker", args, cancellationToken).ConfigureAwait(false);
        }

        public async Task<ProcessCommandResult> ExecBoundedAsync(
            IReadOnlyList<string> command,
            int maxOutputBytes,
            CancellationToken cancellationToken = default)
        {
            ThrowIfStopped();
            if (maxOutputBytes < 1 || maxOutputBytes > 16 * 1024 * 1024)
                throw new ArgumentOutOfRangeException(nameof(maxOutputBytes));
            if (_processRunner is not IBoundedOutputProcessCommandRunner boundedProcess)
                throw new NotSupportedException("Isolated tasks require a process runner with bounded host output capture.");

            var args = BuildExecArguments(SessionId, command);
            return await boundedProcess.RunBoundedAsync("docker", args, maxOutputBytes, cancellationToken)
                .ConfigureAwait(false);
        }

        private void ThrowIfStopped()
        {
            if (Volatile.Read(ref _stopped) != 0)
            {
                throw new InvalidOperationException(
                    $"Sandbox session '{SessionId}' has been stopped; executing against a torn-down "
                    + "session must be loud, never silently re-created.");
            }
        }

        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _stopped, 1) != 0)
                return;

            using var deadline = new CancellationTokenSource(TeardownTimeout, _clock);
            using var teardown = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
            try
            {
                var result = await _processRunner
                    .RunAsync("docker", new[] { "rm", "-f", SessionId }, teardown.Token)
                    .ConfigureAwait(false);
                if (!result.Succeeded)
                {
                    // Never throw: teardown failure must not mask the work's own result.
                    // The deadline label + reaper are the backstop for whatever survived.
                    _logger?.LogWarning(
                        "Sandbox session '{SessionId}' teardown returned exit {ExitCode}: {StdErr}",
                        SessionId, result.ExitCode, result.StdErr);
                }

                _recordProvenance(new SessionProvenanceEvent
                {
                    SessionId = SessionId,
                    Outcome = result.Succeeded
                        ? SessionProvenanceOutcomes.Stopped
                        : SessionProvenanceOutcomes.TeardownFailed,
                    Timestamp = _clock.GetUtcNow(),
                    Image = _spec.Image,
                    FailureCode = result.Succeeded ? null : $"exit-{result.ExitCode}",
                    Reason = result.Succeeded ? null : result.StdErr,
                });
            }
            catch (OperationCanceledException ex)
            {
                // A timed-out or explicitly cancelled cleanup must not replace the work's
                // exception/result. The deadline label lets the reaper retry the removal.
                _logger?.LogWarning(ex, "Sandbox session '{SessionId}' teardown cancelled or exceeded {Timeout}",
                    SessionId, TeardownTimeout);
                _recordProvenance(new SessionProvenanceEvent
                {
                    SessionId = SessionId,
                    Outcome = SessionProvenanceOutcomes.TeardownFailed,
                    Timestamp = _clock.GetUtcNow(),
                    Image = _spec.Image,
                    FailureCode = "cancelled",
                    Reason = "Session teardown was cancelled or exceeded its cleanup timeout; the reaper must retry.",
                });
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Sandbox session '{SessionId}' teardown failed", SessionId);
                _recordProvenance(new SessionProvenanceEvent
                {
                    SessionId = SessionId,
                    Outcome = SessionProvenanceOutcomes.TeardownFailed,
                    Timestamp = _clock.GetUtcNow(),
                    Image = _spec.Image,
                    FailureCode = "exception",
                    Reason = ex.Message,
                });
            }
        }

        public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
    }
}
