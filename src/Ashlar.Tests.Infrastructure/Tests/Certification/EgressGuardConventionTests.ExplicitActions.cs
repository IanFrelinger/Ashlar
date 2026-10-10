using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

public sealed partial class EgressGuardConventionTests
{
    [Fact]
    public void F11_evaluated_process_entry_is_confined_to_the_two_guarded_funnels()
    {
        Tree.Value.Files.Where(path => Regex.IsMatch(Tree.Value.Model(path)!.Code, @"\bRunEvaluatedAsync\s*\(", RegexOptions.CultureInvariant))
            .Should().BeEquivalentTo(new[] { ProcessGuardImplFile, "src/Ashlar.Infrastructure/Scaling/ProcessCommandRunner.cs" });
    }

    /// <summary>
    /// Positive textual rule for explicit decisions. Reuses the inventory's production population and cleaned
    /// source. A chained throw or a declared local whose Refuses member is read within its scope is required.
    /// This does not prove control flow: the behavioral twins and post-commit mutations prove the actual effects.
    /// </summary>
    [Fact]
    public void F10_every_explicit_decision_is_acted_on()
    {
        var scan = Tree.Value;
        var models = scan.Files.Select(path => scan.Model(path)!).ToList();
        models.Sum(model => Scanner.GuardCall.Matches(model.Code).Count).Should().BeGreaterThanOrEqualTo(17);
        models.SelectMany(ExplicitActionProblems).Should().BeEmpty(
            "every explicit decision must chain ThrowIfRefused() or store a local and read its Refuses property in the same member and scope");
    }

    [Theory]
    [InlineData("g.Evaluate(new EgressRequest(f, s, d)).ThrowIfRefused();", true)]
    [InlineData("g.Evaluate(new EgressRequest(f, s, d) { Initiator = i }).ThrowIfRefused();", true)]
    [InlineData("var decision = g.Evaluate(new EgressRequest(f, s, d)); if (decision.Refuses) return;", true)]
    [InlineData("EgressDecision decision = g.Evaluate(new EgressRequest(f, s, d)); if (decision.Refuses) return;", true)]
    [InlineData("g.Evaluate(new EgressRequest(f, s, d));", false)]
    [InlineData("_ = g.Evaluate(new EgressRequest(f, s, d));", false)]
    [InlineData("var decision = g.Evaluate(new EgressRequest(f, s, d));", false)]
    [InlineData("var decision = g.Evaluate(new EgressRequest(f, s, d)); var text = \"decision.Refuses\";", false)]
    [InlineData("var decision = g.Evaluate(new EgressRequest(f, s, d)); // decision.Refuses", false)]
    [InlineData("field = g.Evaluate(new EgressRequest(f, s, d)); if (field.Refuses) return;", false)]
    [InlineData("var decision = g.Evaluate(new EgressRequest(f, s, d)); } void Other() { if (decision.Refuses) return;", false)]
    [InlineData("{ var decision = g.Evaluate(new EgressRequest(f, s, d)); } if (decision.Refuses) return;", false)]
    public void F10_explicit_action_controls(string body, bool accepted)
    {
        var model = new SourceModel("control.cs", "class C { void Run() { " + body + "\n} }");
        Scanner.GuardCall.Matches(model.Code).Should().ContainSingle();
        ExplicitActionProblems(model).Any().Should().Be(!accepted);
    }

    private static IEnumerable<string> ExplicitActionProblems(SourceModel model)
    {
        var code = model.Code;
        foreach (Match call in Scanner.GuardCall.Matches(code))
        {
            var open = code.IndexOf('(', call.Index);
            var close = Scanner.ClosingParen(code, open);
            if (close < code.Length && Regex.IsMatch(code[(close + 1)..], @"^\s*\.\s*ThrowIfRefused\s*\(\s*\)", RegexOptions.CultureInvariant))
                continue;

            var start = Scanner.StatementStart(code, call.Index);
            var prefix = code[start..call.Index];
            var local = Regex.Match(prefix, @"^\s*(?:var|EgressDecision)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*[A-Za-z_][A-Za-z0-9_.\s]*$", RegexOptions.CultureInvariant);
            var block = model.Innermost(call.Index);
            var end = Math.Min(model.Member(call.Index).End, block < 0 ? code.Length : model.Blocks[block].Close);
            if (local.Success && close < end && Regex.IsMatch(code[(close + 1)..end],
                @"(?<![A-Za-z0-9_.])" + Regex.Escape(local.Groups["name"].Value) + @"\s*\.\s*Refuses\b", RegexOptions.CultureInvariant))
                continue;
            yield return $"{model.Path}:{model.LineOf(call.Index)}: explicit decision is not acted on";
        }
    }
}
