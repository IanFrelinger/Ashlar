using FluentAssertions;
using Ashlar.Core.Application.Pipelines.Models;
using Ashlar.Infrastructure.Pipelines;
using Ashlar.Tests.Infrastructure.Helpers;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Pipelines;

/// <summary>Tests for in memory pipeline run store.</summary>
public sealed class InMemoryPipelineRunStoreTests
{
    [Fact]
    public async Task MergeAsync_ThenGetAsync_ReturnsSavedRun()
    {
        var sut = new InMemoryPipelineRunStore();
        var run = new PipelineRun
        {
            RunId = "run-1",
            TemplateId = "tpl-1",
            State = PipelineRunState.Running,
            StageRuns = new[]
            {
                new PipelineStageRun
                {
                    StageId = "stage-1",
                    State = PipelineStageRunState.Completed,
                    Attempt = 1,
                    WorkerType = PipelineWorkerType.Deterministic
                }
            }
        };

        await sut.PutAsync(run);
        var stored = await sut.GetAsync("run-1");

        stored.Should().NotBeNull();
        stored!.TemplateId.Should().Be("tpl-1");
        stored.StageRuns.Should().ContainSingle(s => s.StageId == "stage-1");
    }

    [Fact]
    public async Task GetAsync_WhenMissingRun_ReturnsNull()
    {
        var sut = new InMemoryPipelineRunStore();

        var stored = await sut.GetAsync("missing");

        stored.Should().BeNull();
    }

    [Fact]
    public async Task MergeAsync_WithCancelledToken_Throws()
    {
        var sut = new InMemoryPipelineRunStore();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await sut.PutAsync(new PipelineRun { RunId = "run-x", TemplateId = "tpl-x" }, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
