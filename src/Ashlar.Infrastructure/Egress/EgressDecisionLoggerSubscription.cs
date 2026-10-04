using Ashlar.Abstractions.Security.Egress;
using Microsoft.Extensions.Logging;

namespace Ashlar.Infrastructure.Egress;

/// <summary>
/// Subscribes a <see cref="LoggerEgressDecisionSink"/> to <see cref="EgressDecisionLog"/> for as long as it lives.
/// </summary>
/// <remarks>
/// <para><see cref="EgressServiceCollectionExtensions.AddAshlarEgressGuard"/> registers it as a singleton, so it
/// subscribes when the container first builds it and unsubscribes when the container is disposed. The container
/// builds it when a host starts (through a hosted service) or when a factory client's handlers are first built,
/// whichever comes first.</para>
/// <para>The decision log is process-wide, so each container that builds one of these logs every decision made in the
/// process, including decisions made outside that container.</para>
/// </remarks>
public sealed class EgressDecisionLoggerSubscription : IDisposable
{
    private readonly IDisposable _subscription;

    /// <summary>Subscribes a sink that writes to the <c>Ashlar.Egress</c> logger of <paramref name="loggerFactory"/>.</summary>
    /// <param name="loggerFactory">The logger factory.</param>
    /// <exception cref="ArgumentNullException"><paramref name="loggerFactory"/> is <see langword="null"/>.</exception>
    public EgressDecisionLoggerSubscription(ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _subscription = EgressDecisionLog.Subscribe(new LoggerEgressDecisionSink(loggerFactory));
    }

    /// <summary>Unsubscribes the sink. Disposing twice does nothing.</summary>
    public void Dispose() => _subscription.Dispose();
}
