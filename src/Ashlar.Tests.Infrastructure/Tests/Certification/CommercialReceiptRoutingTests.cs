using System.Text.RegularExpressions;
using Ashlar.Core.Application.Paths;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>
/// Pins the three literal receipt inputs and their direct commands. This is not a general Bash
/// or workflow interpreter. AI routing's separate convention checks the entire path-list grammar
/// and parity; these controls ensure the commercial additions and receipt calls stay present.
/// </summary>
public sealed class CommercialReceiptRoutingTests
{
    private static readonly string[] Inputs =
    ["ci/commercial-test-suites.json", "scripts/ci/verify-commercial-receipts.py", "scripts/ci/test-commercial-receipts.py"];
    private const string ReceiptCommand = "python scripts/ci/verify-commercial-receipts.py --root . --output \"${{ runner.temp }}/commercial-receipts\"";
    private const string ControlCommand = "python3 scripts/ci/test-commercial-receipts.py";
    private static string Readiness => Read("full-platform-readiness-gate.yml");
    private static string ShellLint => Read("shell-lint.yml");

    [Fact]
    public void Receipt_inputs_reach_the_native_check_and_its_required_file_controls()
        => Check(Readiness, ShellLint);

    public static IEnumerable<object[]> MissingInputs() => Inputs.SelectMany(input =>
        new[] { new object[] { input, true }, new object[] { input, false } });

    [Theory]
    [MemberData(nameof(MissingInputs))]
    public void Dropping_a_receipt_input_from_either_path_list_fails(string input, bool push)
    {
        var yaml = Readiness;
        var line = (push ? "      - \"" : "            \"") + input + "\"\n";
        Assert.Contains(line, yaml);
        Assert.Throws<InvalidOperationException>(() => Check(yaml.Replace(line, ""), ShellLint));
    }

    [Theory]
    [InlineData("receipt")]
    [InlineData("controls")]
    [InlineData("upload")]
    public void Removing_an_evidence_or_control_command_fails(string missing)
    {
        var readiness = Readiness;
        var shell = ShellLint;
        if (missing == "receipt") readiness = readiness.Replace(ReceiptCommand, "echo skipped");
        if (missing == "controls") shell = shell.Replace(ControlCommand, "echo skipped");
        if (missing == "upload") readiness = readiness.Replace("            ${{ runner.temp }}/commercial-receipts/\n", "");
        Assert.Throws<InvalidOperationException>(() => Check(readiness, shell));
    }

    [Theory]
    [InlineData("control job skipped")]
    [InlineData("control job tolerates failure")]
    [InlineData("native job skipped")]
    [InlineData("native job tolerates failure")]
    [InlineData("controls path filtered")]
    public void Job_level_bypasses_cannot_leave_the_commands_as_decoration(string bypass)
    {
        var readiness = Readiness;
        var shell = ShellLint;
        if (bypass == "control job skipped") shell = shell.Replace("  shell-lint:\n", "  shell-lint:\n    if: false\n");
        if (bypass == "control job tolerates failure") shell = shell.Replace("  shell-lint:\n", "  shell-lint:\n    continue-on-error: true\n");
        if (bypass == "native job skipped") readiness = readiness.Replace("    if: needs.changes.outputs.run_heavy == 'true'", "    if: false");
        if (bypass == "native job tolerates failure") readiness = readiness.Replace("  native-platform:\n", "  native-platform:\n    continue-on-error: true\n");
        if (bypass == "controls path filtered") shell = shell.Replace("  pull_request:\n", "  pull_request:\n    paths: ['docs/**']\n");
        Assert.Throws<InvalidOperationException>(() => Check(readiness, shell));
    }

    private static void Check(string readinessYaml, string shellYaml)
    {
        var readiness = Parse(readinessYaml);
        var push = ((YamlSequenceNode)At(readiness, "on", "push", "paths")).Children.Select(Value).ToArray();
        var filter = Steps(readiness, "changes").Single(step => step.Children.TryGetValue(new YamlScalarNode("id"), out var id) && Value(id) == "filter");
        var script = Value(At(filter, "run"));
        var array = Regex.Matches(script, @"(?ms)^READINESS_PATHS=\(\r?\n(?<body>.*?)^\)");
        Require(array.Count == 1, "expected one literal readiness path array");
        var internalPaths = Regex.Matches(array[0].Groups["body"].Value, "(?m)^\\s*\"([^\"]+)\"\\s*$")
            .Select(match => match.Groups[1].Value).ToArray();
        Require(push.Length > 0 && internalPaths.Length > 0, "empty readiness paths");
        foreach (var input in Inputs)
            Require(push.Contains(input) && internalPaths.Contains(input), "receipt input missing from routing: " + input);

        var nativeJob = (YamlMappingNode)At(readiness, "jobs", "native-platform");
        Require(Value(At(nativeJob, "if")) == "needs.changes.outputs.run_heavy == 'true'"
            && !nativeJob.Children.ContainsKey(new YamlScalarNode("continue-on-error")),
            "native evidence must follow heavy routing and propagate failures");
        var native = Steps(readiness, "native-platform");
        var receipt = DirectStep(native, ReceiptCommand);
        var verify = DirectStep(native, "dotnet run --project application/src/Ashlar.CLI --no-build -- ci verify");
        Require(native.IndexOf(receipt) > native.IndexOf(verify), "receipt check must follow ci verify");
        var upload = native.Single(step => step.Children.TryGetValue(new YamlScalarNode("uses"), out var uses)
            && Value(uses).StartsWith("actions/upload-artifact@", StringComparison.Ordinal));
        Require(Value(At(upload, "with", "path")).Split('\n').Select(line => line.Trim())
            .Contains("${{ runner.temp }}/commercial-receipts/"), "receipt summary and TRXs must be uploaded");
        var shell = Parse(shellYaml);
        var events = (YamlMappingNode)At(shell, "on");
        Require(events.Children.TryGetValue(new YamlScalarNode("pull_request"), out var trigger)
            && trigger is YamlScalarNode scalar && string.IsNullOrEmpty(scalar.Value),
            "receipt file controls must run on every pull request without event filters");
        var controlJob = (YamlMappingNode)At(shell, "jobs", "shell-lint");
        Require(!controlJob.Children.ContainsKey(new YamlScalarNode("if"))
            && !controlJob.Children.ContainsKey(new YamlScalarNode("continue-on-error")),
            "required receipt controls cannot skip or tolerate failure at job level");
        DirectStep(Steps(shell, "shell-lint"), ControlCommand);
    }

    private static YamlMappingNode DirectStep(List<YamlMappingNode> steps, string command)
    {
        var matches = steps.Where(step => step.Children.TryGetValue(new YamlScalarNode("run"), out var run)
            && Value(run).Trim() == command).ToArray();
        Require(matches.Length == 1, "expected one direct command: " + command);
        var match = matches[0];
        Require(!match.Children.ContainsKey(new YamlScalarNode("if"))
            && !match.Children.ContainsKey(new YamlScalarNode("continue-on-error")), "evidence command must run and propagate failure");
        return match;
    }

    private static List<YamlMappingNode> Steps(YamlMappingNode workflow, string job) =>
        ((YamlSequenceNode)At(workflow, "jobs", job, "steps")).Children.Cast<YamlMappingNode>().ToList();
    private static YamlNode At(YamlNode node, params string[] keys)
    {
        foreach (var key in keys) node = ((YamlMappingNode)node).Children[new YamlScalarNode(key)];
        return node;
    }
    private static string Value(YamlNode node) => ((YamlScalarNode)node).Value!;
    private static YamlMappingNode Parse(string text)
    {
        var yaml = new YamlStream();
        yaml.Load(new StringReader(text));
        return (YamlMappingNode)yaml.Documents.Single().RootNode;
    }
    private static string Read(string name) => File.ReadAllText(Path.Combine(RepoPathResolver.FindRepoRoot(), ".github", "workflows", name)).Replace("\r\n", "\n");
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
