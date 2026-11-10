using System.Threading.Channels;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Ashlar.Core.Application.Observation.Models;
using Ashlar.Core.Application.Observation.Ports;

namespace Ashlar.Infrastructure.Observation;
/// <summary>
/// Event source that monitors process start/stop via polling.
/// Emits NormalizedEvent with Category="process-names", SourceId="process-monitor".
/// </summary>
public sealed class ProcessEventSource : IObservableEventSource
{
    private const string SourceIdValue = "process-monitor";
    private const string CategoryValue = "process-names";
    private readonly IReadOnlyList<string> _processFilters;
    private readonly TimeSpan _pollInterval;
    private readonly string? _projectPath;
    private readonly ILogger<ProcessEventSource>? _logger;
    /// <summary>
    /// Creates a process event source.
    /// </summary>
    /// <param name = "processFilters">Process names to track (e.g. dotnet, msbuild, ashlar). Case-insensitive partial match.</param>
    /// <param name = "projectPath">Optional project path for per-project gating.</param>
    /// <param name = "pollInterval">How often to poll for process changes.</param>
    /// <param name = "logger">Optional logger.</param>
    public ProcessEventSource(IEnumerable<string> processFilters, string? projectPath = null, TimeSpan? pollInterval = null, ILogger<ProcessEventSource>? logger = null)
    {
        _processFilters = processFilters?.Where(n => !string.IsNullOrWhiteSpace(n)).ToList() ?? throw new ArgumentNullException(nameof(processFilters));
        _projectPath = projectPath;
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(2);
        if (_pollInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(pollInterval));
        _logger = logger;
    }

    /// <inheritdoc/>
    public string SourceId => SourceIdValue;

    /// <inheritdoc/>
    public async IAsyncEnumerable<NormalizedEvent> SubscribeAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (_processFilters.Count == 0)
        {
            _logger?.LogWarning("No process filters configured for ProcessEventSource");
            yield break;
        }

        // Every enumeration owns its polling state, queue and cancellation. Slow consumers retain
        // only the latest 256 observations, and a plain break stops polling without a caller token.
        using var producerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var channel = Channel.CreateBounded<NormalizedEvent>(new BoundedChannelOptions(256)
        {
            SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.DropOldest
        });
        var pollTask = Task.Run(async () =>
        {
            try { await PollAsync(channel.Writer, producerCts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (producerCts.IsCancellationRequested) { }
            finally { channel.Writer.TryComplete(); }
        }, CancellationToken.None);
        try
        {
            await foreach (var evt in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                yield return evt;
        }
        finally
        {
            producerCts.Cancel();
            await pollTask.ConfigureAwait(false);
        }
    }

    private async Task PollAsync(ChannelWriter<NormalizedEvent> writer, CancellationToken ct)
    {
        var knownProcesses = new Dictionary<int, string>();
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var currentIds = new HashSet<int>();
                foreach (var proc in Process.GetProcesses())
                {
                    try
                    {
                        var name = proc.ProcessName;
                        var matches = _processFilters.Any(f => name.Contains(f, StringComparison.OrdinalIgnoreCase));
                        if (matches)
                        {
                            currentIds.Add(proc.Id);
                            if (!knownProcesses.ContainsKey(proc.Id))
                            {
                                knownProcesses[proc.Id] = name;
                                Enqueue(writer, "started", name, proc.Id);
                            }
                        }
                    }
                    catch
                    {
                        // Swallow per-process exceptions to keep polling
                    }
                    finally
                    {
                        proc.Dispose();
                    }
                }

                foreach (var kvp in knownProcesses.ToList())
                {
                    if (!currentIds.Contains(kvp.Key))
                    {
                        knownProcesses.Remove(kvp.Key);
                        Enqueue(writer, "stopped", kvp.Value, kvp.Key);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "ProcessEventSource poll failed");
            }

            await Task.Delay(_pollInterval, ct).ConfigureAwait(false);
        }
    }

    private void Enqueue(ChannelWriter<NormalizedEvent> writer, string changeType, string processName, int processId)
    {
        var payload = JsonSerializer.SerializeToElement(new { changeType, processName, processId, });
        var evt = new NormalizedEvent
        {
            EventId = Guid.NewGuid().ToString("N"),
            Timestamp = DateTimeOffset.UtcNow,
            SourceId = SourceIdValue,
            Category = CategoryValue,
            ProjectPath = _projectPath,
            Payload = payload,
        };
        writer.TryWrite(evt);
    }
}