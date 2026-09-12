using FluentAssertions;
using Ashlar.Core.Application.Pipelines.Models;
using Ashlar.Core.Application.Pipelines.Ports;
using Ashlar.Infrastructure.Pipelines;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>Behavioral controls for the exclusive destination policy used by the orchestrator.</summary>
[Trait("Category", "Certification")]
public sealed class PipelineRunPersistenceCertificationTests
{
    private static readonly DateTimeOffset Started = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(PipelineRunState.Pending)]
    [InlineData(PipelineRunState.Running)]
    [InlineData(PipelineRunState.Completed)]
    [InlineData(PipelineRunState.Failed)]
    public void An_existing_destination_cannot_be_claimed(PipelineRunState state)
    {
        var stored = Run() with { State = state };
        var competing = Run() with { TemplateId = "different-template", StartedAt = Started.AddHours(1) };
        var act = () => PipelineRunPersistence.Create(stored, competing);
        act.Should().Throw<InvalidOperationException>().WithMessage("*already exists*fresh --run-id*");
    }

    [Fact]
    public void A_fresh_destination_is_created_and_can_finish()
    {
        var created = PipelineRunPersistence.Create(null, Run());
        var finished = created with
        {
            State = PipelineRunState.Completed, CompletedAt = Started.AddMinutes(1),
            StageRuns = [new PipelineStageRun { StageId = "a", State = PipelineStageRunState.Completed, Output = "actual-output", Attempt = 2, WorkerId = "owner" }],
        };
        PipelineRunPersistence.Advance(created, finished).Should().BeEquivalentTo(finished);
    }

    [Fact]
    public void A_failed_execution_keeps_its_error_and_failed_state()
    {
        var created = PipelineRunPersistence.Create(null, Run());
        var failed = created with
        {
            State = PipelineRunState.Failed, CompletedAt = Started.AddMinutes(1),
            StageRuns = [new PipelineStageRun { StageId = "a", State = PipelineStageRunState.Failed, Error = "actual-error", Attempt = 2, WorkerId = "owner" }],
        };
        PipelineRunPersistence.Advance(created, failed).Should().BeEquivalentTo(failed);
    }

    [Fact]
    public void An_advance_cannot_recreate_a_missing_record()
    {
        var act = () => PipelineRunPersistence.Advance(null, Run());
        act.Should().Throw<InvalidOperationException>().WithMessage("*no longer matches*");
    }

    [Theory]
    [InlineData("run")]
    [InlineData("template")]
    [InlineData("start")]
    public void An_advance_cannot_change_execution_identity(string field)
    {
        var stored = Run();
        var next = field switch
        {
            "run" => stored with { RunId = "elsewhere" },
            "template" => stored with { TemplateId = "elsewhere" },
            _ => stored with { StartedAt = Started.AddMinutes(1) },
        };
        var act = () => PipelineRunPersistence.Advance(stored, next);
        act.Should().Throw<InvalidOperationException>().WithMessage("*no longer matches*");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Both_stores_preserve_the_owner_when_creation_is_refused(bool durable)
    {
        var path = Path.Combine(Path.GetTempPath(), $"pipeline-create-{Guid.NewGuid():N}.db");
        try
        {
            IPipelineRunStore store = durable ? new LiteDbPipelineRunStore(path) : new InMemoryPipelineRunStore();
            var initial = Run();
            await store.MergeAsync(initial.RunId, stored => PipelineRunPersistence.Create(stored, initial));
            IPipelineRunStore second = durable ? new LiteDbPipelineRunStore(path) : store;
            var competitor = initial with { TemplateId = "other", StartedAt = Started.AddHours(1) };
            var act = () => second.MergeAsync(initial.RunId, stored => PipelineRunPersistence.Create(stored, competitor));
            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*already exists*");
            (await second.GetAsync(initial.RunId))!.Should().BeEquivalentTo(initial);
            var finished = initial with { State = PipelineRunState.Completed, CompletedAt = Started.AddMinutes(1) };
            await store.MergeAsync(initial.RunId, stored => PipelineRunPersistence.Advance(stored, finished));
            (await second.GetAsync(initial.RunId))!.Should().BeEquivalentTo(finished);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task The_durable_store_returns_the_persisted_identity_before_advance()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pipeline-precision-{Guid.NewGuid():N}.db");
        try
        {
            var store = new LiteDbPipelineRunStore(path);
            var initial = Run() with { StartedAt = Started.AddTicks(1234) };
            var created = await store.MergeAsync(initial.RunId, stored => PipelineRunPersistence.Create(stored, initial));
            var reread = await new LiteDbPipelineRunStore(path).GetAsync(initial.RunId);
            created.Should().BeEquivalentTo(reread, "the returned creation identity must survive the store's timestamp normalization");
            var finished = created with { State = PipelineRunState.Completed, CompletedAt = Started.AddMinutes(1).AddTicks(5678) };
            var advanced = await store.MergeAsync(initial.RunId, stored => PipelineRunPersistence.Advance(stored, finished));
            advanced.Should().BeEquivalentTo(await store.GetAsync(initial.RunId));
            advanced.State.Should().Be(PipelineRunState.Completed);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Invalid_transforms_do_not_change_either_store(bool durable)
    {
        var path = Path.Combine(Path.GetTempPath(), $"pipeline-invalid-{Guid.NewGuid():N}.db");
        try
        {
            IPipelineRunStore store = durable ? new LiteDbPipelineRunStore(path) : new InMemoryPipelineRunStore();
            var initial = Run();
            await store.MergeAsync(initial.RunId, stored => PipelineRunPersistence.Create(stored, initial));
            var nullResult = () => store.MergeAsync(initial.RunId, _ => null!);
            await nullResult.Should().ThrowAsync<InvalidOperationException>().WithMessage("*no decline outcome*");
            var moved = () => store.MergeAsync(initial.RunId, stored => stored! with { RunId = "elsewhere" });
            await moved.Should().ThrowAsync<InvalidOperationException>().WithMessage("*may not change RunId*");
            (await store.GetAsync(initial.RunId))!.Should().BeEquivalentTo(initial);
            (await store.GetAsync("elsewhere")).Should().BeNull();
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static PipelineRun Run() => new()
    {
        RunId = "run", TemplateId = "template", StartedAt = Started, State = PipelineRunState.Running,
        StageRuns = [new PipelineStageRun { StageId = "a", State = PipelineStageRunState.Pending }],
    };
}
