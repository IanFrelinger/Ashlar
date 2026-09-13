using System.Text.RegularExpressions;
using Ashlar.Core.Application.Paths;
using FluentAssertions;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>
/// The runner column of <c>ci/test-ownership.tsv</c> must be checked syntax over a real gate
/// vocabulary.
///
/// <para><b>What was wrong.</b> The registry's <c>runner</c> column was read exactly once in this
/// repository — <c>TestOwnershipConventionTests</c>, compared against the literal <c>UNOWNED</c>.
/// Every other value was inert text: <c>banana</c>, the empty string, <c>kernel-gate </c>, or a gate
/// deleted two years ago all passed. The registry's own header said so, in a note this change
/// replaced: <i>"The runner column is documentation until a lane-deriving suite gate exists; the
/// assertion that bites today is completeness."</i> That sentence is quoted rather than cited,
/// because the lines carrying it are the lines this change rewrote.</para>
///
/// <para><b>What this proves.</b> That a runner name DENOTES a gate this repository defines, and
/// that the registry is machine-readable at all. Five live holes in <c>ReadRegistry</c> close with
/// it: a row with fewer than four fields is silently dropped (<c>:189-190</c>); a duplicate project
/// is a last-wins Dictionary overwrite rather than an error (<c>:194</c>), which is what makes
/// <c>ci/cert-gate-assertions.md:34</c>'s "exactly one row" false today; <c>raw.Trim()</c>
/// (<c>:184</c>) strips a TRAILING TAB, so an empty note column collapses a 4-field row to 3 and
/// the project then reads as unregistered; a tab typed inside a note truncates the row; and a date
/// on a gated row is never read, because <c>:151-152</c> skips every non-UNOWNED row before
/// reaching <c>Expires</c>.</para>
///
/// <para><b>What this does NOT prove, and must never be read as proving.</b> That the named gate
/// runs THIS project, runs it unfiltered, or runs on a pull request. <c>products-gate</c> resolves
/// and runs one filtered class of <c>Ashlar.Tests.Contracts</c>; <c>perf-gate-tier-a</c> resolves
/// to a script and a Make target with no workflow of its own. None of the five rows corrected on
/// 2026-09-13 would have failed here: three said <c>UNOWNED</c>, a legal sentinel, and two named
/// gates that exist and are spelled correctly. This catches typos, invented names and
/// renamed-or-deleted gates — not wrong-but-real names. Say so when quoting it.</para>
///
/// <para><b>Why the strict parser below is NOT <c>ReadRegistry</c>, and must not be merged with
/// it.</b> Checks 1 and 2 are assertions about what <c>ReadRegistry</c> silently tolerates. By the
/// time it returns, malformed rows are already dropped and duplicates already merged, so a shared
/// lenient parser cannot express them. Consolidating the two would leave Checks 1 and 2 running
/// and unfalsifiable. <see cref="The_registry_parse_and_the_gate_name_corpus_are_not_vacuous"/>
/// will NOT notice — it counts rows, and the lenient parser still returns rows. The negative
/// controls at the bottom of this file are what notice: they drive these matchers over in-memory
/// text, so a lenient parser makes them fail loudly.</para>
///
/// <para><b>One CRLF rule, applied to every read.</b> <c>.gitattributes:3</c> is
/// <c>* text=auto</c> and the <c>eol=lf</c> pins at <c>:7-10</c> cover <c>Makefile</c>,
/// <c>Dockerfile*</c>, <c>*.sh</c> and <c>*.py</c> — not <c>*.yml</c>, not this registry. Every
/// line of <c>ci/test-ownership.tsv</c> and every workflow carries CRLF in a Windows working tree.
/// Regexes anchored with <c>$</c> do not match across <c>\r\n</c>, so every file this class reads
/// goes through <see cref="Normalize"/> first, and a CRLF fixture pins it.</para>
///
/// <para><b>Hermetic, and clock-free by construction</b> (Rules 3 and 4 of
/// <c>ci/cert-gate-assertions.md</c>, cited by number because that file is edited by row
/// insertion and every line range into it rots). Pure file reads and two directory walks: no build, no
/// SDK, no network, no reflection, no git, and no <c>DateTime</c> anywhere. Check 4 is a string
/// comparison against <c>"-"</c>, the exact complement of
/// <c>NoUnownedRow_IsPastItsExpiry</c>, so it adds zero exposure to the scheduled-outage hazard
/// that file's header records at <c>ci/test-ownership.tsv:24-35</c>.</para>
///
/// <para><b>Two checks deliberately excluded.</b> (1) Binding the readiness token to
/// <c>ValidationServiceAdapter.IsDiscoverableTestProject</c>. Measured on this tree: 23 rows, 10 of
/// which name readiness, and 22 of the 23 projects are discoverable — so 12 discoverable rows do
/// not name it. Eleven of those twelve are honest; the twelfth is row 58, below. The relation is
/// not a biconditional, and even the sound direction couples a required check to production
/// business logic that has its own reasons to change. (2) Textual reachability from a gate to a
/// <c>dotnet test</c> invocation. It ships RED, on row 58.</para>
///
/// <para><b>Row 58 is misnamed, not uncovered.</b> It credits <c>kernel-gate</c>, whose Make recipe
/// builds <c>Ashlar.Runtime.sln</c> and runs two filtered <c>dotnet test</c> invocations against
/// <c>Tests.Infrastructure</c> plus <c>$(MAKE) meai-pipeline-gate</c> for <c>Tests.AI.Pipeline</c> —
/// nothing against the kernel suite. But <c>full-platform-readiness-gate</c> runs <c>ci verify</c>,
/// whose <c>validate</c> lane enumerates <c>*.csproj</c> recursively and keeps whatever
/// <see cref="Ashlar.Infrastructure.Validation.Adapters.ValidationServiceAdapter"/> accepts as a test
/// project; <c>Ashlar.Tests.Kernel.csproj</c> passes that predicate. So the suite IS run — by
/// discovery rather than by name, which is why no textual scan can see it, and which is the same
/// mechanism #619 used to re-point three rows out of <c>UNOWNED</c> (row 48 carries the note).
/// Retargeting row 58 is a registry change, not a guard change, so it is left to one. This guard
/// passes it either way, <c>kernel-gate</c> being a real name — which is the scope limit below,
/// demonstrated rather than asserted.</para>
///
/// <para>The failure messages below cite a few anchors by line — the <c>.PHONY</c> line, the
/// <c>kernel-gate:</c> target, the <c>readiness-summary</c> job — because a developer staring at a
/// red assertion wants somewhere to go. Those are diagnostics, checked when this changed. Prose
/// claims are a different matter: this class doc cites <c>ci/cert-gate-assertions.md</c> by RULE
/// NUMBER, because ranges into a file edited by row insertion rot, and two written here already
/// did.</para>
/// </summary>
[Trait("Category", "Certification")]
public sealed class TestOwnershipRunnerConventionTests
{
    private const string RegistryRelativePath = "ci/test-ownership.tsv";
    private const string MakefileRelativePath = "Makefile";
    private const string WorkflowsRelativePath = ".github/workflows";

    /// <summary>Exact case, matching <c>TestOwnershipConventionTests.cs:151</c>.</summary>
    private const string UnownedSentinel = "UNOWNED";

    /// <summary>The value the expires column takes on every row that names a gate.</summary>
    private const string NoExpiry = "-";

    /// <summary>The header IS the column contract; see <c>ci/test-ownership.tsv:15-19</c>.</summary>
    private static readonly string[] ExpectedHeader = ["project", "runner", "expires", "note"];

    /// <summary>Trees walked for gate-definition filenames. Recursive, not fixed paths.</summary>
    private static readonly string[] GateTrees = [".github", "scripts"];

    // =====================================================================================
    // Check 1 — the registry is machine-readable
    // =====================================================================================

    [Fact]
    public void The_header_and_every_row_have_four_non_empty_tab_separated_fields()
    {
        var root = RepoPathResolver.FindRepoRoot();
        var problems = ArityProblems(ParseStrict(ReadText(root, RegistryRelativePath)));

        problems.Should().BeEmpty(
            "{0} is TAB-separated with exactly four non-empty fields per row: "
            + "project <TAB> runner <TAB> expires <TAB> note (the COLUMNS block at "
            + "{0}:15-19). A row that does not parse is NOT rejected today — "
            + "TestOwnershipConventionTests.ReadRegistry silently skips it "
            + "(TestOwnershipConventionTests.cs:189-190), so the project drops out of the registry "
            + "and EveryTestProject_IsRegistered then blames the PROJECT for being unregistered "
            + "instead of the ROW for being malformed. Note that raw.Trim() at "
            + "TestOwnershipConventionTests.cs:184 strips a trailing TAB, so an EMPTY note column "
            + "is one of the ways in. Tabs only — a space-indented row is one field — and never a "
            + "tab inside the note, because the format has no escaping. Offending lines:\n{1}",
            RegistryRelativePath, string.Join("\n", problems));
    }

    // =====================================================================================
    // Check 2 — one row per project
    // =====================================================================================

    [Fact]
    public void No_project_is_registered_twice()
    {
        var root = RepoPathResolver.FindRepoRoot();
        var problems = DuplicateProjectProblems(ParseStrict(ReadText(root, RegistryRelativePath)));

        problems.Should().BeEmpty(
            "each project gets exactly one row in {0}. ci/cert-gate-assertions.md:34 already "
            + "claims \"exactly one row\"; the parser does not enforce it — "
            + "TestOwnershipConventionTests.cs:194 assigns into a Dictionary, so a duplicate is a "
            + "silent LAST-WINS merge and the surviving row is whichever appears later in the "
            + "file. Two rows that disagree about the runner resolve in favour of file order, "
            + "which no reviewer will predict. The realistic way in is a badly resolved merge "
            + "conflict. Delete the wrong one. Duplicated:\n{1}",
            RegistryRelativePath, string.Join("\n", problems));
    }

    // =====================================================================================
    // Check 3 — the runner value is a well-formed name list
    // =====================================================================================

    [Fact]
    public void Every_runner_value_is_a_well_formed_gate_name_list()
    {
        var root = RepoPathResolver.FindRepoRoot();
        var problems = RunnerGrammarProblems(ParseStrict(ReadText(root, RegistryRelativePath)));

        problems.Should().BeEmpty(
            "the runner column is a ';'-separated list of gate names with no whitespace anywhere "
            + "— see {0}:52, :54 and :55 for the three multi-runner rows and {0}:15-19 for the "
            + "column contract. There is deliberately NO character-set rule here: a future gate "
            + "called Readiness_Summary or grpc.transport.gate must not be rejected on cosmetics. "
            + "Whether a name is REAL is decided by "
            + "Every_named_runner_resolves_to_a_gate_definition_on_disk; this fact only decides "
            + "whether the value is STRUCTURED. {1} is a sentinel meaning \"no pull-request check "
            + "runs this project's tests\" ({0}:21-22) — a claim about the whole row, which cannot "
            + "coexist with a gate name. Offending lines:\n{2}",
            RegistryRelativePath, UnownedSentinel, string.Join("\n", problems));
    }

    // =====================================================================================
    // Check 4 — the expires column, complemented
    // =====================================================================================

    [Fact]
    public void Every_named_runner_row_has_no_expiry_date()
    {
        var root = RepoPathResolver.FindRepoRoot();
        var problems = ExpiryProblems(ParseStrict(ReadText(root, RegistryRelativePath)));

        problems.Should().BeEmpty(
            "the expires column is read ONLY for {1} rows — TestOwnershipConventionTests.cs:151-152 "
            + "skips every other row before reaching it. A date on a row that names a gate is "
            + "inert: it looks like a deadline and nothing will ever act on it. Per {0}:18 the "
            + "value is '-' unless the runner is {1}. If you want to record \"revisit this "
            + "ownership by <date>\", put it in the NOTE column, where a human reading the row "
            + "will see it. This fact is a string comparison and carries no clock, so it cannot "
            + "trip on a date (Rule 4 of ci/cert-gate-assertions.md). Offending lines:\n{2}",
            RegistryRelativePath, UnownedSentinel, string.Join("\n", problems));
    }

    // =====================================================================================
    // Check 5 — the vocabulary is real
    // =====================================================================================

    [Fact]
    public void Every_named_runner_resolves_to_a_gate_definition_on_disk()
    {
        var root = RepoPathResolver.FindRepoRoot();
        var registry = ParseStrict(ReadText(root, RegistryRelativePath));
        var corpus = GateDefinitionNames(root);

        var problems = ResolutionProblems(registry, corpus);

        problems.Should().BeEmpty(
            "every gate named in the runner column of {0} must be a gate this repository actually "
            + "defines. Resolution is by EXACT name, case-sensitive, against:\n"
            + "  - the filename stem of any file under .github/ (recursive)\n"
            + "      cert-gate -> .github/workflows/cert-gate.yml\n"
            + "  - the filename stem of any file under scripts/ (recursive)\n"
            + "      perf-gate-tier-a -> scripts/perf-gate-tier-a.sh\n"
            + "      kernel-coverage-gate -> scripts/ci/kernel-coverage-gate.sh (a SUBdirectory; "
            + "the walk is recursive for exactly this reason)\n"
            + "  - a Makefile target header, or a name on the .PHONY line at Makefile:1 (the "
            + "UNION of the two: 110 .PHONY tokens, 140 target headers)\n"
            + "      kernel-gate -> Makefile:194\n"
            + "  - a job id in any .github/workflows/*.yml\n"
            + "      readiness-summary -> full-platform-readiness-gate.yml:586, which carries the "
            + "required 'Readiness summary' check and has no file of its own\n"
            + "Matching is on the BASENAME, never a workflow's display name: cert-gate.yml "
            + "declares 'name: Cert gate' and mcp-a2a-gate.yml declares 'name: MCP + A2A protocol "
            + "gate'.\n"
            + "If you renamed or deleted a gate, update the rows that name it in THIS pull "
            + "request — that is the whole obligation this check adds. If you added a gate "
            + "somewhere none of the four rules reach, either name the file after the gate (the "
            + "convention every one of today's nine tokens already follows) or widen "
            + "GateDefinitionNames() by one line and say why in its doc comment.\n"
            + "This proves only that the NAME denotes something. It does NOT prove the gate runs "
            + "that project, runs it unfiltered, or runs on pull requests, and it does not check "
            + "whether {1} is true — see {0}:37-38. Unresolved:\n{2}",
            RegistryRelativePath, UnownedSentinel, string.Join("\n", problems));
    }

    // =====================================================================================
    // Check 6 — the positive control
    // =====================================================================================

    /// <summary>
    /// Modelled on <c>LaneBlameWindowConventionTests.The_scan_and_the_reflected_inventory_are_not_empty</c>
    /// (<c>:122</c>) and on <c>CertGateFilterCoverageTests.cs:45-47</c>, which asserts its filter
    /// parse is non-empty before using it. Measured on 2026-09-13: 23 rows, 9 distinct named
    /// tokens, 390 corpus names — so the floors below sit at 43%, 33% and 13% of what is there.
    /// The corpus floor is the loose one, deliberately: the corpus is a union over workflows,
    /// scripts and Make targets, and it moves far more than the registry does. These are the only
    /// numbers in this file that can rot, and they rot in the permissive direction.
    /// </summary>
    [Fact]
    public void The_registry_parse_and_the_gate_name_corpus_are_not_vacuous()
    {
        var root = RepoPathResolver.FindRepoRoot();
        var registry = ParseStrict(ReadText(root, RegistryRelativePath));
        var corpus = GateDefinitionNames(root);
        var named = NamedRunnerTokens(registry);

        registry.Rows.Count.Should().BeGreaterThanOrEqualTo(10,
            "this guard measured nothing, so its other five facts passed vacuously — the failure "
            + "mode docs/HowGatesGoQuiet.md catalogues, where a check reports clean because its "
            + "input went missing. {0} held 23 data rows on 2026-09-13", RegistryRelativePath);

        named.Count.Should().BeGreaterThanOrEqualTo(3,
            "the runner vocabulary parse came back near-empty; there were 9 distinct named gate "
            + "tokens on 2026-09-13");

        corpus.Count.Should().BeGreaterThanOrEqualTo(50,
            "the gate-name corpus came back near-empty (390 names on 2026-09-13), which makes "
            + "Every_named_runner_resolves_to_a_gate_definition_on_disk fail for everything or "
            + "nothing depending on ordering. Check that .github/ and scripts/ still exist under "
            + "the root RepoPathResolver.FindRepoRoot() returned");

        corpus.Should().Contain("cert-gate",
            "canary: .github/workflows/cert-gate.yml is the required check this test itself rides. "
            + "If the corpus cannot see it, the corpus is not seeing this repository");

        named.Should().Contain("cert-gate",
            "canary: {0} names cert-gate as the runner of src/Ashlar.Tests.Infrastructure "
            + "(the project this assembly IS). If no row names it, the registry parse is reading "
            + "something else", RegistryRelativePath);
    }

    // =====================================================================================
    // Negative controls. The facts above have one positive control; without these,
    // consolidating the strict parser onto ReadRegistry would leave Checks 1 and 2 running and
    // unfalsifiable, and Check 6 would not notice because the lenient parser still returns rows.
    // Precedent: AiPipelineCiRoutingConventionTests.cs:81-146,
    // CommercialCoverageConventionTests.cs:38-91.
    // =====================================================================================

    private const string FixtureHeader = "project\trunner\texpires\tnote\n";

    private static string FixtureRow(string project, string runner, string expires = NoExpiry)
        => $"{project}\t{runner}\t{expires}\tnote\n";

    [Fact]
    public void A_well_formed_fixture_produces_no_problems_at_all()
    {
        var registry = ParseStrict(
            FixtureHeader
            + FixtureRow("src/A/A.csproj", "cert-gate")
            + FixtureRow("src/B/B.csproj", "cert-gate;kernel-gate")
            + FixtureRow("src/C/C.csproj", UnownedSentinel, "2027-06-30"));

        var corpus = new HashSet<string>(StringComparer.Ordinal) { "cert-gate", "kernel-gate" };

        ArityProblems(registry).Should().BeEmpty();
        DuplicateProjectProblems(registry).Should().BeEmpty();
        RunnerGrammarProblems(registry).Should().BeEmpty();
        ExpiryProblems(registry).Should().BeEmpty(
            "an UNOWNED row's date is the point; NoUnownedRow_IsPastItsExpiry owns it");
        ResolutionProblems(registry, corpus).Should().BeEmpty();
    }

    [Theory]
    [InlineData("src/A/A.csproj\tcert-gate\t-\n")]                     // three fields
    [InlineData("src/A/A.csproj\tcert-gate\t-\tnote\textra\n")]        // five fields
    [InlineData("src/A/A.csproj\tcert-gate\t-\tnote\t\n")]             // trailing TAB
    [InlineData("src/A/A.csproj\tcert-gate\t-\t\n")]                   // empty note
    [InlineData("src/A/A.csproj\t\t-\tnote\n")]                        // empty runner
    [InlineData("src/A/A.csproj cert-gate - note\n")]                  // spaces, not tabs
    [InlineData("src/A/A.csproj\tcert-gate\t-\tsee\tdocs\n")]          // TAB inside the note
    public void A_row_that_does_not_parse_is_refused(string row)
        => ArityProblems(ParseStrict(FixtureHeader + row)).Should().NotBeEmpty(
            "ReadRegistry drops this row silently (TestOwnershipConventionTests.cs:189-190); "
            + "this fact is what makes it loud. If this assertion ever passes an empty list, the "
            + "strict parser has been replaced by a lenient one");

    [Theory]
    [InlineData("project\trunner\texpires\n")]
    [InlineData("project\trunner\texpires\tnote\tscope\n")]
    [InlineData("Project\tRunner\tExpires\tNote\n")]
    [InlineData("src/A/A.csproj\tcert-gate\t-\tnote\n")]               // header deleted
    public void A_header_that_is_not_the_column_contract_is_refused(string header)
        => ArityProblems(ParseStrict(header + FixtureRow("src/A/A.csproj", "cert-gate")))
            .Should().NotBeEmpty(
                "the header IS the column contract (ci/test-ownership.tsv:15-19). Adding a fifth "
                + "column is a deliberate schema change that must update the COLUMNS block, the "
                + "header row, every data row and this test in one pull request");

    [Fact]
    public void A_project_registered_twice_is_refused()
        => DuplicateProjectProblems(ParseStrict(
                FixtureHeader
                + FixtureRow("src/A/A.csproj", "cert-gate")
                + FixtureRow("src\\A\\A.csproj", "kernel-gate")))
            .Should().NotBeEmpty(
                "separators are normalised before comparison, exactly as "
                + "TestOwnershipConventionTests.Normalize does at :260, so the same project spelled "
                + "two ways is still one project. If this passes empty, duplicate detection has "
                + "been moved onto a Dictionary and has stopped existing");

    [Theory]
    [InlineData("kernel-gate products-gate")]     // two gates, one space
    [InlineData("kernel-gate, products-gate")]    // two gates, comma
    [InlineData("kernel-gate; products-gate")]    // space after the separator
    [InlineData("kernel-gate;")]                  // trailing separator
    [InlineData(";kernel-gate")]                  // leading separator
    [InlineData("kernel-gate;;products-gate")]    // empty middle token
    [InlineData("kernel-gate;kernel-gate")]       // repeated token
    [InlineData("UNOWNED;kernel-gate")]           // sentinel inside a list
    [InlineData("kernel-gate;UNOWNED")]           // sentinel inside a list, other end
    public void A_malformed_runner_list_is_refused(string runner)
        => RunnerGrammarProblems(ParseStrict(FixtureHeader + FixtureRow("src/A/A.csproj", runner)))
            .Should().NotBeEmpty();

    [Theory]
    [InlineData("Readiness_Summary")]
    [InlineData("grpc.transport.gate")]
    [InlineData("a;b;c;d")]
    public void An_unusually_spelled_but_structured_runner_list_is_accepted(string runner)
        => RunnerGrammarProblems(ParseStrict(FixtureHeader + FixtureRow("src/A/A.csproj", runner)))
            .Should().BeEmpty(
                "there is no character-set rule by design; resolution decides what is real");

    [Theory]
    [InlineData("cert-gate", "2027-03-31", true)]
    [InlineData("cert-gate", "-", false)]
    [InlineData("cert-gate;kernel-gate", "2027-03-31", true)]
    [InlineData(UnownedSentinel, "2027-06-30", false)]
    [InlineData(UnownedSentinel, "-", false)]
    public void A_date_on_a_gated_row_is_refused(string runner, string expires, bool expected)
        => ExpiryProblems(ParseStrict(FixtureHeader + FixtureRow("src/A/A.csproj", runner, expires)))
            .Any().Should().Be(expected);

    [Theory]
    [InlineData("kernel-gate-teir-c")]   // transposition
    [InlineData("Cert-Gate")]            // case — pins Ordinal, not File.Exists
    [InlineData("cert gate")]            // display name, not basename
    [InlineData("nightly-readiness")]    // invented
    public void A_runner_token_outside_the_vocabulary_is_refused(string token)
        => ResolutionProblems(
                ParseStrict(FixtureHeader + FixtureRow("src/A/A.csproj", token)),
                new HashSet<string>(StringComparer.Ordinal) { "cert-gate", "kernel-gate-tier-c" })
            .Should().NotBeEmpty(
                "resolution is Ordinal set membership over a corpus, never File.Exists. "
                + "File.Exists is case-INsensitive on a Windows host and case-SENSITIVE inside "
                + "scripts/test-in-container.sh, which re-clones into a Linux container — a "
                + "green-locally/red-in-CI divergence is the most deletable kind of guard");

    [Fact]
    public void The_UNOWNED_sentinel_needs_no_gate_to_resolve()
        => ResolutionProblems(
                ParseStrict(FixtureHeader + FixtureRow("src/A/A.csproj", UnownedSentinel, "2027-06-30")),
                new HashSet<string>(StringComparer.Ordinal))
            .Should().BeEmpty("UNOWNED names no gate, by definition (ci/test-ownership.tsv:21-22)");

    [Fact]
    public void A_CRLF_registry_parses_exactly_as_an_LF_one_does()
    {
        var lf = FixtureHeader + FixtureRow("src/A/A.csproj", "cert-gate");
        var crlf = lf.Replace("\n", "\r\n");

        var parsed = ParseStrict(Normalize(crlf));

        ArityProblems(parsed).Should().BeEmpty(
            "ci/test-ownership.tsv is CRLF on every line in a Windows working tree — "
            + ".gitattributes:3 is '* text=auto' and the eol=lf pins at :7-10 do not cover it. A "
            + "stray carriage return would land in the note field and, worse, break any "
            + "'$'-anchored regex downstream");
        parsed.Rows.Should().ContainSingle();
        parsed.Rows[0].Fields[3].Should().Be("note");
    }

    [Theory]
    [InlineData("\tkernel-gate:\n", false)]            // a recipe line, not a target header
    [InlineData("PRIME_TIME_SLNF := x\n", false)]      // an assignment, not a target
    [InlineData("kernel-gate:\n", true)]
    [InlineData("kernel-gate: dep-a dep-b\n", true)]
    [InlineData(".PHONY: kernel-gate\n", true)]
    public void Makefile_names_come_from_target_headers_and_the_PHONY_union(string text, bool expected)
        => MakefileNames(Normalize(text)).Contains("kernel-gate").Should().Be(expected);

    [Theory]
    [InlineData("jobs:\n  readiness-summary:\n", true)]
    [InlineData("jobs:\n  readiness-summary:\r\n", true)]   // the CRLF control
    [InlineData("jobs:\n    readiness-summary:\n", false)]  // a step key, not a job id
    [InlineData("jobs:\n  readiness-summary: value\n", false)]
    public void Workflow_job_ids_are_two_space_keys_and_survive_CRLF(string text, bool expected)
        => WorkflowKeyNames(Normalize(text)).Contains("readiness-summary").Should().Be(expected);

    // =====================================================================================
    // The strict parser.  DO NOT merge this with TestOwnershipConventionTests.ReadRegistry:
    // Checks 1 and 2 are assertions about what that parser silently tolerates, and a shared
    // lenient parser deletes them while leaving them apparently running.
    // =====================================================================================

    private sealed record RegistryLine(int LineNumber, string[] Fields);

    private sealed record Registry(RegistryLine? Header, IReadOnlyList<RegistryLine> Rows);

    /// <summary>
    /// Splits RAW lines. Never <c>Trim()</c>s before splitting: <c>char.IsWhiteSpace('\t')</c> is
    /// true, so trimming first strips a TRAILING TAB and silently turns a four-field row with an
    /// empty note into a three-field row — the hole at
    /// <c>TestOwnershipConventionTests.cs:184</c> then <c>:189-190</c>. Nothing is skipped for
    /// being malformed; malformed rows are carried through and reported by name.
    /// </summary>
    private static Registry ParseStrict(string text)
    {
        RegistryLine? header = null;
        var rows = new List<RegistryLine>();
        var lines = text.Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            var raw = lines[i];
            if (raw.Trim().Length == 0)
                continue;
            if (raw.TrimStart().StartsWith("#", StringComparison.Ordinal))
                continue;

            var entry = new RegistryLine(i + 1, raw.Split('\t'));
            if (header is null)
                header = entry;
            else
                rows.Add(entry);
        }

        return new Registry(header, rows);
    }

    private static List<string> ArityProblems(Registry registry)
    {
        var problems = new List<string>();

        if (registry.Header is null)
        {
            problems.Add(
                "  the file has no header row. Expected 'project<TAB>runner<TAB>expires<TAB>note' "
                + "(ci/test-ownership.tsv:40)");
            return problems;
        }

        if (!registry.Header.Fields.SequenceEqual(ExpectedHeader, StringComparer.Ordinal))
        {
            problems.Add(
                $"  line {registry.Header.LineNumber}: header is [{Describe(registry.Header.Fields)}], "
                + "expected ['project' | 'runner' | 'expires' | 'note']. The header is the column "
                + "contract; changing it means changing the COLUMNS block at "
                + "ci/test-ownership.tsv:15-19, every data row, and this test, in one pull request");
        }

        foreach (var row in registry.Rows)
        {
            if (row.Fields.Length != 4)
            {
                problems.Add(
                    $"  line {row.LineNumber}: {row.Fields.Length} TAB-separated field(s), "
                    + $"expected 4 — [{Describe(row.Fields)}]{ArityHint(row.Fields)}");
                continue;
            }

            for (var i = 0; i < 4; i++)
            {
                if (row.Fields[i].Trim().Length == 0)
                    problems.Add($"  line {row.LineNumber}: field {i + 1} ({ExpectedHeader[i]}) is empty");
            }
        }

        return problems;
    }

    private static string ArityHint(string[] fields)
    {
        if (fields.Length == 1)
            return ". No TAB found — the columns look separated by spaces";
        if (fields.Length > 4 && fields[^1].Length == 0)
            return ". Field " + fields.Length + " is empty: there is a trailing TAB at end of line "
                + "(invisible in most editors)";
        if (fields.Length > 4)
            return ". The note column probably contains a literal TAB; the format has no escaping";
        return string.Empty;
    }

    private static List<string> DuplicateProjectProblems(Registry registry)
    {
        var seen = new Dictionary<string, List<int>>(StringComparer.Ordinal);

        foreach (var row in registry.Rows)
        {
            if (row.Fields.Length == 0)
                continue;

            var project = NormalizePath(row.Fields[0]);
            if (project.Length == 0)
                continue;

            if (!seen.TryGetValue(project, out var lines))
                seen[project] = lines = [];

            lines.Add(row.LineNumber);
        }

        return seen
            .Where(entry => entry.Value.Count > 1)
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(entry => $"  {entry.Key}: lines {string.Join(" and ", entry.Value)}")
            .ToList();
    }

    private static List<string> RunnerGrammarProblems(Registry registry)
    {
        var problems = new List<string>();

        foreach (var row in registry.Rows)
        {
            if (row.Fields.Length < 2)
                continue; // arity is Check 1's fact, not this one's

            var runner = row.Fields[1];

            if (runner.Any(char.IsWhiteSpace))
            {
                problems.Add(
                    $"  line {row.LineNumber}: runner '{runner}' contains whitespace. Two gates are "
                    + "written 'a;b' — one semicolon, no spaces, no comma");
                continue;
            }

            var tokens = runner.Split(';');

            if (tokens.Any(token => token.Length == 0))
            {
                problems.Add(
                    $"  line {row.LineNumber}: runner '{runner}' has an empty token — a leading, "
                    + "trailing or doubled ';'");
                continue;
            }

            var repeated = tokens
                .GroupBy(token => token, StringComparer.Ordinal)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();

            if (repeated.Count > 0)
            {
                problems.Add(
                    $"  line {row.LineNumber}: runner '{runner}' names "
                    + $"{string.Join(", ", repeated)} more than once. A repeated token is a no-op "
                    + "that reads to a human like a second lane");
            }

            if (tokens.Length > 1 && tokens.Contains(UnownedSentinel, StringComparer.Ordinal))
            {
                problems.Add(
                    $"  line {row.LineNumber}: runner '{runner}' combines the {UnownedSentinel} "
                    + "sentinel with a gate name. Either a gate runs it (drop the sentinel and set "
                    + "expires to '-') or none does (drop the gate and keep the expiry date)");
            }
        }

        return problems;
    }

    private static List<string> ExpiryProblems(Registry registry)
    {
        var problems = new List<string>();

        foreach (var row in registry.Rows)
        {
            if (row.Fields.Length < 3)
                continue;

            // Trimmed, to match what the shipped parser compares
            // (TestOwnershipConventionTests.cs:194 stores parts[1].Trim(), :151 compares it).
            // Surrounding whitespace is Check 3's complaint, not this one's.
            var runner = row.Fields[1].Trim();
            if (string.Equals(runner, UnownedSentinel, StringComparison.Ordinal))
                continue;

            var expires = row.Fields[2].Trim();
            if (!string.Equals(expires, NoExpiry, StringComparison.Ordinal))
            {
                problems.Add(
                    $"  line {row.LineNumber}: runner '{runner}', expires '{expires}' — expected "
                    + $"'{NoExpiry}'");
            }
        }

        return problems;
    }

    private static List<string> ResolutionProblems(Registry registry, IReadOnlySet<string> corpus)
    {
        var problems = new List<string>();

        foreach (var row in registry.Rows)
        {
            if (row.Fields.Length < 2)
                continue;

            foreach (var token in row.Fields[1].Split(';'))
            {
                var name = token.Trim();
                if (name.Length == 0)
                    continue; // Check 3 owns empty tokens
                if (string.Equals(name, UnownedSentinel, StringComparison.Ordinal))
                    continue;
                if (corpus.Contains(name))
                    continue;

                var near = NearestNames(name, corpus);
                problems.Add(
                    $"  line {row.LineNumber}: runner token '{name}' resolves to nothing"
                    + (near.Count > 0
                        ? $". Closest existing names: {string.Join(", ", near)}"
                        : string.Empty));
            }
        }

        return problems;
    }

    private static List<string> NamedRunnerTokens(Registry registry)
        => registry.Rows
            .Where(row => row.Fields.Length >= 2)
            .SelectMany(row => row.Fields[1].Split(';'))
            .Select(token => token.Trim())
            .Where(token => token.Length > 0
                && !string.Equals(token, UnownedSentinel, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(token => token, StringComparer.Ordinal)
            .ToList();

    // =====================================================================================
    // The gate-name corpus
    // =====================================================================================

    private static readonly Regex PhonyLine =
        new(@"^\.PHONY:(?<names>[^\n]*)$", RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>
    /// A target header: a name at column 0 followed by ':'. The negative lookahead excludes
    /// <c>VAR := value</c>; the column-0 anchor excludes recipe lines, which are TAB-indented.
    /// </summary>
    private static readonly Regex MakeTarget =
        new(@"^(?<name>[A-Za-z0-9_.\-]+)[ \t]*:(?!=)", RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>
    /// A workflow job id: a two-space-indented key on a line of its own. This deliberately also
    /// matches the keys under <c>on:</c> (<c>push</c>, <c>pull_request</c>, <c>schedule</c>,
    /// <c>workflow_dispatch</c>). That over-permissiveness is chosen: an extra name in the corpus
    /// can only cause a missed detection, while a missing name causes a FALSE FAILURE on a
    /// required check. Under this repository's constraints that direction is always the right one.
    /// </summary>
    private static readonly Regex WorkflowKey =
        new(@"^  (?<name>[A-Za-z0-9_.\-]+):[ \t]*$", RegexOptions.Multiline | RegexOptions.Compiled);

    private static HashSet<string> GateDefinitionNames(string root)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var tree in GateTrees)
        {
            var start = Path.Combine(root, tree);
            if (Directory.Exists(start))
                CollectStems(start, names);
        }

        var makefile = Path.Combine(root, MakefileRelativePath);
        if (File.Exists(makefile))
        {
            foreach (var name in MakefileNames(Normalize(File.ReadAllText(makefile))))
                names.Add(name);
        }

        var workflows = Path.Combine(root, WorkflowsRelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (Directory.Exists(workflows))
        {
            foreach (var file in Directory.EnumerateFiles(workflows))
            {
                if (!file.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)
                    && !file.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (var name in WorkflowKeyNames(Normalize(File.ReadAllText(file))))
                    names.Add(name);
            }
        }

        return names;
    }

    private static HashSet<string> MakefileNames(string text)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (Match match in PhonyLine.Matches(text))
        {
            foreach (var token in match.Groups["names"].Value.Split(' ', '\t'))
            {
                if (token.Length > 0)
                    names.Add(token);
            }
        }

        foreach (Match match in MakeTarget.Matches(text))
            names.Add(match.Groups["name"].Value);

        return names;
    }

    private static HashSet<string> WorkflowKeyNames(string text)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in WorkflowKey.Matches(text))
            names.Add(match.Groups["name"].Value);
        return names;
    }

    private static void CollectStems(string directory, HashSet<string> names)
    {
        foreach (var file in Directory.EnumerateFiles(directory))
            names.Add(Path.GetFileNameWithoutExtension(file));

        foreach (var child in Directory.EnumerateDirectories(directory))
        {
            if (IsPruned(child))
                continue;

            CollectStems(child, names);
        }
    }

    /// <summary>
    /// The same pruning discipline as <c>TestOwnershipConventionTests.cs:243-257</c>, for the
    /// reason its doc comment gives at <c>:206-211</c>: a single <c>git worktree</c> inside the
    /// repository turned a required check red on developers' machines while CI stayed green. Here
    /// a worktree could only ADD names — the direction that cannot cause a false failure — but
    /// pruning keeps the corpus count in Check 6 meaningful.
    /// </summary>
    private static bool IsPruned(string directory)
    {
        var name = Path.GetFileName(directory);

        if (string.Equals(name, "bin", StringComparison.Ordinal)
            || string.Equals(name, "obj", StringComparison.Ordinal)
            || string.Equals(name, ".claude", StringComparison.Ordinal))
        {
            return true;
        }

        var git = Path.Combine(directory, ".git");
        return File.Exists(git) || Directory.Exists(git);
    }

    // =====================================================================================
    // Plumbing
    // =====================================================================================

    private static string ReadText(string root, string relativePath)
    {
        var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(path).Should().BeTrue(
            "{0} is the registry this class enforces; it must exist at {1}", relativePath, path);

        return Normalize(File.ReadAllText(path));
    }

    /// <summary>One line-ending rule for every file this class reads. See the class remarks.</summary>
    private static string Normalize(string text)
        => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private static string NormalizePath(string value) => value.Replace('\\', '/').Trim();

    private static string Describe(string[] fields)
        => string.Join(" | ", fields.Select(field =>
            "'" + (field.Length > 48 ? field[..45] + "..." : field) + "'"));

    /// <summary>
    /// Edit distance of 2 or less, which converts the most likely failure — a typo — from a lookup
    /// into a one-word fix. Only ever computed on the failure path.
    /// </summary>
    private static List<string> NearestNames(string name, IEnumerable<string> corpus)
        => corpus
            .Select(candidate => (Name: candidate, Distance: EditDistance(name, candidate)))
            .Where(pair => pair.Distance <= 2)
            .OrderBy(pair => pair.Distance)
            .ThenBy(pair => pair.Name, StringComparer.Ordinal)
            .Take(3)
            .Select(pair => pair.Name)
            .ToList();

    private static int EditDistance(string a, string b)
    {
        if (Math.Abs(a.Length - b.Length) > 2)
            return int.MaxValue;

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
            previous[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}
