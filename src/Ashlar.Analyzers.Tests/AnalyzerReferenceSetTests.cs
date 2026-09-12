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
/// <para>The last two are the convention half, in both directions
/// (<c>docs/HowGatesGoQuiet.md</c> section 7): a NEW hand-rolled reference set fails, and the
/// helper quietly reverting to the ambient scheme fails. Without the second, the first freezes a
/// rule that no longer describes anything.</para>
/// </summary>
public sealed class AnalyzerReferenceSetTests
{
    // The needles are assembled from fragments on purpose: this file sits in the directory it
    // scans, and a literal here would match itself.
    private static readonly string AmbientLoadContextCall = "AppDomain.CurrentDomain." + "GetAssemblies";
    private static readonly string ReferenceFactoryCall = "MetadataReference." + "CreateFromFile";
    private static readonly string TrustedPlatformList = "TRUSTED_PLATFORM" + "_ASSEMBLIES";

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

    /// <summary>Direction one: a second hand-rolled reference set anywhere in this project fails.</summary>
    [Fact]
    public void No_other_file_in_this_project_builds_its_own_reference_set()
    {
        var sources = ProjectSourceFiles();

        // Positive control: a scan that found nothing would pass every assertion below.
        sources.Should().HaveCountGreaterThan(
            5, "the scan must actually be reading this project's sources, otherwise it is vacuous");

        var offenders = sources
            .Where(file => !string.Equals(
                Path.GetFileName(file), "AnalyzerReferenceSet.cs", StringComparison.Ordinal))
            .Where(file => !string.Equals(
                Path.GetFileName(file), "AnalyzerReferenceSetTests.cs", StringComparison.Ordinal))
            .Where(file =>
            {
                var text = File.ReadAllText(file);
                return text.Contains(AmbientLoadContextCall, StringComparison.Ordinal)
                    || text.Contains(ReferenceFactoryCall, StringComparison.Ordinal);
            })
            .Select(Path.GetFileName)
            .ToArray();

        offenders.Should().BeEmpty(
            "every analyzer sample compiles against AnalyzerReferenceSet. A private copy "
            + "reintroduces the defect one file at a time — which is how two copies of the old "
            + "one came to exist");
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

    private static IReadOnlyList<string> ProjectSourceFiles()
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

        return Directory.GetFiles(directory!.FullName, "*.cs", SearchOption.TopDirectoryOnly);
    }
}
