using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Ashlar.Core.Application.Pipelines.Models;
using Ashlar.Infrastructure.Pipelines;
using Ashlar.Infrastructure.Pipelines.Sdk.Extensions;
using Ashlar.Tests.Infrastructure.Helpers;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.VirtualProduction;

/// <summary>
/// Prod-style coverage for the pipeline default-adapter fix, through the real registration.
///
/// <para>The unit tests in <c>InfrastructurePipelinesGapCoverageTests</c> construct the
/// adapters directly. This one resolves them from the same composition production uses —
/// <c>AddPipelineCompositionLayer()</c>, the call the kernel registrar makes when the pipeline
/// module is enabled — and asserts that the DEFAULT (unconfigured) deterministic adapter that
/// the shipped <c>ashlar pipeline run</c> path resolves reports FAILURE, not the fabricated
/// success it used to. A stage that did no work must not be reported as having run.</para>
/// </summary>
[Trait("Category", "ProdStyle")]
[Trait("Category", "E2E")]
[Collection("EnvironmentVariables")]
public sealed class PipelineDefaultAdapterProdStyleTests
{
    [Fact(Timeout = TestTimeouts.E2E)]
    public async Task Default_deterministic_adapter_from_real_composition_reports_failure_not_fabricated_success()
    {
        // The CI Windows lane exports ASHLAR_ALLOW_MOCK=1, and the default adapter treats that as
        // permission to report a stage it never ran as succeeded. Clear it for this test so the
        // unconfigured-adapter contract is what is under assertion. (Parallel writers of the same
        // variable are handled by the EnvironmentVariables collection on this class.)
        using var allowMock = EnvironmentVariableScope.Unset("ASHLAR_ALLOW_MOCK");

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPipelineCompositionLayer();
        using var provider = services.BuildServiceProvider(validateScopes: true);

        // The default deterministic adapter the shipped pipeline path falls back to.
        var adapter = provider.GetServices<IPipelineStageExecutionAdapter>()
            .Single(a => a.AdapterKey == "default" && a.WorkerType == PipelineWorkerType.Deterministic);

        var request = new PipelineStageExecutionRequest
        {
            RunId = "run-1",
            StageId = "stage-1",
            Attempt = 1,
            WorkerType = PipelineWorkerType.Deterministic,
            Stage = new PipelineStageDefinition { Id = "stage-1", Name = "stage-1" },
            Node = new PipelineExecutionNode { StageId = "stage-1", Mode = PipelineExecutionMode.Deterministic },
        };

        var result = await adapter.ExecuteAsync(request, CancellationToken.None);

        result.Succeeded.Should().BeFalse(
            "an unconfigured default adapter must not report success for a stage it did not execute");
        result.Error.Should().Contain("No deterministic pipeline adapter is configured");
    }
}
