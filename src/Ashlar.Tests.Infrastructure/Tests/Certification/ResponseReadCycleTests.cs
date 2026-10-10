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
    public async Task Single_turn_preserves_read_refusal_even_when_audit_fails(bool wrapped, bool auditFault)
    {
        var model = new RefusingModel(true, wrapped);
        var tools = new CapabilityRegistry();
        tools.Register(new RefusingTool(wrapped));
        var events = new List<EventRecord>();
        var memory = new Mock<IAgentMemory>();
        memory.Setup(m => m.Write(It.IsAny<EventRecord>())).Callback<EventRecord>(e =>
        {
            events.Add(e);
            if (auditFault) throw new IOException("audit-private-canary");
        });
        var error = await Record.ExceptionAsync(() => new ToolCallingAgent("read-test", model).ThinkAsync(
            new AgentObservation(WorldSnapshot.ForRepo("/repo")), tools, memory.Object, default));
        error.Should().BeOfType<ReadRefusedException>();
        error!.ToString().Should().NotContain("private-canary");
        events.Should().ContainSingle(e => e.EventType == "read.refused");
        model.Calls.Should().Be(1);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public async Task Model_and_tool_read_refusals_stop_without_another_model_turn(bool modelRefuses, bool auditFault, bool wrapped)
    {
        var model = new RefusingModel(modelRefuses, wrapped);
        var tools = new CapabilityRegistry();
        tools.Register(new RefusingTool(wrapped));
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
        events.Where(e => e.EventType == "read.refused").Should().OnlyContain(e => !e.Message.Contains("private-canary", StringComparison.Ordinal));
    }

    private static Exception Refusal(bool wrapped)
    {
        var refusal = new ReadRefusedException(AccessDenialReason.SystemHighData);
        return wrapped ? new AggregateException("wrapper-private-canary",
            new IOException("unrelated-private-canary"), new HttpRequestException("nested-private-canary", refusal)) : refusal;
    }

    private sealed class RefusingModel(bool refuses, bool wrapped) : IModel
    {
        public int Calls { get; private set; }
        public Task<ModelOutput> CompleteAsync(ModelInput input, CancellationToken ct)
        {
            Calls++;
            if (refuses) throw Refusal(wrapped);
            return Task.FromResult(new ModelOutput("""{"tool_calls":[{"id":"peer","arguments":{}}]}"""));
        }
    }

    private sealed class RefusingTool(bool wrapped) : ITool
    {
        public string Id => "peer";
        public ToolSchema Schema => new(Id, Id, """{"type":"object"}""");
        public Task<ToolResult> InvokeAsync(ToolCall toolCall, WorldSnapshot s, CancellationToken ct) =>
            throw Refusal(wrapped);
    }
}
