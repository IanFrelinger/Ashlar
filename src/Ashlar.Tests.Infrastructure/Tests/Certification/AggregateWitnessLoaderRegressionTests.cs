using System.Text.Json;
using Ashlar.Infrastructure.Certification;
using Ashlar.Tests.Infrastructure.Certification.Fixtures;
using FluentAssertions;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>Issue #462: loading a witness must preserve aggregate output kinds.</summary>
[Trait("Category", "Certification")]
public sealed class AggregateWitnessLoaderRegressionTests
{
    [Theory]
    [InlineData("{\"message\":\"a\"}")]
    [InlineData("[\"a\"]")]
    public async Task A_loaded_aggregate_witness_rejects_a_brick_returning_its_json_as_a_string(string json)
    {
        var directory = Path.Combine(Path.GetTempPath(), "ashlar-462-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "Brick.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
            await File.WriteAllTextAsync(Path.Combine(directory, "Brick.cs"), MutationProbeBrickSource.Code);
            var witnessPath = Path.Combine(directory, "witness.json");
            await File.WriteAllTextAsync(witnessPath, JsonSerializer.Serialize(new
            {
                brickId = "mutation-probe-brick",
                cases = new[] { new
                {
                    input = new { logText = "ERROR " + json },
                    expectedOutput = new Dictionary<string, JsonElement>
                    {
                        ["firstErrorMessage"] = JsonSerializer.Deserialize<JsonElement>(json)
                    }
                } }
            }));

            var request = await BrickCertificationProjectLoader.LoadAsync(directory, witnessPath);
            var result = await WitnessRunner.RunAsync(request.Brick, request.Witness,
                new AuditExecutionContext(), CancellationToken.None);

            result.Passed.Should().BeFalse("an object/array witness cannot be satisfied by a string containing its JSON");
            result.Failures.Should().ContainSingle();
            request.Witness.Cases[0].ExpectedOutput["firstErrorMessage"].Should().BeOfType<JsonElement>();
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
