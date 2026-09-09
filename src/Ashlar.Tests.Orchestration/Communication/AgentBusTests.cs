using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Ashlar.Orchestration.Communication;
using Ashlar.Orchestration.Communication.Models;
using Xunit;

namespace Ashlar.Tests.Orchestration.Communication;

/// <summary>
/// Tests for agent bus.
///
/// AgentBus.PublishAsync does not invoke subscribers inline: it schedules each matching
/// handler via a fire-and-forget Task.Run and returns Task.CompletedTask immediately.
/// Handlers therefore run on a thread-pool thread at some later point, so these tests
/// synchronize on an explicit signal completed by the handler rather than sleeping.
/// </summary>
public class AgentBusTests
{
    /// <summary>
    /// Upper bound for waiting on a delivery that is expected to happen. A genuine bug
    /// (handler never invoked) fails fast with a TimeoutException instead of hanging.
    /// </summary>
    private static readonly TimeSpan DeliveryTimeout = TimeSpan.FromSeconds(10);

    private readonly Mock<ILogger<AgentBus>> _loggerMock;
    private readonly AgentBus _bus;

    public AgentBusTests()
    {
        _loggerMock = new Mock<ILogger<AgentBus>>();
        _bus = new AgentBus(_loggerMock.Object);
    }

    [Fact]
    public async Task PublishAsync_MessagePublished_SubscribersReceiveIt()
    {
        // Arrange
        var messageReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var message = new OutputEmitted
        {
            MessageId = "msg-1",
            FromAgentId = "agent-1",
            MessageType = "OutputEmitted",
            Output = new { Result = "test" }
        };

        // Act
        await _bus.SubscribeAsync("OutputEmitted", (msg, ct) =>
        {
            messageReceived.TrySetResult(true);
            return Task.CompletedTask;
        });

        await _bus.PublishAsync(message);

        // Assert
        // The handler is dispatched on the thread pool after PublishAsync returns, so wait
        // for the handler's own signal instead of sleeping.
        var received = await messageReceived.Task.WaitAsync(DeliveryTimeout);
        received.Should().BeTrue();
    }

    [Fact]
    public async Task SubscribeAsync_WithAgentIdFilter_OnlyReceivesMatchingMessages()
    {
        // Arrange
        // ConcurrentQueue: the handler runs on a thread-pool thread while the test thread reads.
        var receivedMessages = new ConcurrentQueue<AgentMessage>();
        var firstDelivery = new TaskCompletionSource<AgentMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var message1 = new OutputEmitted
        {
            MessageId = "msg-1",
            FromAgentId = "agent-1",
            ToAgentId = "agent-2",
            MessageType = "OutputEmitted",
            Output = new { Result = "test" }
        };

        var message2 = new OutputEmitted
        {
            MessageId = "msg-2",
            FromAgentId = "agent-3",
            ToAgentId = "agent-4",
            MessageType = "OutputEmitted",
            Output = new { Result = "test2" }
        };

        // Act
        await _bus.SubscribeAsync("OutputEmitted", (msg, ct) =>
        {
            receivedMessages.Enqueue(msg);
            firstDelivery.TrySetResult(msg);
            return Task.CompletedTask;
        }, "agent-2");

        await _bus.PublishAsync(message1);
        await _bus.PublishAsync(message2);

        // Assert
        // Positive half: wait for the handler to signal that the matching message arrived.
        var delivered = await firstDelivery.Task.WaitAsync(DeliveryTimeout);
        delivered.MessageId.Should().Be("msg-1");

        // Negative half: msg-2 must NOT be delivered. There is no signal for "nothing happened",
        // so allow a short grace after the positive signal. This grace can only produce a false
        // PASS (if the bus were both broken and slow enough to deliver msg-2 after 50 ms); it can
        // never produce a false FAIL, because a correct bus never enqueues msg-2 at all.
        await Task.Delay(50);
        receivedMessages.Should().HaveCount(1);
        receivedMessages.Single().MessageId.Should().Be("msg-1");
    }

    [Fact]
    public async Task GetMessagesForAgentAsync_ReturnsMessagesForAgent()
    {
        // Arrange
        var message = new OutputEmitted
        {
            MessageId = "msg-1",
            FromAgentId = "agent-1",
            ToAgentId = "agent-2",
            MessageType = "OutputEmitted",
            Output = new { Result = "test" }
        };

        await _bus.PublishAsync(message);

        // Act
        var messages = await _bus.GetMessagesForAgentAsync("agent-2");

        // Assert
        messages.Should().Contain(m => m.MessageId == "msg-1");
    }
}
