using Ashlar.Infrastructure.Certification;
using Ashlar.Tests.Infrastructure.Certification.Fixtures;
using FluentAssertions;
using Mono.Cecil;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>Issue #493: author project inputs cannot become a different gate-emitted program.</summary>
[Trait("Category", "Certification")]
public sealed class CompiledSourceSetRegressionTests
{
    [Theory]
    [InlineData("non-cs-include")]
    [InlineData("nested-obj")]
    [InlineData("nested-bin")]
    [InlineData("remove-and-replace")]
    public async Task Unjudged_project_sources_are_absent_from_the_emitted_artifact(string vector)
    {
        var directory = Path.Combine(Path.GetTempPath(), "ashlar-493-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var payloadPath = vector.StartsWith("nested-", StringComparison.Ordinal)
                ? Path.Combine("Sub", vector[7..], "Payload.cs") : "Payload.cstxt";
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(directory, payloadPath))!);
            // A harmless initializer that would throw if the old project-build/load path ran it.
            await File.WriteAllTextAsync(Path.Combine(directory, payloadPath), """
                using System;
                using System.Runtime.CompilerServices;
                public static class UnjudgedPayload
                {
                    [ModuleInitializer] public static void Initialize() => throw new Exception("unjudged initializer executed");
                }
                """);
            var items = vector == "remove-and-replace"
                ? "<Compile Remove=\"Brick.cs\"/><Compile Include=\"Payload.cstxt\"/>"
                : vector == "non-cs-include" ? "<Compile Include=\"Payload.cstxt\"/>" : "";
            await File.WriteAllTextAsync(Path.Combine(directory, "Brick.csproj"),
                $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup><ItemGroup>{items}</ItemGroup></Project>");
            await File.WriteAllTextAsync(Path.Combine(directory, "Brick.cs"), MutationProbeBrickSource.Code);
            var witness = Path.Combine(directory, "witness.json");
            await File.WriteAllTextAsync(witness, """{"brickId":"mutation-probe-brick","cases":[]}""");

            var request = await BrickCertificationProjectLoader.LoadAsync(directory, witness);

            request.SourceCode.Should().Be(MutationProbeBrickSource.Code);
            request.EmittedArtifact.Should().NotBeNull();
            using var module = ModuleDefinition.ReadModule(new MemoryStream(request.EmittedArtifact!.AssemblyBytes));
            module.Types.Should().NotContain(t => t.Name == "UnjudgedPayload");
            request.Brick.GetType().Module.ModuleVersionId.Should().Be(module.Mvid,
                "the brick activated by the loader must be the gate-emitted bytes");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task Directory_build_props_is_refused_before_any_author_code_runs()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ashlar-493-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "Brick.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"/>");
            await File.WriteAllTextAsync(Path.Combine(directory, "Brick.cs"), MutationProbeBrickSource.Code);
            await File.WriteAllTextAsync(Path.Combine(directory, "Directory.Build.props"),
                "<Project><ItemGroup><Compile Include=\"Payload.cstxt\"/><PackageReference Include=\"Newtonsoft.Json\" Version=\"13.0.3\"/></ItemGroup></Project>");
            var load = () => BrickCertificationProjectLoader.LoadAsync(directory, Path.Combine(directory, "witness.json"));
            await load.Should().ThrowAsync<InvalidOperationException>().WithMessage("*build-surface fence*Directory.Build.props*");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
