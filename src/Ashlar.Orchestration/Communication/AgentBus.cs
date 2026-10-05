using System.Collections.Concurrent;
using Ashlar.Abstractions.Security.Egress;
using Microsoft.Extensions.Logging;
using Ashlar.Orchestration.Communication.Models;

namespace Ashlar.Orchestration.Communication;

/// <summary>
/// In-memory pub/sub message bus for agent communication.
/// 
/// Thread-safe implementation of IAgentBus that:
/// - Stores message history (last 1000 messages)
/// - Routes messages to matching subscribers
/// - Supports message type and agent ID filtering
/// - Handles subscription lifecycle
/// - Provides message retrieval by agent
/// 
/// Uses concurrent collections for thread-safe operations in parallel execution scenarios.
/// </summary>
public sealed class AgentBus : IAgentBus
{
    private readonly ILogger<AgentBus> _logger;
    private readonly ConcurrentDictionary<string, ConcurrentBag<MessageSubscription>> _subscriptions = new();
    private readonly ConcurrentQueue<AgentMessage> _messageHistory = new();
    private readonly object _lock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="AgentBus"/> class.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    public AgentBus(ILogger<AgentBus> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Publishes a message to the bus.
    /// 
    /// Stores the message in history (last 1000 messages) and notifies all matching subscribers.
    /// Subscribers are matched by message type and optional agent ID filter.
    /// </summary>
    /// <param name="message">The message to publish.</param>
    /// <param name="cancellationToken">A token to observe while waiting for the task to complete.</param>
    /// <returns>A task that represents the asynchronous publish operation.</returns>
    /// <exception cref="ArgumentNullException">Thrown if message is null.</exception>
    public Task PublishAsync(AgentMessage message, CancellationToken cancellationToken = default)
    {
        if (message == null)
        {
            throw new ArgumentNullException(nameof(message));
        }

        _logger.LogDebug("Publishing message {MessageType} from {FromAgentId} to {ToAgentId}",
            message.MessageType, message.FromAgentId, message.ToAgentId ?? "all");

        // Store in history (limit to last 1000 messages)
        _messageHistory.Enqueue(message);
        while (_messageHistory.Count > 1000)
        {
            _messageHistory.TryDequeue(out _);
        }

        // Notify subscribers. Each handler is another component's work, so it must not be decided at the
        // publisher's mark: the dispatch tasks start detached, with no egress subject (SystemHigh), and keep it
        // after the publisher's flow is restored (SPEC-007 PR 4, D18).
        var subscriptions = GetMatchingSubscriptions(message);
        using (EgressSubject.Detach())
        {
            foreach (var subscription in subscriptions)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await subscription.Handler(message, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error handling message {MessageId} in subscription", message.MessageId);
                    }
                }, cancellationToken);
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Subscribes to messages of a specific type.
    /// 
    /// Creates a subscription that will receive messages matching the specified type and optional agent ID filter.
    /// Returns a disposable token that can be used to unsubscribe.
    /// </summary>
    /// <param name="messageType">Type of messages to subscribe to (null for all messages).</param>
    /// <param name="handler">Handler function to process messages.</param>
    /// <param name="agentId">Optional agent ID filter (only receive messages for this agent).</param>
    /// <param name="cancellationToken">A token to observe while waiting for the task to complete.</param>
    /// <returns>A disposable subscription token that can be used to unsubscribe.</returns>
    /// <exception cref="ArgumentNullException">Thrown if handler is null.</exception>
    public Task<IDisposable> SubscribeAsync(
        string? messageType,
        Func<AgentMessage, CancellationToken, Task> handler,
        string? agentId = null,
        CancellationToken cancellationToken = default)
    {
        if (handler == null)
        {
            throw new ArgumentNullException(nameof(handler));
        }

        var key = messageType ?? "*";
        var subscription = new MessageSubscription
        {
            MessageType = messageType,
            AgentId = agentId,
            Handler = handler,
            Id = Guid.NewGuid().ToString()
        };

        _subscriptions.AddOrUpdate(key, new ConcurrentBag<MessageSubscription> { subscription },
            (k, existing) =>
            {
                existing.Add(subscription);
                return existing;
            });

        _logger.LogDebug("Subscribed to {MessageType} for agent {AgentId}", messageType ?? "all", agentId ?? "all");

        return Task.FromResult<IDisposable>(new SubscriptionToken(key, subscription.Id, this));
    }

    /// <summary>
    /// Gets all messages for a specific agent from message history.
    /// 
    /// Retrieves messages where the agent is either the sender or receiver,
    /// optionally filtered by message type.
    /// </summary>
    /// <param name="agentId">The ID of the agent to get messages for.</param>
    /// <param name="messageType">Optional message type filter.</param>
    /// <param name="cancellationToken">A token to observe while waiting for the task to complete.</param>
    /// <returns>A read-only list of messages for the agent.</returns>
    public Task<IReadOnlyList<AgentMessage>> GetMessagesForAgentAsync(
        string agentId,
        string? messageType = null,
        CancellationToken cancellationToken = default)
    {
        var messages = _messageHistory
            .Where(m =>
                (m.ToAgentId == null || m.ToAgentId == agentId || m.FromAgentId == agentId) &&
                (messageType == null || m.MessageType == messageType))
            .ToList();

        return Task.FromResult<IReadOnlyList<AgentMessage>>(messages);
    }

    /// <summary>
    /// Gets all subscriptions that match a message.
    /// 
    /// Matches subscriptions by:
    /// - Message type (exact match or wildcard "*")
    /// - Agent ID filter (if specified in message)
    /// </summary>
    /// <param name="message">The message to find matching subscriptions for.</param>
    /// <returns>An enumerable of matching message subscriptions.</returns>
    private IEnumerable<MessageSubscription> GetMatchingSubscriptions(AgentMessage message)
    {
        var subscriptions = new List<MessageSubscription>();

        // Get subscriptions for specific message type
        if (_subscriptions.TryGetValue(message.MessageType, out var typeSubscriptions))
        {
            subscriptions.AddRange(typeSubscriptions);
        }

        // Get subscriptions for all messages
        if (_subscriptions.TryGetValue("*", out var allSubscriptions))
        {
            subscriptions.AddRange(allSubscriptions);
        }

        // Filter by agent ID if specified
        if (!string.IsNullOrEmpty(message.ToAgentId))
        {
            subscriptions = subscriptions
                .Where(s => s.AgentId == null || s.AgentId == message.ToAgentId)
                .ToList();
        }

        return subscriptions;
    }

    /// <summary>
    /// Unsubscribes a subscription from the bus.
    /// </summary>
    /// <param name="key">The subscription key (message type or "*").</param>
    /// <param name="subscriptionId">The unique subscription ID to unsubscribe.</param>
    internal void Unsubscribe(string key, string subscriptionId)
    {
        if (_subscriptions.TryGetValue(key, out var subscriptions))
        {
            var subscription = subscriptions.FirstOrDefault(s => s.Id == subscriptionId);
            if (subscription != null)
            {
                var newSubscriptions = new ConcurrentBag<MessageSubscription>(
                    subscriptions.Where(s => s.Id != subscriptionId));
                _subscriptions.TryUpdate(key, newSubscriptions, subscriptions);
                _logger.LogDebug("Unsubscribed {SubscriptionId} from {MessageType}", subscriptionId, key);
            }
        }
    }

    private sealed class MessageSubscription
    {
        /// <summary>Unique subscription identifier.</summary>
        public required string Id { get; init; }

        /// <summary>Subscribed message type, or null for all types.</summary>
        public string? MessageType { get; init; }

        /// <summary>Optional agent ID filter for delivery.</summary>
        public string? AgentId { get; init; }

        /// <summary>Handler invoked when a matching message arrives.</summary>
        public required Func<AgentMessage, CancellationToken, Task> Handler { get; init; }
    }

    private sealed class SubscriptionToken : IDisposable
    {
        private readonly string _key;
        private readonly string _subscriptionId;
        private readonly AgentBus _bus;
        private bool _disposed;

        public SubscriptionToken(string key, string subscriptionId, AgentBus bus)
        {
            _key = key;
            _subscriptionId = subscriptionId;
            _bus = bus;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _bus.Unsubscribe(_key, _subscriptionId);
                _disposed = true;
            }
        }
    }
}

