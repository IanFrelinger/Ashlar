using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ashlar.Analyzers.Tests;

/// <summary>
/// Pins the property that <see cref="AnalyzerReferenceSet"/> exists for: the references the
/// analyzer samples compile against are DECLARED, not whatever the test host happened to load.
///
/// <para>The first two facts are the regression. Test.Sdk 18.9.0 stopped flowing
/// <c>Newtonsoft.Json</c> into the test host, the <c>System.ComponentModel</c> facade that
/// carries the <see cref="IServiceProvider"/> type-forward stopped being loaded with it, and
/// seven analyzer tests went red with CS1069 — with no change to any test, analyzer, or product
/// file. These two assert the set no longer has that dependency, and they fail on the old
/// harness under any runner that does not happen to load the facade.</para>
///
/// <para>The rest are the convention half, in both directions
/// (<c>docs/HowGatesGoQuiet.md</c> section 7): a NEW hand-rolled reference set fails, the helper
/// quietly reverting to the ambient scheme fails, and the scan itself is pinned to the one file it
/// is meant to find. Without the second, the first freezes a rule that no longer describes
/// anything; without the third, it can stop recognising what it forbids and still pass.</para>
///
/// <para><b>None of this runs in CI.</b> <c>ci/test-ownership.tsv</c> records
/// <c>Ashlar.Analyzers.Tests</c> as UNOWNED — it is in <c>Ashlar.sln</c> and named by no gate — so
/// these guards, and the seven <c>SelfRecursiveRegistrationAnalyzerTests</c> whose CS1069
/// regression prompted them, are visible to a local run and to nothing else. The regression that
/// motivated this file was itself invisible to every check on every pull request. Read a green
/// pull request accordingly, and run this project before changing the test SDK pins.</para>
/// </summary>
public sealed class AnalyzerReferenceSetTests
{
    // The needles are assembled from fragments on purpose: this file sits in the tree it scans,
    // and a literal here would match itself.
    private static readonly string AmbientLoadContextCall = "AppDomain.CurrentDomain." + "GetAssemblies";
    private static readonly string TrustedPlatformList = "TRUSTED_PLATFORM" + "_ASSEMBLIES";

    /// <summary>
    /// Every way Roslyn will hand back a reference, not only the spelling the two removed copies
    /// happened to use. One literal plus a top-directory scan is a convention that forbids one
    /// spelling in one folder: a copy one directory down walked straight past it, and so did the
    /// same hand-rolled set built through <c>AssemblyMetadata</c> rather than
    /// <c>MetadataReference</c>. Both rebuild the defect exactly; neither was visible.
    /// </summary>
    private static readonly string[] ReferenceFactoryCalls =
    [
        "MetadataReference." + "CreateFromFile",
        "MetadataReference." + "CreateFromStream",
        "MetadataReference." + "CreateFromImage",
        "AssemblyMetadata." + "CreateFromFile",
        "AssemblyMetadata." + "CreateFromStream",
        "AssemblyMetadata." + "CreateFromImage",
    ];

    /// <summary>
    /// The positive control. Both types below live in shared-framework assemblies a test host has
    /// no reason to load — <see cref="IServiceProvider"/> is forwarded to System.ComponentModel,
    /// <c>BigInteger</c> lives in System.Runtime.Numerics — and both were dropped from the loaded
    /// set by Test.Sdk 18.9.0. If the set is ever derived from loaded assemblies again, this
    /// reddens directly instead of through seven unrelated-looking analyzer failures.
    /// </summary>
    [Fact]
    public void Resolves_framework_types_the_test_host_has_no_reason_to_load()
    {
        const string source = @"
            using System;
            using System.Numerics;

            public static class UsesTypesNothingInThisHostLoads
            {
                public static object Resolve(IServiceProvider provider, Type service)
                    => provider.GetService(service);

                public static BigInteger Big() => BigInteger.One;
            }
            ";

        var compilation = CSharpCompilation.Create(
            "AnalyzerReferenceSetProbe",
            new[] { CSharpSyntaxTree.ParseText(source) },
            AnalyzerReferenceSet.For(typeof(IServiceCollection)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToArray();

        errors.Should().BeEmpty(
            "the reference set must carry the whole shared framework the host was launched with, "
            + "not the subset it happens to have loaded: "
            + string.Join(" | ", errors.Select(e => e.ToString())));
    }

    /// <summary>
    /// The same fact stated on the set itself, so a failure names the assembly that went missing
    /// rather than only reporting that a sample stopped compiling.
    /// </summary>
    [Fact]
    public void Carries_the_whole_shared_framework_and_the_named_anchors()
    {
        var fileNames = AnalyzerReferenceSet.FileNamesFor(
            typeof(IServiceCollection), typeof(ServiceCollection));

        fileNames.Should().Contain(
            "System.ComponentModel.dll",
            "it carries the type-forward for System.IServiceProvider; losing it is exactly the "
            + "CS1069 that Test.Sdk 18.9.0 produced");
        fileNames.Should().Contain("System.Private.CoreLib.dll");
        fileNames.Should().Contain("Microsoft.Extensions.DependencyInjection.Abstractions.dll");
        fileNames.Should().Contain("Microsoft.Extensions.DependencyInjection.dll");
        fileNames.Should().HaveCountGreaterThan(
            100,
            "the shared framework alone is well over a hundred assemblies; a set the size of a "
            + "loaded-assembly list means the framework half was silently dropped");
    }

    /// <summary>
    /// Direction one: a second hand-rolled reference set anywhere in this project fails —
    /// <em>anywhere</em> meaning every subdirectory, and <em>hand-rolled</em> meaning any of the
    /// Roslyn reference factories, not one blessed spelling of one of them.
    /// </summary>
    [Fact]
    public void No_other_file_in_this_project_builds_its_own_reference_set()
    {
        var sources = ProjectSourceFiles();

        sources.Should().HaveCountGreaterThan(
            5, "the scan must actually be reading this project's sources, otherwise it is vacuous");

        var offenders = sources
            .Where(file => !string.Equals(
                Path.GetFileName(file), "AnalyzerReferenceSet.cs", StringComparison.Ordinal))
            .Where(file => !string.Equals(
                Path.GetFileName(file), "AnalyzerReferenceSetTests.cs", StringComparison.Ordinal))
            .Where(BuildsItsOwnReferenceSet)
            .Select(file => Path.GetRelativePath(ProjectDirectory(), file))
            .ToArray();

        offenders.Should().BeEmpty(
            "every analyzer sample compiles against AnalyzerReferenceSet. A private copy "
            + "reintroduces the defect one file at a time — which is how two copies of the old "
            + "one came to exist. Offending: " + string.Join(", ", offenders));
    }

    /// <summary>
    /// The positive control for the scan above, which is otherwise a list of things it did not
    /// find. It states, in both directions, that the reader reads and the needles match: with the
    /// two exemptions lifted the scan finds <c>AnalyzerReferenceSet.cs</c> and nothing else, and
    /// the recursive walk really is recursive.
    /// </summary>
    /// <remarks>
    /// <c>AnalyzerReferenceSetTests.cs</c> does not appear because its needles are assembled from
    /// fragments at run time, so the literals are never in the file. If a needle ever stops
    /// matching the helper, this fails here rather than leaving the convention above passing
    /// because it can no longer recognise the thing it forbids.
    /// </remarks>
    [Fact]
    public void The_scan_finds_the_one_file_that_is_allowed_to_build_a_reference_set()
    {
        var matched = ProjectSourceFiles()
            .Where(BuildsItsOwnReferenceSet)
            .Select(Path.GetFileName)
            .ToArray();

        matched.Should().BeEquivalentTo(
            ["AnalyzerReferenceSet.cs"],
            "the needles must still match the one implementation they were written against");

        var all = RawProjectFiles();
        all.Should().Contain(
            file => file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal),
            "the walk must descend into subdirectories, and the generated sources MSBuild writes "
            + "under obj/ are the subdirectory this project always has. A top-directory scan "
            + "misses them — and missed a hand-rolled reference set one folder down");
    }

    private static bool BuildsItsOwnReferenceSet(string file)
    {
        var text = File.ReadAllText(file);
        return text.Contains(AmbientLoadContextCall, StringComparison.Ordinal)
            || ReferenceFactoryCalls.Any(call => text.Contains(call, StringComparison.Ordinal));
    }

    /// <summary>
    /// Direction two: the helper itself cannot quietly go back to the ambient load context. A
    /// one-directional convention test freezes a rule that has stopped describing anything.
    /// </summary>
    [Fact]
    public void The_shared_helper_still_derives_its_set_from_declared_inputs()
    {
        var helper = ProjectSourceFiles()
            .Single(file => string.Equals(
                Path.GetFileName(file), "AnalyzerReferenceSet.cs", StringComparison.Ordinal));
        var text = File.ReadAllText(helper);

        text.Should().Contain(
            TrustedPlatformList,
            "the framework half of the set comes from the list the host was LAUNCHED with");
        text.Should().NotContain(
            AmbientLoadContextCall,
            "reverting the helper to the loaded-assembly list restores the defect for every caller "
            + "at once, and would otherwise leave the convention test above passing");
    }

    private static string ProjectDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
            && !File.Exists(Path.Combine(directory.FullName, "Ashlar.Analyzers.Tests.csproj")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull(
            "this test scans its own project sources; no Ashlar.Analyzers.Tests.csproj was found "
            + $"walking up from '{AppContext.BaseDirectory}', so the scan cannot run and must not "
            + "report a clean result");

        return directory!.FullName;
    }

    /// <summary>Every <c>.cs</c> under the project, build outputs included.</summary>
    private static IReadOnlyList<string> RawProjectFiles()
        => Directory.GetFiles(ProjectDirectory(), "*.cs", SearchOption.AllDirectories);

    /// <summary>
    /// The project's own sources: the whole tree, not one folder, minus the build directories —
    /// MSBuild writes generated <c>.cs</c> there, and they are output rather than source.
    /// </summary>
    private static IReadOnlyList<string> ProjectSourceFiles()
    {
        var generated = new[]
        {
            $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
            $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
        };

        return RawProjectFiles()
            .Where(file => !generated.Any(part => file.Contains(part, StringComparison.Ordinal)))
            .ToArray();
    }
}
