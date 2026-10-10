using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using System.Reflection;
using Ashlar.Abstractions.Security;
using Ashlar.Abstractions.Security.Egress;
using FluentAssertions;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>
/// SPEC-007 PR 4.6, the mode table: one resolver, <c>EgressEnforcement.ResolveMode(profile, override)</c>, pinned
/// row by row for the six profiles against no override, <c>report</c>, <c>enforce</c> and an unrecognised value,
/// with AirGapped and SecureWorkstation enforcing by default; and what an explicit-profile guard records from it:
/// <see cref="EgressDecision.Mode"/>, <see cref="EgressDecision.ModeBasis"/>, <see cref="EgressDecision.Refused"/>,
/// <see cref="EgressDecision.Ref"/>, and the three fields appended to event 1.
/// </summary>
/// <remarks>
/// <para>The rows are written out, not computed, so a resolver that drifts cannot carry its own expectation with
/// it. The resolver is internal to <c>Ashlar.Abstractions</c> and this assembly is not in its
/// <c>InternalsVisibleTo</c>, so it is called by reflection.</para>
/// <para>Hermetic: every guard here is built with a profile and an override, so no decision reads the
/// environment or the process latch (that is pinned, with the environment, in
/// <c>EgressModeProcessBindingTests</c>). Records are filtered by a site unique to the test.</para>
/// <para>The mode and access decision together determine whether a route must refuse.</para>
/// </remarks>
[Trait("Category", "Certification")]
public sealed class EgressModeResolutionTests
{
    private const string Report = "report";
    private const string Enforce = "enforce";
    private const string Override = "override";
    private const string Remote = "https://remote.example/v1/chat";
    private const string Loopback = "http://127.0.0.1:11434/api";

    private static readonly MethodInfo Resolver = typeof(EgressGuard).Assembly
        .GetType("Ashlar.Abstractions.Security.Egress.EgressEnforcement", throwOnError: true)!
        .GetMethod("ResolveMode", BindingFlags.NonPublic | BindingFlags.Static)!;

    /// <summary>The six profiles against the four kinds of override. AirGapped cannot be lowered; SecureWorkstation records its break-glass.</summary>
    [Theory]
    [InlineData("full", null, Report, "profile:full")]
    [InlineData("full", "report", Report, Override)]
    [InlineData("full", "enforce", Enforce, Override)]
    [InlineData("full", "junk", Enforce, Override)]
    [InlineData("server", null, Report, "profile:server")]
    [InlineData("server", "report", Report, Override)]
    [InlineData("server", "enforce", Enforce, Override)]
    [InlineData("server", "junk", Enforce, Override)]
    [InlineData("edge", null, Report, "profile:edge")]
    [InlineData("edge", "report", Report, Override)]
    [InlineData("edge", "enforce", Enforce, Override)]
    [InlineData("edge", "junk", Enforce, Override)]
    [InlineData("air-gapped", null, Enforce, "profile:air-gapped")]
    [InlineData("air-gapped", "report", Enforce, "override-ignored")]
    [InlineData("air-gapped", "enforce", Enforce, Override)]
    [InlineData("air-gapped", "junk", Enforce, Override)]
    [InlineData("system", null, Report, "profile:system")]
    [InlineData("system", "report", Report, Override)]
    [InlineData("system", "enforce", Enforce, Override)]
    [InlineData("system", "junk", Enforce, Override)]
    [InlineData("secure-workstation", null, Enforce, "profile:secure-workstation")]
    [InlineData("secure-workstation", "report", Report, "break-glass")]
    [InlineData("secure-workstation", "enforce", Enforce, Override)]
    [InlineData("secure-workstation", "junk", Enforce, Override)]
    public void The_mode_table_pins_every_profile_against_every_kind_of_override(
        string profile,
        string? modeOverride,
        string mode,
        string basis)
    {
        Resolve(profile, modeOverride).Should().Be((mode, basis));
    }

    [Theory]
    [InlineData("full", Report)]
    [InlineData("server", Report)]
    [InlineData("edge", Report)]
    [InlineData("air-gapped", Enforce)]
    [InlineData("system", Report)]
    [InlineData("secure-workstation", Enforce)]
    public void Unset_and_blank_overrides_preserve_each_profiles_default(string profile, string mode)
    {
        Resolve(profile, null).Mode.Should().Be(mode);
        Resolve(profile, string.Empty).Mode.Should().Be(mode, "a blank override is no override");
        Resolve(profile, "   ").Mode.Should().Be(mode);
    }

    [Theory]
    [InlineData("AirGapped", Enforce, "profile:air-gapped")]
    [InlineData("AIR_GAPPED", Enforce, "profile:air-gapped")]
    [InlineData("airgapped", Enforce, "profile:air-gapped")]
    [InlineData(" Air-Gapped ", Enforce, "profile:air-gapped")]
    [InlineData("SecureWorkstation", Enforce, "profile:secure-workstation")]
    [InlineData("workstation", Enforce, "profile:secure-workstation")]
    [InlineData("SECURE_WORKSTATION", Enforce, "profile:secure-workstation")]
    [InlineData("core", Report, "profile:system")]
    [InlineData("FULL", Report, "profile:full")]
    public void A_profile_spelling_AddAshlar_accepts_resolves_to_its_canonical_basis(string profile, string mode, string basis)
    {
        Resolve(profile, null).Should().Be((mode, basis));
    }

    [Theory]
    [InlineData(" Enforce ", Enforce)]
    [InlineData("ENFORCE", Enforce)]
    [InlineData("Report", Report)]
    [InlineData("  report\t", Report)]
    [InlineData("enforcing", Enforce)]
    [InlineData("off", Enforce)]
    [InlineData("0", Enforce)]
    public void An_override_is_trimmed_and_any_case_and_anything_else_fails_closed(string modeOverride, string mode)
    {
        Resolve("full", modeOverride).Should().Be((mode, Override));
    }

    [Theory]
    [InlineData(null, Enforce, "profile:unrecognised")]
    [InlineData("report", Enforce, "override-ignored")]
    [InlineData("enforce", Enforce, Override)]
    [InlineData("junk", Enforce, Override)]
    public void An_unrecognised_profile_fails_closed_and_no_override_lowers_it(
        string? modeOverride,
        string mode,
        string basis)
    {
        Resolve("air-gapped-ish", modeOverride).Should().Be((mode, basis));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_profile_is_the_default_profile(string? profile)
    {
        Resolve(profile, null).Should().Be((Report, "profile:full"), "AddAshlar defaults an unset profile to full");
        Resolve(profile, "enforce").Should().Be((Enforce, Override));
    }

    // ---------------------------------------------------------------------------------------------------------
    // What an explicit-profile guard records
    // ---------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("full", null, Report, "profile:full")]
    [InlineData("air-gapped", null, Enforce, "profile:air-gapped")]
    [InlineData("secure-workstation", "report", Report, "break-glass")]
    [InlineData("server", "enforce", Enforce, Override)]
    [InlineData("air-gapped", "enforce", Enforce, Override)]
    [InlineData("edge", "junk", Enforce, Override)]
    [InlineData("air-gapped-ish", null, Enforce, "profile:unrecognised")]
    public void An_explicit_guard_records_the_resolved_mode_and_refuses_only_under_enforce(
        string profile,
        string? modeOverride,
        string mode,
        string basis)
    {
        var guard = new EgressGuard(profile, modeOverride);

        var remote = guard.Evaluate(new EgressRequest(EgressFamilies.ModelMeai, NewSite(), new Uri(Remote)));
        remote.Mode.Should().Be(mode);
        remote.ModeBasis.Should().Be(basis);
        remote.Access.Allowed.Should().BeFalse("with no subject a remote destination is refused with SystemHighData");
        remote.Refused.Should().Be(mode == Enforce, "Refused is Mode == enforce and the access is not allowed");
        remote.Fault.Should().BeNull("the mode does not change the classification");

        var host = guard.Evaluate(new EgressRequest(EgressFamilies.ModelMeai, NewSite(), new Uri(Loopback)));
        host.Mode.Should().Be(mode);
        host.Access.Allowed.Should().BeTrue("the host boundary is SystemHigh");
        host.Refused.Should().BeFalse("an allowed egress is never refused, whatever the mode");
    }

    [Fact]
    public void A_classification_fault_under_enforce_is_refused_and_under_report_is_not()
    {
        var relative = new Uri("/v1/chat", UriKind.Relative);

        var enforced = new EgressGuard("full", "enforce").Evaluate(new EgressRequest(EgressFamilies.Http, NewSite(), relative));
        enforced.Fault.Should().Be(typeof(ArgumentException).FullName);
        enforced.Access.Should().Be(default(AccessDecision));
        enforced.Mode.Should().Be(Enforce, "the mode is resolved apart from the classification");
        enforced.Refused.Should().BeTrue("a fault is NoDecision, and NoDecision under enforce is refused");

        var reported = new EgressGuard("full").Evaluate(new EgressRequest(EgressFamilies.Http, NewSite(), relative));
        reported.Mode.Should().Be(Report);
        reported.Refused.Should().BeFalse();
    }

    [Fact]
    public void Every_decision_carries_a_fresh_random_reference_of_16_hex_digits()
    {
        var guard = new EgressGuard("full");
        var site = NewSite();

        var decisions = Enumerable.Range(0, 256)
            .Select(_ => guard.Evaluate(new EgressRequest(EgressFamilies.Http, site, new Uri(Remote))))
            .ToList();

        decisions.Should().OnlyContain(
            d => d.Ref.Length == 16 && d.Ref.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')),
            "a reference is 64 random bits as 16 lowercase hex digits");
        decisions.Select(d => d.Ref).Should().OnlyHaveUniqueItems("each decision draws its own reference");
        decisions.Select(d => d.Ref).Should().NotContain(
            decisions.Select(d => d.Sequence.ToString("x16", System.Globalization.CultureInfo.InvariantCulture)),
            "the reference is not the sequence number, which would leak the rate of other decisions");
    }

    [Fact]
    public void Event_1_carries_the_mode_basis_refused_and_the_reference()
    {
        var guard = new EgressGuard("full", "enforce");
        guard.Evaluate(new EgressRequest(EgressFamilies.Http, NewSite(), new Uri(Remote)));

        var site = NewSite();
        using var listener = new ModeListener();
        var decision = guard.Evaluate(new EgressRequest(EgressFamilies.Http, site, new Uri(Remote)));

        var e = listener.Events.Where(w => w.EventId == 1 && Field(w, "site") as string == site)
            .Should().ContainSingle().Which;
        e.PayloadNames.Should().EndWith(
            new[] { "fault", "modeBasis", "refused", "ref" },
            "the contract is append-only: the three fields follow fault");
        Field(e, "mode").Should().Be(Enforce);
        Field(e, "modeBasis").Should().Be(Override);
        Field(e, "refused").Should().Be(true);
        Field(e, "ref").Should().Be(decision.Ref);
        decision.Refused.Should().BeTrue();
        e.Version.Should().Be(1, "appending fields bumps the event's version, which ETW and TraceEvent consumers key manifests on");
    }

    private static (string Mode, string ModeBasis) Resolve(string? profile, string? modeOverride) =>
        ((string, string))Resolver.Invoke(null, [profile, modeOverride])!;

    private static string NewSite() => "EG-MODE-" + Guid.NewGuid().ToString("N");

    private static object? Field(EventWrittenEventArgs e, string name)
    {
        var index = e.PayloadNames?.IndexOf(name) ?? -1;
        return index < 0 || e.Payload is null || index >= e.Payload.Count ? null : e.Payload[index];
    }

    private sealed class ModeListener : EventListener
    {
        private readonly ConcurrentQueue<EventWrittenEventArgs> _events = new();

        public IReadOnlyList<EventWrittenEventArgs> Events => _events.ToArray();

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name == "Ashlar-Egress")
                EnableEvents(eventSource, EventLevel.Verbose);
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData) => _events.Enqueue(eventData);
    }
}
