using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ashlar.Core.Application.Autonomy;
using Ashlar.Infrastructure.Execution.Sandbox;

namespace Ashlar.Infrastructure.Autonomy;

/// <summary>
/// Drives <see cref="DockerSandboxSessionReaper"/> on a periodic sweep: sessions leaked by
/// a crashed or killed host have deadline labels but nobody left to honor them, so the
/// reaper is the backstop that eventually kills them. Sweep failures never stop the
/// service — the next sweep retries, which is the whole point of a periodic backstop.
/// </summary>
[Experimental(AutonomyExperimental.DiagnosticId, UrlFormat = AutonomyExperimental.UrlFormat)]
internal sealed class SandboxSessionReaperService : BackgroundService
{
    private readonly DockerSandboxSessionReaper _reaper;
    private readonly TimeSpan _interval;
    private readonly ILogger<SandboxSessionReaperService>? _logger;

    /// <summary>Creates the sweep service.</summary>
    public SandboxSessionReaperService(
        DockerSandboxSessionReaper reaper,
        IOptions<AshlarAutonomyOptions> options,
        ILogger<SandboxSessionReaperService>? logger = null)
    {
        _reaper = reaper ?? throw new ArgumentNullException(nameof(reaper));
        var seconds = options?.Value.ReaperSweepSeconds ?? 60;
        _interval = TimeSpan.FromSeconds(seconds > 0 ? seconds : 60);
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_interval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                    return;

                var reaped = await _reaper.SweepAsync(stoppingToken).ConfigureAwait(false);
                if (reaped.Count > 0)
                {
                    _logger?.LogWarning(
                        "Reaped {Count} leaked sandbox session(s): {Sessions}",
                        reaped.Count, string.Join(", ", reaped));
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // A failed sweep (daemon down, transient CLI error) must not end the
                // backstop; the next tick retries.
                _logger?.LogWarning(ex, "Sandbox session sweep failed; retrying on the next tick");
            }
        }
    }
}
