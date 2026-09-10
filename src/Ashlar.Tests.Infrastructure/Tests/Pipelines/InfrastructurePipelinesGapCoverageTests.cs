using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Ashlar.Core.Application.Pipelines.Models;
using Ashlar.Infrastructure.Pipelines;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Pipelines;

/// <summary>Tests for infrastructure pipelines gap coverage.</summary>
[Collection("EnvironmentVariables")]
public class InfrastructurePipelinesGapCoverageTests
{
    [Fact]
    public async Task DefaultAgenticStageExecutionAdapter_executes_stage()
    {
        // CI may set ASHLAR_ALLOW_MOCK=1 for perf gates; clear it to test the placeholder path.
        var prevMock = Environment.GetEnvironmentVariable("ASHLAR_ALLOW_MOCK");
        try
        {
            Environment.SetEnvironmentVariable("ASHLAR_ALLOW_MOCK", null);

            var adapter = new DefaultAgenticStageExecutionAdapter(
                NullLogger<DefaultAgenticStageExecutionAdapter>.Instance);

            adapter.AdapterKey.Should().Be("default");
            adapter.WorkerType.Should().Be(PipelineWorkerType.Agentic);

            // The default adapter is a placeholder that performs no work. It must report FAILURE,
            // not fabricated success — otherwise `ashlar pipeline run` claims stages ran when they
            // did not. (Was: Succeeded=true, Output "...:ok".)
            var result = await adapter.ExecuteAsync(SampleRequest("stage-a"), CancellationToken.None);
            result.Succeeded.Should().BeFalse();
            result.WorkerId.Should().Be("agentic-default");
            result.Output.Should().Contain("agentic:stage-a:no-op");
            result.Error.Should().Contain("No agentic pipeline adapter is configured");
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASHLAR_ALLOW_MOCK", prevMock);
        }
    }

    [Fact]
    public async Task DefaultDeterministicStageExecutionAdapter_executes_stage()
    {
        // CI may set ASHLAR_ALLOW_MOCK=1 for perf gates; clear it to test the placeholder path.
        var prevMock = Environment.GetEnvironmentVariable("ASHLAR_ALLOW_MOCK");
        try
        {
            Environment.SetEnvironmentVariable("ASHLAR_ALLOW_MOCK", null);

            var adapter = new DefaultDeterministicStageExecutionAdapter(
                NullLogger<DefaultDeterministicStageExecutionAdapter>.Instance);

            adapter.WorkerType.Should().Be(PipelineWorkerType.Deterministic);

            // Placeholder → must fail, not fabricate success. (Was: Succeeded=true, "...:ok".)
            var result = await adapter.ExecuteAsync(SampleRequest("stage-d"), CancellationToken.None);
            result.Succeeded.Should().BeFalse();
            result.Output.Should().Contain("deterministic:stage-d:no-op");
            result.Error.Should().Contain("No deterministic pipeline adapter is configured");
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASHLAR_ALLOW_MOCK", prevMock);
        }
    }

    [Fact]
    public async Task Pipeline_adapters_reject_null_request_and_honor_cancellation()
    {
        var agentic = new DefaultAgenticStageExecutionAdapter(
            NullLogger<DefaultAgenticStageExecutionAdapter>.Instance);
        var deterministic = new DefaultDeterministicStageExecutionAdapter(
            NullLogger<DefaultDeterministicStageExecutionAdapter>.Instance);

        var act = () => agentic.ExecuteAsync(null!, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentNullException>();

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var act2 = () => deterministic.ExecuteAsync(SampleRequest("x"), cts.Token);
        await act2.Should().ThrowAsync<OperationCanceledException>();
    }

    private static PipelineStageExecutionRequest SampleRequest(string stageId)
    {
        var stage = new PipelineStageDefinition { Id = stageId, Name = stageId };
        var node = new PipelineExecutionNode { StageId = stageId, Mode = PipelineExecutionMode.Deterministic };
        return new PipelineStageExecutionRequest
        {
            RunId = "run-1",
            StageId = stageId,
            Attempt = 1,
            WorkerType = PipelineWorkerType.Deterministic,
            Stage = stage,
            Node = node,
        };
    }
}
