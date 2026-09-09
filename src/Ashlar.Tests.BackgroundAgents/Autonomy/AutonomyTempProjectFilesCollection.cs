using Xunit;

namespace Ashlar.Tests.BackgroundAgents.Autonomy;

/// <summary>
/// Serialises every test class that drives <c>AutonomyLoopService.SweepAsync</c>. Each sweep
/// iteration writes a compile-time <c>ashlar-objective-*.csproj</c> to the process-wide temp
/// directory and deletes it on the way out, and <c>Sweep_DoesNotLeakTheTemporaryProjectFile</c>
/// asserts on that directory's contents before and after. Two sweeps in flight at once make that
/// snapshot race: a sibling's project file can be present at "before" and gone at "after"
/// (seen on windows-latest, Full Platform Readiness Gate run 34364912942). One collection, no
/// concurrent writers — the assertion stays exact instead of being loosened to a set difference.
/// Other collections still run alongside; only these classes are sequenced with each other.
/// </summary>
[CollectionDefinition(Name)]
public sealed class AutonomyTempProjectFilesCollection
{
    /// <summary>Collection name shared by the sweep-driving test classes.</summary>
    public const string Name = "AutonomyTempProjectFiles";
}
