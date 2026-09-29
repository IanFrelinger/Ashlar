using Ashlar.Core.Application.Composition.Ports;
using Ashlar.Core.Application.ParallelTesting.Models;
using Ashlar.Core.Application.ParallelTesting.Ports;

namespace Ashlar.Infrastructure.Composition;

/// <summary>
/// Runs tests through a composed agent pipeline.
/// Phase D: Composition-driven testing (Block 7–8).
/// </summary>
internal sealed class ComposedTestRunner : IComposedTestRunner
{
    private static readonly string[] TestRunnerComponents = ["test-discovery", "test-execution", "result-aggregation"];

    private readonly ICompositionEngine _compositionEngine;
    private readonly IParameterMatrixGenerator _matrixGenerator;
    private readonly IInstanceSpawner _spawner;
    private readonly IResultCollector _collector;

    /// <summary>Initializes a new composed test runner.</summary>
    public ComposedTestRunner(
        ICompositionEngine compositionEngine,
        IParameterMatrixGenerator matrixGenerator,
        IInstanceSpawner spawner,
        IResultCollector collector)
    {
        _compositionEngine = compositionEngine ?? throw new ArgumentNullException(nameof(compositionEngine));
        _matrixGenerator = matrixGenerator ?? throw new ArgumentNullException(nameof(matrixGenerator));
        _spawner = spawner ?? throw new ArgumentNullException(nameof(spawner));
        _collector = collector ?? throw new ArgumentNullException(nameof(collector));
    }

    /// <inheritdoc />
    public async Task<AggregatedTestResult?> RunViaCompositionAsync(
        string problemDescription,
        Scenario scenario,
        int instanceCount = 1,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var composed = await _compositionEngine.ComposeAsync(
            problemDescription,
            TestRunnerComponents,
            cancellationToken).ConfigureAwait(false);

        if (composed == null)
            return null;

        var hasTestRunnerPipeline = TestRunnerComponents.All(id =>
            composed.ComponentIds.Contains(id, StringComparer.OrdinalIgnoreCase));

        if (!hasTestRunnerPipeline)
            return null;

        var paramSets = await _matrixGenerator.GenerateAsync(scenario, cancellationToken).ConfigureAwait(false);
        if (paramSets.Count == 0)
            paramSets = [new ParameterSet()];

        var instances = await _spawner.SpawnAsync(
            instanceCount,
            paramSets,
            scenario.SolutionOrProjectPath,
            cancellationToken).ConfigureAwait(false);

        var aggregated = await _collector.CollectAsync(instances, cancellationToken).ConfigureAwait(false);
        return aggregated;
    }
}
