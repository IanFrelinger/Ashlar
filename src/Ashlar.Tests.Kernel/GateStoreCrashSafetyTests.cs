using FluentAssertions;
using Ashlar.Manifest;
using Ashlar.Manifest.Admission;
using Xunit;

namespace Ashlar.Tests.Kernel;

/// <summary>
/// Crash-safety tests (gold plan step 4): what the store does when a previous process died
/// at the worst moment. The write protocol is write-tmp-then-move, so the interesting cases
/// are the leftovers.
///
/// <para>Deliberately absent: chmod-based read-only tests. The dev container runs as root,
/// and root bypasses DAC permission checks — this repository has learned three separate
/// times that a mode-000 premise silently evaporates under root. Filesystem-permission
/// behaviour is covered by the OS, not simulated here.</para>
/// </summary>
[Collection("EnvironmentSensitive")]
public sealed class GateStoreCrashSafetyTests : IDisposable
{
    /// <summary>A GateStore pins its signers from this variable; see
    /// <see cref="EnvironmentSensitiveCollection"/> for why every store-constructing fact pins it
    /// to an EMPTY directory of its own.</summary>
    private const string KeyDirVariable = "ASHLAR_KEY_DIR";

    private readonly string _dir;
    private readonly string? _previousKeyDir;

    public GateStoreCrashSafetyTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "crash-" + Guid.NewGuid().ToString("N"));
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

    private static ExtensionProposal Proposal(string id) => new()
    {
        Id = id,
        Kind = "brick",
        Summary = "crash case",
        ProposedBy = "t",
        ProposedAt = Now,
        Courses = [new CourseResult { Name = "tests", Passed = true, Detail = "ok" }],
    };

    private string GatesDir => Path.Combine(_dir, "gates");

    [Fact]
    public async Task A_stray_tmp_from_a_crashed_write_never_appears_in_listings()
    {
        var store = new GateStore(_dir);
        await store.RecordAsync(Proposal("ext-live"),
            new AdmissionOutcome { State = ProposalState.Held, Reason = "held" }, Now);

        // Simulate a writer that died between write and move.
        File.WriteAllText(Path.Combine(GatesDir, "ext-dead.json.tmp"), "{ \"partial\": ");

        var listed = await new GateStore(_dir).ListAsync();

        listed.Should().ContainSingle().Which.Proposal.Id.Should().Be("ext-live",
            "a .json.tmp is not a record and must never be read as one");
    }

    [Fact]
    public async Task The_next_locked_operation_sweeps_the_stray()
    {
        var stray = Path.Combine(GatesDir, "ext-dead.json.tmp");
        Directory.CreateDirectory(GatesDir);
        File.WriteAllText(stray, "{ \"partial\": ");

        // Any locked operation acquires the lock, which sweeps.
        await new GateStore(_dir).RecordAsync(Proposal("ext-after"),
            new AdmissionOutcome { State = ProposalState.Held, Reason = "held" }, Now);

        File.Exists(stray).Should().BeFalse("strays are janitored under the lock, where no writer can be mid-move");
    }

    [Fact]
    public async Task A_crashed_rewrite_leaves_the_previous_committed_record_intact()
    {
        // The decide path rewrites a record via the same tmp-then-move protocol. If the
        // rewriter dies before the move, the COMMITTED record must still read cleanly —
        // the half-written verdict simply never happened.
        var store = new GateStore(_dir);
        await store.RecordAsync(Proposal("ext-held"),
            new AdmissionOutcome { State = ProposalState.Held, Reason = "held" }, Now);
        File.WriteAllText(Path.Combine(GatesDir, "ext-held.json.tmp"), "{ \"State\": \"Admi");

        var record = await new GateStore(_dir).GetAsync("ext-held");

        record!.State.Should().Be(ProposalState.Held,
            "a crash before the move means the decision never happened; the held record survives");
    }

    [Fact]
    public async Task Sweeping_never_touches_committed_records()
    {
        var store = new GateStore(_dir);
        await store.RecordAsync(Proposal("ext-a"),
            new AdmissionOutcome { State = ProposalState.Held, Reason = "h" }, Now);
        await store.RecordAsync(Proposal("ext-b"),
            new AdmissionOutcome { State = ProposalState.Rejected, Reason = "r" }, Now);
        File.WriteAllText(Path.Combine(GatesDir, "ext-c.json.tmp"), "junk");

        // Many lock acquisitions, records untouched every time.
        for (var i = 0; i < 3; i++)
        {
            await new GateStore(_dir).ListAsync();
            await new GateStore(_dir).RecordAsync(Proposal($"ext-more-{i}"),
                new AdmissionOutcome { State = ProposalState.Held, Reason = "h" }, Now);
        }

        (await new GateStore(_dir).ListAsync()).Should().HaveCount(5);
        (await new GateStore(_dir).GetAsync("ext-b"))!.State.Should().Be(ProposalState.Rejected);
    }
}
