using Xunit;

namespace Ashlar.Tests.Application.Tests.Generation;

/// <summary>
/// Runs every test class that constructs <see cref="Ashlar.Agents.TestKit.OfflineAgentEnvironment"/>
/// on its own, with nothing else in flight. The kit sets and restores PROCESS-WIDE environment
/// variables (<c>SKIP_OLLAMA</c>, <c>COMPILE_GATE</c>, <c>WORKSPACE_ROOT</c>), and
/// <c>Offline_environment_sets_the_knobs_and_restores_them</c> asserts on them while a sibling's
/// <c>Dispose</c> can put the previous value back — observed on windows-latest (Full Platform
/// Readiness Gate run 34374416444: <c>SKIP_OLLAMA</c> read as "1" on one line and
/// <c>IsOffline</c> false two lines later). The same race runs the other way: an inherited
/// conformance case can lose its offline knob mid-run and reach for a real model.
/// <c>DisableParallelization</c> (the shape of <c>Trust/DataTaxonomyGapCollection</c> in the
/// background-agents suite) keeps this collection from overlapping ANY other collection, which is
/// what process-global state needs; the sweep-temp-dir collection next door only needs its members
/// sequenced with each other.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class OfflineAgentEnvironmentCollection
{
    /// <summary>Collection name shared by the classes that construct the offline kit.</summary>
    public const string Name = "OfflineAgentEnvironment";
}
