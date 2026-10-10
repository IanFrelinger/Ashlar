using Ashlar.Abstractions;
using Ashlar.Abstractions.Security;
using Ashlar.BackgroundAgents.Agents;
using Ashlar.Runtime;
using FluentAssertions;
using Moq;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

[Trait("Category", "Certification")]
public sealed class ResponseReadCycleTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Model_and_tool_read_refusals_stop_without_another_model_turn(bool modelRefuses, bool auditFault)
    {
        var model = new RefusingModel(modelRefuses);
        var tools = new CapabilityRegistry();
        tools.Register(new RefusingTool());
        var events = new List<EventRecord>();
        var memory = new Mock<IAgentMemory>();
        memory.Setup(m => m.Write(It.IsAny<EventRecord>())).Callback<EventRecord>(e =>
        {
            events.Add(e);
            if (auditFault && e.EventType == "read.refused") throw new IOException("audit-private-canary");
        });
        var cycle = await new ToolCallingAgent("read-test", model).RunCycleAsync(
            WorldSnapshot.ForRepo("/repo"), tools, new PolicyEngine([]), null, memory.Object, default);
        cycle.StoppedReason.Should().Be("read_refused");
        cycle.ReadRefusals.Should().Be(1);
        cycle.EgressRefusals.Should().Be(0);
        cycle.ToolCallsExecuted.Should().Be(0);
        cycle.MergedDelta.Should().BeNull();
        model.Calls.Should().Be(1);
        events.Should().ContainSingle(e => e.EventType == "read.refused");
        events.Should().NotContain(e => e.EventType == "react.error");
    }

    private sealed class RefusingModel(bool refuses) : IModel
    {
        public int Calls { get; private set; }
        public Task<ModelOutput> CompleteAsync(ModelInput input, CancellationToken ct)
        {
            Calls++;
            if (refuses) throw new ReadRefusedException(AccessDenialReason.SystemHighData);
            return Task.FromResult(new ModelOutput("""{"tool_calls":[{"id":"peer","arguments":{}}]}"""));
        }
    }

    private sealed class RefusingTool : ITool
    {
        public string Id => "peer";
        public ToolSchema Schema => new(Id, Id, """{"type":"object"}""");
        public Task<ToolResult> InvokeAsync(ToolCall toolCall, WorldSnapshot s, CancellationToken ct) =>
            throw new ReadRefusedException(AccessDenialReason.SystemHighData);
    }
}
