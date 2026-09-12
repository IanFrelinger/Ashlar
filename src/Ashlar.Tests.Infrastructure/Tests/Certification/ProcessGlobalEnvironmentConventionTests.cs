using FluentAssertions;
using Ashlar.Core.Application.Paths;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>
/// A test that writes an environment variable must be serialized against everything else.
///
/// <para><b>Why this blocks a merge.</b> An environment variable belongs to the PROCESS, not to
/// the test. Restoring it in a <c>finally</c>, or in an <c>IDisposable</c> scope, does nothing
/// about a class running concurrently: the window between the write and the restore is enough, and
/// under xUnit's defaults (one collection per class, all collections in parallel) there is always
/// something in that window. The only fix is a collection with
/// <c>DisableParallelization = true</c>, or not writing the variable at all.</para>
///
/// <para><b>This has now been the same bug three times.</b> #578 found it in the offline-agent
/// tests. #581 swept three more projects and wrote
/// <c>GrpcTransportEnvironmentCollection</c>, whose summary still reads "parallel runs caused
/// AllowInsecure validation flakes in CI". Then <c>EndpointHealthMonitorTests</c> copy-pasted the
/// restoring scope out of those very files and left the <c>[Collection]</c> behind — it took the
/// half that does not fix the flake — and held <c>DOTNET_ENVIRONMENT=Development</c> across a
/// 2.5 second wait while 50-odd other classes ran. Each previous pass fixed the instances it
/// found and left nothing behind to stop the next one arriving. This is that thing.</para>
///
/// <para>The inventory below is a frozen list of known offenders, not an approval. Both
/// directions fail: a NEW env-mutating test class outside a collection fails
/// <see cref="No_unlisted_test_file_mutates_the_environment_unserialized"/>, and a row that has
/// since been fixed fails <see cref="No_allowlisted_file_still_needs_its_row"/>, so the list can
/// only shrink honestly.</para>
///
/// <para><b>What this cannot see.</b> It is a text scan. It cannot tell that a
/// <c>[Collection]</c> actually covers the right window, and it deliberately does not model xUnit
/// semantics. <see cref="Only_known_collection_definitions_run_in_parallel"/> closes the one
/// loophole that would otherwise make the marker meaningless — joining a collection that does not
/// serialize — by freezing the three definitions that do not set
/// <c>DisableParallelization</c>.</para>
///
/// <para>Hermetic: pure file reads, no build, no network, no SDK, and the same structural pruning
/// as <see cref="TestOwnershipConventionTests"/>, because a nested git worktree holds a second
/// copy of every file here and counting those turned the only required check on master red on
/// developers' machines while CI stayed green.</para>
/// </summary>
[Trait("Category", "Certification")]
public sealed class ProcessGlobalEnvironmentConventionTests
{
    /// <summary>A project is a test project if it pulls in the test SDK. Nothing else is reliable.</summary>
    private const string TestSdkMarker = "Microsoft.NET.Test.Sdk";

    /// <summary>
    /// Built by concatenation so this file does not match its own scan. The alternative — an
    /// explicit self-exclusion — is a hole someone can widen; a marker that cannot appear in prose
    /// here is not.
    /// </summary>
    private static readonly string EnvironmentWrite = "Environment.Set" + "EnvironmentVariable";

    /// <summary>Only a file that declares tests is scheduled into a collection of its own.</summary>
    private static readonly string[] FactMarkers =
    [
        "[Fact", "[Theory", "[NotOnCiFact", "[OptInFact", "[DockerFact",
    ];

    /// <summary>Concatenated for the same reason as <see cref="EnvironmentWrite"/>.</summary>
    private static readonly string CollectionMarker = "[Coll" + "ection(";

    /// <summary>Concatenated for the same reason as <see cref="EnvironmentWrite"/>.</summary>
    private static readonly string CollectionDefinitionMarker = "[Coll" + "ectionDefinition(";

    private const string SerializingMarker = "DisableParallelization";

    /// <summary>
    /// Test files known on 2026-09-11 to write a process-global environment variable without
    /// joining a non-parallel collection, repo-root-relative. This list is expected to SHRINK.
    /// Removing a row because the class now serializes (or no longer writes the variable at all)
    /// is the point; adding one means saying so in a diff a reviewer sees.
    /// </summary>
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        "application/src/Ashlar.Tests.CLI/Tests/Commands/CertifyCommandTests.cs",
        "application/src/Ashlar.Tests.CLI/Tests/Commands/SelfExtendAutoShareEnvTests.cs",
        "products/tests/Ashlar.Tests.Products/ProductScaffoldTests.cs",
        "src/Ashlar.Mcp.Server.Tests/AshlarMcpServerServiceCollectionExtensionsTests.cs",
        "src/Ashlar.Tests.AI.Pipeline/MeaiPipelineRegistrationTests.cs",
        "src/Ashlar.Tests.AI.Pipeline/OllamaEndpointResolverTests.cs",
        "src/Ashlar.Tests.Application/ApplicationRemainingCoverageTests.cs",
        "src/Ashlar.Tests.Application/Tests/Autonomy/RecursionDisciplineTests.cs",
        "src/Ashlar.Tests.Application/Tests/Execution/ScratchAndPathPolicyTests.cs",
        "src/Ashlar.Tests.BackgroundAgents/HostRunners/ConfinedToolboxFactoryTests.cs",
        "src/Ashlar.Tests.Infrastructure/Tests/Barriers/HttpBarrierContextMiddlewareTests.cs",
        "src/Ashlar.Tests.Infrastructure/Tests/Certification/AnalyzerFenceGateTests.cs",
        "src/Ashlar.Tests.Infrastructure/Tests/Certification/AnalyzerGateAdversarialCampaignTests.cs",
        "src/Ashlar.Tests.Infrastructure/Tests/Certification/CrossProjectReuseTests.cs",
        "src/Ashlar.Tests.Infrastructure/Tests/Certification/DamageResolverDogfoodTests.cs",
        "src/Ashlar.Tests.Infrastructure/Tests/Execution/InfrastructureExecutionGapCoverageTests.cs",
        "src/Ashlar.Tests.Infrastructure/Tests/Mesh/FileBasedInstanceDiscoveryTests.cs",
        "src/Ashlar.Tests.Infrastructure/Tests/Pipelines/PipelineServiceCollectionExtensionsTests.cs",
        "src/Ashlar.Tests.Infrastructure/Tests/Policies/BuildTestBudgetGapCoverageTests.cs",
        "src/Ashlar.Tests.Infrastructure/Tests/SDK/InfrastructureSdkGapCoverageTests.cs",
        "src/Ashlar.Transport.A2A.Server.Tests/ValidateAshlarA2AServerOptionsTests.cs",
        "src/Ashlar.Transport.A2A.Tests/ValidateA2ATransportOptionsTests.cs",
    };

    /// <summary>
    /// The collection definitions that deliberately run in parallel, repo-root-relative. Joining
    /// one of these is not serialization, so a new entry here would quietly widen the loophole
    /// that <see cref="No_unlisted_test_file_mutates_the_environment_unserialized"/> depends on.
    /// All three gate on an external resource (a Docker container, a temp project tree) rather
    /// than on process-global state.
    /// </summary>
    private static readonly HashSet<string> ParallelCollectionDefinitions = new(StringComparer.Ordinal)
    {
        "src/Ashlar.Tests.BackgroundAgents/Autonomy/AutonomyTempProjectFilesCollection.cs",
        "src/Ashlar.Tests.Infrastructure/Tests/Ingress/IngressDynamoDbDockerCollection.cs",
        "src/Ashlar.Tests.Infrastructure/Tests/Mesh/MeshLabDockerCollection.cs",
    };

    [Fact]
    public void No_unlisted_test_file_mutates_the_environment_unserialized()
    {
        var root = RepoPathResolver.FindRepoRoot();

        var unlisted = UnserializedEnvironmentWriters(root)
            .Where(path => !Allowed.Contains(path))
            .ToList();

        unlisted.Should().BeEmpty(
            "an environment variable is process-global, so restoring it afterwards does nothing "
            + "about the class running beside you. Put the class in a collection whose definition "
            + "sets DisableParallelization = true (EnvironmentVariablesCollection, "
            + "GrpcTransportEnvironmentCollection and three siblings already exist), or better, "
            + "stop writing the variable -- most readers of one have a seam that takes the value "
            + "directly. Failing both, add the file to the allowlist in this class and say why in "
            + "the pull request. Unlisted: {0}",
            string.Join(", ", unlisted));
    }

    /// <summary>
    /// A stale row is its own failure: it reads as accounted-for debt that is in fact gone, which
    /// is how a shrinking inventory stops meaning anything.
    /// </summary>
    [Fact]
    public void No_allowlisted_file_still_needs_its_row()
    {
        var root = RepoPathResolver.FindRepoRoot();
        var actual = UnserializedEnvironmentWriters(root).ToHashSet(StringComparer.Ordinal);

        var stale = Allowed.Where(a => !actual.Contains(a)).OrderBy(a => a, StringComparer.Ordinal).ToList();

        stale.Should().BeEmpty(
            "these files no longer write a process-global environment variable outside a "
            + "collection, so their allowlist rows overstate the remaining debt. Delete the rows "
            + "with the fix. Stale: {0}",
            string.Join(", ", stale));
    }

    /// <summary>
    /// Closes the loophole the marker above depends on: if a class could satisfy
    /// <c>[Collection(...)]</c> by joining a collection that still runs in parallel, the first
    /// assertion would be decorative.
    /// </summary>
    [Fact]
    public void Only_known_collection_definitions_run_in_parallel()
    {
        var root = RepoPathResolver.FindRepoRoot();
        var actual = ParallelDefinitions(root).ToHashSet(StringComparer.Ordinal);

        var unlisted = actual.Where(a => !ParallelCollectionDefinitions.Contains(a))
            .OrderBy(a => a, StringComparer.Ordinal).ToList();
        var stale = ParallelCollectionDefinitions.Where(a => !actual.Contains(a))
            .OrderBy(a => a, StringComparer.Ordinal).ToList();

        unlisted.Should().BeEmpty(
            "a collection definition without DisableParallelization does not serialize anything, "
            + "so joining it is not protection against process-global state. Set "
            + "DisableParallelization = true, or list the definition here and say what external "
            + "resource it gates instead. Unlisted: {0}",
            string.Join(", ", unlisted));

        stale.Should().BeEmpty(
            "these definitions now serialize (or are gone), so their rows are stale. "
            + "Stale: {0}",
            string.Join(", ", stale));
    }

    /// <summary>
    /// Repo-root-relative paths of files that declare tests, write a process-global environment
    /// variable, and carry no <c>[Collection]</c> attribute.
    /// </summary>
    private static IEnumerable<string> UnserializedEnvironmentWriters(string root)
    {
        var found = new List<string>();

        foreach (var (path, text) in TestSources(root))
        {
            if (!text.Contains(EnvironmentWrite, StringComparison.Ordinal))
                continue;

            // A helper with no facts of its own is not scheduled into a collection, so a
            // [Collection] on it would be ignored; it runs inside whichever test called it.
            if (!FactMarkers.Any(m => text.Contains(m, StringComparison.Ordinal)))
                continue;

            if (text.Contains(CollectionMarker, StringComparison.Ordinal))
                continue;

            found.Add(path);
        }

        found.Sort(StringComparer.Ordinal);
        return found.Distinct(StringComparer.Ordinal);
    }

    /// <summary>Repo-root-relative paths of collection definitions that do NOT serialize.</summary>
    private static IEnumerable<string> ParallelDefinitions(string root)
    {
        var found = new List<string>();

        foreach (var (path, text) in TestSources(root))
        {
            var start = text.IndexOf(CollectionDefinitionMarker, StringComparison.Ordinal);
            if (start < 0)
                continue;

            var end = text.IndexOf(']', start);
            var attribute = end < 0 ? text[start..] : text[start..end];

            if (!attribute.Contains(SerializingMarker, StringComparison.Ordinal))
                found.Add(path);
        }

        found.Sort(StringComparer.Ordinal);
        return found.Distinct(StringComparer.Ordinal);
    }

    /// <summary>Every C# source file that belongs to a test project, with its text.</summary>
    private static IEnumerable<(string Path, string Text)> TestSources(string root)
    {
        foreach (var project in DiscoverTestProjects(root))
        {
            var directory = Path.GetDirectoryName(Path.Combine(root, project));
            if (directory is null || !Directory.Exists(directory))
                continue;

            foreach (var file in Sources(root, directory))
                yield return (file, File.ReadAllText(Path.Combine(root, file)));
        }
    }

    private static IEnumerable<string> Sources(string root, string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*.cs"))
            yield return Normalize(Path.GetRelativePath(root, file));

        foreach (var child in Directory.EnumerateDirectories(directory))
        {
            if (IsPruned(child))
                continue;

            foreach (var file in Sources(root, child))
                yield return file;
        }
    }

    private static List<string> DiscoverTestProjects(string root)
    {
        var found = new List<string>();
        CollectProjects(root, root, found);
        found.Sort(StringComparer.Ordinal);
        return found;
    }

    private static void CollectProjects(string root, string directory, List<string> found)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*.csproj"))
        {
            if (File.ReadAllText(file).Contains(TestSdkMarker, StringComparison.Ordinal))
                found.Add(Normalize(Path.GetRelativePath(root, file)));
        }

        foreach (var child in Directory.EnumerateDirectories(directory))
        {
            if (IsPruned(child))
                continue;

            CollectProjects(root, child, found);
        }
    }

    /// <summary>
    /// Build output, agent scratch space, and the root of any nested checkout. The
    /// nested-checkout rule is structural — <c>git worktree add</c> writes a .git FILE and a
    /// nested clone has a .git DIRECTORY — so a vendored copy this repository never names is
    /// caught too. Only ever called on directories below the repo root.
    /// </summary>
    private static bool IsPruned(string directory)
    {
        var name = Path.GetFileName(directory);

        if (string.Equals(name, "bin", StringComparison.Ordinal)
            || string.Equals(name, "obj", StringComparison.Ordinal)
            || string.Equals(name, ".claude", StringComparison.Ordinal))
        {
            return true;
        }

        var git = Path.Combine(directory, ".git");
        return File.Exists(git) || Directory.Exists(git);
    }

    private static string Normalize(string path) => path.Replace('\\', '/').Trim();
}
