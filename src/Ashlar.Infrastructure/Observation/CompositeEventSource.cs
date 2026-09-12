using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Ashlar.Core.Application.Observation.Models;
using Ashlar.Core.Application.Observation.Ports;

namespace Ashlar.Infrastructure.Observation;
/// <summary>
/// Merges multiple event sources into a single stream.
/// </summary>
/// <remarks>
/// <para><b>The subscription owns its children.</b> When <see cref="SubscribeAsync"/> returns — the
/// consumer broke out of the loop, threw, or the token tripped — every child pump has been
/// cancelled AND joined. That is what makes "the composite stopped" mean anything to a caller such
/// as <c>ObservationPipelineService</c>, whose <c>StopAsync</c> completes as soon as its
/// <c>ExecuteAsync</c> returns: without the join the host tears down the DI container while live
/// <see cref="System.IO.FileSystemWatcher"/> callback threads are still enqueueing into a child
/// that nobody is reading.</para>
/// <para>Cancelling first is not optional. <c>ProcessEventSource</c> awaits its own poll task in a
/// <c>finally</c>, and <c>FileSystemEventSource</c> waits on a channel read; both loop until their
/// token trips, so awaiting the pumps without cancelling them would hang rather than join.</para>
/// </remarks>
public sealed class CompositeEventSource : IObservableEventSource
{
    private const string SourceIdValue = "composite";
    private readonly IReadOnlyList<IObservableEventSource> _sources;
    private readonly ILogger<CompositeEventSource>? _logger;

    /// <summary>
    /// Creates a composite source that merges events from the given sources.
    /// </summary>
    /// <param name="sources">The child sources to merge.</param>
    /// <param name="logger">Optional logger. Without one, a child that dies takes its half of the
    /// stream with it and says nothing.</param>
    public CompositeEventSource(IEnumerable<IObservableEventSource> sources, ILogger<CompositeEventSource>? logger = null)
    {
        _sources = sources?.ToList() ?? throw new ArgumentNullException(nameof(sources));
        _logger = logger;
    }

    /// <inheritdoc/>
    public string SourceId => SourceIdValue;

    /// <inheritdoc/>
    public async IAsyncEnumerable<NormalizedEvent> SubscribeAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (_sources.Count == 0)
            yield break;
        var channel = Channel.CreateUnbounded<NormalizedEvent>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false, });
        var writer = channel.Writer;

        // Linked, so the children stop when this subscription ends even if the caller's token never
        // trips (the `break` case, which is how every consumer in this repository exits).
        using var childCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var childToken = childCts.Token;

        var tasks = _sources.Select(async source =>
        {
            try
            {
                await foreach (var evt in source.SubscribeAsync(childToken).WithCancellation(childToken))
                {
                    await writer.WriteAsync(evt, childToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when subscription is cancelled
            }
            catch (Exception ex)
            {
                // The composite keeps serving its other children rather than tearing the whole
                // stream down, but a silently dead child is indistinguishable from a quiet one.
                _logger?.LogError(ex, "Event source {SourceId} stopped with an error; the composite will no longer receive its events.", source.SourceId);
            }
        }).ToList();

        var pump = Task.WhenAll(tasks);
        _ = pump.ContinueWith(
            static (_, state) => ((ChannelWriter<NormalizedEvent>)state!).TryComplete(),
            writer,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        try
        {
            await foreach (var evt in channel.Reader.ReadAllAsync(cancellationToken))
            {
                yield return evt;
            }
        }
        finally
        {
            childCts.Cancel();
            try
            {
                await pump.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Every per-child failure is already handled inside the pump; this only catches a
                // pump that failed outside its own try (for example an IAsyncEnumerable whose
                // GetAsyncEnumerator threw) and must not mask the consumer's own exception.
                _logger?.LogError(ex, "Composite event source children did not shut down cleanly.");
            }
        }
    }
}
