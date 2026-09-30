using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Ashlar.Core.Application.Paths;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>
/// Two ways a warning pragma quietly unmakes a promise, and the rules that close them.
///
/// <para><b>What was wrong.</b> (1) <c>Ashlar.Brick.Contracts</c> is a stable-tier package, and the
/// mechanism <c>docs/SdkCompatibilityPolicy.md</c> names for its promise is the public-API analyzer:
/// RS0016 fails the build when a public symbol is in neither <c>PublicAPI.*.txt</c> file. Five files
/// under <c>Authoring/Ports/</c> opened with a file-wide <c>#pragma warning disable RS0016</c> and
/// never restored it, so the 18 public types they declare - unchanged since <c>v0.1.0</c> and published
/// in three releases - were tracked by nothing, and any change to them would have passed every build.
/// (2) <c>src/Ashlar.Hosting/Sdk/Builders/HostAshlarSdkBuilder.cs:7</c> disabled CS0618 "because
/// AshlarSdkBuilder is an obsolete type forwarder in this file"; #223 (2026-07-04) moved the forwarder
/// out, and the pragma then suppressed nothing for 88 days while still reading as a reason. A dead
/// suppression is a loaded one: the next obsolete call written into that file is silenced on arrival.</para>
///
/// <para><b>Rule 1 - public-API tracking is never switched off.</b> No source anywhere in the tree
/// disables RS0016, RS0017, RS0026 or RS0027 by pragma or by <c>[SuppressMessage]</c>/
/// <c>[UnconditionalSuppressMessage]</c>, and no source carries a bare <c>#pragma warning disable</c>
/// (it switches off every diagnostic, these included). No MSBuild file lists those IDs in
/// <c>NoWarn</c> or <c>WarningsNotAsErrors</c>; no <c>.editorconfig</c>/<c>.globalconfig</c> lowers
/// their severity to none, silent or suggestion, or tells the analyzer to skip a namespace
/// (<c>dotnet_public_api_analyzer.skip_namespaces</c>, the same hole as the Ports pragmas by another
/// spelling); no config that applies to a project using the analyzer lowers the <c>ApiDesign</c>
/// category or every analyzer that way; and neither such a project nor a <c>Directory.Build.*</c>
/// above it turns analyzers off (<c>RunAnalyzers</c>, <c>RunAnalyzersDuringBuild</c>) or removes an
/// API file from <c>AdditionalFiles</c>. Every project that references the analyzer wires BOTH API
/// files as <c>AdditionalFiles</c>, since without them the analyzer has nothing to compare. The source scan is
/// deliberately wider than the analyzer's projects: a pragma acts on the file it is in, and a file is
/// compiled into those projects from outside their directories too (the <c>Ashlar.Compat</c> polyfills
/// linked by <c>Directory.Build.targets</c>), while an RS0016 pragma anywhere else is dead by definition.</para>
///
/// <para><b>Rule 2 - a CS0618 or ASHLAREXP001 disable names what it is for, and that thing is still
/// there.</b> A <c>#pragma warning disable</c> of either id - in any spelling the compiler accepts:
/// any case, and for CS0618 the bare number too (<c>618</c>, <c>0618</c>), which Roslyn maps to
/// <c>CS0618</c> exactly as it does on <c>/nowarn</c> - must carry a trailing comment that names, as
/// an exact identifier, a symbol this tree declares <c>[Obsolete]</c> (without <c>error: true</c>,
/// which is CS0619, and without a <c>DiagnosticId</c>, which replaces CS0618) or <c>[Experimental]</c>
/// with the ASHLAREXP001 id; at least one symbol it names must occur in the code the pragma governs
/// (from the pragma to a matching restore, else to the end of the file) OUTSIDE a declaration that
/// carries the same attribute - the compiler reports neither diagnostic inside an obsolete or
/// experimental context, which is exactly why the two <c>Ashlar.Sdk/Legacy</c> pragmas below are
/// dead; and the comment must carry a reason of at least <see cref="MinimumReasonWords"/> words
/// besides the names. A disable that cannot carry such a comment is not allowed to stand in for one:
/// a <c>[SuppressMessage]</c>/<c>[UnconditionalSuppressMessage]</c> naming either id, a
/// <c>NoWarn</c> or <c>WarningsNotAsErrors</c> (element or attribute) listing either id in any MSBuild
/// file, and a <c>dotnet_diagnostic.&lt;id&gt;.severity</c> of none, silent or suggestion in any
/// <c>.editorconfig</c>/<c>.globalconfig</c> are all Rule 2 violations. A project-wide disable names
/// no symbol by construction, so each one that exists must be on
/// <see cref="ReviewedProjectWideDisables"/>, which records why; that list fails in both directions
/// too.</para>
///
/// <para><b>Grandfathered, and why it is a ratchet rather than an exemption.</b> Eight existing disables
/// fail Rule 2 and live in files this change does not own; <see cref="Grandfathered"/> lists each by
/// its exact text. The list fails in BOTH directions: a new violation fails, and an entry whose pragma
/// has been fixed, edited or deleted fails too, so the list can only shrink and every shrink is a
/// reviewed diff. What each needs is in its note. Measured with the compiler on f0fd3fde (removing the
/// pragma and building): both <c>Ashlar.Sdk/Legacy</c> CS0618 pragmas are dead (the build stays clean),
/// the test one is live, and all six ASHLAREXP001 pragmas are live - five of them simply name no symbol
/// (<c>touch-set</c>, <c>lineage</c>), and the sixth, in <c>ObjectiveDocument.cs</c>, already passes.</para>
///
/// <para><b>What this does NOT prove.</b> Rule 2 is lexical. It proves a disable names a real
/// deprecated or experimental symbol that the governed code still mentions outside that symbol's own
/// context; it cannot prove the compiler would report there (a use inside a lambda in an obsolete
/// member, <c>nameof</c>, or a same-named symbol from another assembly all read as uses). A CS0618
/// disable for an obsolete API declared OUTSIDE this tree (BCL, a package) cannot name an in-tree
/// symbol and so fails; that is a deliberate, reviewed exception to add to <see cref="Grandfathered"/>,
/// not something to loosen. CS0612 (an <c>[Obsolete]</c> with no message) is a different id and is
/// not policed. Neither rule evaluates MSBuild: conditions are ignored (a disable is flagged whatever
/// its <c>Condition</c>), and a disable routed through a property of another name
/// (<c>&lt;NoWarn&gt;$(MyList)&lt;/NoWarn&gt;</c>) or passed on a command line (<c>-p:NoWarn=</c>,
/// <c>-nowarn:</c> in a script or workflow) is not seen. Nor does either rule check
/// <c>TreatWarningsAsErrors</c> (conditional in the root props) or <c>WarningLevel</c>, or read the
/// bulk <c>dotnet_analyzer_diagnostic.*</c> settings for Rule 2. Rule 1 follows no imports beyond the
/// <c>Directory.Build.*</c> chain, and an <c>// &lt;auto-generated/&gt;</c> header is out of scope.
/// Code inside <c>#if</c> branches is scanned whether or not any build defines the symbol -
/// over-approximating costs a red test, never a silent pass.</para>
/// </summary>
[Trait("Category", "Certification")]
public sealed class DiagnosticSuppressionConventionTests
{
    private const string ObsoleteId = "CS0618";
    private const string ExperimentalId = "ASHLAREXP001";
    private const string ApiAnalyzerPackage = "Microsoft.CodeAnalysis.PublicApiAnalyzers";
    private const string BrickContractsProject = "src/Ashlar.Brick.Contracts/Ashlar.Brick.Contracts.csproj";

    /// <summary>A comment must say why, not only what: this many words besides the names.</summary>
    private const int MinimumReasonWords = 3;

    // Non-vacuity floors. STATED here and lowered only by a visible diff: a count derived from the tree
    // moves down with the tree and can never fire. Measured on f0fd3fde (2026-09-30): the walk reads
    // 3149 C# files and 133 MSBuild files; 6 projects reference the analyzer; the tree declares 65
    // distinct ASHLAREXP001 [Experimental] names and 2 CS0618 [Obsolete] names (AshlarSdkBuilder,
    // AddAshlarSdk); after this change it carries 15 warning-disable pragmas. The floors sit under those
    // numbers, and above what a broken walk or parser yields (zero, or the handful in one directory).
    // The obsolete floor is 1, not the measured 2: a broken parser yields 0, while removing one of the
    // two forwarders is the normal end of a deprecation (docs/SdkCompatibilityPolicy.md) and must not
    // read as a broken parser. Removing the LAST [Obsolete] symbol in the tree legitimately yields 0;
    // lower this floor to 0 in that diff (the in-memory controls below still pin the parser).
    private const int SourceFilesFloor = 2500;
    private const int MsBuildFilesFloor = 100;
    private const int ApiProjectsFloor = 6;
    private const int ExperimentalSymbolsFloor = 40;
    private const int ObsoleteSymbolsFloor = 1;
    private const int DisablePragmasFloor = 10;

    private static readonly Regex ApiTrackingId = new(@"(?<![A-Za-z0-9])RS00(?:16|17|26|27)(?![0-9])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex DeprecationIdInText = new(@"(?<![A-Za-z0-9])(?:CS0618|ASHLAREXP001)(?![A-Za-z0-9])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex PragmaLine = new(@"^\s*#\s*pragma\s+warning\s+(disable|restore)\b(.*)$",
        RegexOptions.CultureInvariant);

    private static readonly Regex SuppressAttribute = new(
        @"(?<![\w.@])(?:global\s*::\s*)?(?:System\s*\.\s*Diagnostics\s*\.\s*CodeAnalysis\s*\.\s*)?(?:Unconditional)?SuppressMessage(?:Attribute)?\s*\(",
        RegexOptions.CultureInvariant);

    private static readonly Regex DeprecationAttribute = new(
        @"(?<![\w.@])(?:global\s*::\s*)?(?:System\s*\.\s*)?(?:Diagnostics\s*\.\s*CodeAnalysis\s*\.\s*)?(?<kind>Obsolete|Experimental)(?:Attribute)?(?![\w.])",
        RegexOptions.CultureInvariant);

    private static readonly Regex Identifier = new(@"@?[A-Za-z_][A-Za-z0-9_]*", RegexOptions.CultureInvariant);

    private static readonly Regex ReasonWord = new(@"[A-Za-z]{2,}", RegexOptions.CultureInvariant);

    private static readonly Regex WeakSeverity = new(@"^(none|silent|suggestion)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex DiagnosticSeverity = new(
        @"^dotnet_diagnostic\.(?<id>[A-Za-z]+[0-9]+)\.severity\s*=\s*(?<sev>[A-Za-z]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex AnalyzerSeverity = new(
        @"^dotnet_analyzer_diagnostic\.(?:category-(?<cat>[A-Za-z-]+)\.)?severity\s*=\s*(?<sev>[A-Za-z]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex SkipNamespaces = new(@"^dotnet_public_api_analyzer\.skip_namespaces\s*=\s*\S",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> NotANameKeywords = new(StringComparer.Ordinal)
    {
        "public", "private", "protected", "internal", "static", "sealed", "abstract", "virtual", "override",
        "readonly", "const", "new", "partial", "extern", "unsafe", "volatile", "async", "required", "file",
        "event", "fixed", "implicit", "explicit", "ref", "out", "in", "params", "void", "get", "set", "init",
        "add", "remove", "where", "operator", "this", "return",
    };

    /// <summary>
    /// Existing Rule 2 violations in files this change does not own, keyed by exact pragma text. Fails in
    /// both directions (see the class summary). Each note says what clears the entry.
    /// </summary>
    private static readonly (string Path, string Pragma, string Clears)[] Grandfathered =
    [
        ("src/Ashlar.Sdk/Legacy/AshlarSdkBuilder.cs",
            "#pragma warning disable CS0618 // intentional obsolete forwarding surface for NuGet compatibility",
            "DELETE the line: it is dead (measured: Ashlar.Sdk builds clean without it; the only use of "
            + "AshlarSdkBuilder here is inside its own [Obsolete] declaration)."),
        ("src/Ashlar.Sdk/Legacy/AshlarSdkLegacyExtensions.cs",
            "#pragma warning disable CS0618 // intentional obsolete forwarding surface for NuGet compatibility",
            "DELETE the line: it is dead (measured: Ashlar.Sdk builds clean without it; every use of "
            + "AshlarSdkBuilder here is inside the [Obsolete] AddAshlarSdk)."),
        ("src/Ashlar.Tests.Infrastructure/Tests/SDK/HostAshlarSdkBuilderGapCoverageTests.cs",
            "#pragma warning disable CS0618",
            "live, but says nothing: '// AshlarSdkBuilder: this fact exercises the obsolete forwarding "
            + "constructor on purpose'."),
        ("src/Ashlar.Infrastructure/Certification/AnalyzerFenceGate.cs",
            "#pragma warning disable ASHLAREXP001 // The gate enforces the experimental autonomy contract (touch-set, kernel prefixes) by design; see docs/SdkCompatibilityPolicy.md.",
            "live (TouchSet, TrustKernel); name them: '// TouchSet, TrustKernel: the gate enforces ...'."),
        ("src/Ashlar.Infrastructure/Certification/AnalyzerFencePostValidator.cs",
            "#pragma warning disable ASHLAREXP001 // The gate enforces the experimental autonomy contract (touch-set) by design; see docs/SdkCompatibilityPolicy.md.",
            "live (TouchSet); name it: '// TouchSet: the gate enforces ...'."),
        ("src/Ashlar.Infrastructure/Certification/CertificationGate.cs",
            "#pragma warning disable ASHLAREXP001 // The gate enforces the experimental autonomy contract (touch-set, lineage) by design; see docs/SdkCompatibilityPolicy.md.",
            "live (GenerationLineage, RecursionDiscipline, CertificationRequest.Lineage and .TouchSet); "
            + "name them: '// GenerationLineage, RecursionDiscipline, Lineage, TouchSet: the gate enforces ...'."),
        ("src/Ashlar.Infrastructure/Certification/GenerationLineageInputs.cs",
            "#pragma warning disable ASHLAREXP001 // Projects the experimental lineage contract into certificate evidence by design; see docs/SdkCompatibilityPolicy.md.",
            "live (GenerationLineage); name it: '// GenerationLineage: projects the experimental ...'."),
        ("src/Ashlar.BackgroundAgents/Objectives/ObjectiveDocumentParser.cs",
            "#pragma warning disable ASHLAREXP001 // Parses the experimental tiering fields (source, touch-set) of an objective by design; see docs/SdkCompatibilityPolicy.md.",
            "live (ObjectiveSource, TouchSet, ObjectiveDocument.Source and .Touch); name them: "
            + "'// ObjectiveSource, TouchSet, Source, Touch: parses the experimental tiering fields ...'."),
    ];

    /// <summary>
    /// Every project-wide disable of CS0618 or ASHLAREXP001 in the tree, keyed by file and exact setting
    /// text. A project-wide disable covers every file its project compiles, so it cannot name a symbol;
    /// the reason lives here instead, and a new one needs a reviewed entry. Fails in both directions,
    /// like <see cref="Grandfathered"/>: an unlisted disable fails, and so does an entry whose setting
    /// has been edited or deleted. Both below were measured on f0fd3fde and each carries its own comment
    /// in the file saying the same thing.
    /// </summary>
    private static readonly (string Path, string Setting, string Why)[] ReviewedProjectWideDisables =
    [
        ("Directory.Build.targets",
            "<NoWarn>$(NoWarn);ASHLAREXP001</NoWarn>",
            "Conditioned on IsTestProject: the repo's own test projects exercise the experimental autonomy "
            + "surface by design, and the diagnostic exists for EXTERNAL consumers "
            + "(docs/SdkCompatibilityPolicy.md, 'Experimental tier')."),
        ("spikes/autonomy-first-flight/FirstFlight/FirstFlight.csproj",
            "<NoWarn>$(NoWarn);ASHLAREXP001</NoWarn>",
            "The spike IS a consumer of the experimental autonomy surface; it is outside Ashlar.sln and "
            + "every CI gate, and its output is a recorded run, not a shipped artifact."),
    ];

    private static readonly Lazy<RepoTree> Tree = new(() => RepoTree.Load(RepoPathResolver.FindRepoRoot()));

    private readonly ITestOutputHelper _output;

    public DiagnosticSuppressionConventionTests(ITestOutputHelper output) => _output = output;

    // ------------------------------------------------------------------------------------------------
    // The real tree
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void Nothing_in_the_tree_switches_off_public_api_tracking()
    {
        var tree = Tree.Value;
        tree.Sources.Count.Should().BeGreaterThanOrEqualTo(SourceFilesFloor,
            "the walk read {0} C# files; below the stated floor of {1} it is broken and an empty scan would pass",
            tree.Sources.Count, SourceFilesFloor);
        tree.MsBuild.Count.Should().BeGreaterThanOrEqualTo(MsBuildFilesFloor,
            "the walk read {0} MSBuild files; below the stated floor of {1} it is broken", tree.MsBuild.Count, MsBuildFilesFloor);
        tree.AnalyzerConfigs.Should().Contain(c => c.Path == ".editorconfig",
            "the root .editorconfig applies to every project; a walk that misses it reads no analyzer config at all");

        var problems = new List<string>();
        foreach (var source in tree.Sources)
            problems.AddRange(ApiTrackingSourceViolations(source));
        foreach (var (path, doc) in tree.MsBuild)
            problems.AddRange(ApiTrackingMsBuildViolations(path, doc, tree.GovernsApiProject(path)));
        foreach (var (path, text) in tree.AnalyzerConfigs)
            problems.AddRange(ApiTrackingAnalyzerConfigViolations(path, text, tree.ConfigAppliesToApiProject(path)));

        problems.Should().BeEmpty(
            "public-API tracking is what makes a stable package's promise enforceable; switching it off "
            + "for a file or a project leaves that surface promised by nothing. Record the surface in "
            + "PublicAPI.Shipped.txt or PublicAPI.Unshipped.txt instead (docs/SdkCompatibilityPolicy.md). "
            + "{0} found:\n{1}", problems.Count, string.Join("\n", problems));

        _output.WriteLine($"sources={tree.Sources.Count} msbuild={tree.MsBuild.Count} "
            + $"analyzer-configs={tree.AnalyzerConfigs.Count} api-projects={tree.ApiProjects.Count}");
    }

    [Fact]
    public void Every_public_api_analyzer_project_wires_both_api_files()
    {
        var tree = Tree.Value;
        tree.ApiProjects.Count.Should().BeGreaterThanOrEqualTo(ApiProjectsFloor,
            "{0} projects reference {1}; below the stated floor of {2} discovery is broken and every check "
            + "scoped to them passes on nothing", tree.ApiProjects.Count, ApiAnalyzerPackage, ApiProjectsFloor);
        tree.ApiProjects.Should().Contain(BrickContractsProject);

        var shared = tree.MsBuild
            .Where(m => !m.Path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .Where(m => m.Doc.Descendants().Any(e =>
                (e.Name.LocalName is "PackageReference" or "GlobalPackageReference")
                && string.Equals((string?)e.Attribute("Include"), ApiAnalyzerPackage, StringComparison.OrdinalIgnoreCase)))
            .Select(m => m.Path)
            .ToList();
        shared.Should().BeEmpty(
            "discovery reads the analyzer reference from project files; a shared MSBuild file that adds it "
            + "makes every project below it an analyzer project that this convention cannot see. Extend "
            + "discovery before adding one: {0}", string.Join(", ", shared));

        var problems = new List<string>();
        foreach (var project in tree.ApiProjects)
        {
            var doc = tree.MsBuild.Single(m => m.Path == project).Doc;
            var included = doc.Descendants()
                .Where(e => e.Name.LocalName == "AdditionalFiles")
                .Select(e => ((string?)e.Attribute("Include") ?? "").Replace('\\', '/').Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var dir = DirectoryOf(project);
            foreach (var file in new[] { "PublicAPI.Shipped.txt", "PublicAPI.Unshipped.txt" })
            {
                if (!included.Contains(file))
                    problems.Add($"{project}: no <AdditionalFiles Include=\"{file}\" />, so the analyzer never reads it");
                if (!File.Exists(Path.Combine(tree.Root, dir, file)))
                    problems.Add($"{project}: {dir}/{file} does not exist");
            }
        }

        problems.Should().BeEmpty("the analyzer compares the surface against these two files and nothing else:\n{0}",
            string.Join("\n", problems));
    }

    [Fact]
    public void Every_obsolete_or_experimental_disable_names_a_live_symbol_and_a_reason()
    {
        var tree = Tree.Value;
        var symbols = tree.Symbols;
        symbols.Experimental.Count.Should().BeGreaterThanOrEqualTo(ExperimentalSymbolsFloor,
            "found {0} [Experimental({1})] names; below the stated floor of {2} the declaration parser is broken "
            + "and every pragma would read as naming nothing", symbols.Experimental.Count, ExperimentalId, ExperimentalSymbolsFloor);
        symbols.Obsolete.Count.Should().BeGreaterThanOrEqualTo(ObsoleteSymbolsFloor,
            "found {0} CS0618 [Obsolete] names against a stated floor of {1}. If the tree still declares an "
            + "[Obsolete] symbol (without error: true or a DiagnosticId), the declaration parser is broken. If "
            + "the last one was removed at the end of its deprecation, lower ObsoleteSymbolsFloor to 0 in "
            + "that diff", symbols.Obsolete.Count, ObsoleteSymbolsFloor);
        symbols.Obsolete.Should().Contain("AshlarSdkBuilder",
            "src/Ashlar.Hosting/Sdk/Builders/AshlarSdkBuilder.cs declared it [Obsolete] when this anchor was "
            + "written. If it is still declared there, the declaration parser is broken. If it was removed - "
            + "the normal end of a deprecation under docs/SdkCompatibilityPolicy.md - point this anchor at "
            + "another in-tree [Obsolete] symbol, or delete it together with the floor above");
        symbols.Obsolete.Should().NotContain("Mutate",
            "LiteDbAtomic.Mutate is [Obsolete(..., error: true)]: a use is CS0619, which a CS0618 disable cannot silence");
        symbols.Experimental.Should().Contain(new[] { "TouchSet", "GenerationLineage", "Source", "Touch" },
            "these were declared [Experimental(ASHLAREXP001)] when this anchor was written. If they still are, "
            + "the declaration parser is broken. If one graduated or was removed, point the anchor (and the "
            + "ObjectiveDocument.cs anchor below) at another experimental symbol in that diff");

        var pragmas = tree.Sources.SelectMany(Pragmas).Where(p => p.Disable).ToList();
        pragmas.Count.Should().BeGreaterThanOrEqualTo(DisablePragmasFloor,
            "found {0} warning-disable pragmas; below the stated floor of {1} the pragma scan is broken",
            pragmas.Count, DisablePragmasFloor);

        var found = tree.Sources.SelectMany(s => DeprecationSuppressionViolations(s, symbols))
            .Concat(tree.Sources.SelectMany(DeprecationSuppressAttributeViolations))
            .ToList();

        // Positive anchor: the one real disable that already complies must be seen AND pass, so a scan
        // that stopped finding uses (every pragma "dead") or stopped finding pragmas cannot pass here.
        const string compliant = "src/Ashlar.BackgroundAgents/Objectives/ObjectiveDocument.cs";
        pragmas.Should().Contain(p => p.Path == compliant && p.Ids.Contains(ExperimentalId),
            "{0} carries a live, named ASHLAREXP001 disable; not finding it means the scan is broken", compliant);
        found.Should().NotContain(v => v.Path == compliant,
            "{0}'s disable names Source and Touch, both used outside their [Experimental] declarations", compliant);

        var allowed = Grandfathered.Select(g => (g.Path, g.Pragma)).ToHashSet();
        var unexpected = found.Where(v => !allowed.Contains((v.Path, v.Pragma))).Select(v => v.Message).ToList();
        var seen = found.Select(v => (v.Path, v.Pragma)).ToHashSet();
        var stale = Grandfathered.Where(g => !seen.Contains((g.Path, g.Pragma)))
            .Select(g => $"{g.Path}: '{g.Pragma}' is grandfathered but no longer violates; delete its entry")
            .ToList();

        unexpected.Should().BeEmpty(
            "a CS0618/ASHLAREXP001 disable must name, as an exact identifier, an in-tree [Obsolete] or "
            + "[Experimental({0})] symbol that the code it governs still uses outside that symbol's own "
            + "context, and say why in at least {1} words. A dead disable silences the next use written "
            + "into its file. {2} found:\n{3}", ExperimentalId, MinimumReasonWords, unexpected.Count, string.Join("\n", unexpected));
        stale.Should().BeEmpty("the grandfather list only shrinks, and each shrink is a reviewed diff:\n{0}",
            string.Join("\n", stale));

        _output.WriteLine($"experimental-names={symbols.Experimental.Count} obsolete-names={symbols.Obsolete.Count} "
            + $"disable-pragmas={pragmas.Count} violations={found.Count} grandfathered={Grandfathered.Length}");
        foreach (var p in pragmas)
            _output.WriteLine($"  {p.Path}:{p.Line} disable {string.Join(",", p.Ids)}");
        foreach (var g in Grandfathered)
            _output.WriteLine($"  grandfathered {g.Path}: {g.Clears}");
    }

    [Fact]
    public void Every_project_wide_obsolete_or_experimental_disable_is_reviewed()
    {
        var tree = Tree.Value;
        tree.MsBuild.Count.Should().BeGreaterThanOrEqualTo(MsBuildFilesFloor,
            "the walk read {0} MSBuild files; below the stated floor of {1} it is broken", tree.MsBuild.Count, MsBuildFilesFloor);
        tree.AnalyzerConfigs.Should().Contain(c => c.Path == ".editorconfig",
            "the root .editorconfig applies to every project; a walk that misses it reads no analyzer config at all");

        var found = tree.MsBuild.SelectMany(m => DeprecationMsBuildDisables(m.Path, m.Doc))
            .Concat(tree.AnalyzerConfigs.SelectMany(c => DeprecationAnalyzerConfigDisables(c.Path, c.Text)))
            .ToList();

        // Non-vacuity is the stale direction: the two reviewed entries exist today, so a scan that stopped
        // finding anything turns both of them stale and fails below.
        var reviewed = ReviewedProjectWideDisables.Select(r => (r.Path, r.Setting)).ToHashSet();
        var unexpected = found.Where(v => !reviewed.Contains((v.Path, v.Pragma))).Select(v => v.Message).ToList();
        var seen = found.Select(v => (v.Path, v.Pragma)).ToHashSet();
        var stale = ReviewedProjectWideDisables.Where(r => !seen.Contains((r.Path, r.Setting)))
            .Select(r => $"{r.Path}: '{r.Setting}' is on the reviewed list but no longer found; delete its entry")
            .ToList();

        unexpected.Should().BeEmpty(
            "a project-wide disable of {0} or {1} silences every use in every file the project compiles, so it "
            + "cannot name the symbol it is for or say why. Use a scoped '#pragma warning disable' that names "
            + "the symbol and gives a reason (Rule 2), or, if the whole project genuinely consumes the surface, "
            + "add a ReviewedProjectWideDisables entry that says why. {2} found:\n{3}",
            ObsoleteId, ExperimentalId, unexpected.Count, string.Join("\n", unexpected));
        stale.Should().BeEmpty("the reviewed list must match the tree exactly:\n{0}", string.Join("\n", stale));

        _output.WriteLine($"project-wide-disables={found.Count} reviewed={ReviewedProjectWideDisables.Length}");
        foreach (var v in found)
            _output.WriteLine($"  {v.Message}");
    }

    // ------------------------------------------------------------------------------------------------
    // Controls: the matchers over in-memory text. Negative controls must be caught; positive ones not.
    // ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("#pragma warning disable RS0016\npublic class A { }")]
    [InlineData("  #  pragma   warning   disable   CS1591, rs0017 // mixed case, second id\npublic class A { }")]
    [InlineData("#pragma warning disable RS0026\n")]
    [InlineData("#pragma warning disable RS0027 // restored later\n#pragma warning restore RS0027\n")]
    [InlineData("#pragma warning disable\npublic class A { }")]
    [InlineData("[SuppressMessage(\"ApiDesign\", \"RS0016:Add public types and members to the declared API\")]\npublic class A { }")]
    [InlineData("[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage(\"ApiDesign\", \"RS0017\", Justification = \"x\")]")]
    [InlineData("[UnconditionalSuppressMessage(\"ApiDesign\",\n    \"RS0026:Do not add multiple overloads with optional parameters\")]\npublic class A { }")]
    [InlineData("[SuppressMessageAttribute(\"ApiDesign\", \"RS0027\")] public void M() { }")]
    public void A_source_suppression_of_api_tracking_is_caught(string source)
    {
        var file = SourceFile.Parse("fixture/A.cs", source);
        ApiTrackingSourceViolations(file).Should().NotBeEmpty("this spelling switches off public-API tracking:\n{0}", source);
    }

    [Theory]
    [InlineData("#pragma warning restore RS0016\n")]
    [InlineData("#pragma warning disable RS1001 // Missing [DiagnosticAnalyzer]: intentional\n#pragma warning restore RS1001\n")]
    [InlineData("#pragma warning disable RS00160 // not an id this rule owns\n")]
    [InlineData("// #pragma warning disable RS0016 (commented out)\npublic class A { }")]
    [InlineData("/*\n#pragma warning disable RS0016\n*/\npublic class A { }")]
    [InlineData("var s = \"#pragma warning disable RS0016\";")]
    [InlineData("var s = @\"\n#pragma warning disable RS0016\n\";")]
    [InlineData("var s = \"\"\"\n#pragma warning disable RS0016\n[SuppressMessage(\"ApiDesign\", \"RS0016\")]\n\"\"\";")]
    [InlineData("[SuppressMessage(\"Design\", \"CA1062:Validate arguments\")] public void M(object o) { }")]
    [InlineData("var text = \"SuppressMessage(\\\"ApiDesign\\\", \\\"RS0016\\\")\";")]
    public void A_source_that_does_not_suppress_api_tracking_passes(string source)
    {
        var file = SourceFile.Parse("fixture/A.cs", source);
        ApiTrackingSourceViolations(file).Should().BeEmpty("this text does not suppress public-API tracking:\n{0}", source);
    }

    [Theory]
    [InlineData("<Project><PropertyGroup><NoWarn>$(NoWarn);1591;RS0016</NoWarn></PropertyGroup></Project>", false)]
    [InlineData("<Project><PropertyGroup><NoWarn>$(NoWarn);RS0017;</NoWarn></PropertyGroup></Project>", false)]
    [InlineData("<Project><PropertyGroup><WarningsNotAsErrors>rs0026</WarningsNotAsErrors></PropertyGroup></Project>", false)]
    [InlineData("<Project><ItemGroup><PackageReference Include=\"X\" NoWarn=\"RS0027\" /></ItemGroup></Project>", false)]
    [InlineData("<Project><PropertyGroup><RunAnalyzers>false</RunAnalyzers></PropertyGroup></Project>", true)]
    [InlineData("<Project><PropertyGroup><RunAnalyzersDuringBuild> False </RunAnalyzersDuringBuild></PropertyGroup></Project>", true)]
    [InlineData("<Project><ItemGroup><AdditionalFiles Remove=\"PublicAPI.Shipped.txt\" /></ItemGroup></Project>", true)]
    public void An_msbuild_suppression_of_api_tracking_is_caught(string xml, bool governsApiProject)
    {
        ApiTrackingMsBuildViolations("fixture/A.csproj", XDocument.Parse(xml), governsApiProject)
            .Should().NotBeEmpty("this switches off public-API tracking:\n{0}", xml);
    }

    [Theory]
    [InlineData("<Project><PropertyGroup><NoWarn>$(NoWarn);1591;RS0036;RS0037</NoWarn></PropertyGroup></Project>", true)]
    [InlineData("<Project><PropertyGroup><RunAnalyzers>false</RunAnalyzers></PropertyGroup></Project>", false)]
    [InlineData("<Project><PropertyGroup><RunAnalyzers>true</RunAnalyzers><WarningsAsErrors>RS0016</WarningsAsErrors></PropertyGroup></Project>", true)]
    [InlineData("<Project><ItemGroup><AdditionalFiles Include=\"PublicAPI.Shipped.txt\" /><AdditionalFiles Remove=\"stale.json\" /></ItemGroup></Project>", true)]
    [InlineData("<Project><ItemGroup><AdditionalFiles Remove=\"PublicAPI.Shipped.txt\" /></ItemGroup></Project>", false)]
    public void An_msbuild_file_that_does_not_suppress_api_tracking_passes(string xml, bool governsApiProject)
    {
        ApiTrackingMsBuildViolations("fixture/A.csproj", XDocument.Parse(xml), governsApiProject)
            .Should().BeEmpty("this does not switch off public-API tracking for an analyzer project:\n{0}", xml);
    }

    [Theory]
    [InlineData("[*.cs]\ndotnet_diagnostic.RS0016.severity = none", false, true)]
    [InlineData("[*.cs]\ndotnet_diagnostic.rs0017.severity=silent", false, true)]
    [InlineData("is_global = true\ndotnet_diagnostic.RS0027.severity = suggestion", false, true)]
    [InlineData("[*.cs]\ndotnet_analyzer_diagnostic.category-ApiDesign.severity = none", true, true)]
    [InlineData("[*.cs]\ndotnet_analyzer_diagnostic.severity = silent", true, true)]
    [InlineData("[*.cs]\ndotnet_diagnostic.RS0016.severity = error", true, false)]
    [InlineData("[*.cs]\n# dotnet_diagnostic.RS0016.severity = none", true, false)]
    [InlineData("[*.cs]\ndotnet_diagnostic.RS0036.severity = none", true, false)]
    [InlineData("[*.cs]\ndotnet_analyzer_diagnostic.category-ApiDesign.severity = none", false, false)]
    [InlineData("[*.cs]\ndotnet_analyzer_diagnostic.category-Style.severity = none", true, false)]
    [InlineData("[*.cs]\ndotnet_public_api_analyzer.skip_namespaces = Ashlar.Core.Domain.Bricks.Ports", false, true)]
    [InlineData("[*.cs]\ndotnet_public_api_analyzer.require_api_files = true", true, false)]
    public void An_analyzer_config_relaxing_api_tracking_is_caught_and_only_then(string text, bool appliesToApiProject, bool caught)
    {
        var problems = ApiTrackingAnalyzerConfigViolations("fixture/.editorconfig", text, appliesToApiProject).ToList();
        (problems.Count > 0).Should().Be(caught, "config:\n{0}\nproblems: {1}", text, string.Join("; ", problems));
    }

    /// <summary>
    /// The defect this convention was written for, verbatim: HostAshlarSdkBuilder.cs as it stood on
    /// f0fd3fde, 88 days after #223 moved the forwarder its CS0618 disable was written for.
    /// </summary>
    private const string DeadPragmaAsShipped = """
        using Ashlar.Abstractions;
        using Ashlar.Core.Domain.Agents;
        using Ashlar.Core.Domain.Bricks;
        using Ashlar.Infrastructure.Sdk.Ports;

        namespace Ashlar.Hosting.Sdk.Builders;
        #pragma warning disable CS0618 // AshlarSdkBuilder is an obsolete type forwarder in this file

        /// <summary>
        /// Default implementation of <see cref="IAshlarSdkBuilder"/>. Configures <see cref="AshlarSdkOptions"/> for kernel registration.
        /// </summary>
        public class HostAshlarSdkBuilder : IAshlarSdkBuilder
        {
            private readonly AshlarSdkOptions _options;

            /// <summary>
            /// Creates a new SDK builder with the given options.
            /// </summary>
            /// <param name="options">Options to populate.</param>
            public HostAshlarSdkBuilder(AshlarSdkOptions options)
            {
                _options = options ?? throw new ArgumentNullException(nameof(options));
            }

            /// <inheritdoc />
            public IAshlarSdkBuilder RegisterBrick<T>() where T : DomainBrick
            {
                _options.BrickTypes.Add(typeof(T));
                return this;
            }
        }
        """;

    /// <summary>The forwarder's own file on f0fd3fde: the declaration the dead pragma pointed at.</summary>
    private const string ForwarderAsShipped = """
        namespace Ashlar.Hosting.Sdk.Builders;

        /// <summary>
        /// Back-compat type name for <see cref="HostAshlarSdkBuilder"/>.
        /// </summary>
        [Obsolete("Renamed to HostAshlarSdkBuilder.", error: false)]
        public sealed class AshlarSdkBuilder : HostAshlarSdkBuilder
        {
            /// <inheritdoc cref="HostAshlarSdkBuilder(AshlarSdkOptions)"/>
            public AshlarSdkBuilder(AshlarSdkOptions options)
                : base(options)
            {
            }
        }
        """;

    private const string ExperimentalDeclarations = """
        namespace Ashlar.Core.Application.Autonomy;

        [Experimental(AutonomyExperimental.DiagnosticId, UrlFormat = AutonomyExperimental.UrlFormat)]
        public sealed record TouchSet(IReadOnlyList<string> Paths);

        public sealed class LegacyStore
        {
            [Obsolete("Use the async overload.", error: true)]
            public void Mutate(Action a) { }

            [Obsolete("Has its own id.", DiagnosticId = "ASHOBS1")]
            public void Reset() { }
        }
        """;

    /// <summary>
    /// The shipped pragma as written, and in the bare-number spellings the compiler reads as the same id
    /// (Roslyn turns a numeric pragma id into "CS" + the number padded to four digits).
    /// </summary>
    [Theory]
    [InlineData("CS0618")]
    [InlineData("618")]
    [InlineData("0618")]
    [InlineData("cs0618")]
    public void The_disable_that_outlived_its_forwarder_is_caught_as_dead(string spelling)
    {
        var source = DeadPragmaAsShipped.Replace("#pragma warning disable CS0618 ", $"#pragma warning disable {spelling} ",
            StringComparison.Ordinal);
        source.Should().Contain($"#pragma warning disable {spelling} // AshlarSdkBuilder", "the fixture must carry the spelling under test");

        var violations = Rule2(source, ForwarderAsShipped);

        violations.Should().ContainSingle("the shipped HostAshlarSdkBuilder.cs pragma governs no use of AshlarSdkBuilder")
            .Which.Message.Should().Contain("dead").And.Contain("AshlarSdkBuilder");
    }

    [Theory]
    // A live, named, reasoned disable passes.
    [InlineData("#pragma warning disable CS0618 // AshlarSdkBuilder: this fact pins the forwarding constructor on purpose\nvar b = new AshlarSdkBuilder(options);\n#pragma warning restore CS0618\n", null)]
    // Fully qualified use counts as a use.
    [InlineData("#pragma warning disable CS0618 // AshlarSdkBuilder kept for one more minor release\nvar b = new Ashlar.Hosting.Sdk.Builders.AshlarSdkBuilder(o);\n", null)]
    // No comment at all.
    [InlineData("#pragma warning disable CS0618\nvar b = new AshlarSdkBuilder(options);\n", "no comment")]
    // Names a symbol that is not [Obsolete].
    [InlineData("#pragma warning disable CS0618 // HostAshlarSdkBuilder is the forwarding target here\nvar b = new AshlarSdkBuilder(options);\n", "names no")]
    // Names the symbol but gives no reason.
    [InlineData("#pragma warning disable CS0618 // AshlarSdkBuilder\nvar b = new AshlarSdkBuilder(options);\n", "reason")]
    // Restored before the only use: dead.
    [InlineData("#pragma warning disable CS0618 // AshlarSdkBuilder: pins the forwarding constructor\n#pragma warning restore CS0618\nvar b = new AshlarSdkBuilder(options);\n", "dead")]
    // The only mention is inside a string literal: dead.
    [InlineData("#pragma warning disable CS0618 // AshlarSdkBuilder: pins the forwarding constructor\nvar s = \"AshlarSdkBuilder\";\n", "dead")]
    // The only mention is a substring of another identifier: dead.
    [InlineData("#pragma warning disable CS0618 // AshlarSdkBuilder: pins the forwarding constructor\nvar b = new HostAshlarSdkBuilder(options);\n", "dead")]
    // The only use is inside an [Obsolete] member, where the compiler never reports CS0618: dead.
    [InlineData("#pragma warning disable CS0618 // AshlarSdkBuilder: forwarding surface for NuGet compatibility\npublic static class Legacy\n{\n    [Obsolete(\"Use the new one.\", error: false)]\n    public static void AddAshlarSdk(Action<AshlarSdkBuilder>? configure = null)\n    {\n        configure?.Invoke(new AshlarSdkBuilder(null!));\n    }\n}\n", "dead")]
    // ...but the same use outside that member is live.
    [InlineData("#pragma warning disable CS0618 // AshlarSdkBuilder: forwarding surface for NuGet compatibility\npublic static class Legacy\n{\n    [Obsolete(\"Use the new one.\", error: false)]\n    public static void AddAshlarSdk() { }\n\n    public static object Make() => new AshlarSdkBuilder(null!);\n}\n", null)]
    // CS0619 (error: true) and a custom DiagnosticId are not CS0618: naming them names nothing.
    [InlineData("#pragma warning disable CS0618 // Mutate is still called by the migration shim\nstore.Mutate(() => { });\n", "names no")]
    [InlineData("#pragma warning disable CS0618 // Reset is still called by the migration shim\nstore.Reset();\n", "names no")]
    // ASHLAREXP001: exact identifier required; prose spelling does not name the symbol.
    [InlineData("#pragma warning disable ASHLAREXP001 // TouchSet: the gate enforces the experimental contract by design\nvar t = new TouchSet(paths);\n", null)]
    [InlineData("#pragma warning disable ASHLAREXP001 // The gate enforces the experimental contract (touch-set) by design\nvar t = new TouchSet(paths);\n", "names no")]
    // A disable naming both ids is checked for each.
    [InlineData("#pragma warning disable CS0618, ASHLAREXP001 // TouchSet: the gate enforces the experimental contract by design\nvar t = new TouchSet(paths);\n", "names no")]
    // The bare number is CS0618 to the compiler, so it is judged exactly like CS0618.
    [InlineData("#pragma warning disable 618\nvar b = new AshlarSdkBuilder(options);\n", "no comment")]
    [InlineData("#pragma warning disable 0618 // AshlarSdkBuilder: pins the forwarding constructor\nvar s = \"x\";\n", "dead")]
    [InlineData("#pragma warning disable 618 // HostAshlarSdkBuilder is the forwarding target here\nvar b = new AshlarSdkBuilder(options);\n", "names no")]
    [InlineData("#pragma warning disable 618 // AshlarSdkBuilder: this fact pins the forwarding constructor on purpose\nvar b = new AshlarSdkBuilder(options);\n#pragma warning restore 618\n", null)]
    // A numeric restore ends a CS0618 region, so the use after it is not governed: dead.
    [InlineData("#pragma warning disable CS0618 // AshlarSdkBuilder: pins the forwarding constructor\n#pragma warning restore 0618\nvar b = new AshlarSdkBuilder(options);\n", "dead")]
    // Other numbers are other ids: CS8618 and CS6180 are not CS0618, so Rule 2 has nothing to say.
    [InlineData("#pragma warning disable 8618, 6180\nvar b = new AshlarSdkBuilder(options);\n", null)]
    public void A_deprecation_disable_is_judged_on_name_liveness_and_reason(string source, string? expected)
    {
        var violations = Rule2(source, ForwarderAsShipped, ExperimentalDeclarations);

        if (expected is null)
            violations.Should().BeEmpty("this disable names a live symbol and says why:\n{0}", source);
        else
            violations.Should().ContainSingle("source:\n{0}", source).Which.Message.Should().Contain(expected);
    }

    [Theory]
    [InlineData("[SuppressMessage(\"Usage\", \"CS0618:Type or member is obsolete\", Justification = \"AshlarSdkBuilder forwarding\")]\npublic void M() { }", true)]
    [InlineData("[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage(\"Usage\", \"ASHLAREXP001\")]", true)]
    [InlineData("[UnconditionalSuppressMessage(\"Usage\",\n    \"cs0618\")]\npublic void M() { }", true)]
    [InlineData("[SuppressMessage(\"Usage\", \"CS8618:Non-nullable field\")]\npublic void M() { }", false)]
    [InlineData("[SuppressMessage(\"Usage\", \"ASHLAREXP0010\")]\npublic void M() { }", false)]
    [InlineData("// [SuppressMessage(\"Usage\", \"CS0618\")]\npublic void M() { }", false)]
    [InlineData("var s = \"[SuppressMessage(\\\"Usage\\\", \\\"CS0618\\\")]\";", false)]
    public void A_suppress_attribute_naming_a_deprecation_id_is_caught_and_only_then(string source, bool caught)
    {
        var problems = DeprecationSuppressAttributeViolations(SourceFile.Parse("fixture/A.cs", source)).ToList();
        (problems.Count > 0).Should().Be(caught, "source:\n{0}\nproblems: {1}", source,
            string.Join("; ", problems.Select(p => p.Message)));
    }

    [Theory]
    // Every spelling of the id the compiler accepts, in an element or an attribute, in either property.
    [InlineData("<Project><PropertyGroup><NoWarn>$(NoWarn);CS0618</NoWarn></PropertyGroup></Project>", true)]
    [InlineData("<Project><PropertyGroup><NoWarn>$(NoWarn);618</NoWarn></PropertyGroup></Project>", true)]
    [InlineData("<Project><PropertyGroup><NoWarn>0618,1591</NoWarn></PropertyGroup></Project>", true)]
    [InlineData("<Project><PropertyGroup Condition=\"'$(IsTestProject)' == 'true'\"><NoWarn>$(NoWarn);ashlarexp001</NoWarn></PropertyGroup></Project>", true)]
    [InlineData("<Project><PropertyGroup><WarningsNotAsErrors>$(WarningsNotAsErrors);CS0618</WarningsNotAsErrors></PropertyGroup></Project>", true)]
    [InlineData("<Project><ItemGroup><PackageReference Include=\"X\" NoWarn=\"618\" /></ItemGroup></Project>", true)]
    // Other ids, and settings that strengthen rather than disable.
    [InlineData("<Project><PropertyGroup><NoWarn>$(NoWarn);1591;CS8618;RS0036;RS0037</NoWarn></PropertyGroup></Project>", false)]
    [InlineData("<Project><PropertyGroup><NoWarn>$(NoWarn);6180;ASHLAREXP0010;CS06180</NoWarn></PropertyGroup></Project>", false)]
    [InlineData("<Project><PropertyGroup><WarningsAsErrors>CS0618;ASHLAREXP001</WarningsAsErrors></PropertyGroup></Project>", false)]
    [InlineData("<Project><!-- <NoWarn>$(NoWarn);CS0618</NoWarn> --></Project>", false)]
    public void An_msbuild_disable_of_a_deprecation_id_is_caught_and_only_then(string xml, bool caught)
    {
        var problems = DeprecationMsBuildDisables("fixture/A.csproj", XDocument.Parse(xml)).ToList();
        (problems.Count > 0).Should().Be(caught, "xml:\n{0}\nproblems: {1}", xml, string.Join("; ", problems.Select(p => p.Message)));
    }

    [Theory]
    [InlineData("[*.cs]\ndotnet_diagnostic.CS0618.severity = none", true)]
    [InlineData("[*.cs]\ndotnet_diagnostic.cs0618.severity=silent", true)]
    [InlineData("is_global = true\ndotnet_diagnostic.ASHLAREXP001.severity = suggestion", true)]
    [InlineData("[*.cs]\ndotnet_diagnostic.CS0618.severity = error", false)]
    [InlineData("[*.cs]\n# dotnet_diagnostic.CS0618.severity = none", false)]
    [InlineData("[*.cs]\ndotnet_diagnostic.CS8618.severity = none", false)]
    public void An_analyzer_config_disable_of_a_deprecation_id_is_caught_and_only_then(string text, bool caught)
    {
        var problems = DeprecationAnalyzerConfigDisables("fixture/.editorconfig", text).ToList();
        (problems.Count > 0).Should().Be(caught, "config:\n{0}\nproblems: {1}", text, string.Join("; ", problems.Select(p => p.Message)));
    }

    [Fact]
    public void The_lexer_blanks_comments_and_literals_and_keeps_every_line()
    {
        const string source = "var a = \"x // y\"; // tail\n/* block\n spans */ var b = 'c';\n#if DEBUG // note\nvar c = @\"v\"\"w\";\n#endif\n";
        var file = SourceFile.Parse("fixture/A.cs", source);

        file.Bare.Length.Should().Be(file.Text.Length);
        file.Bare.Split('\n').Length.Should().Be(file.Text.Split('\n').Length);
        file.Bare.Should().NotContain("tail").And.NotContain("block").And.NotContain("x //").And.NotContain("note");
        file.Bare.Should().Contain("var a =").And.Contain("var b =").And.Contain("#if DEBUG").And.Contain("var c =");
        file.Code.Should().Contain("\"x // y\"", "Code keeps literals, which the attribute reader needs");
        file.Code.Should().NotContain("tail");
    }

    private static List<Violation> Rule2(string source, params string[] declarationFiles)
    {
        var files = new List<SourceFile> { SourceFile.Parse("fixture/Subject.cs", source) };
        files.AddRange(declarationFiles.Select((text, i) => SourceFile.Parse($"fixture/Decl{i}.cs", text)));
        var symbols = DeclaredSymbols.Collect(files);
        return DeprecationSuppressionViolations(files[0], symbols).ToList();
    }

    // ------------------------------------------------------------------------------------------------
    // Rule 1 matchers
    // ------------------------------------------------------------------------------------------------

    private static IEnumerable<string> ApiTrackingSourceViolations(SourceFile file)
    {
        foreach (var pragma in Pragmas(file).Where(p => p.Disable))
        {
            if (pragma.Ids.Count == 0)
            {
                yield return $"{file.Path}:{pragma.Line}: a bare '#pragma warning disable' switches off every diagnostic "
                    + "in the file, public-API tracking and CS0618/ASHLAREXP001 included; name the ids";
                continue;
            }

            foreach (var id in pragma.Ids.Where(id => ApiTrackingId.IsMatch(id) && id.Length == 6))
                yield return $"{file.Path}:{pragma.Line}: '#pragma warning disable {id}' switches off public-API tracking";
        }

        foreach (Match m in SuppressAttribute.Matches(file.Bare))
        {
            var open = m.Index + m.Length - 1;
            var close = MatchForward(file.Bare, open, '(', ')');
            var args = file.Code[open..(close < 0 ? file.Code.Length : close + 1)];
            if (ApiTrackingId.IsMatch(args))
                yield return $"{file.Path}:{LineOf(file, m.Index)}: a SuppressMessage attribute switches off public-API tracking "
                    + $"({ApiTrackingId.Match(args).Value})";
        }
    }

    private static IEnumerable<string> ApiTrackingMsBuildViolations(string path, XDocument doc, bool governsApiProject)
    {
        foreach (var element in doc.Descendants())
        {
            var name = element.Name.LocalName;
            if ((name is "NoWarn" or "WarningsNotAsErrors") && ApiTrackingId.IsMatch(element.Value))
                yield return $"{path}: <{name}> lists {ApiTrackingId.Match(element.Value).Value}, which switches off public-API tracking";

            foreach (var attribute in element.Attributes().Where(a => a.Name.LocalName is "NoWarn" or "WarningsNotAsErrors"))
            {
                if (ApiTrackingId.IsMatch(attribute.Value))
                    yield return $"{path}: {name} {attribute.Name.LocalName}=\"{attribute.Value}\" lists a public-API tracking id";
            }

            if (governsApiProject && (name is "RunAnalyzers" or "RunAnalyzersDuringBuild")
                && string.Equals(element.Value.Trim(), "false", StringComparison.OrdinalIgnoreCase))
                yield return $"{path}: <{name}>false</{name}> switches off every analyzer, public-API tracking included, "
                    + "for a project that references " + ApiAnalyzerPackage;

            var removed = (string?)element.Attribute("Remove") ?? "";
            if (governsApiProject && name == "AdditionalFiles" && removed.Contains("PublicAPI", StringComparison.OrdinalIgnoreCase))
                yield return $"{path}: <AdditionalFiles Remove=\"{removed}\"> takes an API file away from the analyzer";
        }
    }

    private static IEnumerable<string> ApiTrackingAnalyzerConfigViolations(string path, string text, bool appliesToApiProject)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line[0] is '#' or ';')
                continue;

            var diagnostic = DiagnosticSeverity.Match(line);
            if (diagnostic.Success && ApiTrackingId.IsMatch(diagnostic.Groups["id"].Value)
                && diagnostic.Groups["id"].Value.Length == 6 && WeakSeverity.IsMatch(diagnostic.Groups["sev"].Value))
                yield return $"{path}:{i + 1}: '{line}' switches off public-API tracking";

            if (SkipNamespaces.IsMatch(line))
                yield return $"{path}:{i + 1}: '{line}' tells the analyzer to skip a namespace, which untracks its surface";

            var analyzer = AnalyzerSeverity.Match(line);
            if (appliesToApiProject && analyzer.Success && WeakSeverity.IsMatch(analyzer.Groups["sev"].Value)
                && (!analyzer.Groups["cat"].Success
                    || string.Equals(analyzer.Groups["cat"].Value, "ApiDesign", StringComparison.OrdinalIgnoreCase)))
                yield return $"{path}:{i + 1}: '{line}' applies to a project that references {ApiAnalyzerPackage} "
                    + "and switches off its ApiDesign rules";
        }
    }

    // ------------------------------------------------------------------------------------------------
    // Rule 2 matcher
    // ------------------------------------------------------------------------------------------------

    private sealed record Violation(string Path, string Pragma, string Message);

    private static IEnumerable<Violation> DeprecationSuppressionViolations(SourceFile file, DeclaredSymbols symbols)
    {
        foreach (var pragma in Pragmas(file).Where(p => p.Disable))
        {
            var problems = new List<string>();
            foreach (var id in pragma.Ids.Where(id => id is ObsoleteId or ExperimentalId).Distinct())
            {
                var obsolete = id == ObsoleteId;
                var declared = obsolete ? symbols.Obsolete : symbols.Experimental;
                var attribute = obsolete ? "[Obsolete]" : $"[Experimental({ExperimentalId})]";
                if (pragma.Comment.Length == 0)
                {
                    problems.Add($"{id}: carries no comment; name the {attribute} symbol it is for and say why");
                    continue;
                }

                var named = Identifier.Matches(pragma.Comment).Select(m => m.Value.TrimStart('@'))
                    .Where(declared.Contains).Distinct(StringComparer.Ordinal).ToList();
                if (named.Count == 0)
                {
                    problems.Add($"{id}: names no in-tree {attribute} symbol (an exact identifier, e.g. TouchSet, not 'touch-set')");
                    continue;
                }

                var spans = obsolete ? file.ObsoleteSpans : file.ExperimentalSpans;
                if (!named.Any(symbol => IsUsed(file, symbol, pragma.RegionStart, RegionEnd(file, pragma, id), spans)))
                    problems.Add($"{id}: is dead - {string.Join(", ", named)} is not used outside its own {attribute} context "
                        + "between this line and its restore (or the end of the file)");

                var reason = ReasonWord.Matches(pragma.Comment).Select(m => m.Value)
                    .Count(w => !named.Contains(w, StringComparer.Ordinal));
                if (reason < MinimumReasonWords)
                    problems.Add($"{id}: gives no reason ({reason} word(s) besides the names; at least {MinimumReasonWords})");
            }

            if (problems.Count > 0)
                yield return new Violation(file.Path, pragma.Text,
                    $"{file.Path}:{pragma.Line}: '{pragma.Text}' - {string.Join("; ", problems)}");
        }
    }

    /// <summary>
    /// A [SuppressMessage]/[UnconditionalSuppressMessage] naming CS0618 or ASHLAREXP001. It has no place
    /// for the named symbol and reason Rule 2 requires, so it is refused whether or not a given compiler
    /// honours it for a compiler diagnostic: a scoped pragma says the same thing and can be checked.
    /// </summary>
    private static IEnumerable<Violation> DeprecationSuppressAttributeViolations(SourceFile file)
    {
        foreach (Match m in SuppressAttribute.Matches(file.Bare))
        {
            var open = m.Index + m.Length - 1;
            var close = MatchForward(file.Bare, open, '(', ')');
            var end = close < 0 ? file.Code.Length : close + 1;
            var id = DeprecationIdInText.Match(file.Code[open..end]);
            if (!id.Success)
                continue;

            var text = file.Code[m.Index..end].Trim();
            yield return new Violation(file.Path, text,
                $"{file.Path}:{LineOf(file, m.Index)}: a SuppressMessage attribute disables {id.Value.ToUpperInvariant()}; "
                + "it cannot name the symbol it is for or say why - use a scoped '#pragma warning disable' that does (Rule 2)");
        }
    }

    /// <summary>
    /// NoWarn / WarningsNotAsErrors, as an element or an attribute, listing CS0618 (any spelling the
    /// compiler accepts: <c>CS0618</c>, <c>618</c>, <c>0618</c>, any case) or ASHLAREXP001. The key is the
    /// exact setting text, so a reviewed entry survives a line move but not an edit.
    /// </summary>
    private static IEnumerable<Violation> DeprecationMsBuildDisables(string path, XDocument doc)
    {
        foreach (var element in doc.Descendants())
        {
            var name = element.Name.LocalName;
            if (name is ("NoWarn" or "WarningsNotAsErrors") && !element.HasElements)
            {
                var ids = DeprecationIdsInList(element.Value);
                if (ids.Count > 0)
                {
                    var setting = $"<{name}>{element.Value.Trim()}</{name}>";
                    yield return new Violation(path, setting,
                        $"{path}{LineSuffix(element)}: '{setting}' {Effect(name)} {string.Join(", ", ids)} project-wide");
                }
            }

            foreach (var attribute in element.Attributes().Where(a => a.Name.LocalName is "NoWarn" or "WarningsNotAsErrors"))
            {
                var ids = DeprecationIdsInList(attribute.Value);
                if (ids.Count > 0)
                {
                    var setting = $"{name} {attribute.Name.LocalName}=\"{attribute.Value}\"";
                    yield return new Violation(path, setting,
                        $"{path}{LineSuffix(element)}: '{setting}' {Effect(attribute.Name.LocalName)} {string.Join(", ", ids)} project-wide");
                }
            }
        }

        static string Effect(string property) =>
            property == "NoWarn" ? "disables" : "keeps TreatWarningsAsErrors from failing on";

        static string LineSuffix(XElement element)
        {
            var info = (System.Xml.IXmlLineInfo)element;
            return info.HasLineInfo() ? $":{info.LineNumber}" : "";
        }
    }

    /// <summary>A dotnet_diagnostic.&lt;id&gt;.severity of none, silent or suggestion for CS0618 or ASHLAREXP001.</summary>
    private static IEnumerable<Violation> DeprecationAnalyzerConfigDisables(string path, string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line[0] is '#' or ';')
                continue;

            var diagnostic = DiagnosticSeverity.Match(line);
            if (diagnostic.Success && NormalizeId(diagnostic.Groups["id"].Value) is ObsoleteId or ExperimentalId
                && WeakSeverity.IsMatch(diagnostic.Groups["sev"].Value))
                yield return new Violation(path, line, $"{path}:{i + 1}: '{line}' disables {NormalizeId(diagnostic.Groups["id"].Value)} "
                    + "for every file the config applies to");
        }
    }

    private static List<string> DeprecationIdsInList(string list) =>
        list.Split([';', ',', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(NormalizeId)
            .Where(id => id is ObsoleteId or ExperimentalId)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// A warning id as the compiler compares it: case-insensitively, with a bare number read as a C#
    /// compiler warning. Roslyn maps a numeric id to "CS" plus the number padded to four digits, both for
    /// a <c>#pragma warning</c> and for <c>/nowarn</c> (so <c>618</c> and <c>0618</c> are <c>CS0618</c>).
    /// </summary>
    private static string NormalizeId(string id)
    {
        var trimmed = id.Trim();
        if (trimmed.Length > 0 && trimmed.All(char.IsAsciiDigit)
            && int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            return "CS" + number.ToString("0000", CultureInfo.InvariantCulture);
        return trimmed.ToUpperInvariant();
    }

    private static bool IsUsed(SourceFile file, string symbol, int start, int end, IReadOnlyList<(int Start, int End)> spans)
    {
        if (end <= start)
            return false;
        var pattern = new Regex(@"(?<![\w@])@?" + Regex.Escape(symbol) + @"(?!\w)", RegexOptions.CultureInvariant);
        for (var m = pattern.Match(file.Bare, start, end - start); m.Success; m = m.NextMatch())
        {
            if (IsDirectiveLine(file, m.Index))
                continue;
            if (!spans.Any(s => m.Index >= s.Start && m.Index < s.End))
                return true;
        }

        return false;
    }

    private static int RegionEnd(SourceFile file, Pragma disable, string id)
    {
        var restore = Pragmas(file).FirstOrDefault(p => !p.Disable && p.Line > disable.Line && (p.Ids.Count == 0 || p.Ids.Contains(id)));
        return restore is null ? file.Bare.Length : file.LineStarts[restore.Line - 1];
    }

    private static bool IsDirectiveLine(SourceFile file, int offset)
    {
        var i = file.LineStarts[LineOf(file, offset) - 1];
        while (i < file.Bare.Length && file.Bare[i] is ' ' or '\t')
            i++;
        return i < file.Bare.Length && file.Bare[i] == '#';
    }

    // ------------------------------------------------------------------------------------------------
    // Pragmas, declarations, lexing
    // ------------------------------------------------------------------------------------------------

    private sealed record Pragma(string Path, int Line, bool Disable, IReadOnlyList<string> Ids, string Comment, string Text, int RegionStart);

    private static IEnumerable<Pragma> Pragmas(SourceFile file)
    {
        var bareLines = file.Bare.Split('\n');
        var textLines = file.Text.Split('\n');
        for (var i = 0; i < bareLines.Length; i++)
        {
            var m = PragmaLine.Match(bareLines[i]);
            if (!m.Success)
                continue;

            var ids = m.Groups[2].Value.Split([',', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries)
                .Select(NormalizeId).ToList();
            var original = textLines[i];
            var slash = original.IndexOf("//", StringComparison.Ordinal);
            var comment = slash < 0 ? "" : original[(slash + 2)..].Trim();
            var regionStart = i + 1 < file.LineStarts.Length ? file.LineStarts[i + 1] : file.Bare.Length;
            yield return new Pragma(file.Path, i + 1, m.Groups[1].Value == "disable", ids, comment, original.Trim(), regionStart);
        }
    }

    private sealed class DeclaredSymbols
    {
        public HashSet<string> Obsolete { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Experimental { get; } = new(StringComparer.Ordinal);

        public static DeclaredSymbols Collect(IEnumerable<SourceFile> files)
        {
            var symbols = new DeclaredSymbols();
            foreach (var file in files)
            {
                foreach (var declaration in file.Declarations)
                {
                    if (declaration.Name is null)
                        continue;
                    (declaration.Obsolete ? symbols.Obsolete : symbols.Experimental).Add(declaration.Name);
                }
            }

            return symbols;
        }
    }

    private sealed record Declaration(string? Name, bool Obsolete, int Start, int End);

    private sealed class SourceFile
    {
        private SourceFile(string path, string text, string code, string bare)
        {
            Path = path;
            Text = text;
            Code = code;
            Bare = bare;
            var starts = new List<int> { 0 };
            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n')
                    starts.Add(i + 1);
            }

            LineStarts = starts.ToArray();
            Declarations = ReadDeclarations(this).ToList();
            ObsoleteSpans = Declarations.Where(d => d.Obsolete).Select(d => (d.Start, d.End)).ToList();
            ExperimentalSpans = Declarations.Where(d => !d.Obsolete).Select(d => (d.Start, d.End)).ToList();
        }

        public string Path { get; }

        /// <summary>The source, CRLF normalised to LF.</summary>
        public string Text { get; }

        /// <summary>Comments blanked, literals kept; same length and line breaks as <see cref="Text"/>.</summary>
        public string Code { get; }

        /// <summary>Comments AND literal contents blanked; same length and line breaks as <see cref="Text"/>.</summary>
        public string Bare { get; }

        public int[] LineStarts { get; }

        /// <summary>Declarations carrying a CS0618 [Obsolete] or an ASHLAREXP001 [Experimental].</summary>
        public IReadOnlyList<Declaration> Declarations { get; }

        public IReadOnlyList<(int Start, int End)> ObsoleteSpans { get; }

        public IReadOnlyList<(int Start, int End)> ExperimentalSpans { get; }

        public static SourceFile Parse(string path, string raw)
        {
            var text = raw.Replace("\r\n", "\n");
            var (code, bare) = Lex(text);
            return new SourceFile(path, text, code, bare);
        }
    }

    private static IEnumerable<Declaration> ReadDeclarations(SourceFile file)
    {
        var bare = file.Bare;
        foreach (Match m in DeprecationAttribute.Matches(bare))
        {
            var sectionOpen = AttributeSectionStart(bare, m.Index);
            if (sectionOpen < 0)
                continue;
            var sectionClose = MatchForward(bare, sectionOpen, '[', ']');
            if (sectionClose < 0)
                continue;

            var after = m.Index + m.Length;
            while (after < bare.Length && char.IsWhiteSpace(bare[after]))
                after++;
            var argsBare = "";
            var argsCode = "";
            if (after < bare.Length && bare[after] == '(')
            {
                var close = MatchForward(bare, after, '(', ')');
                if (close > 0)
                {
                    argsBare = bare[after..(close + 1)];
                    argsCode = file.Code[after..(close + 1)];
                }
            }

            bool obsolete;
            if (m.Groups["kind"].Value == "Obsolete")
            {
                // error: true makes it CS0619; a DiagnosticId replaces CS0618. Read on the blanked text so
                // a message containing the word "true" cannot flip it.
                if (Regex.IsMatch(argsBare, @"\btrue\b") || Regex.IsMatch(argsBare, @"\bDiagnosticId\s*="))
                    continue;
                obsolete = true;
            }
            else
            {
                if (!argsCode.Contains("AutonomyExperimental.DiagnosticId", StringComparison.Ordinal)
                    && !argsCode.Contains("\"" + ExperimentalId + "\"", StringComparison.Ordinal))
                    continue;
                obsolete = false;
            }

            var (name, end) = ReadDeclaration(bare, sectionClose + 1);
            yield return new Declaration(name, obsolete, sectionOpen, end);
        }
    }

    /// <summary>
    /// Walks back from an attribute name to the '[' that opens its section, over earlier attributes in
    /// the same section and an attribute target. -1 when the name is not in an attribute section, or the
    /// section targets the assembly, module, a return value or a parameter.
    /// </summary>
    private static int AttributeSectionStart(string bare, int nameStart)
    {
        var p = nameStart - 1;
        while (p >= 0)
        {
            var c = bare[p];
            if (char.IsWhiteSpace(c))
            {
                p--;
                continue;
            }

            if (c == '[')
                return p;

            if (c == ':')
            {
                if (p > 0 && bare[p - 1] == ':')
                    return -1;
                p--;
                while (p >= 0 && char.IsWhiteSpace(bare[p]))
                    p--;
                var end = p;
                while (p >= 0 && IsIdentChar(bare[p]))
                    p--;
                var target = bare[(p + 1)..(end + 1)];
                if (target is "assembly" or "module" or "return" or "param" or "typevar")
                    return -1;
                continue;
            }

            if (c == ',')
            {
                p--;
                while (p >= 0 && char.IsWhiteSpace(bare[p]))
                    p--;
                if (p >= 0 && bare[p] == ')')
                {
                    p = MatchBackward(bare, p, '(', ')') - 1;
                    while (p >= 0 && char.IsWhiteSpace(bare[p]))
                        p--;
                }

                while (p >= 0 && (IsIdentChar(bare[p]) || bare[p] == '.' || bare[p] == ':'))
                    p--;
                continue;
            }

            return -1;
        }

        return -1;
    }

    /// <summary>
    /// Reads the declaration after an attribute section: its name (null when it has none this reader
    /// understands - an operator, an indexer, a tuple-returning member) and the end of its extent, so a
    /// use inside it can be told from a use outside it.
    /// </summary>
    private static (string? Name, int End) ReadDeclaration(string bare, int from)
    {
        var n = bare.Length;
        var i = from;
        while (true)
        {
            while (i < n && char.IsWhiteSpace(bare[i]))
                i++;
            if (i < n && bare[i] == '#')
            {
                i = LineEnd(bare, i);
                continue;
            }

            if (i < n && bare[i] == '[')
            {
                var close = MatchForward(bare, i, '[', ']');
                if (close < 0)
                    return (null, n);
                i = close + 1;
                continue;
            }

            break;
        }

        var headerStart = i;
        var angle = 0;
        while (i < n)
        {
            var c = bare[i];
            if (c is '{' or ';')
                break;
            if (c == '<')
                angle++;
            else if (c == '>' && angle > 0)
                angle--;
            else if (angle == 0 && c is '=' or '(' or ',' or ')' or '}')
                break;
            i++;
        }

        var header = bare[headerStart..Math.Min(i, n)];
        var tokens = Identifier.Matches(StripGenerics(header)).Select(t => t.Value.TrimStart('@')).ToList();
        var typeKeyword = tokens.FindIndex(t => t is "class" or "struct" or "interface" or "enum" or "record");
        if (typeKeyword >= 0)
        {
            var at = typeKeyword + 1;
            if (tokens[typeKeyword] == "record" && at < tokens.Count && tokens[at] is "class" or "struct")
                at++;
            var typeName = at < tokens.Count ? tokens[at] : null;
            var body = ScanToBodyOrSemicolon(bare, headerStart);
            return (typeName, body);
        }

        string? name = null;
        if (tokens.Count > 0 && !tokens.Contains("operator") && !tokens.Contains("this"))
            name = NotANameKeywords.Contains(tokens[^1]) ? null : tokens[^1];

        return (name, MemberEnd(bare, i));
    }

    private static int ScanToBodyOrSemicolon(string bare, int from)
    {
        var depth = 0;
        for (var i = from; i < bare.Length; i++)
        {
            var c = bare[i];
            if (c == '(')
                depth++;
            else if (c == ')')
                depth--;
            else if (depth == 0 && c == ';')
                return i + 1;
            else if (depth == 0 && c == '{')
            {
                var close = MatchForward(bare, i, '{', '}');
                return close < 0 ? bare.Length : close + 1;
            }
        }

        return bare.Length;
    }

    private static int MemberEnd(string bare, int stop)
    {
        if (stop >= bare.Length)
            return bare.Length;
        switch (bare[stop])
        {
            case '{':
            {
                var close = MatchForward(bare, stop, '{', '}');
                if (close < 0)
                    return bare.Length;
                var j = close + 1;
                while (j < bare.Length && char.IsWhiteSpace(bare[j]))
                    j++;
                return j < bare.Length && bare[j] == '=' ? StatementEnd(bare, j) : close + 1;
            }
            case '(':
                return ScanToBodyOrSemicolon(bare, stop);
            case '=':
                return StatementEnd(bare, stop);
            case ';':
                return stop + 1;
            default:
                return stop;
        }
    }

    private static int StatementEnd(string bare, int from)
    {
        var depth = 0;
        for (var i = from; i < bare.Length; i++)
        {
            var c = bare[i];
            if (c is '(' or '{' or '[')
                depth++;
            else if (c is ')' or '}' or ']')
                depth--;
            else if (depth <= 0 && c == ';')
                return i + 1;
        }

        return bare.Length;
    }

    private static string StripGenerics(string header)
    {
        var previous = "";
        while (previous != header)
        {
            previous = header;
            header = Regex.Replace(header, "<[^<>]*>", " ");
        }

        return header;
    }

    /// <summary>
    /// Splits C# into two views of the same length and line breaks: Code (comments blanked) and Bare
    /// (comments and literal contents blanked; interpolation holes kept, since they are code). A
    /// preprocessor line keeps its directive and loses only a trailing // comment.
    /// </summary>
    private static (string Code, string Bare) Lex(string text)
    {
        var code = text.ToCharArray();
        var bare = text.ToCharArray();
        var n = text.Length;
        var i = 0;
        var atLineStart = true;
        while (i < n)
        {
            var c = text[i];
            if (c == '\n')
            {
                atLineStart = true;
                i++;
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (atLineStart && c == '#')
            {
                var end = LineEnd(text, i);
                var comment = text.IndexOf("//", i, end - i, StringComparison.Ordinal);
                if (comment >= 0)
                {
                    Blank(code, comment, end);
                    Blank(bare, comment, end);
                }

                i = end;
                continue;
            }

            atLineStart = false;
            if (c == '/' && i + 1 < n && text[i + 1] == '/')
            {
                var end = LineEnd(text, i);
                Blank(code, i, end);
                Blank(bare, i, end);
                i = end;
                continue;
            }

            if (c == '/' && i + 1 < n && text[i + 1] == '*')
            {
                var close = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                var end = close < 0 ? n : close + 2;
                Blank(code, i, end);
                Blank(bare, i, end);
                i = end;
                continue;
            }

            if (c == '\'')
            {
                var j = i + 1;
                while (j < n && text[j] != '\'' && text[j] != '\n')
                    j += text[j] == '\\' ? 2 : 1;
                j = Math.Min(j, n);
                Blank(bare, i + 1, j);
                i = Math.Min(j + 1, n);
                continue;
            }

            if (c == '"' || (c is '$' or '@' && StartsString(text, i)))
            {
                i = SkipString(text, i, bare);
                continue;
            }

            if (IsIdentChar(c))
            {
                while (i < n && IsIdentChar(text[i]))
                    i++;
                continue;
            }

            i++;
        }

        return (new string(code), new string(bare));
    }

    private static bool StartsString(string text, int i)
    {
        var j = i;
        while (j < text.Length && text[j] is '$' or '@')
            j++;
        return j > i && j < text.Length && text[j] == '"';
    }

    private static int SkipString(string text, int i, char[] bare)
    {
        var n = text.Length;
        var dollars = 0;
        var verbatim = false;
        while (text[i] is '$' or '@')
        {
            if (text[i] == '$')
                dollars++;
            else
                verbatim = true;
            i++;
        }

        var quotes = 0;
        while (i + quotes < n && text[i + quotes] == '"')
            quotes++;

        if (!verbatim && quotes >= 3)
        {
            var start = i + quotes;
            var k = start;
            while (k < n)
            {
                if (text[k] == '"')
                {
                    var run = 0;
                    while (k + run < n && text[k + run] == '"')
                        run++;
                    if (run >= quotes)
                    {
                        BlankLiteral(text, bare, start, k, dollars, raw: true);
                        return k + quotes;
                    }

                    k += run;
                    continue;
                }

                k++;
            }

            BlankLiteral(text, bare, start, n, dollars, raw: true);
            return n;
        }

        var begin = i + 1;
        var e = begin;
        while (e < n)
        {
            var ch = text[e];
            if (!verbatim && ch == '\n')
                break;
            if (!verbatim && ch == '\\')
            {
                e += 2;
                continue;
            }

            if (ch == '"')
            {
                if (verbatim && e + 1 < n && text[e + 1] == '"')
                {
                    e += 2;
                    continue;
                }

                break;
            }

            if (dollars > 0 && ch == '{' && !(e + 1 < n && text[e + 1] == '{'))
            {
                var depth = 0;
                while (e < n)
                {
                    if (text[e] == '{')
                        depth++;
                    else if (text[e] == '}' && --depth == 0)
                        break;
                    else if (!verbatim && text[e] == '\n')
                        break;
                    e++;
                }

                e++;
                continue;
            }

            e++;
        }

        e = Math.Min(e, n);
        BlankLiteral(text, bare, begin, e, dollars, raw: false);
        return Math.Min(e + 1, n);
    }

    /// <summary>Blanks a literal's text in Bare, keeping interpolation holes (they are code).</summary>
    private static void BlankLiteral(string text, char[] bare, int start, int end, int dollars, bool raw)
    {
        if (dollars == 0)
        {
            Blank(bare, start, end);
            return;
        }

        var open = raw ? dollars : 1;
        var k = start;
        while (k < end)
        {
            if (text[k] == '{')
            {
                var run = 0;
                while (k + run < end && text[k + run] == '{')
                    run++;
                if (!raw && run >= 2)
                {
                    Blank(bare, k, k + (run / 2 * 2));
                    k += run / 2 * 2;
                    continue;
                }

                if (run >= open)
                {
                    Blank(bare, k, k + run);
                    k += run;
                    var depth = 1;
                    while (k < end && depth > 0)
                    {
                        if (text[k] == '{')
                            depth++;
                        else if (text[k] == '}')
                            depth--;
                        if (depth > 0)
                            k++;
                    }

                    var closeRun = 0;
                    while (k + closeRun < end && text[k + closeRun] == '}')
                        closeRun++;
                    Blank(bare, k, k + Math.Max(closeRun, 1));
                    k += Math.Max(closeRun, 1);
                    continue;
                }
            }

            if (text[k] != '\n')
                bare[k] = ' ';
            k++;
        }
    }

    private static void Blank(char[] chars, int start, int end)
    {
        for (var k = Math.Max(0, start); k < Math.Min(end, chars.Length); k++)
        {
            if (chars[k] != '\n')
                chars[k] = ' ';
        }
    }

    private static int MatchForward(string s, int open, char openChar, char closeChar)
    {
        var depth = 0;
        for (var i = open; i < s.Length; i++)
        {
            if (s[i] == openChar)
                depth++;
            else if (s[i] == closeChar && --depth == 0)
                return i;
        }

        return -1;
    }

    private static int MatchBackward(string s, int close, char openChar, char closeChar)
    {
        var depth = 0;
        for (var i = close; i >= 0; i--)
        {
            if (s[i] == closeChar)
                depth++;
            else if (s[i] == openChar && --depth == 0)
                return i;
        }

        return 0;
    }

    private static int LineEnd(string s, int from)
    {
        var end = s.IndexOf('\n', from);
        return end < 0 ? s.Length : end;
    }

    private static int LineOf(SourceFile file, int offset)
    {
        var index = Array.BinarySearch(file.LineStarts, offset);
        return index >= 0 ? index + 1 : ~index;
    }

    private static bool IsIdentChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static string DirectoryOf(string relativePath)
    {
        var slash = relativePath.LastIndexOf('/');
        return slash < 0 ? "" : relativePath[..slash];
    }

    // ------------------------------------------------------------------------------------------------
    // The tree
    // ------------------------------------------------------------------------------------------------

    private sealed class RepoTree
    {
        private RepoTree(string root) => Root = root;

        public string Root { get; }
        public List<SourceFile> Sources { get; } = [];
        public List<(string Path, XDocument Doc)> MsBuild { get; } = [];
        public List<(string Path, string Text)> AnalyzerConfigs { get; } = [];
        public List<string> ApiProjects { get; } = [];
        public DeclaredSymbols Symbols { get; private set; } = new();

        public static RepoTree Load(string root)
        {
            var tree = new RepoTree(root);
            foreach (var file in Walk(root, root))
            {
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                var name = Path.GetFileName(file);
                if (name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                    tree.Sources.Add(SourceFile.Parse(relative, File.ReadAllText(file)));
                else if (name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                         || name.EndsWith(".props", StringComparison.OrdinalIgnoreCase)
                         || name.EndsWith(".targets", StringComparison.OrdinalIgnoreCase))
                    tree.MsBuild.Add((relative, XDocument.Load(file, LoadOptions.SetLineInfo)));
                else if (name == ".editorconfig" || name.EndsWith(".globalconfig", StringComparison.OrdinalIgnoreCase))
                    tree.AnalyzerConfigs.Add((relative, File.ReadAllText(file)));
            }

            foreach (var (path, doc) in tree.MsBuild)
            {
                if (path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                    && doc.Descendants().Any(e => e.Name.LocalName == "PackageReference"
                        && string.Equals((string?)e.Attribute("Include"), ApiAnalyzerPackage, StringComparison.OrdinalIgnoreCase)))
                    tree.ApiProjects.Add(path);
            }

            tree.Symbols = DeclaredSymbols.Collect(tree.Sources);
            return tree;
        }

        /// <summary>An analyzer project's file, or a Directory.Build.* on the chain above one.</summary>
        public bool GovernsApiProject(string path)
        {
            if (ApiProjects.Contains(path))
                return true;
            var name = path[(path.LastIndexOf('/') + 1)..];
            if (name is not ("Directory.Build.props" or "Directory.Build.targets"))
                return false;
            var dir = DirectoryOf(path);
            return ApiProjects.Any(p => IsSameOrUnder(DirectoryOf(p), dir));
        }

        /// <summary>A .globalconfig anywhere, or an .editorconfig above, at or below an analyzer project.</summary>
        public bool ConfigAppliesToApiProject(string path)
        {
            if (path.EndsWith(".globalconfig", StringComparison.OrdinalIgnoreCase))
                return true;
            var dir = DirectoryOf(path);
            return ApiProjects.Select(DirectoryOf).Any(p => IsSameOrUnder(p, dir) || IsSameOrUnder(dir, p));
        }

        private static bool IsSameOrUnder(string path, string ancestor) =>
            ancestor.Length == 0 || path == ancestor || path.StartsWith(ancestor + "/", StringComparison.Ordinal);

        /// <summary>Build output, dependency caches, dot-directories and nested checkouts are not the tree.</summary>
        private static IEnumerable<string> Walk(string root, string directory)
        {
            foreach (var file in Directory.EnumerateFiles(directory))
                yield return file;

            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                var name = Path.GetFileName(child);
                if (name.StartsWith('.') || name is "bin" or "obj" or "node_modules" or "TestResults"
                    || File.Exists(Path.Combine(child, ".git")) || Directory.Exists(Path.Combine(child, ".git")))
                    continue;

                foreach (var file in Walk(root, child))
                    yield return file;
            }
        }
    }
}
