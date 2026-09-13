using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Ashlar.Core.Application.Paths;
using Ashlar.Infrastructure.Validation.Adapters;
using FluentAssertions;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>
/// Every lane that runs <c>Ashlar.Tests.Infrastructure</c> must give the slowest test it selects
/// room to hit its own deadline before the Blame collector kills the host.
///
/// <para><b>Why this is derived rather than listed.</b> The first version of this convention
/// compared <c>TestTimeouts</c>' literal <c>int</c> fields against a hard-coded inventory of two
/// files. Both halves were blind by construction. A per-test timeout written as a numeric literal
/// — <c>[Fact(Timeout = 300_000)]</c> — is not a <c>TestTimeouts</c> field and was invisible to
/// the reflection; a lane spelled anywhere but those two files was outside the inventory and
/// stayed green. Every inversion this check found on its first run lived in one of those two
/// blind spots, including a live CI lane and the <c>make</c> target that runs byte-for-byte the
/// same filter as the <c>ci verify</c> step the two-file inventory did cover.</para>
///
/// <para>So both sides are now read from the things themselves: the deadlines by reflection over
/// every <c>[Fact]</c>/<c>[Theory]</c> in this assembly, whatever spelling the number came in;
/// the windows by parsing <c>dotnet test</c> command strings in the repository, plus observing
/// validate's argument-list builder directly, and evaluating each <c>--filter</c> against that
/// reflected set. A new command-string lane is covered the day it is written, and a
/// timeout raised anywhere is measured against every lane that can select it.</para>
///
/// <para>Nothing here is allowed to pass by finding nothing: the scan, the reflected inventory and
/// the filter evaluator each carry a positive control, and an input this check cannot parse is a
/// hard failure rather than a clean result (<c>docs/HowGatesGoQuiet.md</c> sections 4 and 5).</para>
///
/// <para><b>The one thing it cannot see.</b> Reflection reaches only the assembly it is running
/// in, so a lane pinned to the target framework this run is not executing is skipped rather than
/// guessed at — <c>Tests/VirtualProduction</c> and <c>Tests/API</c> are compiled out on net8.0,
/// so measuring a net10.0 lane from a net8.0 run would be an answer about a different set of
/// tests. cert-gate runs this suite on net8.0, so that is the framework whose lanes are gated on
/// every pull request; net10.0-pinned lanes are covered only when something runs this convention
/// on net10.0.</para>
/// </summary>
public sealed class LaneBlameWindowConventionTests
{
    /// <summary>
    /// The relation <c>docs/Testing.md</c> documents: a window at least 1.5x the widest per-test
    /// deadline it can select. Strictly-greater would satisfy the mechanism and only just — a test
    /// that ran to within a second of its own net would then race the host kill, and the whole
    /// point is that the per-test net fires FIRST and names the test.
    /// </summary>
    private const double RequiredWindowRatio = 1.5;

    /// <summary>The target framework this assembly is running under, as a lane would spell it.</summary>
    private static readonly string RunningFramework = $"net{Environment.Version.Major}.0";

    /// <summary>One <c>dotnet test</c> invocation found in the repository.</summary>
    private sealed record LaneInvocation(
        string File, int Line, string Target, int? WindowMs, string? Filter, string? Framework);

    /// <summary>One discovered test: what a filter matches on, and the deadline it carries.</summary>
    private sealed record TestCase(
        string FullyQualifiedName,
        IReadOnlyList<string> Categories,
        int TimeoutMs,
        bool SkippedAtDiscovery);

    // ---------------------------------------------------------------------------------------
    // Facts
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The invariant. For every lane that runs this suite, the Blame window must clear the widest
    /// per-test deadline the lane's own filter selects.
    /// </summary>
    /// <remarks>
    /// Below that the per-test timeout is decoration: the stall becomes a host kill, which
    /// discards every result already recorded, names the in-flight test only as one that "may, or
    /// may not be the source of the crash", and leaves nothing to diagnose. That is what the macOS
    /// readiness lane produced twice on
    /// <c>FileSystemEventSourceTests.SubscribeAsync_FileCreated_EmitsEvent</c>, throwing away
    /// ~1900 recorded results each time.
    /// </remarks>
    [Fact]
    public void Every_lane_gives_its_slowest_selected_test_room_to_time_out()
    {
        var inventory = DiscoverTests();

        var offenders = new List<string>();
        foreach (var lane in LanesThatRunThisSuite())
        {
            if (lane.WindowMs is not { } window) continue;   // no window at all: nothing to invert
            if (!MeasurableHere(lane)) continue;

            var selected = Select(inventory, lane.Filter)
                .Where(t => !t.SkippedAtDiscovery && t.TimeoutMs > 0)
                .ToArray();
            if (selected.Length == 0) continue;

            var slowest = selected.MaxBy(t => t.TimeoutMs)!;
            var required = (int)Math.Ceiling(slowest.TimeoutMs * RequiredWindowRatio);
            if (window >= required) continue;

            offenders.Add(
                $"{lane.File}:{lane.Line} runs a {window / 1000}s window over "
                + $"{slowest.FullyQualifiedName} (Timeout = {slowest.TimeoutMs} ms); "
                + $"needs at least {required / 1000}s");
        }

        offenders.Should().BeEmpty(
            "a per-test timeout only ever fires if the harness window above it is wider. Below "
            + "that, every stall is a host kill that discards the whole run and names no failing "
            + $"test. Required: window >= {RequiredWindowRatio}x the widest deadline the lane's "
            + $"filter selects, measured on {RunningFramework}. Offending lanes:\n"
            + string.Join("\n", offenders));
    }

    /// <summary>
    /// Positive control for the two halves above, as its own fact so that a scan which found
    /// nothing fails here instead of passing the invariant vacuously.
    /// </summary>
    [Fact]
    public void The_scan_and_the_reflected_inventory_are_not_empty()
    {
        var inventory = DiscoverTests();
        var lanes = LanesThatRunThisSuite();

        inventory.Should().HaveCountGreaterThan(
            1000,
            "this assembly carries thousands of tests; a near-empty reflection means the deadline "
            + "half of the comparison silently disappeared");
        inventory.Count(t => t.TimeoutMs > 0).Should().BeGreaterThan(
            100, "the comparison is against per-test deadlines; none found compares nothing");
        inventory.Count(t => t.Categories.Contains("ProdStyle")).Should().BeGreaterThan(
            10, "Category traits are what half the lane filters select on");

        lanes.Should().HaveCountGreaterThan(
            15,
            "this suite is run from the Makefile, the gate scripts, the workflows and the CLI "
            + "lanes; a short list means the scan stopped reading one of those");

        lanes.Select(l => Path.GetFileName(l.File)).Distinct().Should().Contain(
            new[]
            {
                "Makefile",
                "kernel-gate-tier-c.sh",
                "full-platform-readiness-gate.yml",
                "CiCommand.cs",
                "ValidationServiceAdapter.cs",
            },
            "each of these was an offender when this check was written. CLI argument strings "
            + "are parsed, and validate's ArgumentList is observed directly. Losing one "
            + "from the scan is how the inventory rots back into a two-file list");
    }

    /// <summary>
    /// The filter evaluator has to actually filter. A "select everything" bug would make every
    /// lane look like the broadest one (loud, and therefore self-correcting); a "select nothing"
    /// bug would make every lane look clean, which is the silent direction and the one worth
    /// pinning.
    /// </summary>
    [Fact]
    public void The_filter_evaluator_selects_a_proper_subset()
    {
        var inventory = DiscoverTests();

        Select(inventory, null).Count.Should().Be(inventory.Count, "no filter selects every test");

        var sweep = Select(inventory, "Category!=DockerOptional&Category!=Stress").Count;
        sweep.Should().BeGreaterThan(1000, "the validate sweep runs nearly the whole suite");
        sweep.Should().BeLessThan(
            inventory.Count, "it excludes two categories, so it cannot select all of them");

        var narrow = Select(inventory, "FullyQualifiedName~BaseFrameworkSmokeTests");
        narrow.Should().NotBeEmpty("BaseFrameworkSmokeTests is the smoke lane's whole selection");
        narrow.Should().OnlyContain(t => t.FullyQualifiedName.Contains("BaseFrameworkSmokeTests"));

        Select(inventory, "Category=ProdStyle").Should().NotBeEmpty();
        Select(inventory,
                "(FullyQualifiedName~BaseFrameworkSmokeTests|FullyQualifiedName~RuntimeStudioBlackBoxSmokeTests)")
            .Count.Should().BeGreaterThan(narrow.Count, "an OR widens the selection");
    }

    // ---------------------------------------------------------------------------------------
    // Reflected inventory
    // ---------------------------------------------------------------------------------------

    private static IReadOnlyList<TestCase> DiscoverTests()
    {
        var cases = new List<TestCase>();

        foreach (var type in typeof(LaneBlameWindowConventionTests).Assembly.GetTypes())
        {
            if (!type.IsClass || type.IsAbstract) continue;

            var typeCategories = CategoriesOf(type.GetCustomAttributesData());

            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                // TheoryAttribute and OptInFactAttribute both derive from FactAttribute, so one
                // lookup covers every shape of test in this assembly.
                var fact = method.GetCustomAttribute<FactAttribute>();
                if (fact is null) continue;

                var categories = typeCategories
                    .Concat(CategoriesOf(method.GetCustomAttributesData()))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();

                cases.Add(new TestCase(
                    $"{type.FullName}.{method.Name}".Replace('+', '.'),
                    categories,
                    fact.Timeout,
                    fact.Skip is not null));
            }
        }

        return cases;
    }

    private static string[] CategoriesOf(IEnumerable<CustomAttributeData> attributes) =>
        attributes
            .Where(a => a.AttributeType.Name == "TraitAttribute" && a.ConstructorArguments.Count >= 2)
            .Where(a => a.ConstructorArguments[0].Value?.ToString() == "Category")
            .Select(a => a.ConstructorArguments[1].Value?.ToString() ?? string.Empty)
            .ToArray();

    // ---------------------------------------------------------------------------------------
    // Lane scan
    // ---------------------------------------------------------------------------------------

    private static readonly Regex DotnetTestCall = new(@"\bdotnet\s+test\b", RegexOptions.Compiled);

    private static readonly Regex WindowArgument =
        new(@"--blame-hang-timeout\s+(\S+?)s(?=[\s""']|$)", RegexOptions.Compiled);

    private static readonly Regex FilterArgument =
        new("--filter\\s+(?:\"([^\"]*)\"|'([^']*)'|(\\S+))", RegexOptions.Compiled);

    private static readonly Regex FrameworkArgument =
        new(@"(?:--framework|-f)\s+[""']?(net[0-9]+\.[0-9]+)[""']?", RegexOptions.Compiled);

    /// <summary>
    /// A lane pinned to a framework this run is not executing selects a different set of tests, so
    /// it is left to the run of this convention that happens under that framework rather than
    /// measured against the wrong inventory.
    /// </summary>
    private static bool MeasurableHere(LaneInvocation lane) =>
        lane.Framework is null
        || string.Equals(lane.Framework, RunningFramework, StringComparison.Ordinal);

    private static IReadOnlyList<LaneInvocation> LanesThatRunThisSuite()
    {
        var root = RepoPathResolver.FindRepoRoot();
        // Validate now builds argv, not a command string. Measure the actual builder with the
        // broadest permitted selection: caller filters can only narrow its default exclusions.
        // This is the explicit ArgumentList-backed lane; the textual scan below still covers
        // command strings only, and must be extended for other argv-backed test launchers.
        var targetProject = Path.Combine(root, "src", "Ashlar.Tests.Infrastructure", "Ashlar.Tests.Infrastructure.csproj");
        var validate = ValidationServiceAdapter.CreateDotnetTestStartInfo(targetProject, RunningFramework, null, false);
        validate.FileName.Should().Be("dotnet");
        validate.ArgumentList[0].Should().Be("test");
        string Option(string name)
        {
            var indexes = validate.ArgumentList.Select((value, index) => (value, index))
                .Where(item => item.value == name).Select(item => item.index).ToArray();
            indexes.Should().ContainSingle($"validate must supply exactly one {name}");
            return validate.ArgumentList[indexes.Single() + 1];
        }
        var validationWindow = Option("--blame-hang-timeout");
        validationWindow.Should().EndWith("s");
        int.TryParse(validationWindow[..^1], out var validationSeconds).Should().BeTrue();
        var lanes = new List<LaneInvocation>
        {
            new("src/Ashlar.Infrastructure/Validation/Adapters/ValidationServiceAdapter.cs", 1,
                validate.ArgumentList[1], validationSeconds * 1000, Option("--filter"), Option("--framework"))
        };

        foreach (var file in FilesToScan(root))
        {
            File.Exists(file).Should().BeTrue(
                $"'{file}' is an input to this convention; a missing input is a fault in the "
                + "check, never a clean result");

            var isCSharp = file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
            var raw = File.ReadAllText(file).Replace("\r\n", "\n", StringComparison.Ordinal);

            // One normalization pass, carrying a map back to the original offsets so a failure
            // names the line a human has to edit rather than a line in a rewritten buffer.
            var text = Normalize(raw, isCSharp, out var offsets);
            var variables = DeclaredVariables(raw, isCSharp);
            var lineOf = LineIndex(raw);

            foreach (Match call in DotnetTestCall.Matches(text))
            {
                var end = text.IndexOf('\n', call.Index);
                var command = end < 0 ? text[call.Index..] : text[call.Index..end];

                var target = TargetOf(command, variables);
                if (target is null) continue;
                if (!RunsThisSuite(root, target, isCSharp)) continue;

                int? windowMs = null;
                var window = WindowArgument.Match(command);
                if (window.Success)
                {
                    var seconds = Resolve(window.Groups[1].Value, variables);
                    int.TryParse(seconds, out var value).Should().BeTrue(
                        $"'{Path.GetRelativePath(root, file)}' spells its blame window as "
                        + $"'{window.Groups[1].Value}', which this check cannot resolve to a "
                        + "number of seconds. An unparsable input is a hard failure, not a pass");
                    windowMs = value * 1000;
                }

                var filter = FilterArgument.Match(command);
                var filterText = filter.Success
                    ? (filter.Groups[1].Success ? filter.Groups[1].Value
                        : filter.Groups[2].Success ? filter.Groups[2].Value
                        : filter.Groups[3].Value)
                    : null;

                var framework = FrameworkArgument.Match(command);

                lanes.Add(new LaneInvocation(
                    Path.GetRelativePath(root, file).Replace('\\', '/'),
                    lineOf(offsets[call.Index]),
                    target,
                    windowMs,
                    ResolvedFilterOrNull(filterText, variables),
                    framework.Success ? framework.Groups[1].Value : null));
            }
        }

        return lanes;
    }

    private static IEnumerable<string> FilesToScan(string root)
    {
        yield return Path.Combine(root, "Makefile");

        foreach (var sh in Directory.EnumerateFiles(
            Path.Combine(root, "scripts"), "*.sh", SearchOption.AllDirectories))
        {
            yield return sh;
        }

        var workflows = Path.Combine(root, ".github", "workflows");
        foreach (var yml in Directory.EnumerateFiles(workflows, "*.yml", SearchOption.TopDirectoryOnly))
            yield return yml;
        foreach (var yaml in Directory.EnumerateFiles(workflows, "*.yaml", SearchOption.TopDirectoryOnly))
            yield return yaml;

        // The lanes that build their `dotnet test` argument string in C#.
        foreach (var cs in Directory.EnumerateFiles(
            Path.Combine(root, "application", "src", "Ashlar.CLI", "Commands"),
            "*.cs",
            SearchOption.AllDirectories))
        {
            yield return cs;
        }

        foreach (var cs in Directory.EnumerateFiles(
            Path.Combine(root, "src", "Ashlar.Infrastructure", "Validation", "Adapters"),
            "*.cs",
            SearchOption.AllDirectories))
        {
            yield return cs;
        }
    }

    /// <summary>
    /// The FIRST <c>NAME=value</c> / <c>NAME := value</c> / <c>var name = "value"</c> assignment
    /// of each name, which is how every gate script and Makefile target here declares its test
    /// project — plus the one C# constant a lane interpolates into its argument string.
    /// </summary>
    /// <remarks>
    /// First, not last, on purpose: a script that parses its own options reassigns the same name
    /// from <c>"$2"</c> further down, and taking the last one leaves the check holding
    /// <c>${2:?--filter needs an expression}</c> instead of the default it was written to measure.
    ///
    /// <para>C# is scraped for nothing at all, on purpose. A shell script has one scope, so its
    /// first assignment really is the value the command below it runs with; a C# file has many,
    /// and picking one by textual order is a guess in the unsafe direction. <c>TestMultiEnvCommand</c>
    /// makes the point twice over: <c>{csprojPath}</c> would pick up
    /// <c>var csprojPath = testProject.FullName;</c> — an expression, not a value — and
    /// <c>{filter}</c>, which is whatever the caller typed, would pick up an unrelated
    /// <c>var filter = "FullyQualifiedName~BaseFrameworkSmokeTests";</c> a hundred lines away and
    /// narrow the lane to a handful of fast tests it never actually runs. Both guesses make the
    /// requirement LOOSER. Left unresolved, an interpolated project is in scope and an
    /// interpolated filter is the whole suite, which is the direction that cannot hide a lane.
    /// The one exception is the validate window itself, which is a compile-time constant this
    /// assembly can simply read.</para>
    /// </remarks>
    private static IReadOnlyDictionary<string, string> DeclaredVariables(string text, bool isCSharp)
    {
        var variables = new Dictionary<string, string>(StringComparer.Ordinal);

        const string assignment =
            @"^\s*(?:readonly\s+|export\s+)?([A-Za-z_][A-Za-z0-9_]*)\s*"
            + @"(?::=|\?=|=)\s*(?:""([^""\n]*)""|'([^'\n]*)'|([^\s#][^\n#]*?))\s*$";

        foreach (Match m in isCSharp
            ? Enumerable.Empty<Match>()
            : Regex.Matches(text, assignment, RegexOptions.Multiline).Cast<Match>())
        {
            var name = m.Groups[1].Value;
            if (variables.ContainsKey(name)) continue;

            variables[name] =
                m.Groups[2].Success ? m.Groups[2].Value
                : m.Groups[3].Success ? m.Groups[3].Value
                : m.Groups[4].Value.Trim();
        }

        // The validate window is a C# constant, not a shell variable: resolve the interpolation by
        // name so the lane that started all this is measured on the number it really passes.
        variables[nameof(ValidationServiceAdapter.ValidateBlameHangTimeoutSeconds)] =
            ValidationServiceAdapter.ValidateBlameHangTimeoutSeconds.ToString();

        return variables;
    }

    private static string Resolve(string token, IReadOnlyDictionary<string, string> variables)
    {
        const string reference =
            @"\$\{([A-Za-z_][A-Za-z0-9_]*)\}|\$\(([A-Za-z_][A-Za-z0-9_]*)\)|\{([A-Za-z_][A-Za-z0-9_]*)\}|\$([A-Za-z_][A-Za-z0-9_]*)";

        var previous = string.Empty;
        var current = token;
        for (var i = 0; i < 4 && !string.Equals(current, previous, StringComparison.Ordinal); i++)
        {
            previous = current;
            current = Regex.Replace(current, reference, m =>
            {
                var name = m.Groups[1].Success ? m.Groups[1].Value
                    : m.Groups[2].Success ? m.Groups[2].Value
                    : m.Groups[3].Success ? m.Groups[3].Value
                    : m.Groups[4].Value;
                return variables.TryGetValue(name, out var value) ? value : m.Value;
            });
        }

        return current.Trim('"', '\'');
    }

    /// <summary>
    /// A filter this check cannot resolve to a literal — <c>ashlar test multi-env</c> forwards
    /// whatever the caller typed — is treated as NO filter, i.e. the whole suite. That is the
    /// conservative direction on purpose: an unknown filter can only make the requirement
    /// stricter, never let a lane through unmeasured.
    /// </summary>
    private static string? ResolvedFilterOrNull(
        string? filter, IReadOnlyDictionary<string, string> variables)
    {
        if (filter is null) return null;

        var resolved = Resolve(filter, variables);
        return resolved.Contains('$', StringComparison.Ordinal)
            || resolved.Contains('{', StringComparison.Ordinal)
            ? null
            : resolved;
    }

    /// <summary>First non-flag token after <c>dotnet test</c>, with variables resolved.</summary>
    private static string? TargetOf(string command, IReadOnlyDictionary<string, string> variables)
    {
        var rest = command[(command.IndexOf("test", StringComparison.Ordinal) + 4)..];

        foreach (Match token in Regex.Matches(rest, @"""[^""]*""|'[^']*'|[^\s""']+"))
        {
            var value = token.Value.Trim('"', '\'', '\\');
            if (value.Length == 0) continue;
            if (value.StartsWith('-')) return null;       // `dotnet test --filter ...`: no target
            return Resolve(value, variables);
        }

        return null;
    }

    /// <summary>
    /// Whether the invocation puts this assembly under the window — directly, or through a
    /// solution / solution filter that lists it.
    /// </summary>
    /// <remarks>
    /// A C# lane whose project path is still unresolved after variable substitution counts as in
    /// scope. Those three exist to run this repository's test projects — one names this suite,
    /// one sweeps every discovered test project, one is handed the path at run time — and a
    /// computed path that silently dropped out of scope is precisely the blind spot this check
    /// replaced.
    /// </remarks>
    private static bool RunsThisSuite(string root, string target, bool isCSharp)
    {
        const string suite = "Ashlar.Tests.Infrastructure";
        if (target.Contains(suite, StringComparison.Ordinal)) return true;

        if (isCSharp && (target.Contains('{', StringComparison.Ordinal)
            || target.Contains('$', StringComparison.Ordinal)))
        {
            return true;
        }

        if (!target.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
            && !target.EndsWith(".slnf", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var path = Path.Combine(root, target.Replace('\\', '/'));
        return File.Exists(path) && File.ReadAllText(path).Contains(suite, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------
    // Normalization
    // ---------------------------------------------------------------------------------------

    private static readonly Regex ShellContinuation = new(@"\G\\\n[ \t]*", RegexOptions.Compiled);
    private static readonly Regex CSharpConcatenation = new("\\G\"[ \t\r\n]*\\+[ \t\r\n]*[$@]{0,2}\"", RegexOptions.Compiled);
    private static readonly Regex CSharpTestArguments = new("\\G[$@]{0,2}\"test \\\\\"", RegexOptions.Compiled);

    /// <summary>
    /// Rewrites a file so one logical <c>dotnet test</c> command is one line of text, and returns
    /// a map from each rewritten offset back to the offset it came from.
    /// </summary>
    /// <remarks>
    /// Shell, Make and workflow files need only backslash-continuations joined. C# needs three
    /// more: adjacent string literals joined across the <c>+</c>, <c>\"</c> unescaped, and the
    /// leading <c>test "</c> given back the <c>dotnet</c> that <c>RunProcessAsync</c> supplies as
    /// a separate argument. Without those three, a lane written in C# reads as ordinary source and
    /// the scan walks past it — which is how the inventory this check replaced came to be two
    /// hard-coded paths.
    /// </remarks>
    private static string Normalize(string raw, bool isCSharp, out List<int> offsets)
    {
        var builder = new StringBuilder(raw.Length);
        var map = new List<int>(raw.Length);
        offsets = map;

        void Emit(string value, int origin)
        {
            builder.Append(value);
            for (var i = 0; i < value.Length; i++) map.Add(origin);
        }

        var position = 0;
        while (position < raw.Length)
        {
            var continuation = ShellContinuation.Match(raw, position);
            if (continuation.Success)
            {
                Emit(" ", position);
                position += continuation.Length;
                continue;
            }

            if (isCSharp)
            {
                var concatenation = CSharpConcatenation.Match(raw, position);
                if (concatenation.Success)
                {
                    position += concatenation.Length;
                    continue;
                }

                var arguments = CSharpTestArguments.Match(raw, position);
                if (arguments.Success)
                {
                    Emit("dotnet test \"", position);
                    position += arguments.Length;
                    continue;
                }

                if (raw[position] == '\\' && position + 1 < raw.Length && raw[position + 1] == '"')
                {
                    Emit("\"", position);
                    position += 2;
                    continue;
                }
            }

            builder.Append(raw[position]);
            map.Add(position);
            position++;
        }

        map.Add(raw.Length);
        return builder.ToString();
    }

    private static Func<int, int> LineIndex(string text)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
            if (text[i] == '\n') starts.Add(i + 1);

        return offset =>
        {
            var index = starts.BinarySearch(offset);
            return index >= 0 ? index + 1 : ~index;
        };
    }

    // ---------------------------------------------------------------------------------------
    // vstest filter evaluation
    // ---------------------------------------------------------------------------------------

    private static IReadOnlyList<TestCase> Select(IReadOnlyList<TestCase> tests, string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter)) return tests;

        var parser = new FilterParser(filter);
        var predicate = parser.ParseExpression();

        parser.AtEnd.Should().BeTrue(
            $"this check must understand every filter it measures; '{filter}' still has input at "
            + $"offset {parser.Position}, so it cannot be evaluated and must not be reported clean");

        return tests.Where(predicate).ToArray();
    }

    /// <summary>
    /// Recursive descent over the vstest filter grammar actually used in this repository:
    /// <c>expr := term ('|' term)*</c>, <c>term := factor ('&amp;' factor)*</c>,
    /// <c>factor := '(' expr ')' | property op value</c> — with <c>&amp;</c> binding tighter than
    /// <c>|</c>, as vstest does.
    /// </summary>
    private sealed class FilterParser(string text)
    {
        private int _position;

        public int Position => _position;

        public bool AtEnd => _position >= text.Length;

        public Func<TestCase, bool> ParseExpression()
        {
            var left = ParseTerm();
            while (Peek() == '|')
            {
                _position++;
                var right = ParseTerm();
                var previous = left;
                left = t => previous(t) || right(t);
            }
            return left;
        }

        private Func<TestCase, bool> ParseTerm()
        {
            var left = ParseFactor();
            while (Peek() == '&')
            {
                _position++;
                var right = ParseFactor();
                var previous = left;
                left = t => previous(t) && right(t);
            }
            return left;
        }

        private Func<TestCase, bool> ParseFactor()
        {
            if (Peek() == '(')
            {
                _position++;
                var inner = ParseExpression();
                (Peek() == ')').Should().BeTrue($"unbalanced parenthesis in filter '{text}'");
                _position++;
                return inner;
            }

            var start = _position;
            while (_position < text.Length
                && !"=!~&|()".Contains(text[_position], StringComparison.Ordinal))
            {
                _position++;
            }
            var property = text[start.._position].Trim();

            string op;
            if (Match("!=")) op = "!=";
            else if (Match("!~")) op = "!~";
            else if (Match("=")) op = "=";
            else if (Match("~")) op = "~";
            else
            {
                throw new InvalidOperationException(
                    $"filter '{text}' has no operator after '{property}'; this check must "
                    + "understand every filter it measures rather than report it clean");
            }

            start = _position;
            while (_position < text.Length
                && !"&|)".Contains(text[_position], StringComparison.Ordinal))
            {
                _position++;
            }

            return Compile(property, op, text[start.._position].Trim(), text);
        }

        private bool Match(string op)
        {
            if (!text.AsSpan(_position).StartsWith(op, StringComparison.Ordinal)) return false;
            _position += op.Length;
            return true;
        }

        private char Peek() => _position < text.Length ? text[_position] : '\0';
    }

    private static Func<TestCase, bool> Compile(
        string property, string op, string value, string filter)
    {
        // A xunit fact's DisplayName defaults to its fully qualified name, so both read the same
        // string here; Name is the method half of it.
        Func<TestCase, IEnumerable<string>> subjects = property switch
        {
            "FullyQualifiedName" or "DisplayName" => t => new[] { t.FullyQualifiedName },
            "Name" => t => new[] { t.FullyQualifiedName[(t.FullyQualifiedName.LastIndexOf('.') + 1)..] },
            "Category" or "TestCategory" => t => t.Categories,
            _ => throw new InvalidOperationException(
                $"filter '{filter}' selects on '{property}', which this check does not model. An "
                + "unmodelled property would silently select the wrong set, so it is a hard "
                + "failure rather than a clean result"),
        };

        return op switch
        {
            "=" => t => subjects(t).Any(s => string.Equals(s, value, StringComparison.Ordinal)),
            "!=" => t => !subjects(t).Any(s => string.Equals(s, value, StringComparison.Ordinal)),
            "~" => t => subjects(t).Any(s => s.Contains(value, StringComparison.Ordinal)),
            _ => t => !subjects(t).Any(s => s.Contains(value, StringComparison.Ordinal)),
        };
    }
}
