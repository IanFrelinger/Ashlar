using System.Text.Json;
using FluentAssertions;
using Ashlar.CLI.Commands;
using Xunit;

namespace Ashlar.Tests.CLI.Tests.Commands;

/// <summary>Tests for new command.</summary>
[Trait("Category", "CLI")]
public sealed class NewCommandTests
{
    [Fact]
    public void ExecuteBrick_rejects_invalid_name()
    {
        var output = Path.Combine(Path.GetTempPath(), $"ashlar-new-brick-{Guid.NewGuid():N}");
        NewCommand.ExecuteBrick("123 bad", output, json: false).Should().Be(1);
        Directory.Exists(output).Should().BeFalse();
    }

    [Fact]
    public void ExecuteBrick_generates_files()
    {
        var output = Path.Combine(Path.GetTempPath(), $"ashlar-new-brick-{Guid.NewGuid():N}");
        try
        {
            NewCommand.ExecuteBrick("MyThing", output, json: false).Should().Be(0);

            File.Exists(Path.Combine(output, "MyThingBrick", "MyThingBrick.csproj")).Should().BeTrue();
            File.Exists(Path.Combine(output, "MyThingBrick", "MyThingBrick.cs")).Should().BeTrue();
            File.Exists(Path.Combine(output, "MyThingBrick.Tests", "MyThingBrick.Tests.csproj")).Should().BeTrue();
            File.Exists(Path.Combine(output, "MyThingBrick.Tests", "MyThingBrickTests.cs")).Should().BeTrue();

            File.ReadAllText(Path.Combine(output, "MyThingBrick", "MyThingBrick.cs"))
                .Should()
                .Contain("public sealed class MyThingBrick")
                .And.Contain("Id = \"my-thing\"");

            var leftovers = Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories)
                .SelectMany(f => System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(f), @"__[A-Za-z][A-Za-z ]*__")
                    .Select(m => $"{Path.GetRelativePath(output, f)}: {m.Value}"))
                .ToList();
            leftovers.Should().BeEmpty("every template token is replaced in the scaffold");
        }
        finally
        {
            if (Directory.Exists(output))
                Directory.Delete(output, recursive: true);
        }
    }

    [Fact]
    public void ExecuteBrick_refuses_existing_directory()
    {
        var output = Path.Combine(Path.GetTempPath(), $"ashlar-new-brick-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(output, "MyThingBrick"));
            File.WriteAllText(Path.Combine(output, "MyThingBrick", "existing.txt"), "keep");

            NewCommand.ExecuteBrick("MyThing", output, json: false).Should().Be(1);
            File.ReadAllText(Path.Combine(output, "MyThingBrick", "existing.txt")).Should().Be("keep");
        }
        finally
        {
            if (Directory.Exists(output))
                Directory.Delete(output, recursive: true);
        }
    }

    [Fact]
    public void ExecuteBrick_json_output_is_machine_readable()
    {
        var output = Path.Combine(Path.GetTempPath(), $"ashlar-new-brick-{Guid.NewGuid():N}");
        var writer = new StringWriter();  // not disposed on purpose: a disposed writer left in Console.Out poisons later tests
        Console.SetOut(writer);
        try
        {
            NewCommand.ExecuteBrick("JsonThing", output, json: true).Should().Be(0);
        }
        finally
        {
            Console.SetOut(ConsoleCapture.Out);
            if (Directory.Exists(output))
                Directory.Delete(output, recursive: true);
        }

        using var doc = JsonDocument.Parse(writer.ToString());
        doc.RootElement.GetProperty("ok").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("details").GetProperty("testProject").GetString().Should().Contain("JsonThingBrick.Tests");
        doc.RootElement.GetProperty("details").GetProperty("restoreHint").GetString().Should()
            .Contain("is on nuget.org, a plain dotnet restore is enough")
            .And.Contain("If it is not (NU1101), pass a local feed");
    }
}
