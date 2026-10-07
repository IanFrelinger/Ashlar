using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ashlar.Core.Application.Scaling.Models;
using Ashlar.Core.Application.Scaling.Ports;

namespace Ashlar.Infrastructure.Scaling;

/// <summary>
/// Periodically evaluates demand and calls the active <see cref="IWorkloadScaler"/>.
/// Provider-agnostic: Kubernetes today, Compose/ECS tomorrow without changing this loop.
/// </summary>
internal sealed class ElasticWorkloadAutoscaleService : BackgroundService
{
    private readonly IWorkloadScaler _scaler;
    private readonly IWorkloadDemandSignal _demand;
    private readonly IWorkloadScalePolicy _policy;
    private readonly WorkloadScalerOptions _options;
    private readonly ILogger<ElasticWorkloadAutoscaleService> _logger;

    /// <summary>Creates the autoscale hosted service.</summary>
    public ElasticWorkloadAutoscaleService(
        IWorkloadScaler scaler,
        IWorkloadDemandSignal demand,
        IWorkloadScalePolicy policy,
        IOptions<WorkloadScalerOptions> options,
        ILogger<ElasticWorkloadAutoscaleService> logger)
    {
        _scaler = scaler;
        _demand = demand;
        _policy = policy;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(5, _options.Autoscale.IntervalSeconds));
        _logger.LogInformation(
            "Workload autoscale loop started provider={Provider} interval={IntervalSeconds}s",
            _scaler.ProviderName,
            interval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Workload autoscale tick failed");
            }

            try
            {
                await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task TickAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
            return;

        var workloads = await _scaler.ListWorkloadsAsync(cancellationToken).ConfigureAwait(false);
        var selected = _options.Autoscale.WorkloadIds.Count == 0
            ? workloads
            : workloads.Where(w => _options.Autoscale.WorkloadIds.Contains(w.WorkloadId, StringComparer.OrdinalIgnoreCase))
                .ToList();

        foreach (var workload in selected)
        {
            var replicas = await _scaler.GetReplicasAsync(workload.WorkloadId, cancellationToken)
                .ConfigureAwait(false);
            var demand = await _demand.GetDemandAsync(workload.WorkloadId, cancellationToken)
                .ConfigureAwait(false);
            var decision = _policy.Evaluate(new WorkloadScaleContext(workload, replicas, demand));
            if (!decision.ShouldScale)
                continue;

            var result = await _scaler.ScaleAsync(
                    new WorkloadScaleRequest(workload.WorkloadId, decision.DesiredReplicas, decision.Reason),
                    cancellationToken)
                .ConfigureAwait(false);

            _logger.LogInformation(
                "Autoscale {WorkloadId}: success={Success} {Previous}->{Desired} ({Message})",
                workload.WorkloadId,
                result.Success,
                result.PreviousDesiredReplicas,
                result.DesiredReplicas,
                result.Message ?? decision.Reason);
        }
    }
}
