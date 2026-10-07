using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ashlar.Core.Application.Testing.Abstractions;
using Ashlar.Core.Application.Testing.Ports;
using Ashlar.Infrastructure.Execution;
using Xunit;

namespace Ashlar.Infrastructure.Testing;

/// <summary>
/// Runs concrete <see cref="UnitTestBase"/> types through <see cref="ITestRunner"/> so they appear in VSTest/xUnit.
///
/// <para><b>INTERNAL, and it has to be.</b> This type compiles against xunit, which
/// Ashlar.Infrastructure references as <c>PrivateAssets=all; IncludeAssets=compile</c> - so the
/// package declares no xunit dependency and ships no xunit assembly. It was public and packed in
/// v0.1.0 through v0.1.2, which means every consumer who called <see cref="ExecuteUnitTestAsync"/>
/// got a FileNotFoundException for xunit.core at runtime rather than a test result. The discovery
/// method was no more usable: it special-cases the assembly names <c>Ashlar.Tests.Infrastructure</c>
/// and <c>Ashlar.Tests.CLI</c>, this repository's own test assemblies.</para>
///
/// <para>So nothing outside this repository could have used it successfully, and it is reachable
/// through <c>InternalsVisibleTo</c> by the four test assemblies that actually drive it.
/// PackedSurfaceCompileOnlyDependencyTests keeps the next one of these from shipping.</para>
/// </summary>
internal static class UnitTestFrameworkBridge
{
    /// <summary>
    /// Discovers concrete <see cref="UnitTestBase"/> types in <paramref name="assembly"/> (public only; skips nested private types).
    /// </summary>
    public static IReadOnlyList<Type> DiscoverUnitTestTypesFromAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        var types = assembly
            .GetTypes()
            .Where(t =>
                t is { IsClass: true, IsAbstract: false } &&
                t.IsSubclassOf(typeof(UnitTestBase)) &&
                (!t.IsNested || t.IsNestedPublic))
            .AsEnumerable();

        if (string.Equals(assembly.GetName().Name, "Ashlar.Tests.Infrastructure", StringComparison.Ordinal))
        {
            types = types.Where(t =>
                !string.Equals(t.Name, "SimpleTestForRunner", StringComparison.Ordinal) &&
                !string.Equals(t.Name, "DependencyWrappingArchitectureTests", StringComparison.Ordinal));
        }

        if (string.Equals(assembly.GetName().Name, "Ashlar.Tests.CLI", StringComparison.Ordinal))
        {
            types = types.Where(t => !string.Equals(t.Name, "DoctorCommandTests", StringComparison.Ordinal));
        }

        return types.OrderBy(t => t.FullName, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Executes one <see cref="UnitTestBase"/> type using the same path as the CLI-hosted <see cref="ITestRunner"/>.
    /// </summary>
    public static async Task ExecuteUnitTestAsync(Type testType, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(testType);
        if (!testType.IsSubclassOf(typeof(UnitTestBase)) || testType.IsAbstract)
        {
            throw new ArgumentException($"Type must be a concrete subclass of {nameof(UnitTestBase)}.", nameof(testType));
        }

        PrepareUnitTestEnvironment(testType);

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton<ITestRunner, TestRunnerAdapter>();
        await using var provider = services.BuildServiceProvider();
        var runner = provider.GetRequiredService<ITestRunner>();

        var filter = testType.Name;
        var execution = await runner.RunTestsAsync(filter, cancellationToken: cancellationToken);

        var match = execution.Results.FirstOrDefault(r =>
            string.Equals(r.Name, testType.Name, StringComparison.Ordinal));

        if (match is null)
        {
            var ran = string.Join(", ", execution.Results.Select(r => r.Name));
            Assert.Fail(
                $"No result for '{testType.Name}'. TotalTests={execution.TotalTests}. Ran: [{ran}]");
        }

        AssertPassed(match);
    }

    private static void AssertPassed(Ashlar.Core.Application.Common.Models.TestResult match)
    {
        if (match.Passed)
        {
            return;
        }

        var detail = string.IsNullOrEmpty(match.ErrorMessage)
            ? match.Message
            : match.ErrorMessage;
        var trace = string.IsNullOrEmpty(match.StackTrace) ? "" : Environment.NewLine + match.StackTrace;
        Assert.Fail($"Framework test '{match.Name}' failed: {detail}{trace}");
    }

    private static void PrepareUnitTestEnvironment(Type testType)
    {
        if (!string.Equals(testType.Name, "UiDomainKnowledgeRetentionTests", StringComparison.Ordinal))
            return;

        var repoRoot = FindRepoRootFromTestContext();
        if (repoRoot is not null)
            MockScaffoldingResponder.EnsureUiDomainDemoBaseline(repoRoot);
    }

    private static string? FindRepoRootFromTestContext()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src")) &&
                Directory.Exists(Path.Combine(dir.FullName, "docs")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return null;
    }
}
