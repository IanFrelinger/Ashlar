using Xunit;

namespace Ashlar.Tests.Infrastructure.Helpers;

/// <summary>
/// The one collection for every xUnit test class that touches
/// <c>Ashlar.Infrastructure.Testing.CompatibilityTestHooks</c>.
///
/// <para>The hooks (<c>TypeResolver</c>, <c>ReflectionProbe</c>, <c>PlatformNameProvider</c>) are
/// process-global static properties, and the shipped checkers consult them on every call:
/// <c>PlatformCompatibilityChecker.CheckCompatibility()</c> and
/// <c>AgentPlatformCompatibilityChecker.CheckCompatibility()</c> route their dependency probes
/// through <c>CompatibilityTestHooks.ResolveType</c> / <c>ProbeReflection</c>. A class that
/// installs a stub hook therefore changes what every concurrently running test sees, even though
/// it resets the hooks in its own constructor and <c>Dispose</c>.</para>
///
/// <para>Members: <c>PlatformCompatibilityCheckerGapCoverageTests</c> (the writer — it installs
/// null-returning and throwing resolvers) and <c>InfrastructureTestingGapCoverageTests</c> (the
/// reader — <c>CodeAnalysisPlatformCompatibilityChecker_returns_platform_result</c> asserts
/// <c>IsCompatible</c> is true). This assembly runs collections in parallel with
/// <c>maxParallelThreads: 2</c> (xunit.runner.json), so on Windows CI the reader could run
/// against the writer's stub and see <c>IsCompatible == false</c> — the same shape of flake as
/// the three fixed in #578.</para>
///
/// <para>xUnit runs collections marked <c>DisableParallelization</c> one at a time, apart from
/// the parallel batch, so members of this collection never overlap with anything else.</para>
/// </summary>
[CollectionDefinition("CompatibilityTestHooks", DisableParallelization = true)]
public sealed class CompatibilityTestHooksCollection;
