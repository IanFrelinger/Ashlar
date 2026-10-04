using Microsoft.Extensions.Hosting;

namespace Ashlar.Infrastructure.Egress;

/// <summary>
/// Builds the <see cref="EgressDecisionLoggerSubscription"/> when a host starts, so decisions are logged from the start
/// even before any factory client exists. It does nothing else.
/// </summary>
internal sealed class EgressDecisionLoggerActivator : IHostedService
{
    /// <summary>Takes the subscription, which the container builds to construct this.</summary>
    /// <param name="subscription">The subscription.</param>
    /// <exception cref="ArgumentNullException"><paramref name="subscription"/> is <see langword="null"/>.</exception>
    public EgressDecisionLoggerActivator(EgressDecisionLoggerSubscription subscription)
    {
        ArgumentNullException.ThrowIfNull(subscription);
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
