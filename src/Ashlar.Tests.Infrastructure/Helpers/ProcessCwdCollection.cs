using Xunit;

namespace Ashlar.Tests.Infrastructure.Helpers;

/// <summary>
/// The one collection for every xUnit test class that calls <c>Directory.SetCurrentDirectory</c>.
///
/// <para>The working directory is process-global. This assembly runs test collections in
/// parallel, so a class that flips the cwd — even briefly, even with a <c>finally</c> that
/// restores it — changes what every concurrently running test sees when it resolves a relative
/// path (a sink that lazily creates <c>./audit</c>, an adapter that discovers <c>*.csproj</c>
/// under the cwd, a fixture loaded by relative path).</para>
///
/// <para>xUnit runs collections marked <c>DisableParallelization</c> one at a time, after the
/// parallel batch, so members of this collection never overlap with anything else. Adding a
/// class here is the whole fix; do not create a second cwd collection under another name —
/// the point is that a reviewer can grep for one attribute and see every cwd mutator.</para>
///
/// <para>One member is not itself a cwd mutator: <c>Framework.UnitTestBridgeTests</c>. It runs
/// the assembly's <c>UnitTestBase</c> suites in-process, and two of them —
/// <c>Tests.Agent.AgentExecutorAdapterTests</c> and
/// <c>Tests.Validation.ValidationServiceAdapterTests</c> — call
/// <c>Directory.SetCurrentDirectory</c> and hold the new cwd across an <c>await</c>. Those
/// suites are not xUnit classes, so a <c>[Collection]</c> on them is ignored; the attribute has
/// to go on the bridge theory that executes them.</para>
///
/// <para>Note what this does NOT fix: a test that waits a fixed interval for background I/O
/// still flakes under a starved thread pool (coverlet on a loaded runner). Wait for the
/// observable effect instead — see <c>FileBarrierAuditSinkGapCoverageTests.WaitUntilAsync</c>.</para>
/// </summary>
[CollectionDefinition("ProcessCwd", DisableParallelization = true)]
public sealed class ProcessCwdCollection;
