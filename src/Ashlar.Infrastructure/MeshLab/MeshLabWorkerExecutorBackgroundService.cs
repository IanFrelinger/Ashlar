using Ashlar.Abstractions.Security.Egress;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ashlar.Infrastructure.MeshLab;

/// <summary>
/// Virtual mesh lab: polls the director for assigned tasks and drives Running → Succeeded (optional brick on assigned peer).
/// </summary>
public sealed class MeshLabWorkerExecutorBackgroundService : BackgroundService
{
    private readonly MeshLabWorkerExecutorClient _client;
    private readonly IOptionsMonitor<MeshLabWorkerExecutorOptions> _options;
    private readonly EgressRefusalWarnings _refusalWarnings;
    private readonly ILogger<MeshLabWorkerExecutorBackgroundService> _logger;

    /// <summary>Initializes a new mesh lab worker executor background service.</summary>
    public MeshLabWorkerExecutorBackgroundService(
        MeshLabWorkerExecutorClient client,
        IOptionsMonitor<MeshLabWorkerExecutorOptions> options,
        ILogger<MeshLabWorkerExecutorBackgroundService> logger)
    {
        _client = client;
        _options = options;
        _logger = logger;
        _refusalWarnings = new EgressRefusalWarnings((refusal, suppressed, summary) =>
            _logger?.LogWarning(new EventId(7307, "EgressRefusalHandled"),
                "Egress operation refused. Site={Site} Reason={Reason} Ref={Ref} Suppressed={Suppressed} Summary={Summary}",
                refusal.Site, refusal.Reason, refusal.Ref, suppressed, summary));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var opts = _options.CurrentValue;
            if (!opts.Enabled)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken).ConfigureAwait(false);
                continue;
            }

            try
            {
                var processed = await _client.TryProcessOneAssignedTaskAsync(stoppingToken).ConfigureAwait(false);
                if (processed > 0)
                {
                    _logger.LogInformation("mesh-lab-worker-executor: completed one assigned task");
                }
            }
            catch (Exception ex) when (EgressRefusal.Find(ex) is { } refusal)
            {
                _refusalWarnings.Report(refusal);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                _logger.LogDebug(ex, "mesh-lab-worker-executor: director poll failed");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "mesh-lab-worker-executor: unexpected error");
            }

            var delay = Math.Clamp(opts.PollIntervalMs, 100, 60_000);
            await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
        }
    }
    /// <summary>Flushes suppressed refusal warnings and releases their timers.</summary>
    public override void Dispose()
    {
        _refusalWarnings.Dispose();
        base.Dispose();
    }

}
