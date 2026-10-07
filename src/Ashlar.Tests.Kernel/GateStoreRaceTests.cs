using FluentAssertions;
using Ashlar.Manifest;
using Ashlar.Manifest.Admission;
using Xunit;

namespace Ashlar.Tests.Kernel;

/// <summary>
/// Race tests for the admission boundary. Each test hammers one invariant with many
/// concurrent actors, every actor holding its OWN <see cref="GateStore"/> instance over the
/// same directory — separate instances share no memory, so this is the separate-process
/// case in miniature.
///
/// <para>Written failing-first: before the store took a cross-process lock, two humans could
/// decide the same held proposal and both "win" (one verdict silently overwriting the
/// other), and two self-extending proposals could both read a budget of 0-used and both
/// admit. On an admission boundary those are security bugs, not concurrency niceties.</para>
/// </summary>
[Collection("EnvironmentSensitive")]
public sealed class GateStoreRaceTests : IDisposable
{
    /// <summary>A GateStore pins its signers from this variable; see
    /// <see cref="EnvironmentSensitiveCollection"/> for why every store-constructing fact pins it
    /// to an EMPTY directory of its own.</summary>
    private const string KeyDirVariable = "ASHLAR_KEY_DIR";

    private readonly string _dir;
    private readonly string? _previousKeyDir;

    public GateStoreRaceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "gate-races-" + Guid.NewGuid().ToString("N"));
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

    private static AshlarPolicy Policy(string mode, int budget) => new()
    {
        ApiVersion = "ashlar/v1",
        Kind = "Policy",
        Sandbox = new PolicySandbox { Root = "." },
        SelfExtend = new PolicySelfExtend
        {
            Mode = mode,
            Budget = new PolicyBudget { Extensions = budget, Window = "24h" },
            MayAdd = ["brick"],
            GatesRequired = ["tests"],
        },
        Never = [.. PolicyLoader.RequiredNeverEntries],
    };

    private static ExtensionProposal Proposal(string id) => new()
    {
        Id = id,
        Kind = "brick",
        Summary = "add brick race.demo",
        ProposedBy = "racer",
        ProposedAt = Now,
        Courses = [new CourseResult { Name = "tests", Passed = true, Detail = "ok" }],
    };

    [Fact]
    public async Task Only_one_of_many_concurrent_decisions_on_a_held_proposal_wins()
    {
        // Seed one held proposal.
        var seed = new GateStore(_dir);
        await seed.RecordAsync(Proposal("ext-race"),
            AdmissionGate.Decide(Policy(SelfExtendMode.Proposing, 3), Proposal("ext-race"), 0), Now);

        // Twenty actors — half admitting, half refusing — race the decision. Every actor has
        // its own store instance: no shared memory, only the directory.
        var gun = new TaskCompletionSource();
        var actors = Enumerable.Range(0, 20).Select(i => Task.Run(async () =>
        {
            var store = new GateStore(_dir);
            await gun.Task;
            try
            {
                var admit = i % 2 == 0;
                await store.DecideAsync("ext-race", admit, $"actor-{i}", "racing", Now);
                return true;    // this actor's verdict landed
            }
            catch (InvalidOperationException)
            {
                return false;   // correctly told the proposal was no longer Held
            }
        })).ToList();
        gun.SetResult();
        var results = await Task.WhenAll(actors);

        results.Count(won => won).Should().Be(1,
            "a held proposal is decided exactly once; a second verdict silently overwriting "
            + "the first would let an admit erase a refusal on the admission boundary");

        // And the surviving record is internally consistent.
        var final = await new GateStore(_dir).GetAsync("ext-race");
        final!.State.Should().BeOneOf(ProposalState.Admitted, ProposalState.Refused);
    }

    [Fact]
    public async Task Concurrent_self_extending_proposals_cannot_overrun_the_budget()
    {
        // Budget of exactly 1. Twenty proposals race the propose transaction; at most one
        // may be Admitted, the rest must degrade to Held — never a silent over-admit.
        var policy = Policy(SelfExtendMode.SelfExtending, budget: 1);

        var gun = new TaskCompletionSource();
        var actors = Enumerable.Range(0, 20).Select(i => Task.Run(async () =>
        {
            var store = new GateStore(_dir);
            await gun.Task;
            return await store.ProposeAsync(policy, Proposal($"ext-{i}"), Now);
        })).ToList();
        gun.SetResult();
        var records = await Task.WhenAll(actors);

        records.Count(r => r.State == ProposalState.Admitted).Should().Be(1,
            "budget 1 means one admission, under any concurrency; two racers both reading "
            + "'0 admitted' and both admitting is the budget not existing");
        records.Count(r => r.State == ProposalState.Held).Should().Be(19,
            "budget exhaustion degrades to Held, never to drop");
    }

    [Fact]
    public async Task Concurrent_records_of_the_same_proposal_id_admit_exactly_one()
    {
        // Append-once must hold under concurrency too: two runtimes proposing the same id
        // simultaneously must not both create records.
        var outcome = AdmissionGate.Decide(Policy(SelfExtendMode.Proposing, 3), Proposal("ext-dup"), 0);

        var gun = new TaskCompletionSource();
        var actors = Enumerable.Range(0, 12).Select(_ => Task.Run(async () =>
        {
            var store = new GateStore(_dir);
            await gun.Task;
            try
            {
                await store.RecordAsync(Proposal("ext-dup"), outcome, Now);
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        })).ToList();
        gun.SetResult();
        var results = await Task.WhenAll(actors);

        results.Count(created => created).Should().Be(1, "a proposal is recorded exactly once");
    }

    [Fact]
    public async Task Propose_with_an_unparseable_budget_window_fails_closed_to_held()
    {
        // The loader does not validate budget.window; the transaction must. An unparseable
        // window is never an unlimited allowance — it degrades to a human decision.
        var policy = Policy(SelfExtendMode.SelfExtending, budget: 5) with
        {
            SelfExtend = Policy(SelfExtendMode.SelfExtending, 5).SelfExtend with
            {
                Budget = new PolicyBudget { Extensions = 5, Window = "whenever" },
            },
        };

        var record = await new GateStore(_dir).ProposeAsync(policy, Proposal("ext-w"), Now);

        record.State.Should().Be(ProposalState.Held);
        record.Reason.Should().Contain("window");
    }
}
