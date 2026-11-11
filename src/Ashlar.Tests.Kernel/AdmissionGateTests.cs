using FluentAssertions;
using Ashlar.Manifest;
using Ashlar.Manifest.Admission;
using Xunit;

namespace Ashlar.Tests.Kernel;

/// <summary>
/// Pins SPEC-004: the mode semantics and the transition-authority rules. The one-sentence
/// version under test: there is no path into Admitted except a full pass — no
/// administrative shortcut, including for the vendor.
/// </summary>
[Collection("EnvironmentSensitive")]
public sealed class AdmissionGateTests : IDisposable
{
    /// <summary>A GateStore pins its signers from this variable; see
    /// <see cref="EnvironmentSensitiveCollection"/> for why every store-constructing fact pins it
    /// to an EMPTY directory of its own.</summary>
    private const string KeyDirVariable = "ASHLAR_KEY_DIR";

    private readonly string _dir;
    private readonly string? _previousKeyDir;

    public AdmissionGateTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "gates-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _previousKeyDir = Environment.GetEnvironmentVariable(KeyDirVariable);
        Environment.SetEnvironmentVariable(KeyDirVariable, Directory.CreateDirectory(Path.Combine(_dir, "no-ambient-keys")).FullName);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(KeyDirVariable, _previousKeyDir);
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static readonly DateTimeOffset Now = new(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);

    private static AshlarPolicy Policy(string mode, int budget = 3, params string[] mayAdd) => new()
    {
        ApiVersion = "ashlar/v1",
        Kind = "Policy",
        Sandbox = new PolicySandbox { Root = "." },
        SelfExtend = new PolicySelfExtend
        {
            Mode = mode,
            Budget = new PolicyBudget { Extensions = budget, Window = "24h" },
            MayAdd = mayAdd.Length > 0 ? [.. mayAdd] : ["brick"],
            GatesRequired = ["sandbox", "tests", "security"],
        },
        Never = [.. PolicyLoader.RequiredNeverEntries],
    };

    private static ExtensionProposal Proposal(string id = "ext-1", string kind = "brick", params (string name, bool passed)[] courses)
    {
        var list = courses.Length > 0
            ? courses.Select(c => new CourseResult { Name = c.name, Passed = c.passed, Detail = c.passed ? "ok" : "boom" }).ToList()
            : new List<CourseResult>
            {
                new() { Name = "sandbox", Passed = true, Detail = "confined" },
                new() { Name = "tests", Passed = true, Detail = "14 passed" },
                new() { Name = "security", Passed = true, Detail = "0 findings" },
            };
        return new ExtensionProposal
        {
            Id = id,
            Kind = kind,
            Summary = "add brick invoice.classify.v2",
            ProposedBy = "classifier",
            ProposedAt = Now,
            Courses = list,
        };
    }

    // ─────────────────────────── mode semantics ───────────────────────────

    [Fact]
    public void Null_arguments_are_refused_not_dereferenced()
    {
        // Mutation-run harvest: the ThrowIfNull guards had no covering test.
        var decideNullPolicy = () => AdmissionGate.Decide(null!, Proposal(), 0);
        var decideNullProposal = () => AdmissionGate.Decide(Policy(SelfExtendMode.Proposing), null!, 0);

        decideNullPolicy.Should().Throw<ArgumentNullException>();
        decideNullProposal.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Sealed_rejects_before_anything_else_is_consulted()
    {
        var outcome = AdmissionGate.Decide(Policy(SelfExtendMode.Sealed), Proposal(), admittedInWindow: 0);

        outcome.State.Should().Be(ProposalState.Rejected);
        outcome.Reason.Should().Contain("sealed");
    }

    [Fact]
    public void Proposing_holds_a_full_pass_for_a_person()
    {
        var outcome = AdmissionGate.Decide(Policy(SelfExtendMode.Proposing), Proposal(), 0);

        outcome.State.Should().Be(ProposalState.Held);
        outcome.Reason.Should().Contain("a person seats the stone");
    }

    [Fact]
    public void SelfExtending_admits_within_budget()
    {
        var outcome = AdmissionGate.Decide(Policy(SelfExtendMode.SelfExtending, budget: 3), Proposal(), admittedInWindow: 2);

        outcome.State.Should().Be(ProposalState.Admitted);
        outcome.Reason.Should().Contain("3 of 3");
    }

    [Fact]
    public void SelfExtending_with_spent_budget_degrades_to_held_never_to_admit()
    {
        var outcome = AdmissionGate.Decide(Policy(SelfExtendMode.SelfExtending, budget: 3), Proposal(), admittedInWindow: 3);

        outcome.State.Should().Be(ProposalState.Held,
            "budget exhaustion degrades to a human decision, never to a silent admit or drop");
        outcome.Reason.Should().Contain("budget");
    }

    // ─────────────────────────── envelope and gates ───────────────────────

    [Fact]
    public void A_kind_outside_mayAdd_is_rejected_even_in_self_extending_mode_with_budget()
    {
        var outcome = AdmissionGate.Decide(
            Policy(SelfExtendMode.SelfExtending, budget: 100),
            Proposal(kind: "tool"),
            admittedInWindow: 0);

        outcome.State.Should().Be(ProposalState.Rejected,
            "a tool widens the envelope; no mode and no budget makes that admissible");
        outcome.Reason.Should().Contain("outside the envelope");
    }

    [Fact]
    public void A_failed_course_rejects_in_every_mode()
    {
        foreach (var mode in new[] { SelfExtendMode.Proposing, SelfExtendMode.SelfExtending })
        {
            var outcome = AdmissionGate.Decide(
                Policy(mode),
                Proposal(courses: [("sandbox", true), ("tests", false), ("security", true)]),
                0);

            outcome.State.Should().Be(ProposalState.Rejected, $"no mode admits unverified work (mode: {mode})");
            outcome.Reason.Should().Contain("tests");
        }
    }

    [Fact]
    public void A_required_gate_that_did_not_run_is_a_rejection_not_an_exemption()
    {
        var outcome = AdmissionGate.Decide(
            Policy(SelfExtendMode.Proposing),
            Proposal(courses: [("sandbox", true), ("tests", true)]),   // security never ran
            0);

        outcome.State.Should().Be(ProposalState.Rejected);
        outcome.Reason.Should().Contain("security").And.Contain("did not run");
    }

    // ─────────────────────────── the store ────────────────────────────────

    [Fact]
    public async Task A_held_proposal_survives_process_death()
    {
        // The reviewer is asleep when the app proposes. A held queue in process memory is
        // the same defect the cold-rollback fix closed; pin durability from day one.
        var outcome = AdmissionGate.Decide(Policy(SelfExtendMode.Proposing), Proposal("ext-cold"), 0);
        await new GateStore(_dir).RecordAsync(Proposal("ext-cold"), outcome, Now);

        var coldProcess = new GateStore(_dir);
        var held = await coldProcess.ListAsync(ProposalState.Held);

        held.Should().ContainSingle().Which.Proposal.Id.Should().Be("ext-cold");
    }

    [Fact]
    public async Task Deciding_a_held_proposal_admits_or_refuses_and_records_the_actor()
    {
        var store = new GateStore(_dir);
        await store.RecordAsync(Proposal("ext-a"),
            AdmissionGate.Decide(Policy(SelfExtendMode.Proposing), Proposal("ext-a"), 0), Now);

        var decided = await store.DecideAsync("ext-a", admit: true, actor: "ian.f", reason: "reviewed the diff", Now.AddHours(6));

        decided.State.Should().Be(ProposalState.Admitted);
        decided.Actor.Should().Be("ian.f");
    }

    [Fact]
    public async Task A_refusal_requires_a_reason_because_it_feeds_back_to_the_proposer()
    {
        var store = new GateStore(_dir);
        await store.RecordAsync(Proposal("ext-b"),
            AdmissionGate.Decide(Policy(SelfExtendMode.Proposing), Proposal("ext-b"), 0), Now);

        var act = () => store.DecideAsync("ext-b", admit: false, actor: "ian.f", reason: "  ", Now);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*requires a reason*");
    }

    [Fact]
    public async Task Only_held_proposals_can_be_decided_admitted_and_rejected_are_immutable_history()
    {
        var store = new GateStore(_dir);
        await store.RecordAsync(Proposal("ext-c"),
            AdmissionGate.Decide(Policy(SelfExtendMode.Sealed), Proposal("ext-c"), 0), Now);   // Rejected

        var act = () => store.DecideAsync("ext-c", admit: true, actor: "anyone", reason: "please", Now);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*no administrative path*");
    }

    [Fact]
    public async Task A_proposal_is_recorded_once_and_never_overwritten()
    {
        var store = new GateStore(_dir);
        var outcome = AdmissionGate.Decide(Policy(SelfExtendMode.Proposing), Proposal("ext-d"), 0);
        await store.RecordAsync(Proposal("ext-d"), outcome, Now);

        var act = () => store.RecordAsync(Proposal("ext-d"), outcome, Now);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*append-once*");
    }

    [Fact]
    public async Task The_budget_window_counts_only_recent_admissions()
    {
        var store = new GateStore(_dir);
        var policy = Policy(SelfExtendMode.Proposing);

        // Admit one long ago, one recently.
        await store.RecordAsync(Proposal("ext-old"), AdmissionGate.Decide(policy, Proposal("ext-old"), 0), Now.AddDays(-3));
        await store.DecideAsync("ext-old", true, "ian.f", "ok", Now.AddDays(-3));
        await store.RecordAsync(Proposal("ext-new"), AdmissionGate.Decide(policy, Proposal("ext-new"), 0), Now.AddHours(-1));
        await store.DecideAsync("ext-new", true, "ian.f", "ok", Now.AddHours(-1));

        AdmissionGate.TryParseWindow("24h", out var window).Should().BeTrue();
        (await store.AdmittedInWindowAsync(window, Now)).Should().Be(1);
    }

    [Theory]
    [InlineData("24h", 24 * 60)]
    [InlineData("30m", 30)]
    [InlineData("7d", 7 * 24 * 60)]
    [InlineData("0000000001h", 60)]
    [InlineData("1m", 1)]
    public void Budget_windows_parse(string text, int minutes)
    {
        AdmissionGate.TryParseWindow(text, out var span).Should().BeTrue();
        span.TotalMinutes.Should().Be(minutes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("h")]
    [InlineData("24x")]
    [InlineData("-4h")]
    [InlineData("0d")]
    public void Unparseable_windows_fail_closed_not_infinite(string? text)
    {
        AdmissionGate.TryParseWindow(text, out _).Should().BeFalse(
            "an unparseable window is an error, never an unlimited allowance");
    }
    [Theory]
    [InlineData("2147483647m", 2147483647L * TimeSpan.TicksPerMinute)]
    [InlineData("256204778h", 256204778L * TimeSpan.TicksPerHour)]
    [InlineData("10675199d", 10675199L * TimeSpan.TicksPerDay)]
    public void Largest_supported_windows_parse_exactly(string text, long ticks)
    {
        AdmissionGate.TryParseWindow(text, out var span).Should().BeTrue();
        span.Ticks.Should().Be(ticks);
    }

    [Theory]
    [InlineData("2147483648m")]
    [InlineData("256204779h")]
    [InlineData("10675200d")]
    [InlineData("2147483647h")]
    [InlineData("2147483647d")]
    [InlineData("999999999999999999999999999999m")]
    [InlineData("00000000001h")]
    [InlineData("+1h")]
    [InlineData(" 1h")]
    [InlineData("1 h")]
    [InlineData("1h ")]
    [InlineData("1.5h")]
    [InlineData("1H")]
    [InlineData("１h")]
    [InlineData("1\0h")]
    public void Invalid_or_overflowing_windows_return_false_and_zero(string text)
    {
        AdmissionGate.TryParseWindow(text, out var span).Should().BeFalse();
        span.Should().Be(TimeSpan.Zero);
    }

    [Theory]
    [InlineData("abc", 3)]
    [InlineData("2147483647d", 3)]
    [InlineData("24h", -1)]
    [InlineData(null, 3)]
    public async Task Invalid_programmatic_budgets_never_admit(string? window, int extensions)
    {
        var original = Policy(SelfExtendMode.SelfExtending);
        var policy = original with
        {
            SelfExtend = original.SelfExtend with
            {
                Budget = new PolicyBudget { Extensions = extensions, Window = window! },
            },
        };
        var direct = AdmissionGate.Decide(policy, Proposal(), 0);
        direct.State.Should().Be(ProposalState.Held);
        direct.Reason.Should().Contain("selfExtend.budget");
        var record = await new GateStore(_dir).ProposeAsync(policy, Proposal(), Now);
        record.State.Should().Be(ProposalState.Held);
        record.Reason.Should().Be(direct.Reason);
    }

    [Fact]
    public async Task Null_programmatic_budget_is_held_without_throwing()
    {
        var original = Policy(SelfExtendMode.SelfExtending);
        var policy = original with { SelfExtend = original.SelfExtend with { Budget = null! } };
        AdmissionGate.Decide(policy, Proposal(), 0).State.Should().Be(ProposalState.Held);
        (await new GateStore(_dir).ProposeAsync(policy, Proposal(), Now)).State.Should().Be(ProposalState.Held);
    }

    [Theory]
    [InlineData("tool", "tests", true)]
    [InlineData("brick", "tests", false)]
    [InlineData("brick", "other", true)]
    public async Task Invalid_budget_cannot_turn_envelope_or_gate_rejection_into_a_hold(string kind, string gate, bool passed)
    {
        var original = Policy(SelfExtendMode.SelfExtending);
        var policy = original with
        {
            SelfExtend = original.SelfExtend with { Budget = new PolicyBudget { Extensions = 1, Window = "bad" } },
        };
        var proposal = Proposal("invalid-gate", kind, ("sandbox", true), (gate, passed), ("security", true));
        AdmissionGate.Decide(policy, proposal, 0).State.Should().Be(ProposalState.Rejected);
        (await new GateStore(_dir).ProposeAsync(policy, proposal, Now)).State.Should().Be(ProposalState.Rejected);
    }

    [Theory]
    [InlineData("10675199d", 0)]
    [InlineData("256204778h", 0)]
    [InlineData("2147483647m", 0)]
    [InlineData("1h", 1)]
    [InlineData("1h", -1)]
    [InlineData("90m", 1)]
    public async Task Windows_before_year_one_count_all_admissions_without_clock_underflow(string window, int offsetHours)
    {
        var now = offsetHours == 0 ? Now : new DateTimeOffset(1, 1, 1, window == "90m" ? 2 : offsetHours == 1 ? 1 : 0, 0, 0, TimeSpan.FromHours(offsetHours));
        var original = Policy(SelfExtendMode.SelfExtending, budget: 1);
        var policy = original with
        {
            SelfExtend = original.SelfExtend with { Budget = new PolicyBudget { Extensions = 1, Window = window } },
        };
        var store = new GateStore(_dir);
        (await store.ProposeAsync(policy, Proposal("first"), now)).State.Should().Be(ProposalState.Admitted);
        (await store.ProposeAsync(policy, Proposal("second"), now)).State.Should().Be(ProposalState.Held);
        AdmissionGate.TryParseWindow(window, out var span).Should().BeTrue();
        (await store.AdmittedInWindowAsync(span, now)).Should().Be(1);
    }

}
