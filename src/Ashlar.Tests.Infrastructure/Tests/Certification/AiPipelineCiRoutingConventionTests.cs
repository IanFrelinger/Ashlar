using System.Text.RegularExpressions;
using Ashlar.Core.Application.Paths;
using FluentAssertions;
using Xunit;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>
/// AI-only changes must reach existing automatic test routes. This lens covers AI source/tests,
/// their shared build inputs, and the direct Kernel Tier A Make edge; it is not a transitive
/// inventory of every Kernel tier or a general interpreter for GitHub globs, Bash or GNU Make.
/// Unsupported shapes at the inspected edges fail rather than being approximated. Actual
/// workflow execution still needs the AI-only PR routing probe described in CiGateInventory.
/// Earlier shell steps and Make variable/conditional evaluation are outside this lens.
/// </summary>
public sealed class AiPipelineCiRoutingConventionTests
{
    private const string Kernel = ".github/workflows/kernel-gate.yml";
    private const string Readiness = ".github/workflows/full-platform-readiness-gate.yml";
    private const string AiTests = "src/Ashlar.Tests.AI.Pipeline/Ashlar.Tests.AI.Pipeline.csproj";
    private static readonly string[] RequiredInputs =
    [
        "src/Ashlar.AI.Pipeline/Rag/Nested/Provider.cs",
        "src/Ashlar.Tests.AI.Pipeline/Nested/ProviderTests.cs",
        "src/Ashlar.Abstractions/Nested/Contract.cs",
        "global.json", "Directory.Build.props", "src/Directory.Build.props", "VERSION",
        "Directory.Build.targets", "Directory.Packages.props"
    ];
    private static readonly string[] UnrelatedInputs =
        ["README.md", "docs/unrelated-guide.md", "src/Unrelated/Nested/Other.cs"];

    [Theory]
    [InlineData("pull_request")]
    [InlineData("push")]
    public void Kernel_events_cover_nested_AI_and_shared_inputs_without_covering_everything(string eventName)
    {
        var workflow = ParseYaml(Read(Kernel));
        var trigger = Map(At(workflow, "on", eventName));
        var allowedKeys = eventName == "push" ? new[] { "paths", "branches" } : new[] { "paths" };
        trigger.Children.Keys.Select(Scalar).Should().BeSubsetOf(allowedKeys,
            "additional event filters need an explicit routing review");
        if (eventName == "push")
            Strings(At(trigger, "branches")).Should().Contain("master");
        AssertRouting(Strings(At(trigger, "paths")), RequiredInputs.Concat(["Makefile", "Ashlar.Runtime.sln"]));
    }

    [Fact]
    public void Readiness_push_and_internal_PR_lists_agree_and_cover_nested_AI_inputs()
    {
        var workflow = ParseYaml(Read(Readiness));
        Map(At(workflow, "on", "pull_request")).Children.Keys.Select(Scalar)
            .Should().NotContain(["paths", "paths-ignore"], "every PR still needs a readiness summary");
        var push = Strings(At(workflow, "on", "push", "paths"));
        var filter = Steps(workflow, "changes").Single(step =>
            step.Children.TryGetValue(new YamlScalarNode("id"), out var id) && Scalar(id) == "filter");
        var internalPaths = ReadShellArray(Scalar(At(filter, "run")));
        internalPaths.Should().Equal(push, "both readiness lists must have identical globs and ordering");
        AssertRouting(push, RequiredInputs);
        AssertRouting(internalPaths, RequiredInputs);
    }

    [Fact]
    public void Automatic_Kernel_Tier_A_reaches_the_AI_project_on_net8()
        => AssertKernelRoute(ParseYaml(Read(Kernel)), Read("Makefile"));

    [Theory]
    [InlineData("src/AI/**", "src/AI/File.cs", true)]
    [InlineData("src/AI/**", "src/AI/Nested/Deeper/File.cs", true)]
    [InlineData("src/AI/*", "src/AI/Nested/File.cs", false)]
    [InlineData("src/AI/*", "src/AI/File.cs", true)]
    [InlineData("src/AI/**", "src/AIOther/File.cs", false)]
    [InlineData(".docker/Dockerfile.*", ".docker/Dockerfile.alpine", true)]
    [InlineData(".docker/Dockerfile.*", ".docker/Dockerfile.dir/nested", false)]
    [InlineData("Makefile", "nested/Makefile", false)]
    [InlineData("**", "docs/unrelated-guide.md", true)]
    public void Supported_globs_distinguish_nested_paths(string glob, string path, bool expected)
        => Matches(glob, path).Should().Be(expected);

    [Theory]
    [InlineData("")]
    [InlineData("!src/AI/**")]
    [InlineData("src/[AB]/**")]
    [InlineData("src/AI?.cs")]
    [InlineData("src/{A,B}/**")]
    [InlineData("src/**/File.cs")]
    [InlineData("${ROOT}/**")]
    public void Unsupported_globs_are_errors(string glob)
        => Assert.Throws<InvalidDataException>(() => Matches(glob, "src/AI/File.cs"));

    [Theory]
    [InlineData("on: {push: {paths: []}}")]
    [InlineData("on: {push: {paths: 'src/AI/**'}}")]
    [InlineData("on: {push: {paths: [{bad: value}]}}")]
    [InlineData("on: {push: {other: []}}")]
    [InlineData("on: [malformed")]
    public void Missing_empty_or_unsupported_YAML_paths_are_errors(string yaml)
        => Assert.Throws<InvalidDataException>(() => Strings(At(ParseYaml(yaml), "on", "push", "paths")));

    [Theory]
    [InlineData("READINESS_PATHS=()")]
    [InlineData("READINESS_PATHS=(\n)\n")]
    [InlineData("READINESS_PATHS=(\n  \"${DYNAMIC}/**\"\n)\n")]
    [InlineData("READINESS_PATHS=(\n  unquoted/**\n)\n")]
    [InlineData("READINESS_PATHS=(\n  \"src/AI/**\"\n")]
    [InlineData("READINESS_PATHS=(\n  \"src/AI/**\"\n)\nREADINESS_PATHS=()\n")]
    [InlineData("READINESS_PATHS=(\n  \"src/AI/**\"\n)\nREADINESS_PATHS[0]=\"unrelated/**\"\n")]
    [InlineData("READINESS_PATHS=(\n  \"src/AI/**\"\n)\nunset 'READINESS_PATHS[0]'\n")]
    [InlineData("READINESS_PATHS=(\n  \"src/AI/**\"\n)\nprintf -v 'READINESS_PATHS[0]' 'unrelated/**'\n")]
    public void Missing_empty_or_dynamic_internal_lists_are_errors(string script)
        => Assert.Throws<InvalidDataException>(() => ReadShellArray(script));

    [Fact]
    public void Broad_all_paths_cannot_satisfy_the_routing_contract()
        => Assert.ThrowsAny<Exception>(() => AssertRouting(["**"], RequiredInputs));

    [Theory]
    [InlineData("\t# $(MAKE) meai-pipeline-gate\n")]
    [InlineData("\tif false; then $(MAKE) meai-pipeline-gate; fi\n")]
    [InlineData("\t$(MAKE) another-target\n")]
    public void A_commented_conditional_or_missing_Make_edge_does_not_count(string replacement)
    {
        var make = Read("Makefile").Replace("\t$(MAKE) meai-pipeline-gate\n", replacement);
        make.Should().NotBe(Read("Makefile"), "the fixture must mutate the real direct edge");
        Assert.ThrowsAny<Exception>(() => AssertKernelRoute(ParseYaml(Read(Kernel)), make));
    }

    [Theory]
    [InlineData("--list-tests")]
    [InlineData("--unknown-option")]
    [InlineData("; echo success")]
    public void Unsupported_AI_command_shapes_are_errors(string extra)
    {
        var make = Read("Makefile").Replace("-c Release --nologo \\\n", $"-c Release --nologo {extra} \\\n");
        make.Should().NotBe(Read("Makefile"));
        Assert.Throws<InvalidDataException>(() => AssertKernelRoute(ParseYaml(Read(Kernel)), make));
    }

    [Fact]
    public void A_job_level_failure_mute_is_not_a_valid_Kernel_route()
    {
        var workflow = ParseYaml(Read(Kernel));
        Map(At(workflow, "jobs", "kernel-gate")).Add("continue-on-error", "true");
        Assert.ThrowsAny<Exception>(() => AssertKernelRoute(workflow, Read("Makefile")));
    }

    private static void AssertRouting(IReadOnlyList<string> paths, IEnumerable<string> required)
    {
        paths.Should().NotBeEmpty();
        paths.Should().OnlyHaveUniqueItems();
        var inputs = required.ToArray();
        inputs.Should().NotBeEmpty("the shared-input population must not collapse to an empty check");
        foreach (var path in paths)
            _ = Matches(path, "parser-control"); // Validate every glob, including unused entries.
        foreach (var input in inputs)
            paths.Any(glob => Matches(glob, input)).Should().BeTrue("the route must cover {0}", input);
        foreach (var input in UnrelatedInputs)
            paths.Any(glob => Matches(glob, input)).Should().BeFalse("unrelated {0} must stay outside heavy routing", input);
    }

    private static bool Matches(string glob, string path)
    {
        // Current lists use literals, basename *, and terminal /** only. Unlike Bash's [[ == ]],
        // GitHub's single * must not match '/', which is what catches a narrowed AI wildcard.
        if (!Regex.IsMatch(glob, @"^[A-Za-z0-9_./*\-]+$") || glob.Contains("***", StringComparison.Ordinal)
            || (glob.Contains("**", StringComparison.Ordinal) && glob != "**"
                && (!glob.EndsWith("/**", StringComparison.Ordinal)
                    || glob.IndexOf("**", StringComparison.Ordinal) != glob.Length - 2)))
            throw new InvalidDataException($"Unsupported path glob: {glob}");
        var pattern = Regex.Escape(glob).Replace(@"\*\*", ".*").Replace(@"\*", "[^/]*");
        return Regex.IsMatch(path, "\\A" + pattern + "\\z", RegexOptions.CultureInvariant);
    }

    private static string[] ReadShellArray(string script)
    {
        var lines = script.Replace("\r\n", "\n").Split('\n');
        // The supported variable uses are one literal declaration and the existing read loop.
        // Any other use could overwrite/unset elements (including indexed or printf -v writes).
        foreach (var line in lines.Select(line => line.Trim()).Where(line =>
                     !line.StartsWith('#') && line.Contains("READINESS_PATHS", StringComparison.Ordinal)))
        {
            if (line != "READINESS_PATHS=(" && line != "for pat in \"${READINESS_PATHS[@]}\"; do")
                throw new InvalidDataException($"Unsupported READINESS_PATHS use: {line}");
        }
        var starts = lines.Select((line, index) => (line, index))
            .Where(x => x.line.Trim() == "READINESS_PATHS=(").Select(x => x.index).ToArray();
        if (starts.Length != 1)
            throw new InvalidDataException("Expected exactly one literal READINESS_PATHS array");
        var result = new List<string>();
        for (var i = starts[0] + 1; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line == ")")
                return result.Count > 0 ? result.ToArray() : throw new InvalidDataException("Empty READINESS_PATHS");
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            var match = Regex.Match(line, "^\"([^\"]+)\"$");
            if (!match.Success)
                throw new InvalidDataException($"Unsupported READINESS_PATHS entry: {line}");
            var glob = match.Groups[1].Value;
            _ = Matches(glob, "parser-control");
            result.Add(glob);
        }
        throw new InvalidDataException("Unterminated READINESS_PATHS");
    }

    private static void AssertKernelRoute(YamlMappingNode workflow, string make)
    {
        var job = Map(At(workflow, "jobs", "kernel-gate"));
        job.Children.ContainsKey(new YamlScalarNode("if")).Should().BeFalse("a new job condition needs routing review");
        job.Children.ContainsKey(new YamlScalarNode("continue-on-error")).Should().BeFalse("job failures must remain visible");
        var tier = Steps(workflow, "kernel-gate").Single(step =>
            step.Children.TryGetValue(new YamlScalarNode("name"), out var name) && Scalar(name) == "Tier A");
        Scalar(At(tier, "if")).Trim().Should().Be("github.event_name != 'workflow_dispatch' || inputs.tier == 'a'",
            "this supported condition runs Tier A automatically for PRs and pushes; review a changed condition explicitly");
        Scalar(At(tier, "run")).Trim().Should().Be("make kernel-gate");
        tier.Children.ContainsKey(new YamlScalarNode("continue-on-error")).Should().BeFalse();
        Recipe(make, "kernel-gate").Should().Contain("$(MAKE) meai-pipeline-gate",
            "Tier A must call the AI target as an unconditional recipe command, not in a comment or optional branch");
        var commands = Recipe(make, "meai-pipeline-gate");
        commands.Should().ContainSingle("the direct AI target must contain one unconditional dotnet test command");
        var command = commands.Single();
        if (!Regex.IsMatch(command, @"^[A-Za-z0-9_./\- ]+$"))
            throw new InvalidDataException($"Unsupported AI test command: {command}");
        var argv = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        argv.Take(3).Should().Equal("dotnet", "test", AiTests);
        var options = new Dictionary<string, string?>();
        for (var i = 3; i < argv.Length; i++)
        {
            var option = argv[i];
            if (option == "--nologo")
                options.Add(option, null);
            else if (option is "-f" or "-c" or "--blame-hang-timeout" or "--blame-hang-dump-type")
            {
                if (++i == argv.Length || argv[i].StartsWith('-'))
                    throw new InvalidDataException($"Missing value for {option}");
                options.Add(option, argv[i]);
            }
            else
                throw new InvalidDataException($"Unsupported AI test option: {option}");
        }
        options.Should().ContainKey("-f").WhoseValue.Should().Be("net8.0");
        options.Should().ContainKey("-c").WhoseValue.Should().Be("Release");
    }

    private static string[] Recipe(string make, string target)
    {
        var lines = make.Replace("\r\n", "\n").Split('\n');
        var headers = lines.Select((line, index) => (line, index))
            .Where(x => x.line.StartsWith(target + ":", StringComparison.Ordinal)).ToArray();
        if (headers.Length != 1 || headers[0].line != target + ":")
            throw new InvalidDataException($"Expected one literal Make target without prerequisites: {target}");
        var commands = new List<string>();
        var pending = "";
        for (var i = headers[0].index + 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#'))
                continue;
            if (!line.StartsWith('\t'))
                break;
            var part = line.Trim();
            if (part.EndsWith('\\'))
                pending += part[..^1] + " ";
            else
            {
                commands.Add((pending + part).Trim());
                pending = "";
            }
        }
        if (pending.Length != 0 || commands.Count == 0)
            throw new InvalidDataException($"Empty or incomplete Make recipe: {target}");
        return commands.ToArray();
    }

    private static IEnumerable<YamlMappingNode> Steps(YamlMappingNode workflow, string job)
        => Sequence(At(workflow, "jobs", job, "steps")).Children.Select(Map);

    private static YamlMappingNode ParseYaml(string text)
    {
        try
        {
            var yaml = new YamlStream();
            yaml.Load(new StringReader(text));
            if (yaml.Documents.Count != 1)
                throw new InvalidDataException("Expected one YAML document");
            return Map(yaml.Documents[0].RootNode);
        }
        catch (YamlException ex) { throw new InvalidDataException("Unreadable workflow YAML", ex); }
    }

    private static YamlNode At(YamlNode node, params string[] keys)
    {
        foreach (var key in keys)
            node = Map(node).Children.TryGetValue(new YamlScalarNode(key), out var next)
                ? next : throw new InvalidDataException($"Missing YAML key: {key}");
        return node;
    }

    private static YamlMappingNode Map(YamlNode node)
        => node as YamlMappingNode ?? throw new InvalidDataException("Expected YAML mapping");
    private static YamlSequenceNode Sequence(YamlNode node)
        => node is YamlSequenceNode sequence && sequence.Children.Count > 0
            ? sequence : throw new InvalidDataException("Expected nonempty YAML sequence");
    private static string Scalar(YamlNode node)
        => node is YamlScalarNode { Value: { Length: > 0 } value }
            ? value : throw new InvalidDataException("Expected nonempty YAML scalar");
    private static string[] Strings(YamlNode node) => Sequence(node).Children.Select(Scalar).ToArray();
    private static string Read(string path) => File.ReadAllText(Path.Combine(RepoPathResolver.FindRepoRoot(), path))
        .Replace("\r\n", "\n");
}
