using FluentAssertions;
using Ashlar.Core.Application.Pipelines.Models;
using Ashlar.Infrastructure.Pipelines;
using Ashlar.Tests.Infrastructure.Helpers;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Pipelines;

/// <summary>Tests for lite db pipeline run store.</summary>
public sealed class LiteDbPipelineRunStoreTests
{
    [Fact]
    public async Task MergeAsync_ThenGetAsync_PersistsRunDurably()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ashlar-pipeline-store-{Guid.NewGuid():N}.db");
        try
        {
            var sut = new LiteDbPipelineRunStore(path);
            var run = new PipelineRun
            {
                RunId = "durable-run-1",
                TemplateId = "template-durable",
                State = PipelineRunState.Completed,
                CompletedAt = DateTimeOffset.UtcNow,
                StageRuns = new[]
                {
                    new PipelineStageRun
                    {
                        StageId = "stage-a",
                        State = PipelineStageRunState.Completed,
                        Attempt = 1,
                        WorkerType = PipelineWorkerType.Deterministic,
                        WorkerId = "deterministic-default",
                        Output = "ok"
                    }
                }
            };

            await sut.PutAsync(run);

            var sut2 = new LiteDbPipelineRunStore(path);
            var loaded = await sut2.GetAsync("durable-run-1");

            loaded.Should().NotBeNull();
            loaded!.TemplateId.Should().Be("template-durable");
            loaded.State.Should().Be(PipelineRunState.Completed);
            loaded.StageRuns.Should().ContainSingle(s => s.StageId == "stage-a" && s.Output == "ok");
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
