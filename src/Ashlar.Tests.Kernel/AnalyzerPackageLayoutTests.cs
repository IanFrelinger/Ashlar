using FluentAssertions;
using Xunit;

namespace Ashlar.Tests.Kernel;

/// <summary>
/// The headline half of the analyzer-fence defect: Ashlar.Analyzers packed its assembly ONLY into
/// <c>lib/netstandard2.0/</c>. NuGet loads analyzers from <c>analyzers/dotnet/cs/</c> and nowhere
/// else, so a <c>PackageReference</c> added every rule to the reference set and ran none of them.
/// The build was clean, because a rule that never runs never fires — the fence was silently inert
/// for every brick author consuming Ashlar from nuget.org.
///
/// <para>This pins the packaging shape at its source. The fix was also verified the only way that
/// really proves it: packing the project, unzipping the .nupkg (both paths present), and consuming
/// it from a scratch project, where ASHLAR0002/0006/0009 then fired. A unit test cannot run
/// <c>dotnet pack</c>, but it can stop the item silently disappearing again.</para>
/// </summary>
public sealed class AnalyzerPackageLayoutTests
{
    [Fact]
    public void The_analyzer_project_packs_its_assembly_where_the_compiler_looks()
    {
        var csproj = File.ReadAllText(AnalyzerProjectPath());

        csproj.Should().Contain("analyzers/dotnet/cs",
            "NuGet loads analyzers from analyzers/dotnet/cs and nowhere else; without this a "
            + "PackageReference to Ashlar.Analyzers runs no rules at all");
        csproj.Should().Contain("Pack=\"true\"");
    }

    [Fact]
    public void It_still_packs_the_library_role_as_well()
    {
        // Both roles, deliberately: Ashlar.Infrastructure consumes this assembly at RUNTIME
        // (AnalyzerFenceGate constructs the catalog), so dropping the lib/ copy in favour of the
        // analyzers/ one would break the packed hosting graph.
        var csproj = File.ReadAllText(AnalyzerProjectPath());

        csproj.Should().Contain("<IsPackable>true</IsPackable>");
        csproj.Should().NotContain("<IncludeBuildOutput>false</IncludeBuildOutput>",
            "suppressing the build output would remove the lib/ copy the hosting graph depends on");
        csproj.Should().NotContain("<DevelopmentDependency>true</DevelopmentDependency>",
            "a development dependency is PrivateAssets by default for consumers, which would cut "
            + "Ashlar.Infrastructure's runtime reference");
    }

    private static string AnalyzerProjectPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 12 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Ashlar.Analyzers", "Ashlar.Analyzers.csproj");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        throw new InvalidOperationException(
            "could not locate src/Ashlar.Analyzers/Ashlar.Analyzers.csproj above " + AppContext.BaseDirectory);
    }
}
