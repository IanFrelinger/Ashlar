using System.Text.Json;
using Ashlar.Core.Application.Certification.Models;
using Ashlar.Core.Application.Certification.Ports;
using Ashlar.Infrastructure.Certification;
using FluentAssertions;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>Issue #462: correctness and mutation must agree across CLR and JSON transport values.</summary>
[Trait("Category", "Certification")]
public sealed class WitnessKindRegressionTests
{
    [Theory]
    [InlineData("2", "2.4")]
    [InlineData("2", "1.5")]
    [InlineData("1", "\"1\"")]
    [InlineData("1", "true")]
    [InlineData("0", "false")]
    [InlineData("3.14", "\"3.14\"")]
    [InlineData("true", "\"True\"")]
    public void Different_json_kinds_never_satisfy_either_judge(string expectedJson, string actualJson)
    {
        var expected = JsonSerializer.Deserialize<JsonElement>(expectedJson);
        var actual = JsonSerializer.Deserialize<JsonElement>(actualJson);
        WitnessValueComparer.AreEqual(expected, actual).Should().BeFalse();
        Judge(expected, actual, false);
    }

    [Theory]
    [InlineData("2")]
    [InlineData("3.14")]
    [InlineData("true")]
    [InlineData("\"hello\"")]
    [InlineData("{\"message\":\"a\"}")]
    [InlineData("[\"a\"]")]
    public void Identical_json_values_satisfy_both_judges(string json)
    {
        var value = JsonSerializer.Deserialize<JsonElement>(json);
        WitnessValueComparer.AreEqual(value, value).Should().BeTrue();
        Judge(value, value, true);
    }

    [Fact]
    public void Floating_output_does_not_match_a_string_witness_in_either_direction()
    {
        WitnessValueComparer.AreEqual(3.14, "3.14").Should().BeFalse();
        WitnessValueComparer.AreEqual("3.14", 3.14).Should().BeFalse();
        Judge(3.14, "3.14", false);
        Judge("3.14", 3.14, false);
    }

    [Fact]
    public void A_session_repeat_changing_a_number_into_a_string_is_not_deterministic()
    {
        var number = new CandidateCaseObservation("candidate", 0, 0, false, null, null,
            new Dictionary<string, object?> { ["value"] = 1 });
        var text = number with { Repeat = 1, Outputs = new Dictionary<string, object?> { ["value"] = "1" } };
        WitnessRunner.CanonicalizeObservation(number).Should().NotBe(WitnessRunner.CanonicalizeObservation(text));
    }

    private static void Judge(object expected, object actual, bool passes)
    {
        var witness = new WitnessSpec("kind-probe", [new WitnessCase(
            new Dictionary<string, object>(), new Dictionary<string, object> { ["value"] = expected })]);
        CandidateCaseObservation[] observations = [new("candidate", 0, 0, false, null, null,
            new Dictionary<string, object?> { ["value"] = actual })];
        WitnessRunner.JudgeObservations(witness, observations).Passed.Should().Be(passes);
        WitnessRunner.JudgeMutantObservations(witness, observations).Should().Be(passes);
    }
}
