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

        // Owned here, not per attempt: the question "did an operator stop this process?" outlives
        // any one host, and re-registering signal handlers on every park retry would be churn for
        // no answer. Observational only — it never suppresses the default signal action, so a
        // parked node still dies on `docker stop` exactly as it does today.
        using var operatorSignal = OperatorStopSignal.Listen();

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                return await RunOnceAsync(
                    configPath, duration, patternStorePath, disableObservation, formatJson,
                    operatorSignal, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                WriteStopped(formatJson, reason: "shutdown", cycles: null, window: null, operatorSignal.Name);
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
                //
                // The wording is deliberately not "failed to start": this catch also sees a host
                // that started, ran its whole window, and then threw on the way down, and telling
                // an operator to go and look at start-up for a shutdown fault sends them to the
                // wrong end of the log.
                Park($"the daemon host failed: {ex.GetType().Name}: {ex.Message}", formatJson, backoff);
            }

            try
            {
                await Task.Delay(backoff, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                WriteStopped(formatJson, reason: "shutdown", cycles: null, window: null, operatorSignal.Name);
                return 0;
            }

            backoff = backoff < maxBackoff
                ? TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, maxBackoff.Ticks))
                : maxBackoff;
        }

        WriteStopped(formatJson, reason: "shutdown", cycles: null, window: null, operatorSignal.Name);
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
        OperatorStopSignal operatorSignal,
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
                // Parked, not exited — see the contract at the top of RunAsync. But unlike a
                // config file that has not finished mounting, argv cannot change while this process
                // lives, so retrying will fail identically forever. Say that, and say how to stop:
                // an operator who does not know this is a park will sit watching a command that
                // never returns.
                throw new NodeParkedException(
                    $"invalid --duration value '{duration}'. Use formats like 30s, 5m, or 1h. "
                    + "This one will not fix itself: the value comes from the command line, which does not "
                    + "change while the process runs, so the node will park on it until you stop it. "
                    + "Stop this process (Ctrl+C, or `docker stop`), correct --duration, and start it again; "
                    + "omit --duration entirely to run until stopped.");
            }

            var faultLog = new DaemonFaultLog();

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
                {
                    logging.AddAshlarJsonConsoleIfRequested(context.Configuration);
                    // Observe faults, do not merely print them. A hosted service that throws is
                    // logged and then stops the host; this command used to notice neither, and
                    // reported ok:true / "duration_elapsed" over a host that had been dead for
                    // most of the window. The filter is explicit so an operator's log
                    // configuration can quieten the console without also blinding the report.
                    logging.AddProvider(new DaemonFaultLoggerProvider(faultLog));
                    logging.AddFilter<DaemonFaultLoggerProvider>(level => level >= LogLevel.Error);
                })
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

            // The host stops for three different reasons and this command used to report all three
            // as the same clean exit. ApplicationStopping tells them apart: the operator cancelled
            // (cancellationToken), the window closed (Task.Delay won), or the host tore ITSELF down
            // because a hosted service faulted — the case that printed ok:true after running ~2s of
            // a 15s window with zero cycles.
            var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();

            if (runDuration.HasValue)
            {
                var ran = System.Diagnostics.Stopwatch.StartNew();
                using (var window = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken, lifetime.ApplicationStopping))
                {
                    // Wait through a TaskCompletionSource whose continuations are FORCED onto the
                    // thread pool, rather than awaiting the cancellable Task.Delay directly.
                    //
                    // ConsoleLifetime answers a stop signal by calling StopApplication FROM ITS
                    // SIGNAL HANDLER, and StopApplication runs its ApplicationStopping callbacks
                    // synchronously on the calling thread. Whether the wait below then resumes
                    // inline on that thread is a scheduling detail nothing here controls — and if
                    // it ever does, the rest of this method runs inside the signal handler, ending
                    // in host.Dispose(), which disposes ConsoleLifetime's PosixSignalRegistrations;
                    // PosixSignalRegistration.Dispose waits for its own handler to return, so that
                    // is a self-deadlock whose symptom is a daemon that has to be SIGKILLed. This
                    // is not a bug that was observed here — it is one that cannot happen once the
                    // resumption is guaranteed asynchronous, which is the same reason Microsoft's
                    // own WaitForShutdownAsync builds its TaskCompletionSource this way. The
                    // unbounded branch below inherits that guarantee from WaitForShutdownAsync.
                    var woken = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    using var wakeOnStop = window.Token.Register(
                        static state => ((TaskCompletionSource)state!).TrySetResult(), woken);
                    using var wakeOnWindowClosing = new Timer(
                        static state => ((TaskCompletionSource)state!).TrySetResult(), woken,
                        runDuration.Value, Timeout.InfiniteTimeSpan);
                    await woken.Task.ConfigureAwait(false);
                }
                ran.Stop();
                await SilenceHeartbeatAsync(heartbeat).ConfigureAwait(false);

                // Everything the verdict is made of is read HERE, before StopAsync — because
                // StopAsync changes three of the four. It cancels every hosted service (turning
                // "still running" into "cancelled"), it fires ApplicationStopping itself, and it
                // disposes the registry the cycle count comes from.
                var stop = ObserveStop(host.Services, faultLog, lifetime, operatorSignal, cancellationToken);
                var cycles = CountCycles(host.Services);

                // Not cancellationToken: the window is over either way, and stopping must not be
                // skipped just because the reason for stopping was a fault. A throw here is its
                // own, separately reported failure — folding it into "the host stopped itself"
                // would report a window that fully elapsed as one that did not, and letting it
                // escape would park a completed run forever under a message about starting up.
                try
                {
                    await host.StopAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    return WriteShutdownFailed(formatJson, ex, ran.Elapsed, runDuration.Value, cycles);
                }

                switch (ClassifyBoundedStop(
                    stop.OperatorStopped, stop.HostStopping, stop.ServiceFaulted, stop.ErrorLogged))
                {
                    case BoundedStopVerdict.Faulted:
                        return WriteHostStoppedItself(formatJson, faultLog, ran.Elapsed, runDuration.Value, cycles);
                    case BoundedStopVerdict.CleanStop:
                        // The same reason string an UNBOUNDED run already writes for the same
                        // signal. A bounded run and an unbounded run stopped the same way must not
                        // disagree about what happened, and they did: 0/"shutdown" against
                        // 1/"faulted".
                        WriteStopped(formatJson, reason: "shutdown", cycles, runDuration.Value, operatorSignal.Name);
                        return 0;
                    default:
                        WriteStopped(formatJson, reason: "duration_elapsed", cycles, runDuration.Value);
                        return 0;
                }
            }

            await host.WaitForShutdownAsync(cancellationToken).ConfigureAwait(false);
            await SilenceHeartbeatAsync(heartbeat).ConfigureAwait(false);
            // The same judgement as the bounded branch, from the same four inputs. Two code paths
            // that answer "was this stop a failure?" differently is the shape of the reported
            // defect itself: one signal, two runs, opposite verdicts. The only thing that differs
            // below is what a fault DOES — the bounded run exits, the unbounded run parks.
            var shutdownStop = ObserveStop(host.Services, faultLog, lifetime, operatorSignal, cancellationToken);
            var shutdownCycles = CountCycles(host.Services);
            if (ClassifyBoundedStop(
                shutdownStop.OperatorStopped, shutdownStop.HostStopping,
                shutdownStop.ServiceFaulted, shutdownStop.ErrorLogged) == BoundedStopVerdict.Faulted)
            {
                // PARK, NEVER EXIT still holds for the unbounded run — this is the shape that runs
                // under `restart: unless-stopped`, where a non-zero exit is an untrottled
                // crashloop. Parking is not a quiet success: it writes ok:false and the reason into
                // both the console and the heartbeat, and retries on a backoff. The bounded run
                // above is the one an operator is watching, and that one exits non-zero.
                throw new NodeParkedException(
                    $"the background-agent host stopped itself: {faultLog.Service} failed — {faultLog.Reason}. "
                    + $"{DescribeCycles(shutdownCycles)} Fix the cause (check the config passed to --config, and "
                    + "`ashlar background-agent report` for the node's own view), then the node recovers on the "
                    + "next retry without a restart.");
            }
            WriteStopped(formatJson, reason: "shutdown", shutdownCycles, window: null, operatorSignal.Name);
            return 0;
        }
    }

    /// <summary>The four facts a stop verdict is made of, gathered at one instant.</summary>
    /// <param name="OperatorStopped">A stop signal reached this process, or its token was cancelled.</param>
    /// <param name="HostStopping">The host's <c>ApplicationStopping</c> has fired.</param>
    /// <param name="ServiceFaulted">A hosted service's execute task ended in an exception.</param>
    /// <param name="ErrorLogged">Some component logged an error carrying an exception.</param>
    internal readonly record struct StopObservation(
        bool OperatorStopped, bool HostStopping, bool ServiceFaulted, bool ErrorLogged);

    /// <summary>
    /// Reads the four facts, in the one order that is safe: the hosted services first, while they
    /// are still in the state the stop found them in.
    /// </summary>
    internal static StopObservation ObserveStop(
        IServiceProvider services,
        DaemonFaultLog faultLog,
        IHostApplicationLifetime lifetime,
        OperatorStopSignal operatorSignal,
        CancellationToken cancellationToken)
    {
        var serviceFaulted = faultLog.CaptureHostedServiceFaults(services);
        return new StopObservation(
            // Two independent ways to learn the same thing, because each one has a hole. The token
            // is cancelled by System.CommandLine's CancelOnProcessTermination — which fires on
            // Ctrl+C, but NOT on the SIGTERM that `docker stop` sends, because ConsoleLifetime
            // suppresses the default termination that would have raised ProcessExit. The signal
            // observer covers exactly that hole, and the token covers the platforms where the
            // observer could not register.
            OperatorStopped: cancellationToken.IsCancellationRequested || operatorSignal.Observed,
            HostStopping: lifetime.ApplicationStopping.IsCancellationRequested,
            ServiceFaulted: serviceFaulted,
            ErrorLogged: faultLog.HasFault);
    }

    /// <summary>What a bounded run that ended before its window did actually was.</summary>
    internal enum BoundedStopVerdict
    {
        /// <summary>The window closed on its own.</summary>
        WindowElapsed,

        /// <summary>Someone stopped it, and nothing failed. Exit 0, status "stopped".</summary>
        CleanStop,

        /// <summary>The host tore itself down after something logged a failure. Exit 1.</summary>
        Faulted,
    }

    /// <summary>
    /// The whole judgement of a stop, as a function of four booleans — so the rule can be tested
    /// without standing up a host, which is why it got this wrong for as long as it did.
    /// </summary>
    /// <remarks>
    /// <para>The rule that was here read "ApplicationStopping fired ⇒ faulted". It is not:
    /// <c>ConsoleLifetime</c> answers SIGTERM and Ctrl+C by calling <c>StopApplication</c>, so an
    /// ordinary <c>docker stop</c> raises exactly the same signal a crashing hosted service does.
    /// Measured: an unbounded run + SIGTERM exited 0 with reason "shutdown"; the same daemon with
    /// <c>--duration</c> + SIGTERM exited 1 with <c>ok:false / status:faulted /
    /// reason:host_stopped_early</c>, and wrote "faulted" into the heartbeat the container
    /// HEALTHCHECK reads. Two runs, one signal, opposite verdicts — and the wrong one was the one
    /// an operator sees, because the bounded run is the one they watch.</para>
    ///
    /// <para>A fault is a fault because something FAILED, not because the host stopped. The order
    /// of the tests below is the whole rule, and each rung exists because the one under it is not
    /// trustworthy on its own:</para>
    /// <list type="number">
    /// <item><paramref name="serviceFaulted"/> — a hosted service's execute task ended in an
    /// exception. Structural, not reported: nothing else sets it, and a service cancelled by an
    /// ordinary shutdown ends cancelled rather than faulted. It outranks even an operator stop,
    /// because a service that died did die, and a run that ends on a dead service must exit
    /// non-zero and name it.</item>
    /// <item><paramref name="operatorStopped"/> — a stop signal was delivered, or the command's
    /// token was cancelled. Positive evidence that a person asked for this, and therefore the end
    /// of the question: nothing that gets logged during a shutdown someone asked for turns it into
    /// a failure. This rung is what stops <paramref name="errorLogged"/> re-arming the reported
    /// defect, because <c>HasFault</c> is set by ANY error line carrying an exception — one
    /// unreachable mesh peer is enough.</item>
    /// <item><paramref name="hostStopping"/> with <paramref name="errorLogged"/> — nobody asked,
    /// the host went down anyway, and something had logged a failure. Not proof, but the best
    /// available reading, and it keeps the ORIGINAL defect closed (a service that crashed at
    /// start-up used to report <c>ok:true / duration_elapsed</c>).</item>
    /// <item><paramref name="hostStopping"/> alone — asked to stop, nothing failed. Clean.</item>
    /// </list>
    /// </remarks>
    /// <param name="operatorStopped">A stop signal reached the process, or its token was cancelled.</param>
    /// <param name="hostStopping">The host's <c>ApplicationStopping</c> had fired.</param>
    /// <param name="serviceFaulted">A hosted service's execute task ended in an exception.</param>
    /// <param name="errorLogged">Something logged an error with an exception attached.</param>
    internal static BoundedStopVerdict ClassifyBoundedStop(
        bool operatorStopped, bool hostStopping, bool serviceFaulted, bool errorLogged) =>
        serviceFaulted ? BoundedStopVerdict.Faulted
        : operatorStopped ? BoundedStopVerdict.CleanStop
        : hostStopping && errorLogged ? BoundedStopVerdict.Faulted
        : hostStopping ? BoundedStopVerdict.CleanStop
        : BoundedStopVerdict.WindowElapsed;

    /// <summary>
    /// Total cycles the registered agents have executed, or null when the registry cannot be read.
    /// Null, never 0: "I could not count" and "nothing ran" are different facts, and reporting the
    /// first as the second is the same class of defect as reporting a crash as a clean exit.
    /// </summary>
    private static int? CountCycles(IServiceProvider services)
    {
        try
        {
            var registry = services.GetService<IBackgroundAgentRegistry>();
            if (registry is null)
            {
                return null;
            }
            return registry.GetAll().Sum(instance => instance.ExecutionCount);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            return null;
        }
    }

    internal static string DescribeCycles(int? cycles) => cycles switch
    {
        null => "The cycle count could not be read.",
        0 => "It ran ZERO agent cycles.",
        1 => "It ran 1 agent cycle.",
        _ => $"It ran {cycles} agent cycles.",
    };

    /// <summary>
    /// The machine-readable report for a host that stopped itself. Separated from the writing so
    /// the JUDGEMENT — ok:false, which service, which reason, how many cycles — is testable without
    /// standing up a host, and so the text and JSON forms cannot drift apart.
    /// </summary>
    internal static string HostStoppedItselfJson(DaemonFaultLog faults, TimeSpan ran, TimeSpan window, int? cycles)
    {
        var faulted = faults.HasFault;
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            ok = false,
            status = "faulted",
            reason = faulted ? "background_service_faulted" : "host_stopped_early",
            service = faulted ? faults.Service : null,
            error = faulted ? faults.Reason : null,
            cycles,
            ranSeconds = Math.Round(ran.TotalSeconds, 1),
            windowSeconds = Math.Round(window.TotalSeconds, 1),
        });
    }

    /// <summary>
    /// The machine-readable report for a clean stop. Carries the cycle count, because "the window
    /// elapsed" was never evidence that anything happened inside it — and the stop signal, because
    /// "who stopped this?" is the question a report of a stop should answer.
    /// </summary>
    /// <param name="reason">"shutdown" or "duration_elapsed".</param>
    /// <param name="cycles">Agent cycles run, or null when the count could not be read.</param>
    /// <param name="window">The <c>--duration</c> window, or null for an unbounded run.</param>
    /// <param name="signal">
    /// The stop signal observed (e.g. <c>SIGTERM</c>), or null when none was — an unbounded run
    /// whose host was asked to stop from inside, a window that simply elapsed, or a platform where
    /// the signal could not be observed. Null is honest and never invented: this field is also the
    /// only externally visible evidence that the observer works at all, which is how the fix for
    /// this defect stays measurable rather than merely asserted.
    /// </param>
    internal static string StoppedJson(string reason, int? cycles, TimeSpan? window, string? signal = null) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            ok = true,
            status = "stopped",
            reason,
            signal,
            cycles,
            windowSeconds = window.HasValue ? Math.Round(window.Value.TotalSeconds, 1) : (double?)null,
            note = ZeroCycleNote(cycles),
        });

    /// <summary>What to add when a window closed with nothing having run. Null when cycles ran or
    /// could not be counted — an honest run is not annotated as if it were suspect.</summary>
    internal static string? ZeroCycleNote(int? cycles) => cycles == 0
        ? "no agent cycle ran in this window. that is not necessarily a fault — a window shorter than the "
          + "agents' schedules produces it — but nothing here is evidence that the node does any work. "
          + "run for longer, or check `ashlar background-agent report` for what is registered."
        : null;

    /// <summary>
    /// The report for a bounded run whose host tore itself down before the window closed. Exits
    /// non-zero and names the service and the reason: a daemon that crashed at startup and ran no
    /// cycles used to print <c>ok:true / status:running</c> AFTER shutdown had begun, then
    /// <c>ok:true / reason:"duration_elapsed"</c> — two false statements about the same 2 seconds.
    /// </summary>
    private static int WriteHostStoppedItself(
        bool formatJson, DaemonFaultLog faults, TimeSpan ran, TimeSpan window, int? cycles)
    {
        var faulted = faults.HasFault;
        var reason = faulted ? "background_service_faulted" : "host_stopped_early";

        new NodeHeartbeat
        {
            Status = "faulted",
            Reason = faulted ? $"{faults.Service}: {faults.Reason}" : "the host stopped before the run window elapsed",
            UpdatedAt = DateTimeOffset.UtcNow,
            KeyFingerprint = NodeHeartbeat.TryFingerprint(),
            NodeId = NodeHeartbeat.TryFingerprint(),
            CyclesSinceStart = cycles ?? 0,
        }.Write();

        if (formatJson)
        {
            Console.Out.WriteLine(HostStoppedItselfJson(faults, ran, window, cycles));
            return 1;
        }

        // A service can die without the window ending — the host's fault handling and this
        // command's clock are not the same clock, and BackgroundServiceExceptionBehavior can be
        // configured not to stop the host at all. Saying "stopped itself after 15.0s of a 15.0s
        // window" in that case is a false sentence, and a false sentence in a failure message is
        // the thing an operator chases first.
        var stoppedEarly = ran < window;
        Console.Error.WriteLine(stoppedEarly
            ? $"FAILED: the background-agent host stopped itself after {ran.TotalSeconds:F1}s of a "
              + $"{window.TotalSeconds:F1}s window."
            : $"FAILED: a background-agent component failed during the {window.TotalSeconds:F1}s window.");
        if (faulted)
        {
            Console.Error.WriteLine($"  service: {faults.Service}");
            Console.Error.WriteLine($"  reason:  {faults.Reason}");
        }
        else
        {
            Console.Error.WriteLine("  no component logged an exception — something asked the host to shut down.");
        }
        Console.Error.WriteLine($"  {DescribeCycles(cycles)}");
        Console.Error.WriteLine(stoppedEarly
            ? "  this exits non-zero on purpose: the window did not elapse, so \"duration_elapsed\" would be false."
            : "  this exits non-zero on purpose: the window elapsed, but reporting it as clean would hide the failure above.");
        Console.Error.WriteLine("  fix, in order:");
        Console.Error.WriteLine("  - the config passed to --config: a value the service rejects at load faults it at startup.");
        Console.Error.WriteLine("  - re-run with ASHLAR_LOG_JSON=1 (or Ashlar:Logging:Json=true) for the full structured log.");
        Console.Error.WriteLine("  - `ashlar background-agent report` for the node's own view once it is up.");
        return 1;
    }

    /// <summary>
    /// The report for a run whose window finished but whose host threw on the way down.
    ///
    /// <para>It has its own message for one reason: it is not the failure next to it. Folding it
    /// into "the host stopped itself before the window elapsed" states something false about a
    /// window that did elapse, and letting the exception escape instead sent it to the park handler
    /// — where a completed bounded run was reported as a daemon that "failed to start" and then
    /// retried forever, never returning to the operator watching it.</para>
    /// </summary>
    private static int WriteShutdownFailed(
        bool formatJson, Exception error, TimeSpan ran, TimeSpan window, int? cycles)
    {
        new NodeHeartbeat
        {
            Status = "faulted",
            Reason = $"shutdown failed: {error.GetType().Name}: {error.Message}",
            UpdatedAt = DateTimeOffset.UtcNow,
            KeyFingerprint = NodeHeartbeat.TryFingerprint(),
            NodeId = NodeHeartbeat.TryFingerprint(),
            CyclesSinceStart = cycles ?? 0,
        }.Write();

        if (formatJson)
        {
            Console.Out.WriteLine(ShutdownFailedJson(error, ran, window, cycles));
            return 1;
        }

        Console.Error.WriteLine(
            $"FAILED: the run finished ({ran.TotalSeconds:F1}s of a {window.TotalSeconds:F1}s window) but "
            + "shutting the host down threw.");
        Console.Error.WriteLine($"  error: {error.GetType().Name}: {error.Message}");
        Console.Error.WriteLine($"  {DescribeCycles(cycles)}");
        Console.Error.WriteLine("  the work in the window may well have been fine; what failed is the stop, so anything");
        Console.Error.WriteLine("  a service flushes on shutdown may not have been written.");
        Console.Error.WriteLine("  fix, in order:");
        Console.Error.WriteLine("  - re-run with ASHLAR_LOG_JSON=1 (or Ashlar:Logging:Json=true): the failing StopAsync logs under its own service.");
        Console.Error.WriteLine("  - `ashlar background-agent report` for what the node did record.");
        return 1;
    }

    /// <summary>The machine-readable form of <see cref="WriteShutdownFailed"/>, separated so the
    /// judgement is testable and the two forms cannot drift.</summary>
    internal static string ShutdownFailedJson(Exception error, TimeSpan ran, TimeSpan window, int? cycles) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            ok = false,
            status = "faulted",
            reason = "shutdown_failed",
            service = (string?)null,
            error = $"{error.GetType().Name}: {error.Message}",
            cycles,
            ranSeconds = Math.Round(ran.TotalSeconds, 1),
            windowSeconds = Math.Round(window.TotalSeconds, 1),
        });

    /// <summary>
    /// Stops the heartbeat timer and WAITS for any beat already in flight, before anything writes
    /// the run's final status.
    ///
    /// <para>Without this the two race, and the heartbeat wins often enough to matter: measured, a
    /// bounded run whose agent config faulted the service in its first second exited 1 naming the
    /// service — and left <c>"status": "running"</c> in the heartbeat, because the timer's first
    /// beat (it fires immediately) landed after the fault report. The container HEALTHCHECK reads
    /// that file and nothing else, so a node whose daemon had just died reported healthy. That is
    /// the reported defect's own direction reversed and made worse: a broken deployment that looks
    /// like a working one.</para>
    ///
    /// <para><see cref="Timer.DisposeAsync"/> rather than <c>Dispose</c> because only the async form
    /// completes after a callback that is already running — plain <c>Dispose</c> would return while
    /// a beat was still on its way to the file.</para>
    /// </summary>
    private static async Task SilenceHeartbeatAsync(Timer heartbeat)
    {
        try
        {
            await heartbeat.DisposeAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Already stopped; the point was that it is not beating, and it is not.
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
        services.AddSingleton(new MeshServeSettings(
            port, Ashlar.Manifest.Packaging.MeshStore.Resolve(null), name!,
            string.IsNullOrWhiteSpace(tlsCert) ? null : tlsCert,
            string.IsNullOrWhiteSpace(tlsKey) ? null : tlsKey,
            requireClient,
            string.IsNullOrWhiteSpace(ca) ? null : ca));
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

    /// <summary>
    /// The clean-stop report. <paramref name="cycles"/> travels with it because "the window
    /// elapsed" and "anything happened during it" are different claims, and only the first one was
    /// ever printed — a node that ran zero cycles for fifteen seconds reported exactly what a
    /// working one did.
    /// </summary>
    private static void WriteStopped(
        bool formatJson, string reason, int? cycles, TimeSpan? window, string? signal = null)
    {
        if (formatJson)
        {
            Console.Out.WriteLine(StoppedJson(reason, cycles, window, signal));
            return;
        }

        var by = signal is null ? string.Empty : $" on {signal}";
        Console.Out.WriteLine($"Background-agent daemon stopped{by}. {DescribeCycles(cycles)}");
        if (ZeroCycleNote(cycles) is { } note)
        {
            Console.Out.WriteLine($"  {note}");
        }
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
