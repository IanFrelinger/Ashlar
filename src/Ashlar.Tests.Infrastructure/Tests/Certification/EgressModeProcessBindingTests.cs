using System.Collections.Concurrent;
using Ashlar.Abstractions.Security.Egress;
using Ashlar.Hosting;
using Ashlar.Infrastructure.Egress;
using Ashlar.Tests.Infrastructure.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>
/// SPEC-007 PR 4.6, the two bindings of the egress mode and the process state behind them: the process binding
/// (<see cref="EgressGuard.ProcessDefault"/>, which the explicit sites call) and the composition binding (the guard
/// <c>AddAshlar</c> registers for factory clients and MEAI targets).
/// </summary>
/// <remarks>
/// <para><b>What is pinned.</b> The override variable is read once per process, by <c>AddAshlar</c> or at the first
/// decision, and a later change to it does nothing; <see cref="AshlarHostingOptions.EgressMode"/> can only raise the
/// mode; an unrecognised mode value, an unrecognised profile and a fault while resolving the mode all fail closed to
/// <c>enforce</c>; a guard built with a profile never reads the environment; the strictest profile noted wins;
/// the reset seam restores the noted profile and the latch; <c>AddAshlar</c> replaces a
/// <see cref="EgressGuard.ProcessDefault"/> registration made before it, keeps a host's own guard, and logs one
/// startup line; and a mode other than plain report is written to standard error once. Every profile still
/// defaults to <c>report</c>, and nothing refuses: no route acts on the mode until PR 4.7.</para>
/// <para><b>Process-global state.</b> This class writes the override and profile variables, notes profiles and
/// swaps standard error, so it runs in the serialized <c>EnvironmentVariables</c> collection. The constructor
/// snapshots both variables and the egress state, then clears the state; <c>Dispose</c> restores all of it.</para>
/// </remarks>
[Trait("Category", "Certification")]
[Collection("EnvironmentVariables")]
public sealed class EgressModeProcessBindingTests : IDisposable
{
    private const string ModeVariable = "ASHLAR_EGRESS_MODE";
    private const string ProfileVariable = "ASHLAR_DEPLOYMENT_PROFILE";
    private const string Report = "report";
    private const string Enforce = "enforce";
    private const string Override = "override";
    private const string Remote = "https://remote.example/v1/chat";

    private readonly EnvironmentVariableScope _mode = EnvironmentVariableScope.Unset(ModeVariable);
    private readonly EnvironmentVariableScope _profile = EnvironmentVariableScope.Unset(ProfileVariable);
    private readonly EgressProcessStateScope _state = new(reset: true);

    public void Dispose()
    {
        _state.Dispose();
        _profile.Dispose();
        _mode.Dispose();
    }

    // ---------------------------------------------------------------------------------------------------------
    // The process binding
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void ProcessDefault_reads_the_override_once_and_a_later_change_does_nothing()
    {
        Environment.SetEnvironmentVariable(ModeVariable, "enforce");
        var first = DecideWithProcessDefault();
        first.Mode.Should().Be(Enforce, "enforce is honoured as an opt-in on every profile");
        first.ModeBasis.Should().Be(Override);
        first.Refused.Should().BeTrue("with no subject a remote destination is not allowed, and the mode is enforce");

        Environment.SetEnvironmentVariable(ModeVariable, "report");
        DecideWithProcessDefault().Mode.Should().Be(Enforce, "the variable was read once; a later write changes nothing");
        Environment.SetEnvironmentVariable(ModeVariable, null);
        DecideWithProcessDefault().Mode.Should().Be(Enforce);

        EgressProcessStateScope.Reset();
        var reread = DecideWithProcessDefault();
        reread.Mode.Should().Be(Report, "only the reset seam makes the next decision read the variable again");
        reread.ModeBasis.Should().Be("profile:full");
        reread.Refused.Should().BeFalse();
    }

    [Fact]
    public void A_report_latched_first_is_not_raised_by_a_later_write_of_enforce()
    {
        DecideWithProcessDefault().Mode.Should().Be(Report);

        Environment.SetEnvironmentVariable(ModeVariable, "enforce");

        DecideWithProcessDefault().Mode.Should().Be(Report, "the unset variable was latched at the first decision");
    }

    [Theory]
    [InlineData("full")]
    [InlineData("server")]
    [InlineData("edge")]
    [InlineData("air-gapped")]
    [InlineData("system")]
    [InlineData("secure-workstation")]
    public void With_no_override_ProcessDefault_reports_under_every_profile(string profile)
    {
        Environment.SetEnvironmentVariable(ProfileVariable, profile);

        var decision = DecideWithProcessDefault();

        decision.Mode.Should().Be(Report, "every profile still defaults to report until PR 4.11");
        decision.ModeBasis.Should().Be("profile:" + profile);
        decision.Profile.Should().Be(profile);
        decision.Refused.Should().BeFalse();
    }

    [Fact]
    public void An_unrecognised_mode_value_fails_closed_to_enforce()
    {
        Environment.SetEnvironmentVariable(ModeVariable, "enforce-ish");

        var decision = DecideWithProcessDefault();

        decision.Mode.Should().Be(Enforce);
        decision.ModeBasis.Should().Be(Override);
    }

    [Fact]
    public void An_unrecognised_profile_variable_fails_closed_to_enforce()
    {
        Environment.SetEnvironmentVariable(ProfileVariable, "air-gapped-ish");

        var decision = DecideWithProcessDefault();

        decision.Mode.Should().Be(Enforce, "AddAshlar refuses such a profile; ProcessDefault cannot, so it fails closed");
        decision.ModeBasis.Should().Be("profile:unrecognised");
        decision.Profile.Should().Be("air-gapped-ish");
    }

    [Fact]
    public void A_fault_while_resolving_the_mode_fails_closed_to_enforce()
    {
        EgressProcessStateScope.SetModeResolutionProbe(() => throw new InvalidOperationException("mode probe"));

        var processBound = DecideWithProcessDefault();
        var explicitGuard = new EgressGuard("full").Evaluate(new EgressRequest(EgressFamilies.Http, NewSite(), new Uri(Remote)));

        foreach (var decision in new[] { processBound, explicitGuard })
        {
            decision.Mode.Should().Be(Enforce, "a mode that cannot be resolved fails closed, never open");
            decision.ModeBasis.Should().Be("fault");
            decision.Refused.Should().BeTrue("the remote destination is not allowed and the mode is enforce");
            decision.Fault.Should().BeNull("the mode is resolved in its own step; the classification did not fault");
            decision.Access.Reason.Should().Be(Ashlar.Abstractions.Security.AccessDenialReason.SystemHighData);
        }
    }

    [Fact]
    public void A_guard_built_with_a_profile_never_reads_the_environment()
    {
        Environment.SetEnvironmentVariable(ModeVariable, "enforce");
        Environment.SetEnvironmentVariable(ProfileVariable, "air-gapped");
        DecideWithProcessDefault().Mode.Should().Be(Enforce, "positive control: the process binding sees the variable");

        var decision = new EgressGuard("full").Evaluate(new EgressRequest(EgressFamilies.Http, NewSite(), new Uri(Remote)));

        decision.Mode.Should().Be(Report, "an explicit-profile guard takes its override from its constructor only");
        decision.ModeBasis.Should().Be("profile:full");
        decision.Profile.Should().Be("full");
    }

    [Fact]
    public void A_guard_built_without_a_profile_keeps_its_own_override_when_the_process_raises_nothing()
    {
        var reported = DecideWith(new EgressGuard(deploymentProfile: null, egressMode: "report"));
        reported.Mode.Should().Be(Report);
        reported.ModeBasis.Should().Be(Override);

        var enforced = DecideWith(new EgressGuard(deploymentProfile: null, egressMode: "enforce"));
        enforced.Mode.Should().Be(Enforce, "its own enforce is stricter than the process's unset override");
        enforced.ModeBasis.Should().Be(Override);

        DecideWithProcessDefault().Mode.Should().Be(Report, "positive control: the process itself raised nothing");
    }

    [Theory]
    [InlineData("variable")]
    [InlineData("hosting-option")]
    public void A_guard_built_without_a_profile_takes_the_stricter_of_its_own_override_and_the_process_override(string raisedBy)
    {
        if (raisedBy == "variable")
            Environment.SetEnvironmentVariable(ModeVariable, "enforce");
        else
            new ServiceCollection().AddAshlar(o => o.EgressMode = "enforce");

        var decision = DecideWith(new EgressGuard(deploymentProfile: null, egressMode: "report"));

        decision.Mode.Should().Be(Enforce, "a guard that reads the process state cannot lower the process's enforce with its own report");
        decision.ModeBasis.Should().Be(Override);
        decision.Refused.Should().BeTrue();
    }

    [Fact]
    public void A_decision_names_one_profile_even_when_the_noted_profile_changes_while_it_is_made()
    {
        Environment.SetEnvironmentVariable(ProfileVariable, "edge");
        // Stands in for a concurrent AddAshlar(AirGapped) that lands while ProcessDefault is deciding.
        EgressProcessStateScope.SetModeResolutionProbe(() => EgressProcessStateScope.NoteProfile("air-gapped"));

        var decision = DecideWithProcessDefault();

        EgressProcessStateScope.NotedProfile.Should().Be("air-gapped", "positive control: AirGapped was noted during the decision");
        decision.ModeBasis.Should().Be("profile:edge");
        decision.Profile.Should().Be("edge", "the profile is read once per decision, so the mode and the record cannot disagree");
        decision.ProfileEnforcesByDefault.Should().BeFalse();
    }

    [Fact]
    public void The_mode_resolution_probe_does_not_reach_another_flow()
    {
        EgressProcessStateScope.SetModeResolutionProbe(() => throw new InvalidOperationException("mode probe"));
        DecideWith(new EgressGuard("full")).ModeBasis.Should().Be("fault", "positive control: the probe faults this flow");

        EgressDecision? elsewhere = null;
        var thread = new Thread(() => elsewhere = DecideWith(new EgressGuard("full")));
        using (ExecutionContext.SuppressFlow())
        {
            thread.Start();
        }

        thread.Join();

        elsewhere.Should().NotBeNull();
        elsewhere!.ModeBasis.Should().Be("profile:full", "the probe belongs to the flow that set it, so a guard elsewhere never sees it");
        elsewhere.Mode.Should().Be(Report);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Strictest profile wins, and the reset seam
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_second_AddAshlar_with_no_profile_does_not_lower_an_AirGapped_process()
    {
        new ServiceCollection().AddAshlarProfile(AshlarDeploymentProfile.AirGapped);
        var second = new ServiceCollection();
        second.AddAshlar();

        EgressProcessStateScope.NotedProfile.Should().Be("air-gapped", "once AirGapped is noted nothing lowers it");
        DecideWithProcessDefault().Profile.Should().Be("air-gapped", "the explicit sites decide under the noted profile");

        // The second container's own guard, which its factory clients and MEAI targets use, composes the strictest
        // profile noted in the process, not the Full this call defaulted to.
        using var provider = second.BuildServiceProvider();
        var composed = DecideWith(provider.GetRequiredService<IEgressGuard>());
        composed.Profile.Should().Be("air-gapped", "the composed guard decides under the strictest profile noted");
        composed.ModeBasis.Should().Be("profile:air-gapped");
    }

    [Fact]
    public async Task A_later_AddAshlar_in_an_AirGapped_container_does_not_lower_the_startup_line()
    {
        var services = new ServiceCollection();
        services.AddAshlarProfile(AshlarDeploymentProfile.AirGapped);
        services.AddAshlar();

        var entry = (await StartActivatorAsync(services)).Should().ContainSingle().Which;

        entry.Message.Should().Be(
            "Ashlar egress mode: report (basis profile:air-gapped; profile air-gapped). "
            + "Records only: nothing is refused yet (SPEC-007 PR 4).",
            "the line names the profile the container's guard composed, and that profile was not defaulted");
    }

    [Fact]
    public async Task A_later_stricter_AddAshlar_on_the_same_collection_reaches_its_guard_and_its_startup_line()
    {
        var services = new ServiceCollection();
        services.AddAshlar();
        services.AddAshlarProfile(AshlarDeploymentProfile.AirGapped);

        // The guard the first call composed is replaced like ProcessDefault, so the container decides under the
        // strictest profile noted and the line describes the guard the container uses.
        using var provider = services.BuildServiceProvider();
        var composed = DecideWith(provider.GetRequiredService<IEgressGuard>());
        composed.Profile.Should().Be("air-gapped", "a later AddAshlar on the same collection replaces the guard an earlier one composed");
        composed.ModeBasis.Should().Be("profile:air-gapped");

        var entry = (await StartActivatorAsync(services)).Should().ContainSingle().Which;
        entry.Message.Should().Be(
            "Ashlar egress mode: report (basis profile:air-gapped; profile air-gapped). "
            + "Records only: nothing is refused yet (SPEC-007 PR 4).",
            "the line and the container's guard name the same profile");
    }

    [Fact]
    public async Task A_later_AddAshlar_that_raises_the_mode_on_the_same_collection_reaches_its_guard_and_its_startup_line()
    {
        var services = new ServiceCollection();
        services.AddAshlar();
        services.AddAshlar(o => o.EgressMode = Enforce);

        using var provider = services.BuildServiceProvider();
        var composed = DecideWith(provider.GetRequiredService<IEgressGuard>());
        composed.Mode.Should().Be(Enforce, "a later AddAshlar on the same collection replaces the guard an earlier one composed");
        composed.ModeBasis.Should().Be(Override);

        var entry = (await StartActivatorAsync(services)).Should().ContainSingle().Which;
        entry.Message.Should().StartWith(
            "Ashlar egress mode: enforce (basis override; profile full, defaulted because nothing set it). ",
            "the line and the container's guard name the same mode");
    }

    [Theory]
    [InlineData("air-gapped", "full", "air-gapped")]
    [InlineData("air-gapped", "secure-workstation", "air-gapped")]
    [InlineData("secure-workstation", "full", "secure-workstation")]
    [InlineData("secure-workstation", "server", "secure-workstation")]
    [InlineData("secure-workstation", "air-gapped", "air-gapped")]
    [InlineData("full", "air-gapped", "air-gapped")]
    [InlineData("full", "secure-workstation", "secure-workstation")]
    [InlineData("full", "edge", "edge")]
    [InlineData("edge", "system", "system")]
    public void The_strictest_profile_noted_in_the_process_wins(string first, string second, string noted)
    {
        EgressProcessStateScope.NoteProfile(first);
        EgressProcessStateScope.NoteProfile(second);

        EgressProcessStateScope.NotedProfile.Should().Be(noted, "AirGapped beats SecureWorkstation beats the rest; among the rest the last wins");
    }

    [Fact]
    public void The_reset_seam_restores_the_noted_profile_and_the_mode_latch()
    {
        // A state worth restoring: AirGapped noted, and enforce latched.
        Environment.SetEnvironmentVariable(ModeVariable, "enforce");
        EgressProcessStateScope.NoteProfile("air-gapped");
        DecideWithProcessDefault().Mode.Should().Be(Enforce);

        using (new EgressProcessStateScope(reset: true))
        {
            EgressProcessStateScope.NotedProfile.Should().BeNull("Reset clears the noted profile");
            Environment.SetEnvironmentVariable(ModeVariable, null);
            var cleared = DecideWithProcessDefault();
            cleared.Mode.Should().Be(Report, "Reset clears the latch, so the unset variable is read again");
            cleared.Profile.Should().BeEmpty();

            EgressProcessStateScope.NoteProfile("secure-workstation");
        }

        EgressProcessStateScope.NotedProfile.Should().Be("air-gapped", "Restore puts back the snapshot, bypassing strictest-wins");
        var restored = DecideWithProcessDefault();
        restored.Mode.Should().Be(Enforce, "the latch is restored as it was read, although the variable is now unset");
        restored.Profile.Should().Be("air-gapped");
    }

    // ---------------------------------------------------------------------------------------------------------
    // AddAshlar: the latch, the hosting option, and the composition binding
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void AddAshlar_reads_the_override_once_for_the_whole_process()
    {
        Environment.SetEnvironmentVariable(ModeVariable, "enforce");
        var services = new ServiceCollection();
        services.AddAshlar();
        Environment.SetEnvironmentVariable(ModeVariable, null);

        DecideWithProcessDefault().Mode.Should().Be(Enforce, "AddAshlar latched the variable; the explicit sites see it");
        DecideWith(ComposedGuard(services)).Mode.Should().Be(Enforce, "the composed guard carries the latched override");
    }

    [Theory]
    [InlineData("enforce", LogLevel.Information)]
    [InlineData("junk", LogLevel.Warning)]
    public async Task The_hosting_option_raises_the_mode_for_the_process(string option, LogLevel level)
    {
        var services = new ServiceCollection();
        services.AddAshlar(o => o.EgressMode = option);

        var processBound = DecideWithProcessDefault();
        processBound.Mode.Should().Be(Enforce, "enforce raises the mode, and a value that is neither mode fails closed");
        processBound.ModeBasis.Should().Be(Override);
        var composed = DecideWith(ComposedGuard(services));
        composed.Mode.Should().Be(Enforce);
        composed.ModeBasis.Should().Be(Override);

        var entry = (await StartActivatorAsync(services)).Should().ContainSingle().Which;
        entry.Level.Should().Be(level, "an unrecognised value is logged at Warning");
        entry.Message.Should().StartWith("Ashlar egress mode: enforce (basis override; profile full, defaulted because nothing set it). ");
    }

    [Fact]
    public void The_hosting_option_cannot_lower_the_mode()
    {
        Environment.SetEnvironmentVariable(ModeVariable, "enforce");
        var services = new ServiceCollection();
        services.AddAshlar(o => o.EgressMode = "report");

        DecideWithProcessDefault().Mode.Should().Be(Enforce, "report in the option is not a break-glass");
        DecideWith(ComposedGuard(services)).Mode.Should().Be(Enforce);
    }

    [Fact]
    public void AddAshlar_binds_its_composed_guard_even_when_AddAshlarEgressGuard_ran_first()
    {
        var services = new ServiceCollection();
        services.AddAshlarEgressGuard();
        services.Where(d => d.ServiceType == typeof(IEgressGuard)).Should().ContainSingle()
            .Which.ImplementationInstance.Should().BeSameAs(EgressGuard.ProcessDefault, "positive control: TryAdd registered ProcessDefault");

        services.AddAshlarProfile(AshlarDeploymentProfile.AirGapped);

        var registered = services.Where(d => !d.IsKeyedService && d.ServiceType == typeof(IEgressGuard))
            .Should().ContainSingle("the ProcessDefault descriptor is replaced in place, not joined").Which;
        registered.ImplementationInstance.Should().BeOfType<EgressGuard>()
            .And.NotBeSameAs(EgressGuard.ProcessDefault, "AddAshlar binds the guard it composed");

        using var provider = services.BuildServiceProvider();
        var resolved = provider.GetRequiredService<IEgressGuard>();
        resolved.Should().BeSameAs(registered.ImplementationInstance);

        // The composed guard decides under the composed profile, and never reads the environment again.
        EgressProcessStateScope.Reset();
        Environment.SetEnvironmentVariable(ProfileVariable, "full");
        var decision = DecideWith(resolved);
        decision.Profile.Should().Be("air-gapped");
        decision.ModeBasis.Should().Be("profile:air-gapped");
        decision.Mode.Should().Be(Report, "AirGapped still defaults to report until PR 4.11");
    }

    [Fact]
    public void AddAshlar_keeps_a_guard_the_host_registered_before_it()
    {
        var hostGuard = new EgressGuard("server");
        var services = new ServiceCollection();
        services.AddSingleton<IEgressGuard>(hostGuard);

        services.AddAshlarProfile(AshlarDeploymentProfile.SecureWorkstation);

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IEgressGuard>().Should().BeSameAs(hostGuard, "only a ProcessDefault registration is replaced");
    }

    // ---------------------------------------------------------------------------------------------------------
    // The startup line
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_hosted_activator_logs_one_line_with_mode_basis_profile_and_whether_it_was_defaulted()
    {
        var services = new ServiceCollection();
        services.AddAshlar();

        var entries = await StartActivatorAsync(services);

        var entry = entries.Should().ContainSingle("one line per host start").Which;
        entry.Category.Should().Be("Ashlar.Egress");
        entry.EventId.Should().Be(new EventId(7302, "EgressMode"));
        entry.Level.Should().Be(LogLevel.Information);
        entry.Message.Should().Be(
            "Ashlar egress mode: report (basis profile:full; profile full, defaulted because nothing set it). "
            + "Records only: nothing is refused yet (SPEC-007 PR 4).");
    }

    [Fact]
    public async Task An_unrecognised_override_is_logged_at_Warning()
    {
        Environment.SetEnvironmentVariable(ModeVariable, "off");
        var services = new ServiceCollection();
        services.AddAshlarProfile(AshlarDeploymentProfile.SecureWorkstation);

        var entry = (await StartActivatorAsync(services)).Should().ContainSingle().Which;

        entry.Level.Should().Be(LogLevel.Warning);
        entry.Message.Should().StartWith("Ashlar egress mode: enforce (basis override; profile secure-workstation). ");
        entry.Message.Should().EndWith("so the mode fails closed to enforce.");
    }

    [Fact]
    public void A_mode_other_than_plain_report_is_written_to_standard_error_once()
    {
        var original = Console.Error;
        using var captured = new StringWriter();
        Console.SetError(captured);
        try
        {
            DecideWithProcessDefault();
            captured.ToString().Should().BeEmpty("a plain report mode is not announced");

            EgressProcessStateScope.Reset();
            Environment.SetEnvironmentVariable(ModeVariable, "enforce");
            DecideWithProcessDefault();
            DecideWithProcessDefault();
            new ServiceCollection().AddAshlar();
        }
        finally
        {
            Console.SetError(original);
        }

        captured.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Should().ContainSingle(
                "the line is written once per process, however many decisions and compositions follow")
            .Which.TrimEnd('\r').Should().Be(
                "Ashlar egress mode: enforce (basis override; profile full, defaulted because nothing set it). "
                + "Records only: nothing is refused yet (SPEC-007 PR 4).");
    }

    private static EgressDecision DecideWithProcessDefault() => DecideWith(EgressGuard.ProcessDefault);

    private static EgressDecision DecideWith(IEgressGuard guard) =>
        guard.Evaluate(new EgressRequest(EgressFamilies.Http, NewSite(), new Uri(Remote)));

    private static IEgressGuard ComposedGuard(IServiceCollection services) =>
        (IEgressGuard)services.Single(d => !d.IsKeyedService && d.ServiceType == typeof(IEgressGuard)).ImplementationInstance!;

    private static string NewSite() => "EG-MODE-" + Guid.NewGuid().ToString("N");

    // Builds only the activator, from its own registration, over a capturing logger: the rest of the kernel's hosted
    // services are not constructed.
    private static async Task<IReadOnlyList<LogEntry>> StartActivatorAsync(IServiceCollection services)
    {
        var activator = services.Where(d => d.ServiceType == typeof(IHostedService)
                && d.ImplementationType?.Name == "EgressModeStartupActivator")
            .Should().ContainSingle("AddAshlar registers one activator").Which.ImplementationType!;
        var startup = services.Single(d => d.ServiceType.Name == "EgressModeStartup");

        var capture = new CapturingLoggerProvider();
        IServiceCollection minimal = new ServiceCollection();
        minimal.AddLogging(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(capture));
        minimal.Add(ServiceDescriptor.Singleton(startup.ServiceType, startup.ImplementationInstance!));
        await using var provider = minimal.BuildServiceProvider();

        var hosted = (IHostedService)ActivatorUtilities.CreateInstance(provider, activator);
        await hosted.StartAsync(CancellationToken.None);
        await hosted.StopAsync(CancellationToken.None);
        return capture.Entries;
    }

    private sealed record LogEntry(string Category, LogLevel Level, EventId EventId, string Message);

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<LogEntry> _entries = new();

        public IReadOnlyList<LogEntry> Entries => _entries.ToArray();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _entries);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(string category, ConcurrentQueue<LogEntry> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                entries.Enqueue(new LogEntry(category, logLevel, eventId, formatter(state, exception)));
        }
    }
}
