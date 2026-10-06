using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Ashlar.BackgroundAgents.Extending;
using Ashlar.BackgroundAgents.HostRunners;
using Ashlar.BackgroundAgents.Optimization;
using Ashlar.BackgroundAgents.Registry;
using Ashlar.BackgroundAgents.Testing;
using Ashlar.Core.Application.Paths;
using Ashlar.Hosting;
using Ashlar.Hosting.Sdk.Extensions;
using Ashlar.Runtime;
using Ashlar.Transport.Grpc;

namespace Ashlar.CLI.Commands.BackgroundAgent;

/// <summary>
/// Runs a long-lived host process for background agents.
/// This enables daemon-like execution from the CLI entrypoint.
/// </summary>
public sealed class BackgroundAgentDaemonCommand
{
    /// <summary>Creates a new RunAsync instance.</summary>
    public async Task<int> RunAsync(
        string? configPath,
        string? duration,
        string? patternStorePath,
        bool disableObservation,
        bool formatJson,
        CancellationToken cancellationToken = default)
    {
        // PARK, NEVER EXIT.
        //
        // This method's return value reaches Environment.Exit (Program.BackgroundAgentCommands.cs).
        // Under `restart: unless-stopped` a non-zero exit is an UNTHROTTLED CRASHLOOP: Docker
        // restarts immediately, the same precondition fails again, and the loop writes to the disk
        // as fast as the machine allows. On a node left alone for weeks that is how a card dies,
        // and the repo already documents that outcome against itself
        // (deploy/compose/docker-compose.agent-server.yml:66-68).
        //
        // So a failed precondition parks: the reason goes into the heartbeat, the process stays
        // alive, and it re-evaluates on a backoff. Docker does not restart on HEALTHCHECK — it
        // restarts on EXIT only — so a parked node stays up and is reported unhealthy, which is
        // exactly the state an operator can see from `docker ps` and act on.
        var backoff = TimeSpan.FromSeconds(5);
        var maxBackoff = TimeSpan.FromMinutes(1);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                return await RunOnceAsync(
                    configPath, duration, patternStorePath, disableObservation, formatJson, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                WriteStopped(formatJson, timedOut: false);
                return 0;
            }
            catch (NodeParkedException parked)
            {
                Park(parked.Message, formatJson, backoff);
            }
            catch (Exception ex)
            {
                // An unexpected failure is still not a reason to exit into a restart storm. It is
                // a reason to say so, loudly and repeatedly, somewhere an operator will look.
                Park($"daemon failed to start: {ex.Message}", formatJson, backoff);
            }

            try
            {
                await Task.Delay(backoff, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                WriteStopped(formatJson, timedOut: false);
                return 0;
            }

            backoff = backoff < maxBackoff
                ? TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, maxBackoff.Ticks))
                : maxBackoff;
        }

        WriteStopped(formatJson, timedOut: false);
        return 0;
    }

    /// <summary>Raised when a precondition fails in a way that parks the node rather than ending it.</summary>
    private sealed class NodeParkedException(string message) : Exception(message);

    /// <summary>
    /// Records a parked state and says why, on stderr and in the heartbeat. Both matter: the
    /// console is what an operator standing at the machine sees, the heartbeat is what
    /// `docker ps` and the HEALTHCHECK see three weeks later.
    /// </summary>
    private static void Park(string reason, bool formatJson, TimeSpan retryIn)
    {
        new NodeHeartbeat
        {
            Status = "parked",
            Reason = reason,
            UpdatedAt = DateTimeOffset.UtcNow,
            KeyFingerprint = NodeHeartbeat.TryFingerprint(),
            NodeId = NodeHeartbeat.TryFingerprint(),
        }.Write();

        if (formatJson)
        {
            Console.Out.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
            {
                ok = false,
                status = "parked",
                reason,
                retryInSeconds = (int)retryIn.TotalSeconds,
            }));
        }
        else
        {
            Console.Error.WriteLine($"PARKED: {reason}");
            Console.Error.WriteLine($"  the node stays up and reports unhealthy; retrying in {retryIn.TotalSeconds:F0}s");
        }
    }

    private async Task<int> RunOnceAsync(
        string? configPath,
        string? duration,
        string? patternStorePath,
        bool disableObservation,
        bool formatJson,
        CancellationToken cancellationToken)
    {
        {
            var trimmedConfigPath = string.IsNullOrWhiteSpace(configPath) ? null : configPath.Trim();

            // The state directory holds the identity, the mesh store and the heartbeat itself. On a
            // Pi the obvious move is a bind mount to an external SSD, which arrives root-owned and
            // breaks a container running as USER $APP_UID — so name the fix rather than failing with
            // "Access to the path ... is denied" from somewhere deeper.
            var stateDir = RepoPathResolver.ResolveStateDirectory();
            if (!IsWritable(stateDir))
            {
                throw new NodeParkedException(
                    $"state directory is not writable: {stateDir}. The container runs as an unprivileged "
                    + $"user; fix ownership on the host with:  sudo chown -R 1654:1654 <host path mounted at {stateDir}>");
            }

            if (!string.IsNullOrWhiteSpace(trimmedConfigPath) && !File.Exists(trimmedConfigPath))
            {
                // Not necessarily an error: a config on a volume that has not finished mounting is
                // the same symptom, and it resolves itself.
                throw new NodeParkedException($"config file not found: {trimmedConfigPath}");
            }

            if (!TryParseDuration(duration, out var runDuration))
            {
                throw new NodeParkedException(
                    $"invalid --duration value '{duration}'. Use formats like 30s, 5m, or 1h.");
            }

            var builder = Host.CreateDefaultBuilder()
                .ConfigureAppConfiguration((context, config) =>
                {
                    config.Sources.Insert(0, new MemoryConfigurationSource
                    {
                        InitialData = new Dictionary<string, string?>
                        {
                            // Provide safe defaults so daemon mode can run even when host-level
                            // barrier configuration is not explicitly supplied. This source is
                            // inserted first, so later sources — an operator's JSON file, or the
                            // environment — override every value here.
                            ["Ashlar:Barriers:Levels:0"] = "public",
                            ["Ashlar:Barriers:Levels:1"] = "internal",
                            ["Ashlar:Barriers:RequireExplicitBarrier"] = "false",

                            // A daemon is the one host that runs unattended for weeks, so it is
                            // the last place barrier decisions should go unrecorded. Default it
                            // to the structured-log sink rather than leaving the audit log with
                            // nowhere to write.
                            ["Ashlar:Audit:Sinks:0"] = "StructuredLog",

                            // The node capability runtime probes a model backend on startup. With
                            // no backend reachable, HttpClient's Information-level logging emits a
                            // full multi-line connect stack trace per attempt and buries the
                            // daemon's own output. Warning keeps the failure visible without the
                            // trace.
                            ["Logging:LogLevel:System.Net.Http.HttpClient"] = "Warning"
                        }
                    });

                    if (!string.IsNullOrWhiteSpace(trimmedConfigPath))
                    {
                        config.AddJsonFile(trimmedConfigPath!, optional: false, reloadOnChange: true);
                    }
                })
                // Host.CreateDefaultBuilder already wires the console provider; this only switches it
                // to JSON lines when Ashlar:Logging:Json=true or ASHLAR_LOG_JSON=1 (same flag as Ashlar.API).
                .ConfigureLogging((context, logging) =>
                    logging.AddAshlarJsonConsoleIfRequested(context.Configuration))
                .ConfigureServices((context, services) =>
                {
                    services.Configure<GrpcTransportOptions>(
                        context.Configuration.GetSection("Ashlar:GrpcTransport"));
                    services.AddAshlarRuntimeRouting(context.Configuration);

                    // AddAshlarRuntimeRouting does not register audit sinks — AddBarrierAuditSinks
                    // is what reads Ashlar:Audit:Sinks. Without this call the daemon ignored that
                    // section entirely: an operator could configure the File sink and every barrier
                    // audit event would still be discarded, with only a startup warning to say so.
                    services.AddBarrierAuditSinks(context.Configuration);
                    services.AddAshlar(options =>
                    {
                        options.PatternStorePath = string.IsNullOrWhiteSpace(patternStorePath)
                            ? context.Configuration["Ashlar:PatternStorePath"]
                            : patternStorePath;
                        options.RegisterBackgroundAgentHostedService = true;
                        options.DisableObservationPipeline = disableObservation;
                    });

                    // Keep daemon host behavior aligned with the CLI's adapter stack.
                    services.TryAddSingleton<ICodeAnalysisRunner, CodeAnalysisRunnerAdapter>();
                    services.TryAddSingleton<ITestRunRunner, TestRunRunnerAdapter>();
                    services.TryAddSingleton<SelfExtendRunnerAdapter>();
                    services.TryAddSingleton<ISelfExtendRunner>(sp =>
                        sp.GetRequiredService<SelfExtendRunnerAdapter>());

                    // A5 cross-machine sharing (consumer): opt-in mesh auto-pull. OFF unless a shared
                    // folder OR peer URLs are configured, so the default node is unchanged. Trust is
                    // the operator's (`ashlar keys trust <fp>`) — an empty trust set refuses every package.
                    TryAddMeshAutoPull(services);
                    // F1 the LAN party (producer): opt-in read-only serving of this node's published
                    // signed packages, so peers can pull directly — no hub, no shared folder.
                    TryAddMeshServe(services);
                    // F3 zero-config presence: opt-in multicast discovery feeding auto-pull as one
                    // IPeerSource among any others. Presence, never trust.
                    TryAddMeshDiscovery(services);
                });

            using var host = builder.Build();
            await host.StartAsync(cancellationToken).ConfigureAwait(false);
            WriteStarted(formatJson, runDuration, disableObservation);

            // The heartbeat is written on a FIXED timer, never per cycle. ScheduleExecutor ticks
            // once a second, so "every cycle" would be ~86,000 small writes a day added to a card
            // that already carries seven unbounded appenders.
            using var heartbeat = StartHeartbeat(host.Services, cancellationToken);

            if (runDuration.HasValue)
            {
                await Task.Delay(runDuration.Value, cancellationToken).ConfigureAwait(false);
                await host.StopAsync(cancellationToken).ConfigureAwait(false);
                WriteStopped(formatJson, timedOut: true);
                return 0;
            }

            await host.WaitForShutdownAsync(cancellationToken).ConfigureAwait(false);
            WriteStopped(formatJson, timedOut: false);
            return 0;
        }
    }

    /// <summary>Minimum heartbeat interval. Lower would trade real disk life for no new information.</summary>
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Rewrites the status document every <see cref="HeartbeatInterval"/>, and once immediately so
    /// a node is describable the moment it starts rather than a minute later.
    /// </summary>
    private static Timer StartHeartbeat(IServiceProvider services, CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;

        void Beat(object? _)
        {
            if (cancellationToken.IsCancellationRequested)
                return;

            var cycles = 0;
            DateTimeOffset? lastCompleted = null;
            try
            {
                // Reported from the registry rather than counted here, so the number cannot drift
                // from what the agents actually did.
                var registry = services.GetService<IBackgroundAgentRegistry>();
                foreach (var instance in registry?.GetAll() ?? [])
                {
                    cycles += instance.ExecutionCount;
                    if (instance.LastCompletedAt is { } completed
                        && (lastCompleted is null || completed > lastCompleted))
                    {
                        lastCompleted = completed;
                    }
                }
            }
            catch (ObjectDisposedException)
            {
                // Racing a shutdown. The document simply goes stale, which the HEALTHCHECK reads
                // as unhealthy — the correct answer for a node that is going away.
                return;
            }

            var fingerprint = NodeHeartbeat.TryFingerprint();
            new NodeHeartbeat
            {
                Status = "running",
                Reason = null,
                UpdatedAt = DateTimeOffset.UtcNow,
                NodeId = fingerprint,
                KeyFingerprint = fingerprint,
                CyclesSinceStart = cycles,
                LastAdmissionAt = lastCompleted,
            }.Write();

            _ = startedAt;
        }

        return new Timer(Beat, null, TimeSpan.Zero, HeartbeatInterval);
    }

    /// <summary>
    /// Registers the A5 mesh auto-pull hosted service when <c>ASHLAR_MESH_PULL_DIR</c> is set —
    /// otherwise a no-op, so a default node is unchanged. Interval defaults to 300s; the project whose
    /// gate decides pulled packages defaults to <c>&lt;state&gt;/project</c> (the extender's project),
    /// both overridable via <c>ASHLAR_MESH_PULL_INTERVAL_SECONDS</c> / <c>ASHLAR_MESH_PULL_PROJECT</c>.
    /// </summary>
    private static void TryAddMeshAutoPull(IServiceCollection services)
    {
        var pullDir = Environment.GetEnvironmentVariable("ASHLAR_MESH_PULL_DIR");
        // F2: peer base URLs (comma-separated) — any routable address: LAN, a Tailscale/VPN
        // tailnet, the internet. The transport doesn't care and neither does the trust model.
        // Registered as an IPeerSource — the strategy seam more sources (multicast, tailnet,
        // rendezvous) plug into without touching the pull or the gate.
        var peers = (Environment.GetEnvironmentVariable("ASHLAR_MESH_PEERS") ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        var discoveryOn = Environment.GetEnvironmentVariable("ASHLAR_MESH_DISCOVERY") == "1";
        var tailnetOn = Environment.GetEnvironmentVariable("ASHLAR_MESH_TAILNET") == "1";
        if (string.IsNullOrWhiteSpace(pullDir) && peers.Count == 0 && !discoveryOn && !tailnetOn)
        {
            return;   // consumer auto-pull is opt-in
        }

        var interval = 300;
        if (int.TryParse(Environment.GetEnvironmentVariable("ASHLAR_MESH_PULL_INTERVAL_SECONDS"), out var parsed) && parsed > 0)
        {
            interval = parsed;
        }

        var project = Environment.GetEnvironmentVariable("ASHLAR_MESH_PULL_PROJECT");
        if (string.IsNullOrWhiteSpace(project))
        {
            project = Path.Combine(RepoPathResolver.ResolveStateDirectory(), "project");
        }

        if (peers.Count > 0)
        {
            services.AddSingleton<IPeerSource>(new ConfiguredPeerSource(peers));
        }
        if (tailnetOn)
        {
            // Internet-wide P2P over a tailnet — no LAN required. The serve port is the fleet
            // convention (peers serve on the same port); override with ASHLAR_TAILNET_PEER_PORT.
            var tailnetPort = int.TryParse(Environment.GetEnvironmentVariable("ASHLAR_TAILNET_PEER_PORT"), out var tp) && tp is >= 1 and <= 65535
                ? tp
                : (int.TryParse(Environment.GetEnvironmentVariable("ASHLAR_MESH_SERVE_PORT"), out var sp2) && sp2 is >= 1 and <= 65535 ? sp2 : 7420);
            var refresh = int.TryParse(Environment.GetEnvironmentVariable("ASHLAR_TAILNET_REFRESH_SECONDS"), out var rs) && rs > 0
                ? TimeSpan.FromSeconds(rs) : TimeSpan.FromSeconds(30);
            var cmd = Environment.GetEnvironmentVariable("ASHLAR_TAILNET_CMD");
            if (string.IsNullOrWhiteSpace(cmd)) { cmd = "tailscale"; }
            services.AddSingleton<IPeerSource>(sp => new TailnetPeerSource(
                tailnetPort, refresh, cmd!, sp.GetService<ILoggerFactory>()?.CreateLogger<TailnetPeerSource>()));
        }
        services.AddSingleton(new MeshAutoPullSettings(pullDir?.Trim() ?? string.Empty, project, interval));
        services.AddHostedService<MeshAutoPullService>();
    }

    /// <summary>
    /// Registers F3 LAN discovery when <c>ASHLAR_MESH_DISCOVERY=1</c>: the multicast beacon service,
    /// the discovered-peer registry, and a <see cref="MulticastPeerSource"/> feeding auto-pull.
    /// Discovery is presence, not trust — and it is opt-in, so a default node announces nothing.
    /// </summary>
    private static void TryAddMeshDiscovery(IServiceCollection services)
    {
        if (Environment.GetEnvironmentVariable("ASHLAR_MESH_DISCOVERY") != "1")
        {
            return;
        }
        var name = Environment.GetEnvironmentVariable("ASHLAR_NODE_NAME");
        if (string.IsNullOrWhiteSpace(name))
        {
            name = Environment.MachineName;
        }
        string? fingerprint = null;
        try { fingerprint = Ashlar.Manifest.Signing.OperatorKey.TryLoad()?.Fingerprint; }
        catch { /* corrupt key: announce nothing, still listen */ }
        int? servePort = int.TryParse(Environment.GetEnvironmentVariable("ASHLAR_MESH_SERVE_PORT"), out var sp) && sp is >= 1 and <= 65535
            ? sp : null;

        services.AddSingleton<MeshDiscoveryRegistry>();
        services.AddSingleton(new MeshDiscoverySettings(name!, fingerprint, servePort, RepoPathResolver.ResolveStateDirectory()));
        services.AddHostedService<MeshDiscoveryService>();
        services.AddSingleton<IPeerSource, MulticastPeerSource>();
    }

    /// <summary>
    /// Registers the F1 mesh-serve hosted service when <c>ASHLAR_MESH_SERVE_PORT</c> is set — otherwise
    /// a no-op, so a default node exposes nothing. Serves the node's own published dir (the same one
    /// auto-share writes to), read-only. The announced name is <c>ASHLAR_NODE_NAME</c> or the machine name.
    /// </summary>
    private static void TryAddMeshServe(IServiceCollection services)
    {
        if (!int.TryParse(Environment.GetEnvironmentVariable("ASHLAR_MESH_SERVE_PORT"), out var port) || port is < 1 or > 65535)
        {
            return;   // serving is opt-in
        }
        var name = Environment.GetEnvironmentVariable("ASHLAR_NODE_NAME");
        if (string.IsNullOrWhiteSpace(name))
        {
            name = Environment.MachineName;
        }
        // Optional TLS/mTLS for a private fleet. A cert path with a key makes the endpoint HTTPS;
        // requiring a client cert (+ a CA to validate it against) makes it mutual TLS.
        var tlsCert = Environment.GetEnvironmentVariable("ASHLAR_MESH_SERVE_TLS_CERT");
        var tlsKey = Environment.GetEnvironmentVariable("ASHLAR_MESH_SERVE_TLS_KEY");
        var requireClient = Environment.GetEnvironmentVariable("ASHLAR_MESH_SERVE_REQUIRE_CLIENT_CERT") == "1";
        var ca = Environment.GetEnvironmentVariable("ASHLAR_MESH_SERVE_CA");
        var bind = Environment.GetEnvironmentVariable("ASHLAR_MESH_SERVE_BIND");
        services.AddSingleton(new MeshServeSettings(
            port, Ashlar.Manifest.Packaging.MeshStore.Resolve(null), name!,
            string.IsNullOrWhiteSpace(tlsCert) ? null : tlsCert,
            string.IsNullOrWhiteSpace(tlsKey) ? null : tlsKey,
            requireClient,
            string.IsNullOrWhiteSpace(ca) ? null : ca,
            string.IsNullOrWhiteSpace(bind) ? null : bind.Trim()));
        services.AddHostedService<MeshServeService>();
    }

    /// <summary>
    /// Probes the state directory by actually writing to it. Checking existence or permission bits
    /// would miss the case that matters — a root-owned bind mount, where the directory is present
    /// and readable and simply not ours.
    /// </summary>
    private static bool IsWritable(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, $".writable-probe-{Guid.NewGuid():N}");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool TryParseDuration(string? value, out TimeSpan? duration)
    {
        duration = null;
        if (string.IsNullOrWhiteSpace(value))
            return true;

        var input = value.Trim();
        if (input.Length < 2)
            return false;

        var unit = input[^1];
        if (!double.TryParse(input[..^1], out var magnitude) || magnitude <= 0)
            return false;

        duration = unit switch
        {
            's' or 'S' => TimeSpan.FromSeconds(magnitude),
            'm' or 'M' => TimeSpan.FromMinutes(magnitude),
            'h' or 'H' => TimeSpan.FromHours(magnitude),
            'd' or 'D' => TimeSpan.FromDays(magnitude),
            _ => (TimeSpan?)null
        };

        return duration.HasValue;
    }

    private static void WriteStarted(bool formatJson, TimeSpan? runDuration, bool disableObservation)
    {
        if (formatJson)
        {
            Console.Out.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
            {
                ok = true,
                status = "running",
                observation = disableObservation ? "disabled" : "enabled",
                duration = runDuration?.ToString()
            }));
            return;
        }

        var durationText = runDuration.HasValue
            ? $" for {runDuration.Value}"
            : " until Ctrl+C";
        Console.Out.WriteLine($"Background-agent daemon started{durationText}.");
    }

    private static void WriteStopped(bool formatJson, bool timedOut)
    {
        if (formatJson)
        {
            Console.Out.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
            {
                ok = true,
                status = "stopped",
                reason = timedOut ? "duration_elapsed" : "shutdown"
            }));
            return;
        }

        Console.Out.WriteLine("Background-agent daemon stopped.");
    }

    private static int WriteError(bool formatJson, string error)
    {
        if (formatJson)
        {
            Console.Out.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
            {
                ok = false,
                error
            }));
        }
        else
        {
            Console.Error.WriteLine(error);
        }

        return 1;
    }
}
