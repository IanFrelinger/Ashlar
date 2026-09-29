using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ashlar.Core.Application.Execution.Routing;
using Ashlar.Core.Application.NodeCapabilityRuntime.Ports;
using Ashlar.Infrastructure.NodeCapabilityRuntime;

namespace Ashlar.Infrastructure.Execution.Routing;

/// <summary>
/// Background poller that keeps NCR capability snapshot current for routing.
/// </summary>
internal sealed class NCRCapabilityPoller : BackgroundService, INCRCapabilitySnapshot
{
    private readonly IHardwareProfiler _hardwareProfiler;
    private readonly ILocalQueueDepthProvider _queueDepthProvider;
    private readonly IOptions<NodeCapabilityRuntimeOptions> _runtimeOptions;
    private readonly ILogger<NCRCapabilityPoller> _logger;
    private volatile SnapshotState _snapshot = new();

    /// <summary>Initializes a new n c r capability poller.</summary>
    public NCRCapabilityPoller(
        IHardwareProfiler hardwareProfiler,
        ILocalQueueDepthProvider queueDepthProvider,
        IOptions<NodeCapabilityRuntimeOptions> runtimeOptions,
        ILogger<NCRCapabilityPoller> logger)
    {
        _hardwareProfiler = hardwareProfiler ?? throw new ArgumentNullException(nameof(hardwareProfiler));
        _queueDepthProvider = queueDepthProvider ?? throw new ArgumentNullException(nameof(queueDepthProvider));
        _runtimeOptions = runtimeOptions ?? throw new ArgumentNullException(nameof(runtimeOptions));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Available vram bytes.</summary>
    public long AvailableVramBytes => _snapshot.AvailableVramBytes;

    /// <summary>Compute class.</summary>
    public GpuComputeClass ComputeClass => _snapshot.ComputeClass;

    /// <summary>Current queue depth.</summary>
    public int CurrentQueueDepth => _snapshot.CurrentQueueDepth;

    /// <summary>Captured at.</summary>
    public DateTimeOffset CapturedAt => _snapshot.CapturedAt;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RefreshSnapshotAsync(stoppingToken).ConfigureAwait(false);
        var cadence = _runtimeOptions.Value.ProfileRefreshInterval > TimeSpan.Zero
            ? _runtimeOptions.Value.ProfileRefreshInterval
            : TimeSpan.FromSeconds(30);

        using var timer = new PeriodicTimer(cadence);
        while (!stoppingToken.IsCancellationRequested &&
               await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await RefreshSnapshotAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task RefreshSnapshotAsync(CancellationToken cancellationToken)
    {
        try
        {
            var profile = await _hardwareProfiler.CaptureAsync(cancellationToken).ConfigureAwait(false);
            var computeClass = ResolveComputeClass(profile.Tier);
            var queueDepth = Math.Max(0, _queueDepthProvider.CurrentDepth);
            _snapshot = new SnapshotState
            {
                AvailableVramBytes = profile.AvailableVRAMBytes,
                ComputeClass = computeClass,
                CurrentQueueDepth = queueDepth,
                CapturedAt = DateTimeOffset.UtcNow
            };

            _logger.LogDebug(
                "ncr-capability-poller refreshed snapshot vram={VramBytes} compute={ComputeClass} queueDepth={QueueDepth}",
                _snapshot.AvailableVramBytes,
                _snapshot.ComputeClass,
                _snapshot.CurrentQueueDepth);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Graceful cancellation path.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ncr-capability-poller failed to refresh snapshot");
        }
    }

    private static GpuComputeClass ResolveComputeClass(Ashlar.Core.Application.NodeCapabilityRuntime.Models.NodeTier tier)
    {
        var envOverride = Environment.GetEnvironmentVariable("ASHLAR_GPU_COMPUTE_CLASS");
        if (!string.IsNullOrWhiteSpace(envOverride) &&
            Enum.TryParse<GpuComputeClass>(envOverride, ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        return tier switch
        {
            Ashlar.Core.Application.NodeCapabilityRuntime.Models.NodeTier.Core => GpuComputeClass.Extreme,
            Ashlar.Core.Application.NodeCapabilityRuntime.Models.NodeTier.Standard => GpuComputeClass.High,
            Ashlar.Core.Application.NodeCapabilityRuntime.Models.NodeTier.Micro => GpuComputeClass.Medium,
            Ashlar.Core.Application.NodeCapabilityRuntime.Models.NodeTier.Nano => GpuComputeClass.Low,
            _ => GpuComputeClass.None
        };
    }

    private sealed record SnapshotState
    {
        /// <summary>Available vram bytes.</summary>
        public long AvailableVramBytes { get; init; }
        /// <summary>Compute class.</summary>
        public GpuComputeClass ComputeClass { get; init; } = GpuComputeClass.None;
        /// <summary>Current queue depth.</summary>
        public int CurrentQueueDepth { get; init; }
        /// <summary>Captured at.</summary>
        public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.UtcNow;
    }
}
