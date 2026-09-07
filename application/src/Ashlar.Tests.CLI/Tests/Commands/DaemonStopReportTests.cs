using System.Text.Json;
using FluentAssertions;
using Ashlar.CLI.Commands.BackgroundAgent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Ashlar.Tests.CLI.Tests.Commands;

/// <summary>
/// The defect: <c>ashlar background-agent daemon</c> exited 0 and reported
/// <c>ok:true / reason:"duration_elapsed"</c> after the agent service crashed at startup and ran
/// zero cycles.
///
/// <para>When a hosted service faults, .NET's default
/// <c>BackgroundServiceExceptionBehavior.StopHost</c> logs it and tears the host down —
/// <c>host.StartAsync</c> has already returned by then, so the command sat in its duration delay,
/// woke up and reported a clean, complete window. Under <c>--format-json</c> it emitted
/// <c>ok:true / status:running</c> AFTER shutdown had begun, then
/// <c>ok:true / status:stopped / reason:"duration_elapsed"</c>: two false statements about the same
/// two seconds of a fifteen-second window.</para>
///
/// <para>Then the fix for it inverted the defect: "the host stopped itself ⇒ faulted" made every
/// ordinary <c>docker stop</c> of a <c>--duration</c> run report <c>ok:false / status:faulted /
/// reason:host_stopped_early</c>, exit 1, and write "faulted" into the heartbeat the container
/// HEALTHCHECK reads — a working deployment made to look broken, which is the more damaging
/// direction of the same mistake.</para>
///
/// <para><b>What these cover, and why the split matters.</b> The first half pins the JUDGEMENT: a
/// truth table over the four facts a stop is made of, with every answer written out. The second
/// half — everything below "the wiring, against a real host" — pins where those facts COME FROM,
/// against a real <c>HostBuilder</c>: <c>ApplicationStopping</c> after a <c>StopApplication</c>, a
/// <c>BackgroundService</c> that threw, and one merely cancelled by an ordinary shutdown. That
/// second half is the gap this defect lived in. Every earlier round passed the arithmetic; nothing
/// tested the inputs, so a rule that read them wrongly stayed green through two fixes.</para>
/// </summary>
public sealed class DaemonStopReportTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    private static DaemonFaultLog FaultedLog()
    {
        var log = new DaemonFaultLog();
        log.Capture(
            "Ashlar.BackgroundAgents.Services.BackgroundAgentService",
            "Background agent service failed: role 'extendr' is not a known role",
            new InvalidOperationException("role 'extendr' is not a known role"));
        return log;
    }

    // ── the judgement ──────────────────────────────────────────────────────────────────────
    //
    // ConsoleLifetime answers SIGTERM and Ctrl+C by calling StopApplication, so ApplicationStopping
    // fires on an ordinary `docker stop` exactly as it does on a crash: it can never, by itself,
    // mean "faulted". The guard meant to tell them apart was dead code — the handler passed no
    // cancellation token, so it was always CancellationToken.None.

    private static BackgroundAgentDaemonCommand.BoundedStopVerdict Classify(
        BackgroundAgentDaemonCommand.StopObservation stop) =>
        BackgroundAgentDaemonCommand.ClassifyBoundedStop(
            stop.OperatorStopped, stop.HostStopping, stop.ServiceFaulted, stop.ErrorLogged);

    /// <summary>
    /// Every combination of the four inputs, with the answer written out — no formula, so a change
    /// to the rule has to disagree with a stated case rather than with a restatement of itself.
    /// </summary>
    [Theory]
    // nothing happened: the window ran out.
    [InlineData(false, false, false, false, "WindowElapsed")]
    // an error was logged but the host never stopped and no service died. The window still elapsed:
    // a daemon that logs one unreachable peer has not failed.
    [InlineData(false, false, false, true, "WindowElapsed")]
    // THE REPORTED DEFECT. ApplicationStopping fired, nothing failed, nobody was observed asking.
    // This is the shape a `docker stop` produced before the fix, because the token was never
    // cancelled and the signal was never observed. It used to exit 1 / status:faulted.
    [InlineData(false, true, false, false, "CleanStop")]
    // THE ORIGINAL DEFECT. Host down, something logged a failure, nobody asked: a fault, and it
    // must stay one — this used to report ok:true / duration_elapsed.
    [InlineData(false, true, false, true, "Faulted")]
    // a service's execute task ended in an exception. A fault however the rest reads.
    [InlineData(false, false, true, false, "Faulted")]
    [InlineData(false, false, true, true, "Faulted")]
    [InlineData(false, true, true, false, "Faulted")]
    [InlineData(false, true, true, true, "Faulted")]
    // an operator stop, with the host not yet observed stopping (the two race on one signal).
    [InlineData(true, false, false, false, "CleanStop")]
    // THE RE-ARM. An operator stop where something had logged an error earlier in the run. The
    // fault log alone would call this a fault, which is the reported defect wearing a hat: any
    // long-lived node logs an exception eventually, and the next `docker stop` would report
    // status:faulted and write it into the heartbeat.
    [InlineData(true, false, false, true, "CleanStop")]
    [InlineData(true, true, false, false, "CleanStop")]
    [InlineData(true, true, false, true, "CleanStop")]
    // an operator stop does NOT excuse a service that died. Exit non-zero and name it.
    [InlineData(true, false, true, false, "Faulted")]
    [InlineData(true, false, true, true, "Faulted")]
    [InlineData(true, true, true, false, "Faulted")]
    [InlineData(true, true, true, true, "Faulted")]
    public void The_stop_verdict_truth_table_is_complete(
        bool operatorStopped, bool hostStopping, bool serviceFaulted, bool errorLogged, string expected)
    {
        BackgroundAgentDaemonCommand.ClassifyBoundedStop(
            operatorStopped, hostStopping, serviceFaulted, errorLogged)
            .Should().Be(Enum.Parse<BackgroundAgentDaemonCommand.BoundedStopVerdict>(expected));
    }

    [Fact]
    public void An_operator_stop_of_a_bounded_run_is_a_clean_stop_not_a_fault()
    {
        BackgroundAgentDaemonCommand.ClassifyBoundedStop(
            operatorStopped: true, hostStopping: true, serviceFaulted: false, errorLogged: false)
            .Should().Be(BackgroundAgentDaemonCommand.BoundedStopVerdict.CleanStop,
                "a `docker stop` of a --duration run is the same event as a `docker stop` of an unbounded one, "
                + "and the unbounded one has always exited 0 with reason 'shutdown'");
    }

    // ── the wiring, against a real host ────────────────────────────────────────────────────────
    //
    // Everything above this line is arithmetic on booleans, and every previous round of this defect
    // passed it. What was never tested is where the booleans come from — ApplicationStopping, the
    // hosted services, the fault log — so that is what these do, with a real generic host. They use
    // Microsoft's host and nothing of Ashlar's, so they cost milliseconds and cannot go green
    // because some Ashlar service happened to behave.

    /// <summary>Runs until cancelled, like every well-behaved hosted service.</summary>
    private sealed class HealthyService : BackgroundService
    {
        protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
            Task.Delay(Timeout.Infinite, stoppingToken);
    }

    /// <summary>
    /// Fails the way a real one does: after <c>StartAsync</c> has returned, so the host is up and
    /// the command is already inside its duration window when the ground gives way.
    /// </summary>
    private sealed class ThrowingService : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await Task.Yield();
            throw new InvalidOperationException("role 'extendr' is not a known role");
        }
    }

    private static IHost BuildHost(Action<IServiceCollection> configure) =>
        new HostBuilder().ConfigureServices(configure).Build();

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }
            await Task.Delay(10);
        }
        return condition();
    }

    [Fact]
    public async Task An_ordinary_shutdown_of_a_healthy_host_reads_as_a_clean_stop()
    {
        // StopApplication IS what ConsoleLifetime calls when SIGTERM arrives — this is the
        // `docker stop` of a --duration run, minus the signal. No token, no observed signal:
        // exactly the inputs production had, and the ones that used to produce
        // ok:false / status:faulted / reason:host_stopped_early and exit 1.
        using var host = BuildHost(s => s.AddHostedService<HealthyService>());
        await host.StartAsync();
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        lifetime.StopApplication();

        var faults = new DaemonFaultLog();
        using var signal = OperatorStopSignal.Listen();
        var stop = BackgroundAgentDaemonCommand.ObserveStop(
            host.Services, faults, lifetime, signal, CancellationToken.None);

        stop.HostStopping.Should().BeTrue("ApplicationStopping is what wakes the duration window");
        stop.ServiceFaulted.Should().BeFalse("nothing threw");
        stop.ErrorLogged.Should().BeFalse();
        Classify(stop).Should().Be(BackgroundAgentDaemonCommand.BoundedStopVerdict.CleanStop);

        await host.StopAsync();
    }

    [Fact]
    public async Task An_observed_stop_signal_makes_the_stop_clean_even_with_an_error_in_the_log()
    {
        // The hole the previous fix left open. `HasFault` is set by ANY error line carrying an
        // exception — an unreachable mesh peer, a model backend that refused a connection — and a
        // node that runs for weeks will log one. Under the previous rule the next `docker stop`
        // then reported status:faulted, exit 1, and wrote "faulted" into the heartbeat: the
        // reported defect, re-armed by ordinary operation.
        using var host = BuildHost(s => s.AddHostedService<HealthyService>());
        await host.StartAsync();
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();

        var faults = new DaemonFaultLog();
        faults.Capture("Ashlar.CLI.Commands.BackgroundAgent.MeshAutoPullService",
            "peer unreachable", new HttpRequestException("no route to host"));

        using var signal = OperatorStopSignal.Listen();
        signal.Record("SIGTERM");
        lifetime.StopApplication();

        var stop = BackgroundAgentDaemonCommand.ObserveStop(
            host.Services, faults, lifetime, signal, CancellationToken.None);

        stop.OperatorStopped.Should().BeTrue("the signal was delivered to this process");
        stop.ErrorLogged.Should().BeTrue();
        stop.ServiceFaulted.Should().BeFalse("logging an error is not the same as dying");
        Classify(stop).Should().Be(BackgroundAgentDaemonCommand.BoundedStopVerdict.CleanStop);

        await host.StopAsync();
    }

    [Fact]
    public async Task A_cancellation_token_alone_also_makes_the_stop_clean()
    {
        // The other half of operator detection: Ctrl+C reaches System.CommandLine's
        // CancelOnProcessTermination, which cancels the token the handler passes in. SIGTERM does
        // not reach it — ConsoleLifetime suppresses the default termination that would have raised
        // ProcessExit — which is why the signal observer exists as well as this.
        using var host = BuildHost(s => s.AddHostedService<HealthyService>());
        await host.StartAsync();
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var faults = new DaemonFaultLog();
        using var signal = OperatorStopSignal.Listen();
        var stop = BackgroundAgentDaemonCommand.ObserveStop(
            host.Services, faults, lifetime, signal, cts.Token);

        stop.OperatorStopped.Should().BeTrue();
        Classify(stop).Should().Be(BackgroundAgentDaemonCommand.BoundedStopVerdict.CleanStop);

        await host.StopAsync();
    }

    [Fact]
    public async Task A_hosted_service_that_threw_is_found_from_the_service_itself_not_from_the_log()
    {
        // The original defect, end to end and with an EMPTY fault log — so this passes on the
        // structural signal alone. BackgroundServiceExceptionBehavior.StopHost is the default: the
        // service throws, the host tears itself down, and ExecuteTask stays faulted where anyone
        // can read it.
        using var host = BuildHost(s => s.AddHostedService<ThrowingService>());
        await host.StartAsync();
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();

        (await WaitUntilAsync(() => lifetime.ApplicationStopping.IsCancellationRequested, TimeSpan.FromSeconds(10)))
            .Should().BeTrue("the default StopHost behaviour brings the host down when a hosted service throws");

        var faults = new DaemonFaultLog();
        using var signal = OperatorStopSignal.Listen();
        var stop = BackgroundAgentDaemonCommand.ObserveStop(
            host.Services, faults, lifetime, signal, CancellationToken.None);

        stop.ServiceFaulted.Should().BeTrue();
        faults.Service.Should().Contain("ThrowingService", "an operator needs the component that died");
        faults.Reason.Should().Contain("not a known role");
        Classify(stop).Should().Be(BackgroundAgentDaemonCommand.BoundedStopVerdict.Faulted);

        // And even an operator stopping at that moment does not make it clean.
        signal.Record("SIGTERM");
        var stopWithSignal = BackgroundAgentDaemonCommand.ObserveStop(
            host.Services, faults, lifetime, signal, CancellationToken.None);
        Classify(stopWithSignal).Should().Be(BackgroundAgentDaemonCommand.BoundedStopVerdict.Faulted,
            "a service that died did die; exiting 0 because someone also pressed Ctrl+C would hide it");

        await host.StopAsync();
    }

    [Fact]
    public async Task A_service_cancelled_by_an_ordinary_shutdown_is_not_a_fault()
    {
        // The direction that matters most, because getting it wrong makes a working deployment
        // look broken. A BackgroundService whose ExecuteAsync ends on its stopping token completes
        // CANCELLED, not faulted — assert it, because the whole structural signal rests on it.
        using var host = BuildHost(s => s.AddHostedService<HealthyService>());
        await host.StartAsync();
        await host.StopAsync();

        new DaemonFaultLog().CaptureHostedServiceFaults(host.Services).Should().BeFalse(
            "an ordinary shutdown cancels every hosted service, and cancelled is not faulted");
    }

    [Fact]
    public void The_stop_signal_observer_registers_and_disposes_and_records_the_first_signal()
    {
        // Registration must never be the thing that stops a daemon starting: on a platform that
        // refuses one of the signals, Listen() degrades to Observed == false and the token and
        // ApplicationStopping still carry the verdict.
        using var signal = OperatorStopSignal.Listen();
        signal.Observed.Should().BeFalse();
        signal.Name.Should().BeNull();

        signal.Record("SIGTERM");
        signal.Record("SIGINT");
        signal.Observed.Should().BeTrue();
        signal.Name.Should().Be("SIGTERM", "the first stop is the one that explains the shutdown");
    }

    [Fact]
    public void A_clean_stop_reports_ok_true_and_the_same_reason_an_unbounded_run_reports()
    {
        var json = Parse(BackgroundAgentDaemonCommand.StoppedJson(
            "shutdown", cycles: 3, window: TimeSpan.FromMinutes(5), signal: "SIGTERM"));

        json.GetProperty("ok").GetBoolean().Should().BeTrue();
        json.GetProperty("status").GetString().Should().Be("stopped");
        json.GetProperty("reason").GetString().Should().Be("shutdown",
            "the heartbeat and the HEALTHCHECK read this; 'faulted' on an honest stop is a false alarm "
            + "an operator has to chase");
        json.GetProperty("signal").GetString().Should().Be("SIGTERM",
            "a report of a stop should say who stopped it, and this field is also the only external "
            + "evidence that the signal observer works — an assertion nobody can check is how this "
            + "defect survived six rounds");
    }

    [Fact]
    public void A_stop_with_no_signal_observed_says_so_rather_than_naming_one()
    {
        var json = Parse(BackgroundAgentDaemonCommand.StoppedJson(
            "duration_elapsed", cycles: 3, window: TimeSpan.FromMinutes(5)));

        json.GetProperty("signal").ValueKind.Should().Be(JsonValueKind.Null,
            "a window that simply elapsed was not signalled, and inventing SIGTERM would send an "
            + "operator looking for an orchestrator that never acted");
    }

    [Fact]
    public void A_host_that_faulted_is_not_ok_and_names_the_service_and_the_reason()
    {
        var json = Parse(BackgroundAgentDaemonCommand.HostStoppedItselfJson(
            FaultedLog(), ran: TimeSpan.FromSeconds(2), window: TimeSpan.FromSeconds(15), cycles: 0));

        json.GetProperty("ok").GetBoolean().Should().BeFalse(
            "this is the exact reported shape: ok:true over a host that had been dead for 13 of 15 seconds");
        json.GetProperty("status").GetString().Should().Be("faulted");
        json.GetProperty("reason").GetString().Should().Be("background_service_faulted",
            "\"duration_elapsed\" was factually false — the window did not elapse");
        json.GetProperty("service").GetString().Should().Be("Ashlar.BackgroundAgents.Services.BackgroundAgentService");
        json.GetProperty("error").GetString().Should().Contain("not a known role");
        json.GetProperty("cycles").GetInt32().Should().Be(0);
        json.GetProperty("ranSeconds").GetDouble().Should().Be(2);
        json.GetProperty("windowSeconds").GetDouble().Should().Be(15);
    }

    [Fact]
    public void A_host_that_stopped_early_with_no_logged_exception_is_still_not_a_completed_window()
    {
        var json = Parse(BackgroundAgentDaemonCommand.HostStoppedItselfJson(
            new DaemonFaultLog(), ran: TimeSpan.FromSeconds(1), window: TimeSpan.FromSeconds(30), cycles: null));

        json.GetProperty("ok").GetBoolean().Should().BeFalse();
        json.GetProperty("reason").GetString().Should().Be("host_stopped_early");
        json.GetProperty("service").ValueKind.Should().Be(JsonValueKind.Null,
            "inventing a culprit is worse than saying none was logged");
        json.GetProperty("cycles").ValueKind.Should().Be(JsonValueKind.Null,
            "\"I could not count\" must not deserialize as \"nothing ran\"");
    }

    [Fact]
    public void A_window_that_elapsed_but_failed_to_shut_down_says_which_of_the_two_happened()
    {
        // Its own report, not the "host stopped itself" one: that message states the window did
        // not elapse, and here it did. Before this existed the exception escaped to the park
        // handler, where a completed bounded run was announced as a daemon that "failed to start"
        // and then retried forever without ever returning to the operator watching it.
        var json = Parse(BackgroundAgentDaemonCommand.ShutdownFailedJson(
            new TimeoutException("the pattern store did not flush"),
            ran: TimeSpan.FromSeconds(15), window: TimeSpan.FromSeconds(15), cycles: 4));

        json.GetProperty("ok").GetBoolean().Should().BeFalse();
        json.GetProperty("status").GetString().Should().Be("faulted");
        json.GetProperty("reason").GetString().Should().Be("shutdown_failed",
            "\"host_stopped_early\" would be false — the window ran to the end");
        json.GetProperty("error").GetString().Should().Contain("did not flush");
        json.GetProperty("cycles").GetInt32().Should().Be(4,
            "the work that did happen is still reportable");
        json.GetProperty("ranSeconds").GetDouble().Should().Be(15);
    }

    [Fact]
    public void A_completed_window_that_ran_nothing_says_so()
    {
        var json = Parse(BackgroundAgentDaemonCommand.StoppedJson(
            "duration_elapsed", cycles: 0, window: TimeSpan.FromSeconds(15)));

        json.GetProperty("ok").GetBoolean().Should().BeTrue(
            "a short window with no scheduled work is not a failure — but it is not evidence of one either");
        json.GetProperty("cycles").GetInt32().Should().Be(0);
        json.GetProperty("note").GetString().Should().Contain("no agent cycle ran in this window",
            "the reported defect included a valid config that ran zero cycles and reported exactly what a working node does");
        json.GetProperty("note").GetString().Should().Contain("background-agent report",
            "and it names where to look");
    }

    [Fact]
    public void A_completed_window_that_did_work_is_not_annotated()
    {
        var json = Parse(BackgroundAgentDaemonCommand.StoppedJson(
            "duration_elapsed", cycles: 7, window: TimeSpan.FromMinutes(5)));

        json.GetProperty("ok").GetBoolean().Should().BeTrue();
        json.GetProperty("cycles").GetInt32().Should().Be(7);
        json.GetProperty("note").ValueKind.Should().Be(JsonValueKind.Null,
            "an honest run must not be dressed up as suspect — that inversion would be the worse defect");
    }

    [Fact]
    public void An_uncountable_cycle_count_is_reported_as_unknown_not_as_zero()
    {
        BackgroundAgentDaemonCommand.ZeroCycleNote(null).Should().BeNull();
        BackgroundAgentDaemonCommand.DescribeCycles(null).Should().Contain("could not be read");
        BackgroundAgentDaemonCommand.DescribeCycles(0).Should().Contain("ZERO");
        BackgroundAgentDaemonCommand.DescribeCycles(1).Should().Contain("1 agent cycle.");
    }

    [Fact]
    public void The_fault_log_keeps_the_first_exception_and_ignores_error_lines_without_one()
    {
        var log = new DaemonFaultLog();

        log.Capture("Ashlar.Whatever", "an error with no exception", null);
        log.HasFault.Should().BeFalse("a logged error with no exception is not a host fault");

        log.Capture("Ashlar.First", "first", new InvalidOperationException("boom"));
        log.Capture("Ashlar.Second", "second", new InvalidOperationException("cascade"));

        log.Service.Should().Be("Ashlar.First",
            "a fault cascades, and the last message in the cascade is the least informative one");
        log.Reason.Should().Contain("first");
    }

    [Fact]
    public void A_fault_logged_by_the_generic_host_is_attributed_to_the_ashlar_type_that_threw()
    {
        // This is the real shape: BackgroundServiceExceptionBehavior.StopHost logs under
        // Microsoft.Extensions.Hosting.Internal.Host, so the CATEGORY names the host, not the
        // component. The exception's own stack still names the component.
        var log = new DaemonFaultLog();
        Exception captured;
        try
        {
            ThrowFromAnAshlarType();
            return;
        }
        catch (InvalidOperationException ex)
        {
            captured = ex;
        }

        log.Capture("Microsoft.Extensions.Hosting.Internal.Host", "BackgroundService failed", captured);

        log.Service.Should().StartWith("Ashlar.",
            "an operator needs the component, and \"Microsoft.Extensions.Hosting.Internal.Host\" is not one");
    }

    private static void ThrowFromAnAshlarType() =>
        throw new InvalidOperationException("config load failed");
}
