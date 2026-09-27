using FluentAssertions;
using Ashlar.Manifest;
using Ashlar.Manifest.Admission;
using Xunit;

namespace Ashlar.Tests.Kernel;

/// <summary>
/// Fuzz-informed hardening tests (gold plan step 2): hostile YAML against the loaders, the
/// Windows path alphabet against proposal ids, and corruption against the store. The common
/// invariant: hostile input produces a REJECTION WITH A REASON — never a hang, an
/// exponential blowup, an unhandled exception, or (worst of all) a silently skipped record.
/// </summary>
[Collection("EnvironmentSensitive")]
public sealed class AdmissionFuzzTests : IDisposable
{
    /// <summary>A GateStore pins its signers from this variable; see
    /// <see cref="EnvironmentSensitiveCollection"/> for why every store-constructing fact pins it
    /// to an EMPTY directory of its own.</summary>
    private const string KeyDirVariable = "ASHLAR_KEY_DIR";

    private readonly string _dir;
    private readonly string? _previousKeyDir;

    public AdmissionFuzzTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "fuzz-" + Guid.NewGuid().ToString("N"));
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

    // ─────────────────────────── hostile YAML ───────────────────────────

    [Fact]
    public void Alias_bomb_is_rejected_before_it_can_expand()
    {
        // The classic billion-laughs shape: tiny document, exponential expansion if the
        // parser follows the aliases. The guard rejects anchors outright — these documents
        // never need them.
        var bomb = """
            apiVersion: ashlar/v1
            kind: Policy
            a: &a ["x","x","x","x","x","x","x","x"]
            b: &b [*a,*a,*a,*a,*a,*a,*a,*a]
            c: &c [*b,*b,*b,*b,*b,*b,*b,*b]
            d: &d [*c,*c,*c,*c,*c,*c,*c,*c]
            e: &e [*d,*d,*d,*d,*d,*d,*d,*d]
            f: &f [*e,*e,*e,*e,*e,*e,*e,*e]
            """;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        PolicyLoader.TryLoad(bomb, out _, out var reason).Should().BeFalse();
        sw.Stop();

        reason.Should().Contain("anchor");
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2),
            "the rejection must happen before expansion, not after surviving it");
    }

    [Fact]
    public void Oversized_documents_are_rejected_by_size_not_parsed()
    {
        var giant = "apiVersion: ashlar/v1\nkind: Application\n# " + new string('x', YamlGuard.MaxBytes + 10);

        ManifestLoader.TryLoad(giant, out _, out var reason).Should().BeFalse();
        reason.Should().Contain("limit");
    }

    [Fact]
    public void A_duplicate_mode_key_is_refused_not_resolved_last_wins()
    {
        // A merge-conflicted or tampered policy that names `mode` twice must be refused, not
        // silently armed to the second (more permissive) value. This is the fail-open the
        // never-list strictness exists to prevent.
        var dupMode = """
            apiVersion: ashlar/v1
            kind: Policy
            sandbox:
              root: .
              writable: []
            selfExtend:
              mode: sealed
              mode: self-extending
              budget:
                extensions: 99
                window: 24h
              mayAdd: []
              gatesRequired: [tests]
            never:
              - modify_gate
              - widen_sandbox
              - access_signing_keys
              - truncate_ledger
              - grant_capability
            """;

        PolicyLoader.TryLoad(dupMode, out var policy, out var reason).Should().BeFalse(
            "an ambiguous safety document must fail closed, never resolve to the permissive value");
        policy.Should().BeNull();
        reason.Should().Contain("more than once");
    }

    [Fact]
    public void A_duplicate_top_level_key_is_also_refused()
    {
        var dupTop = """
            apiVersion: ashlar/v1
            kind: Policy
            sandbox:
              root: .
              writable: []
            selfExtend:
              mode: sealed
              budget: { extensions: 0, window: 24h }
              mayAdd: []
              gatesRequired: []
            never: [modify_gate, widen_sandbox, access_signing_keys, truncate_ledger, grant_capability]
            never: []
            """;

        PolicyLoader.TryLoad(dupTop, out _, out var reason).Should().BeFalse();
        reason.Should().Contain("more than once");
    }

    [Theory]
    [InlineData("\0\x01\x02 binary garbage \xff")]
    [InlineData("{{{{{{{{")]
    [InlineData("apiVersion: [this, is, not, a, string]")]
    [InlineData("kind: {nested: {absurdly: {deep: value}}}")]
    [InlineData("apiVersion: ashlar/v1\nkind: Policy\nsandbox: \"not a map\"")]
    [InlineData("apiVersion: ashlar/v1\nkind: Policy\nselfExtend:\n  budget: \"words\"")]
    public void Garbage_and_type_confusion_reject_with_reasons_never_escape_exceptions(string yaml)
    {
        // Both loaders, same invariant: TryLoad returns false with a REJECTED reason.
        // An unhandled exception here becomes a CLI crash on hostile input.
        var actPolicy = () => PolicyLoader.TryLoad(yaml, out _, out _);
        var actManifest = () => ManifestLoader.TryLoad(yaml, out _, out _);

        actPolicy.Should().NotThrow().Which.Should().BeFalse();
        actManifest.Should().NotThrow().Which.Should().BeFalse();
    }

    [Fact]
    public void An_honest_ampersand_in_prose_is_not_an_anchor()
    {
        // The guard is textual and deliberately eager, but plain prose must survive:
        // '&' followed by whitespace is not YAML anchor syntax.
        ProjectScaffold.TryScaffold("fish-and-chips", out var manifest, out _, out _).Should().BeTrue();
        var yaml = manifest.Replace(
            "version: 0.1.0",
            "version: 0.1.0\n  # serves fish & chips daily");

        ManifestLoader.TryLoad(yaml, out _, out var reason).Should().BeTrue(reason);
    }

    // ─────────────────────── the Windows path alphabet ───────────────────────

    public static TheoryData<string> HostileIds => new()
    {
        "CON", "NUL", "PRN", "AUX", "COM1",          // Win32 reserved names
        "ext ", "ext.",                              // trailing space/dot: Win32 strips -> collisions
        "a:b", "ext::$DATA",                         // NTFS alternate data streams
        "..", "../x", "..\\x", "/etc/passwd",        // traversal
        "", " ", "-lead", "_lead",                   // empty / bad leading char
        "éxt", "ｅxt", "ext​1",                 // non-ASCII and zero-width confusables
        "id\twith\ttabs", "id\nnewline",
    };

    [Theory]
    [MemberData(nameof(HostileIds))]
    public async Task Hostile_proposal_ids_are_refused_by_the_allowlist(string id)
    {
        var store = new GateStore(_dir);
        var proposal = new ExtensionProposal
        {
            Id = id,
            Kind = "brick",
            Summary = "hostile",
            ProposedBy = "fuzzer",
            ProposedAt = Now,
            Courses = [new CourseResult { Name = "tests", Passed = true, Detail = "ok" }],
        };

        var act = () => store.RecordAsync(proposal,
            new AdmissionOutcome { State = ProposalState.Held, Reason = "x" }, Now);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*Illegal proposal id*");
    }

    [Theory]
    [InlineData("a")]
    [InlineData("ext-4f2a")]
    [InlineData("A_1-b_2")]
    [InlineData("0start")]
    public async Task Legitimate_ids_still_pass(string id)
    {
        var store = new GateStore(_dir);
        var proposal = new ExtensionProposal
        {
            Id = id, Kind = "brick", Summary = "ok", ProposedBy = "t", ProposedAt = Now,
            Courses = [new CourseResult { Name = "tests", Passed = true, Detail = "ok" }],
        };

        var record = await store.RecordAsync(proposal,
            new AdmissionOutcome { State = ProposalState.Held, Reason = "x" }, Now);

        record.Proposal.Id.Should().Be(id);
    }

    // ─────────────────────────── store corruption ───────────────────────────

    [Fact]
    public async Task A_truncated_record_fails_the_listing_loudly_never_silently_skips()
    {
        // The worst failure shape for an admission store: a corrupt HELD record silently
        // vanishing from the queue is an invisible pending decision.
        var store = new GateStore(_dir);
        await store.RecordAsync(
            new ExtensionProposal
            {
                Id = "ext-c", Kind = "brick", Summary = "s", ProposedBy = "p", ProposedAt = Now,
                Courses = [new CourseResult { Name = "tests", Passed = true, Detail = "ok" }],
            },
            new AdmissionOutcome { State = ProposalState.Held, Reason = "held" }, Now);

        var file = Directory.GetFiles(Path.Combine(_dir, "gates"), "ext-c.json").Single();
        File.WriteAllText(file, File.ReadAllText(file)[..40]);   // truncate mid-object

        var act = () => new GateStore(_dir).ListAsync(ProposalState.Held);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Corrupt gate record*ext-c*");
    }

    [Fact]
    public async Task A_record_containing_null_fails_loudly_too()
    {
        var gatesDir = Path.Combine(_dir, "gates");
        Directory.CreateDirectory(gatesDir);
        File.WriteAllText(Path.Combine(gatesDir, "ext-null.json"), "null");

        var act = () => new GateStore(_dir).GetAsync("ext-null");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*contains no record*");
    }
}
