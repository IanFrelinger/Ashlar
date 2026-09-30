using System.Text.RegularExpressions;
using FluentAssertions;
using Ashlar.Core.Application.Paths;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>
/// No production file hand-rolls a path-containment check; <c>PathContainment</c> is the one place.
///
/// <para><b>Why this blocks a merge.</b> "Is this path inside that root?" is two lines, so every new
/// tool re-derived it. Ten copies had drifted into five semantics — with and without a separator
/// after the root, with and without the root itself, case-folding everywhere, nowhere, or on some
/// platforms — and the one without a separator (<c>OutputPathSandboxed</c>) approved
/// <c>.../out-evil</c> as inside <c>.../out</c> on a live path. The next copy is written the same way
/// unless something stops it.</para>
///
/// <para><b>A per-file ratchet, in both directions.</b> Every production file's count of hand-rolled
/// checks must EQUAL its pin in <see cref="Remaining"/> (an unlisted file is pinned at zero). One
/// more fails as a new copy; one fewer fails as a stale pin, so converting a copy forces the pin down
/// in the same diff. A repo-wide total would let a new copy hide behind a conversion elsewhere.</para>
///
/// <para><b>How the scan tells a containment check from any other <c>StartsWith</c>.</b> Comments
/// and string contents are blanked first, so documentation and code templates never count; the
/// holes of an interpolated string are kept as expressions. An expression has FULL-PATH PROVENANCE
/// when it spells <c>GetFullPath(</c>, <c>DirectorySeparatorChar</c>, <c>AltDirectorySeparatorChar</c>
/// or <c>new FileInfo(</c>/<c>new DirectoryInfo(</c>, or names an identifier that has it. An
/// identifier gets it in three ways, repeated until nothing changes:</para>
/// <list type="number">
///   <item>assigned from an expression that spells one of those marks;</item>
///   <item>assigned ONE PATH-BUILDING STEP from an identifier that has it: <c>Path.Combine</c> or
///   <c>Path.Join</c>, a concatenated separator literal (<c>root + "/"</c>), an interpolated string
///   with a separator in its text (<c>$"{root}/"</c>), or a plain alias (<c>var r = root;</c>,
///   optionally trimmed). Any other transformation drops it: <c>full.Substring(root.Length)</c> is a
///   RELATIVE path, which is how <c>MediatedWritePath.IsUnderAllowlist</c> stays out;</item>
///   <item>as a PARAMETER of a method declared in the same file, when a call in the same file passes
///   that parameter (by position or by name) an argument that has it. This is what sees a
///   separator-less helper such as <c>Inside(string here, string there) =&gt; here.StartsWith(there)</c>
///   fed full paths by its own file.</item>
/// </list>
/// <para>A <c>.StartsWith(</c> call then counts when its first argument is NOT a literal and its
/// receiver or that argument has provenance, or its argument list names <c>PlatformComparison</c>
/// (the helper's case rule exists only to compare paths). A literal first argument classifies a
/// string (<c>"ashlar.model.provider="</c>, the device prefix <c>@"\\.\"</c>) and cannot name a
/// root, so it counts only when it starts with <c>..</c> on a receiver with <c>GetRelativePath</c> or
/// full-path provenance (the other common idiom), or it is an interpolated string whose holes have
/// provenance. Measured on 2026-09-30 at the base of this change: 126 production <c>StartsWith</c>
/// calls, of which exactly the 13 in the ten known copies matched, and no other; the one-step,
/// parameter, <c>FileInfo</c>/<c>DirectoryInfo</c>, interpolation and <c>PlatformComparison</c> rules
/// added no match there, nor across every <c>.cs</c> file in the repository including test projects
/// (3,149 tracked files, 227 calls).</para>
///
/// <para><b>What it cannot see</b>, stated so nobody mistakes green for proof. Provenance is by
/// identifier NAME within one file: not by type, not by scope, and not across files or through a
/// method's return value. So these are NOT counted, and the known-miss controls below pin each
/// <c>StartsWith</c>-shaped one at zero, so a scan that learns to see one must flip its control in
/// the same change:</para>
/// <list type="bullet">
///   <item>a helper whose operands get no provenance in its own file — its callers live in another
///   file, or it compares values that came back from a method call — such as
///   <c>static bool Under(string a, string b) =&gt; a.StartsWith(b, StringComparison.Ordinal)</c>
///   (unless it compares with <c>PlatformComparison</c>);</item>
///   <item><c>FullName</c> of a <c>FileInfo</c>/<c>DirectoryInfo</c> the file receives rather than
///   constructs (a parameter, an enumeration item);</item>
///   <item>a separator-aware prefix whose root has no provenance in the file
///   (<c>candidate.StartsWith(root + "/")</c> with <c>root</c> from a method call): textually it is
///   the relative-allowlist idiom (<c>rel.StartsWith(allowed + "/")</c>, <c>PathAllowlist</c>'s
///   prefixes, <c>TouchSetNarrowing</c>), which the scan deliberately ignores because it compares
///   repo-relative strings against configured prefixes, not a path against a root;</item>
///   <item>anything that is not <c>StartsWith</c>: <c>IndexOf(root) == 0</c>,
///   <c>string.Compare(..., root.Length)</c>, <c>Uri.IsBaseOf</c>, a span <c>SequenceEqual</c>.</item>
/// </list>
/// <para>In the other direction the name-based provenance over-approximates (a same-named
/// identifier elsewhere in the file inherits it), which can only ADD a hit and so fails loudly. The
/// scan is prevention, not proof: <c>PathContainmentTests</c> runs the adversarial tables against the
/// sites themselves, and that is what caught a separator-less helper this scan once missed.</para>
///
/// <para><b>What is production.</b> Every <c>.cs</c> under <see cref="ProductionRoots"/>, minus build
/// output, agent scratch space, nested checkouts and TEST PROJECTS — a directory whose
/// <c>.csproj</c> references <c>Microsoft.NET.Test.Sdk</c>, the identity
/// <c>TestOwnershipConventionTests</c> uses. Not a directory whose name contains "Tests": that rule
/// pruned the production use case <c>Testing/UseCases/RunTests</c>. A test asserting where its own
/// temp file lives is not a security decision.</para>
///
/// <para>Hermetic: pure file reads.</para>
/// </summary>
[Trait("Category", "Certification")]
public sealed class PathContainmentConventionTests
{
    /// <summary>The one sanctioned containment check. Excluded from the ratchet; required to be recognised.</summary>
    private const string HelperPath = "src/Ashlar.Abstractions/Paths/PathContainment.cs";

    /// <summary>The marker <c>TestOwnershipConventionTests</c> identifies a test project by.</summary>
    private const string TestSdkMarker = "Microsoft.NET.Test.Sdk";

    private static readonly string[] ProductionRoots = ["src", "application", "applications", "commercial", "products", "tools"];

    /// <summary>
    /// The copies that remain, with the exact number of checks in each, on 2026-09-30. This list is
    /// expected to SHRINK: convert a copy to <c>PathContainment</c> and lower (or delete) its pin in
    /// the same change. Both remaining copies are already separator-aware; neither is the escape.
    /// </summary>
    private static readonly Dictionary<string, int> Remaining = new(StringComparer.Ordinal)
    {
        // Volume-mount validation on the remote execution surface: separator-aware, root-inclusive,
        // case-insensitive on Windows only. Left because converting it makes a src/ change span
        // application/, which layer-boundary refuses without a [coordinated-integration] PR; it
        // belongs in an application/ follow-up.
        ["application/src/Ashlar.API/Endpoints/AshlarEndpoints.cs"] = 2,

        // sandbox.writable vs sandbox.root: separator-aware, root-inclusive, ordinal. Left because
        // Ashlar.Manifest references no project at all, and PathContainment lives in
        // Ashlar.Abstractions; converting it needs a new ProjectReference, which is its own decision.
        ["src/Ashlar.Manifest/ProjectVerifier.cs"] = 1,
    };

    /// <summary>
    /// Non-vacuity floors. Measured on 2026-09-30 with this change applied: 2,103 production .cs
    /// files scanned and 117 <c>StartsWith</c> calls examined. Set far enough below to survive
    /// ordinary deletions; a scan that silently stops reading the tree falls through them.
    /// </summary>
    private const int ScannedFilesFloor = 1000;

    private const int ExaminedCallsFloor = 60;

    [Fact]
    public void No_production_file_hand_rolls_more_containment_checks_than_its_pin()
    {
        var scan = Scan(RepoPathResolver.FindRepoRoot());

        var over = scan.Hits
            .Where(kv => kv.Key != HelperPath)
            .Select(kv => (File: kv.Key, Found: kv.Value, Pin: Remaining.TryGetValue(kv.Key, out var pin) ? pin : 0))
            .Where(x => x.Found.Count > x.Pin)
            .OrderBy(x => x.File, StringComparer.Ordinal)
            .Select(x => $"{x.File}: {x.Found.Count} found, pinned at {x.Pin} ({string.Join("; ", x.Found)})")
            .ToList();

        over.Should().BeEmpty(
            "a hand-rolled containment check is how OutputPathSandboxed came to admit a sibling of its "
            + "root. Call Ashlar.Abstractions.Paths.PathContainment.IsWithin (or IsStrictlyWithin) "
            + "instead. If this is not a containment check, make that visible to the scan (no path "
            + "provenance on its operands) rather than pinning it. Over the pin: {0}",
            string.Join(" | ", over));
    }

    [Fact]
    public void No_pin_is_higher_than_the_checks_its_file_still_has()
    {
        var scan = Scan(RepoPathResolver.FindRepoRoot());

        var stale = Remaining
            .Select(kv => (File: kv.Key, Pin: kv.Value, Found: scan.Hits.TryGetValue(kv.Key, out var hits) ? hits.Count : 0))
            .Where(x => x.Found < x.Pin)
            .OrderBy(x => x.File, StringComparer.Ordinal)
            .Select(x => $"{x.File}: pinned at {x.Pin}, {x.Found} found")
            .ToList();

        stale.Should().BeEmpty(
            "a pin above what its file still contains overstates the remaining copies and leaves room "
            + "for a new one to arrive unnoticed. Lower the pin, or delete the row at zero. Stale: {0}",
            string.Join(" | ", stale));
    }

    [Fact]
    public void The_scan_reads_the_tree_and_recognises_the_helper_itself()
    {
        var root = RepoPathResolver.FindRepoRoot();
        var scan = Scan(root);

        scan.FilesScanned.Should().BeGreaterThanOrEqualTo(ScannedFilesFloor,
            "the scan must actually walk the production tree; fewer files means a root or a pruning "
            + "rule broke and every assertion above is passing on nothing");
        scan.CallsExamined.Should().BeGreaterThanOrEqualTo(ExaminedCallsFloor,
            "the scan must actually see StartsWith calls to classify");

        File.Exists(Path.Combine(root, HelperPath.Replace('/', Path.DirectorySeparatorChar))).Should().BeTrue(
            "{0} is the one sanctioned containment check", HelperPath);
        scan.Hits.Should().ContainKey(HelperPath,
            "the helper's own separator-aware comparison is the canonical shape; if the scan cannot "
            + "recognise it on a real file, it cannot recognise a copy of it either");
    }

    /// <summary>
    /// Pruning is by test-project identity, not by name. The production use case under
    /// <c>Testing/UseCases/RunTests</c> has "Tests" in its directory name and was once skipped; a real
    /// test project must still be skipped.
    /// </summary>
    [Fact]
    public void Production_code_in_a_directory_named_like_tests_is_scanned_and_a_test_project_is_not()
    {
        var root = RepoPathResolver.FindRepoRoot();
        var scanned = Scan(root).Files;

        bool Scanned(string prefix) => scanned.Any(f => f.StartsWith(prefix, StringComparison.Ordinal));

        Scanned("src/Ashlar.Core.Application/Testing/UseCases/RunTests/").Should().BeTrue(
            "RunTests is production code in Ashlar.Core.Application, not a test project");
        Scanned("products/").Should().BeTrue("products/*/src ships C#");
        Scanned("tools/").Should().BeTrue("tools/ ships C#");
        Scanned("src/Ashlar.Tests.Infrastructure/").Should().BeFalse(
            "a project that references {0} is a test project", TestSdkMarker);
        Scanned("products/tests/").Should().BeFalse("products/tests holds a test project");
    }

    public static TheoryData<string> ControlNames()
    {
        var data = new TheoryData<string>();
        foreach (var name in Controls.Keys.OrderBy(k => k, StringComparer.Ordinal))
            data.Add(name);
        return data;
    }

    /// <summary>
    /// Each control is a real shape from this repository (named where it came from) or a spelling a
    /// careless edit would produce. Positive controls must count; negative controls must not; a
    /// known miss must not either, until the scan learns to see it and the control is flipped.
    /// </summary>
    [Theory]
    [MemberData(nameof(ControlNames))]
    public void The_detector_classifies_each_control(string name)
    {
        var (source, expected) = Controls[name];

        var hits = Scanner.Find(source, out var calls);

        calls.Should().BeGreaterThan(0, "a control with no StartsWith call exercises nothing");
        hits.Should().HaveCount(expected, "control '{0}' ({1})", name, string.Join("; ", hits));
    }

    private static readonly Dictionary<string, (string Source, int Expected)> Controls = new(StringComparer.Ordinal)
    {
        // ── must count ──────────────────────────────────────────────────────────────────────────
        ["positive: OutputPathSandboxed as it was (no separator)"] = ("""
            var full = Path.GetFullPath(output);
            var baseDir = Path.GetFullPath(root);
            if (!full.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase))
                return false;
            """, 1),
        ["positive: separator spelled in the argument (ToolSandbox as it was)"] = ("""
            return candidate.StartsWith(normalisedRoot + Path.DirectorySeparatorChar, PathComparison)
                || candidate.StartsWith(normalisedRoot + Path.AltDirectorySeparatorChar, PathComparison);
            """, 2),
        ["positive: prefix built by a multi-line ternary (ProjectVerifier)"] = ("""
            var rootWithSep = resolvedRoot.EndsWith(Path.DirectorySeparatorChar)
                ? resolvedRoot
                : resolvedRoot + Path.DirectorySeparatorChar;
            if (!resolved.Equals(resolvedRoot, StringComparison.Ordinal)
                && !resolved.StartsWith(rootWithSep, StringComparison.Ordinal))
            {
            }
            """, 1),
        ["positive: fully qualified GetFullPath as the receiver"] = ("""
            if (System.IO.Path.GetFullPath(p).StartsWith(root, StringComparison.Ordinal)) { }
            """, 1),
        ["positive: span receiver"] = ("""
            var full = Path.GetFullPath(p);
            if (full.AsSpan().StartsWith(root.AsSpan(), StringComparison.Ordinal)) { }
            """, 1),
        ["positive: a relative path climbing out"] = ("""
            var rel = Path.GetRelativePath(root, full);
            if (rel.StartsWith("..", StringComparison.Ordinal)) return false;
            """, 1),
        ["positive: a quote char literal does not open a string that hides the check"] = ("""
            if (c == '"' && full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)) { }
            """, 1),
        ["positive: a separator-less helper fed full paths by a caller in its own file (ToolSandbox, reintroduced)"] = ("""
            candidate = Path.GetFullPath(Path.Combine(root, relativePath));
            if (!Inside(candidate, root)) return false;
            private static bool Inside(string here, string there) => here.StartsWith(there, StringComparison.Ordinal);
            """, 1),
        ["positive: a block-bodied helper bound by named arguments"] = ("""
            var full = Path.GetFullPath(p);
            if (!Contains(outer: baseDir, inner: full)) return false;
            private static bool Contains(string inner, string outer)
            {
                return inner.StartsWith(outer, StringComparison.OrdinalIgnoreCase);
            }
            """, 1),
        ["positive: a helper comparing with the helper's own PlatformComparison"] = ("""
            private static bool Under(string a, string b) => a.StartsWith(b, PathContainment.PlatformComparison);
            """, 1),
        ["positive: a separator prefix one assignment away"] = ("""
            var rootFull = Path.GetFullPath(r);
            var prefix = rootFull + "/";
            if (candidate.StartsWith(prefix, StringComparison.Ordinal)) { }
            """, 1),
        ["positive: a backslash char prefix one assignment away"] = ("""
            var rootFull = Path.GetFullPath(r);
            var prefix = rootFull + '\\';
            if (candidate.StartsWith(prefix, StringComparison.Ordinal)) { }
            """, 1),
        ["positive: a Path.Combine receiver one assignment away"] = ("""
            var rootFull = Path.GetFullPath(r);
            var combined = Path.Combine(rootFull, rel);
            if (combined.StartsWith(allowed, StringComparison.Ordinal)) { }
            """, 1),
        ["positive: an alias of a full path, trimmed"] = ("""
            var rootFull = Path.GetFullPath(r);
            var trimmed = rootFull.TrimEnd('/');
            if (candidate.StartsWith(trimmed, StringComparison.Ordinal)) { }
            """, 1),
        ["positive: FileInfo and DirectoryInfo FullName"] = ("""
            if (new FileInfo(p).FullName.StartsWith(new DirectoryInfo(r).FullName, StringComparison.Ordinal)) { }
            """, 1),
        ["positive: a DirectoryInfo held in a local"] = ("""
            var rootInfo = new DirectoryInfo(r);
            if (file.FullName.StartsWith(rootInfo.FullName, StringComparison.Ordinal)) { }
            """, 1),
        ["positive: an interpolated prefix embedding a full path"] = ("""
            var rootFull = Path.GetFullPath(r);
            if (candidate.StartsWith($"{rootFull}/", StringComparison.Ordinal)) { }
            """, 1),
        ["positive: an interpolated prefix spelling the separator"] = ("""
            if (candidate.StartsWith($"{root}{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) { }
            """, 1),
        ["positive: an interpolated prefix one assignment away"] = ("""
            var rootFull = Path.GetFullPath(r);
            var prefix = $"{rootFull}/";
            if (candidate.StartsWith(prefix, StringComparison.Ordinal)) { }
            """, 1),

        // ── must not count ──────────────────────────────────────────────────────────────────────
        ["negative: literal prefix on a config line (MeaiBackedModel)"] = ("""
            if (trimmed.StartsWith("ashlar.model.provider=", StringComparison.OrdinalIgnoreCase)) { }
            """, 0),
        ["negative: literal device prefix on a full path (SafePackageRead)"] = ("""
            var full = Path.GetFullPath(path);
            if (full.StartsWith(@"\\.\", StringComparison.Ordinal)) return false;
            var win = full.StartsWith(@"\\?\", StringComparison.Ordinal) ? full : full;
            """, 0),
        ["negative: char literal on a full path"] = ("""
            var full = Path.GetFullPath(p);
            if (full.StartsWith('/')) { }
            """, 0),
        ["negative: relative allowlist prefix (PathAllowlist)"] = ("""
            var rel = raw.TrimStart('/');
            if (!_allowedPrefixes.Any(a => rel.StartsWith(a, StringComparison.OrdinalIgnoreCase))) return false;
            """, 0),
        ["negative: a substring of a full path is relative again (MediatedWritePath.IsUnderAllowlist)"] = ("""
            var fullPath = Path.GetFullPath(target);
            var normalizedRel = fullPath.Substring(rootWithSep.Length).Replace('\\', '/');
            if (normalizedRel.StartsWith(e + "/", StringComparison.OrdinalIgnoreCase)) { }
            """, 0),
        ["negative: namespace prefix (TouchSetNarrowing)"] = ("""
            if (ns.Equals(p, StringComparison.Ordinal) || ns.StartsWith(p + ".", StringComparison.Ordinal)) { }
            """, 0),
        ["negative: a type's FullName against a namespace"] = ("""
            if (type.FullName.StartsWith(ns + ".", StringComparison.Ordinal)) { }
            """, 0),
        ["negative: URL path prefix (AshlarApiKeyAuthMiddleware)"] = ("""
            return value.StartsWith(A2APathPrefix, StringComparison.OrdinalIgnoreCase);
            """, 0),
        ["negative: an interpolated prefix with no path in it"] = ("""
            if (line.StartsWith($"{prefix}:", StringComparison.Ordinal)) { }
            """, 0),
        ["negative: an interpolated message quoting a full path is not a prefix"] = ("""
            var full = Path.GetFullPath(p);
            var message = $"escapes '{full}'";
            if (message.StartsWith(header, StringComparison.Ordinal)) { }
            """, 0),
        ["negative: a helper whose callers in its file pass no path"] = ("""
            if (Has(name, header)) { }
            private static bool Has(string a, string b) => a.StartsWith(b, StringComparison.Ordinal);
            """, 0),
        ["negative: a check that is commented out"] = ("""
            // if (!full.StartsWith(root + Path.DirectorySeparatorChar)) return false;
            /* candidate.StartsWith(root + Path.DirectorySeparatorChar) */
            if (name.StartsWith(prefix, StringComparison.Ordinal)) { }
            """, 0),
        ["negative: a check inside a string literal (a code template)"] = ("""
            var template = "if (!full.StartsWith(root + Path.DirectorySeparatorChar)) return;";
            if (template.StartsWith(header, StringComparison.Ordinal)) { }
            """, 0),
        ["negative: a dot-dot literal on a string with no path provenance"] = ("""
            if (version.StartsWith("..", StringComparison.Ordinal)) { }
            """, 0),
        ["negative: a check inside a verbatim string with doubled quotes"] = ("""
            var doc = @"say ""x"" then full.StartsWith(root + Path.DirectorySeparatorChar)";
            if (doc.StartsWith(header, StringComparison.Ordinal)) { }
            """, 0),
        ["negative: a check inside a raw string literal (MockScaffoldingResponder templates)"] = (""""
            var t = """
                if (!full.StartsWith(root + Path.DirectorySeparatorChar)) return;
                """;
            if (t.StartsWith(header, StringComparison.Ordinal)) { }
            """", 0),

        // ── known misses: real containment checks the scan cannot see (see the type remarks) ───
        // Flip one to 1 in the same change that teaches the scan to see it.
        ["known miss: a helper whose callers live in another file"] = ("""
            internal static bool Under(string candidate, string root) => candidate.StartsWith(root, StringComparison.Ordinal);
            """, 0),
        ["known miss: a root that comes back from a method call"] = ("""
            var root = ResolveRoot(snapshot);
            if (!candidate.StartsWith(root + "/", StringComparison.Ordinal)) return false;
            """, 0),
        ["known miss: FullName of a FileInfo the method receives"] = ("""
            private static bool Under(FileInfo file, DirectoryInfo dir) => file.FullName.StartsWith(dir.FullName, StringComparison.Ordinal);
            """, 0),
    };

    private sealed record ScanResult(Dictionary<string, List<string>> Hits, List<string> Files, int CallsExamined)
    {
        public int FilesScanned => Files.Count;
    }

    private static ScanResult Scan(string root)
    {
        var hits = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var files = new List<string>();
        var calls = 0;
        foreach (var top in ProductionRoots)
        {
            var dir = Path.Combine(root, top);
            if (Directory.Exists(dir) && !IsPruned(dir))
                Collect(root, dir, hits, files, ref calls);
        }

        return new ScanResult(hits, files, calls);
    }

    private static void Collect(string root, string directory, Dictionary<string, List<string>> hits, List<string> files, ref int calls)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*.cs"))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            files.Add(relative);
            var found = Scanner.Find(File.ReadAllText(file), out var examined);
            calls += examined;
            if (found.Count > 0)
                hits[relative] = found;
        }

        foreach (var child in Directory.EnumerateDirectories(directory))
        {
            if (!IsPruned(child))
                Collect(root, child, hits, files, ref calls);
        }
    }

    /// <summary>Build output, agent scratch space, any nested checkout, and a test project (by its csproj).</summary>
    private static bool IsPruned(string directory)
    {
        var name = Path.GetFileName(directory);
        if (name is "bin" or "obj" or ".claude")
            return true;
        var git = Path.Combine(directory, ".git");
        if (File.Exists(git) || Directory.Exists(git))
            return true;
        return Directory.EnumerateFiles(directory, "*.csproj")
            .Any(project => File.ReadAllText(project).Contains(TestSdkMarker, StringComparison.Ordinal));
    }

    /// <summary>
    /// The classifier. Text-level on purpose (no Roslyn in the required check): blank comments and
    /// literal contents, work out which identifiers have full-path or relative-path provenance, then
    /// classify each <c>.StartsWith(</c> by its receiver and arguments.
    /// </summary>
    private static class Scanner
    {
        private static readonly Regex Assignment = new(@"\b([A-Za-z_][A-Za-z0-9_]*)\s*=(?![=>])", RegexOptions.CultureInvariant);

        private static readonly Regex FullPathMark = new(
            @"GetFullPath\s*\(|\bDirectorySeparatorChar\b|\bAltDirectorySeparatorChar\b|\bnew\s+(?:System\.IO\.)?(?:FileInfo|DirectoryInfo)\s*\(",
            RegexOptions.CultureInvariant);

        private static readonly Regex RelativePathMark = new(@"GetRelativePath\s*\(", RegexOptions.CultureInvariant);
        private static readonly Regex PathBuilder = new(@"\bPath\s*\.\s*(?:Combine|Join)\s*\(", RegexOptions.CultureInvariant);
        private static readonly Regex Alias = new(@"^\s*[A-Za-z_][A-Za-z0-9_]*\s*(?:\.\s*Trim(?:End)?\s*\([^()]*\)\s*)?$", RegexOptions.CultureInvariant);
        private static readonly Regex Concatenation = new(@"\+\s*", RegexOptions.CultureInvariant);
        private static readonly Regex Hole = new(@"\{([^{}]*)\}", RegexOptions.CultureInvariant);
        private static readonly Regex NameBeforeParen = new(@"\b([A-Za-z_][A-Za-z0-9_]*)\s*(?:<[^<>()]*>)?\s*\(", RegexOptions.CultureInvariant);
        private static readonly Regex NamedArgument = new(@"^\s*([A-Za-z_][A-Za-z0-9_]*)\s*:(?!:)", RegexOptions.CultureInvariant);
        private static readonly Regex PassedOut = new(@"^\s*out\b", RegexOptions.CultureInvariant);
        private static readonly Regex PassedIn = new(@"^\s*(?:ref|in)\s+", RegexOptions.CultureInvariant);
        private static readonly Regex PlatformComparison = new(@"\bPlatformComparison\b", RegexOptions.CultureInvariant);
        private static readonly Regex Identifier = new(@"[A-Za-z_][A-Za-z0-9_]*", RegexOptions.CultureInvariant);
        private static readonly Regex StartsWithCall = new(@"\.\s*StartsWith\s*\(", RegexOptions.CultureInvariant);

        /// <summary>A path separator spelled as a C# literal: <c>"/"</c>, <c>"\\"</c>, <c>'/'</c>, <c>'\\'</c>, <c>@"\"</c>.</summary>
        private static readonly HashSet<string> SeparatorLiterals = new(StringComparer.Ordinal)
        {
            "\"/\"", "\"\\\\\"", "'/'", "'\\\\'", "@\"\\\"",
        };

        /// <summary>Words that precede '(' without naming a method.</summary>
        private static readonly HashSet<string> NotAMethod = new(StringComparer.Ordinal)
        {
            "if", "while", "for", "foreach", "switch", "catch", "using", "lock", "return", "nameof", "typeof", "sizeof",
            "default", "new", "when", "fixed", "checked", "unchecked", "base", "this", "stackalloc", "await", "throw",
        };

        /// <summary>Words before a name that make <c>name(...)</c> an expression, not a declaration.</summary>
        private static readonly HashSet<string> ExpressionKeywords = new(StringComparer.Ordinal)
        {
            "return", "new", "await", "throw", "else", "in", "is", "as", "yield", "case", "when", "and", "or", "not", "out", "ref",
        };

        public static List<string> Find(string source, out int calls)
        {
            var literals = new Dictionary<int, string>();
            var code = Clean(source, literals);
            var (fullIds, relativeIds) = Provenance(code, literals);

            var hits = new List<string>();
            calls = 0;
            foreach (Match call in StartsWithCall.Matches(code))
            {
                calls++;
                var open = call.Index + call.Length - 1;
                var receiver = Receiver(code, call.Index);
                var (argumentStart, argument) = Argument(code, open);
                var receiverIds = Identifiers(receiver);
                var lead = argumentStart + (argument.Length - argument.TrimStart().Length);

                bool counts;
                if (literals.TryGetValue(lead, out var literal))
                {
                    var body = literal.TrimStart('@', '$').TrimStart('"');
                    var holes = Holes(literal);
                    counts = (body.StartsWith("..", StringComparison.Ordinal)
                            && (RelativePathMark.IsMatch(receiver) || FullPathMark.IsMatch(receiver)
                                || receiverIds.Overlaps(relativeIds) || receiverIds.Overlaps(fullIds)))
                        || (holes.Length > 0 && (FullPathMark.IsMatch(holes) || Identifiers(holes).Overlaps(fullIds)));
                }
                else
                {
                    counts = FullPathMark.IsMatch(receiver) || FullPathMark.IsMatch(argument)
                        || receiverIds.Overlaps(fullIds) || Identifiers(argument).Overlaps(fullIds)
                        || PlatformComparison.IsMatch(code[open..ClosingParen(code, open)]);
                }

                if (counts)
                {
                    var line = 1 + code.Take(call.Index).Count(ch => ch == '\n');
                    hits.Add($"line {line}: {receiver.Trim()}.StartsWith({argument.Trim()}, ...)");
                }
            }

            return hits;
        }

        private static HashSet<string> Identifiers(string text)
            => Identifier.Matches(text).Select(m => m.Value).ToHashSet(StringComparer.Ordinal);

        /// <summary>
        /// Identifiers with full-path or relative-path provenance: seeded by assignments and bound
        /// arguments that spell a mark, then grown along path-building assignments and parameter
        /// bindings until nothing changes.
        /// </summary>
        private static (HashSet<string> Full, HashSet<string> Relative) Provenance(string code, Dictionary<int, string> literals)
        {
            var full = new HashSet<string>(StringComparer.Ordinal);
            var relative = new HashSet<string>(StringComparer.Ordinal);
            var edges = new List<(string Target, HashSet<string> Sources)>();

            foreach (Match m in Assignment.Matches(code))
            {
                var start = m.Index + m.Length;
                var rhs = RightHandSide(code, start);
                var expression = Expression(code, literals, start, start + rhs.Length);
                var name = m.Groups[1].Value;
                if (FullPathMark.IsMatch(expression))
                    full.Add(name);
                if (RelativePathMark.IsMatch(expression))
                    relative.Add(name);
                if (BuildsAPath(rhs, start, literals))
                    edges.Add((name, Identifiers(expression)));
            }

            foreach (var (parameter, start, end) in Bindings(code))
            {
                var expression = Expression(code, literals, start, end);
                if (FullPathMark.IsMatch(expression))
                    full.Add(parameter);
                edges.Add((parameter, Identifiers(expression)));
            }

            bool grew;
            do
            {
                grew = false;
                foreach (var (target, sources) in edges)
                {
                    if (!full.Contains(target) && sources.Overlaps(full))
                    {
                        full.Add(target);
                        grew = true;
                    }
                }
            }
            while (grew);

            return (full, relative);
        }

        /// <summary>The cleaned code of a span plus the holes of every interpolated literal that starts in it.</summary>
        private static string Expression(string code, Dictionary<int, string> literals, int start, int end)
        {
            var holes = LiteralsIn(literals, start, end).Select(Holes).Where(h => h.Length > 0).ToList();
            return holes.Count == 0 ? code[start..end] : code[start..end] + " " + string.Join(" ", holes);
        }

        private static IEnumerable<string> LiteralsIn(Dictionary<int, string> literals, int start, int end)
        {
            for (var k = start; k < end; k++)
            {
                if (literals.TryGetValue(k, out var literal))
                    yield return literal;
            }
        }

        /// <summary>
        /// True when an assignment's right-hand side builds a path from what it names:
        /// <c>Path.Combine</c>/<c>Path.Join</c>, a plain (optionally trimmed) alias, a concatenated
        /// separator literal, or an interpolated string with a separator in its text.
        /// </summary>
        private static bool BuildsAPath(string rhs, int start, Dictionary<int, string> literals)
        {
            if (PathBuilder.IsMatch(rhs) || Alias.IsMatch(rhs))
                return true;

            foreach (Match plus in Concatenation.Matches(rhs))
            {
                if (literals.TryGetValue(start + plus.Index + plus.Length, out var literal) && SeparatorLiterals.Contains(literal))
                    return true;
            }

            return LiteralsIn(literals, start, start + rhs.Length)
                .Any(l => Holes(l).Length > 0 && Hole.Replace(l, " ").IndexOfAny(['/', '\\']) >= 0);
        }

        /// <summary>The expressions inside an interpolated literal's holes, or empty for any other literal.</summary>
        private static string Holes(string literal)
        {
            var quote = literal.IndexOf('"', StringComparison.Ordinal);
            if (quote <= 0 || literal.IndexOf('$', 0, quote) < 0)
                return string.Empty;
            return string.Join(" ", Hole.Matches(literal).Select(m => m.Groups[1].Value));
        }

        /// <summary>
        /// (parameter, argument span) for every call in this file to a method declared in this file:
        /// each argument binds to the parameter at its position, or of its name. An <c>out</c>
        /// argument flows the other way and binds nothing.
        /// </summary>
        private static List<(string Parameter, int Start, int End)> Bindings(string code)
        {
            var sites = NameBeforeParen.Matches(code)
                .Select(m => (Name: m.Groups[1].Value, At: m.Index, Open: m.Index + m.Length - 1))
                .ToList();

            var declared = new Dictionary<string, List<(int At, string[] Parameters)>>(StringComparer.Ordinal);
            foreach (var (name, at, open) in sites)
            {
                if (!IsDeclaration(code, name, at, open, out var close))
                    continue;
                var parameters = SplitTopLevel(code, open + 1, close, angleBrackets: true)
                    .Select(p => ParameterName(code[p.Start..p.End]))
                    .ToArray();
                if (!declared.TryGetValue(name, out var overloads))
                    declared[name] = overloads = [];
                overloads.Add((at, parameters));
            }

            var bindings = new List<(string Parameter, int Start, int End)>();
            foreach (var (name, at, open) in sites)
            {
                if (!declared.TryGetValue(name, out var overloads) || overloads.Any(o => o.At == at))
                    continue;
                var arguments = SplitTopLevel(code, open + 1, ClosingParen(code, open), angleBrackets: false);
                foreach (var (_, parameters) in overloads)
                {
                    if (arguments.Count > parameters.Length)
                        continue;
                    for (var i = 0; i < arguments.Count; i++)
                    {
                        var (start, end) = arguments[i];
                        if (PassedOut.IsMatch(code[start..end]))
                            continue;
                        var parameter = parameters[i];
                        var named = NamedArgument.Match(code[start..end]);
                        if (named.Success)
                        {
                            parameter = parameters.Contains(named.Groups[1].Value) ? named.Groups[1].Value : string.Empty;
                            start += named.Length;
                        }

                        var passed = PassedIn.Match(code[start..end]);
                        if (passed.Success)
                            start += passed.Length;
                        if (parameter.Length > 0)
                            bindings.Add((parameter, start, end));
                    }
                }
            }

            return bindings;
        }

        /// <summary>
        /// A declaration is <c>Type Name(params)</c> followed by a body (<c>{</c>), an expression body
        /// (<c>=&gt;</c>) or a constraint (<c>where</c>); a call is anything else.
        /// </summary>
        private static bool IsDeclaration(string code, string name, int at, int open, out int close)
        {
            close = ClosingParen(code, open);
            if (NotAMethod.Contains(name))
                return false;

            var j = at - 1;
            while (j >= 0 && char.IsWhiteSpace(code[j]))
                j--;
            if (j < 0 || !(char.IsLetterOrDigit(code[j]) || code[j] is '_' or '>' or ']' or '?'))
                return false;
            if (char.IsLetterOrDigit(code[j]) || code[j] == '_')
            {
                var end = j + 1;
                while (j >= 0 && (char.IsLetterOrDigit(code[j]) || code[j] == '_'))
                    j--;
                if (ExpressionKeywords.Contains(code[(j + 1)..end]))
                    return false;
            }

            var k = close + 1;
            while (k < code.Length && char.IsWhiteSpace(code[k]))
                k++;
            return k < code.Length
                && (code[k] == '{'
                    || string.CompareOrdinal(code, k, "=>", 0, 2) == 0
                    || (string.CompareOrdinal(code, k, "where", 0, 5) == 0
                        && (k + 5 >= code.Length || !(char.IsLetterOrDigit(code[k + 5]) || code[k + 5] == '_'))));
        }

        /// <summary>The name a parameter declares: its last identifier before any default value.</summary>
        private static string ParameterName(string declaration)
        {
            var equals = declaration.IndexOf('=', StringComparison.Ordinal);
            var head = equals < 0 ? declaration : declaration[..equals];
            var names = Identifier.Matches(head);
            return names.Count == 0 ? string.Empty : names[^1].Value;
        }

        /// <summary>Comma-separated spans at bracket depth zero; angle brackets count only in a declaration.</summary>
        private static List<(int Start, int End)> SplitTopLevel(string code, int start, int end, bool angleBrackets)
        {
            var parts = new List<(int Start, int End)>();
            if (end <= start || string.IsNullOrWhiteSpace(code[start..end]))
                return parts;

            var depth = 0;
            var from = start;
            for (var k = start; k < end; k++)
            {
                var ch = code[k];
                if (ch is '(' or '[' or '{' || (angleBrackets && ch == '<'))
                {
                    depth++;
                }
                else if (ch is ')' or ']' or '}' || (angleBrackets && ch == '>'))
                {
                    depth--;
                }
                else if (ch == ',' && depth == 0)
                {
                    parts.Add((from, k));
                    from = k + 1;
                }
            }

            parts.Add((from, end));
            return parts;
        }

        /// <summary>The index of the ')' closing the '(' at <paramref name="open"/>, or the end of the code.</summary>
        private static int ClosingParen(string code, int open)
        {
            var depth = 0;
            for (var k = open; k < code.Length; k++)
            {
                if (code[k] == '(')
                {
                    depth++;
                }
                else if (code[k] == ')')
                {
                    depth--;
                    if (depth == 0)
                        return k;
                }
            }

            return code.Length;
        }

        /// <summary>Up to the end of the expression: a top-level ';' or ',', or a bracket closing one this expression never opened.</summary>
        private static string RightHandSide(string code, int start)
        {
            var depth = 0;
            var k = start;
            for (; k < code.Length; k++)
            {
                var ch = code[k];
                if (ch is '(' or '[' or '{')
                {
                    depth++;
                }
                else if (ch is ')' or ']' or '}')
                {
                    if (depth == 0)
                        break;
                    depth--;
                }
                else if (ch is ';' or ',' && depth == 0)
                {
                    break;
                }
            }

            return code[start..k];
        }

        /// <summary>The member-access chain before '.StartsWith', including balanced (...) and [...].</summary>
        private static string Receiver(string code, int dot)
        {
            var j = dot - 1;
            while (j >= 0 && char.IsWhiteSpace(code[j]))
                j--;
            var end = j + 1;
            var depth = 0;
            for (; j >= 0; j--)
            {
                var ch = code[j];
                if (ch is ')' or ']')
                {
                    depth++;
                }
                else if (ch is '(' or '[')
                {
                    if (depth == 0)
                        break;
                    depth--;
                }
                else if (depth == 0 && !(char.IsLetterOrDigit(ch) || ch is '_' or '.' or '?' or '!'))
                {
                    break;
                }
            }

            return code[(j + 1)..end];
        }

        /// <summary>The first argument: from after '(' to a top-level ',' or the closing ')'.</summary>
        private static (int Start, string Text) Argument(string code, int openParen)
        {
            var depth = 0;
            var k = openParen + 1;
            for (; k < code.Length; k++)
            {
                var ch = code[k];
                if (ch is '(' or '[' or '{')
                {
                    depth++;
                }
                else if (ch is ')' or ']' or '}')
                {
                    if (depth == 0)
                        break;
                    depth--;
                }
                else if (ch == ',' && depth == 0)
                {
                    break;
                }
            }

            return (openParen + 1, code[(openParen + 1)..k]);
        }

        /// <summary>
        /// Blanks comments and the CONTENTS of string and char literals (delimiters and newlines kept,
        /// so offsets and line numbers survive), and records each literal's original text by the
        /// offset of its first character.
        /// </summary>
        private static string Clean(string text, Dictionary<int, string> literals)
        {
            var buffer = text.ToCharArray();
            var n = text.Length;
            var i = 0;
            while (i < n)
            {
                var c = text[i];
                var next = i + 1 < n ? text[i + 1] : '\0';

                if (c == '/' && next == '/')
                {
                    var end = text.IndexOf('\n', i);
                    end = end < 0 ? n : end;
                    Blank(buffer, i, end);
                    i = end;
                    continue;
                }

                if (c == '/' && next == '*')
                {
                    var close = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    var end = close < 0 ? n : close + 2;
                    Blank(buffer, i, end);
                    i = end;
                    continue;
                }

                if (c == '\'')
                {
                    var k = i + 1;
                    while (k < n && text[k] != '\'' && text[k] != '\n')
                        k += text[k] == '\\' ? 2 : 1;
                    var end = Math.Min(k + 1, n);
                    literals[i] = text[i..end];
                    Blank(buffer, i + 1, end - 1);
                    i = end;
                    continue;
                }

                if (c == '"' || (c is '@' or '$' && next is '"' or '@' or '$'))
                {
                    var j = i;
                    var verbatim = false;
                    while (j < n && text[j] is '@' or '$')
                    {
                        verbatim |= text[j] == '@';
                        j++;
                    }

                    if (j >= n || text[j] != '"')
                    {
                        i++;
                        continue;
                    }

                    var q = j;
                    while (q < n && text[q] == '"')
                        q++;
                    var quotes = q - j;
                    if (quotes >= 3)
                    {
                        // Raw string literal: closed by the same run of quotes.
                        var close = text.IndexOf(new string('"', quotes), q, StringComparison.Ordinal);
                        var rawEnd = close < 0 ? n : close + quotes;
                        literals[i] = text[i..rawEnd];
                        Blank(buffer, q, close < 0 ? n : close);
                        i = rawEnd;
                        continue;
                    }

                    var k = j + 1;
                    while (k < n)
                    {
                        var ch = text[k];
                        if (verbatim)
                        {
                            if (ch == '"' && k + 1 < n && text[k + 1] == '"')
                            {
                                k += 2;
                                continue;
                            }

                            if (ch == '"')
                                break;
                        }
                        else
                        {
                            if (ch == '\\')
                            {
                                k += 2;
                                continue;
                            }

                            if (ch is '"' or '\n')
                                break;
                        }

                        k++;
                    }

                    var stringEnd = Math.Min(k + 1, n);
                    literals[i] = text[i..stringEnd];
                    Blank(buffer, j + 1, stringEnd - 1);
                    i = stringEnd;
                    continue;
                }

                i++;
            }

            return new string(buffer);
        }

        private static void Blank(char[] buffer, int from, int to)
        {
            for (var k = from; k < to && k < buffer.Length; k++)
            {
                if (buffer[k] != '\n')
                    buffer[k] = ' ';
            }
        }
    }
}
