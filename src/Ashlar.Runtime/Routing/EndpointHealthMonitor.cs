#if NET8_0_OR_GREATER
using Ashlar.Abstractions.Security.Egress;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ashlar.Abstractions.Routing;
using Ashlar.Transport.Grpc;

namespace Ashlar.Runtime.Routing;

/// <summary>
/// Periodic endpoint health monitor that updates registry health state.
/// </summary>
public sealed class EndpointHealthMonitor : BackgroundService
{
    private readonly IEndpointRegistry _endpointRegistry;
    private readonly GrpcAgentTransport? _grpcTransport;
    private readonly RoutingOptions _routingOptions;
    private readonly EgressRefusalWarnings _refusalWarnings;
    private readonly ILogger<EndpointHealthMonitor> _logger;
    private readonly Dictionary<string, int> _consecutiveFailures = new(StringComparer.OrdinalIgnoreCase);

    public EndpointHealthMonitor(
        IEndpointRegistry endpointRegistry,
        GrpcAgentTransport grpcTransport,
        IOptions<RoutingOptions> routingOptions,
        ILogger<EndpointHealthMonitor> logger)
        : this(endpointRegistry, routingOptions, logger)
    {
        _grpcTransport = grpcTransport ?? throw new ArgumentNullException(nameof(grpcTransport));
    }

    /// <summary>
    /// Transport-less constructor so the monitor resolves under every deployment profile:
    /// <c>AddAshlarRuntimeRouting</c> registers this hosted service unconditionally, while the
    /// kernel registers <see cref="GrpcAgentTransport"/> only for profiles that include runtime
    /// transport (Full/Server). DI picks the transport constructor whenever the transport is
    /// registered and falls back to this one otherwise; without a transport there is nothing
    /// to probe with, so the monitor stays idle instead of failing host start-up on an
    /// unresolvable dependency.
    /// </summary>
    public EndpointHealthMonitor(
        IEndpointRegistry endpointRegistry,
        IOptions<RoutingOptions> routingOptions,
        ILogger<EndpointHealthMonitor> logger)
    {
        _endpointRegistry = endpointRegistry ?? throw new ArgumentNullException(nameof(endpointRegistry));
        _routingOptions = routingOptions?.Value ?? new RoutingOptions();
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _refusalWarnings = new EgressRefusalWarnings((refusal, suppressed, summary) =>
            _logger?.LogWarning(new EventId(7307, "EgressRefusalHandled"),
                "Egress operation refused. Site={Site} Reason={Reason} Ref={Ref} Suppressed={Suppressed} Summary={Summary}",
                refusal.Site, refusal.Reason, refusal.Ref, suppressed, summary));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_grpcTransport is null)
        {
            _logger.LogInformation(
                "Endpoint health monitor is idle: no gRPC agent transport is registered under this deployment profile.");
            return;
        }

        var intervalSeconds = Math.Max(1, _routingOptions.HealthCheckIntervalSeconds);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(intervalSeconds));

        await ProbeAllAsync(_grpcTransport, stoppingToken);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await ProbeAllAsync(_grpcTransport, stoppingToken);
        }
    }

    private async Task ProbeAllAsync(GrpcAgentTransport grpcTransport, CancellationToken cancellationToken)
    {
        foreach (var descriptor in _endpointRegistry.GetAll())
        {
            if (string.IsNullOrWhiteSpace(descriptor.Endpoint))
            {
                continue;
            }

            try
            {
                var health = await grpcTransport.CheckEndpointHealthAsync(
                    descriptor.Endpoint,
                    cancellationToken);
                _endpointRegistry.UpdateHealth(descriptor.Endpoint, health.IsHealthy);

                if (health.Refusal is { } refusal)
                {
                    ResetFailureCount(descriptor.Endpoint);
                    _refusalWarnings.Report(refusal);
                    continue;
                }

                if (!health.IsHealthy)
                {
                    var failures = IncrementFailureCount(descriptor.Endpoint);
                    if (failures >= Math.Max(1, _routingOptions.DegradedLogFailureThreshold))
                    {
                        _logger.LogWarning(
                            "Endpoint {EndpointName} ({Endpoint}) is degraded after {FailureCount} failed probes: {Diagnostic}",
                            descriptor.Name,
                            descriptor.Endpoint,
                            failures,
                            health.DiagnosticMessage ?? health.Message ?? "No diagnostic message");
                    }
                }
                else
                {
                    ResetFailureCount(descriptor.Endpoint);
                }
            }
            catch (Exception ex) when (EgressRefusal.Find(ex) is { } refusal)
            {
                _endpointRegistry.UpdateHealth(descriptor.Endpoint, false);
                ResetFailureCount(descriptor.Endpoint);
                _refusalWarnings.Report(refusal);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _endpointRegistry.UpdateHealth(descriptor.Endpoint, false);
                var failures = IncrementFailureCount(descriptor.Endpoint);
                if (failures >= Math.Max(1, _routingOptions.DegradedLogFailureThreshold))
                {
                    _logger.LogWarning(
                        ex,
                        "Endpoint health probe failed for {EndpointName} ({Endpoint}) after {FailureCount} failed probes",
                        descriptor.Name,
                        descriptor.Endpoint,
                        failures);
                }
            }
        }
    }

    private int IncrementFailureCount(string endpoint)
    {
        lock (_consecutiveFailures)
        {
            _consecutiveFailures.TryGetValue(endpoint, out var current);
            var next = current + 1;
            _consecutiveFailures[endpoint] = next;
            return next;
        }
    }

    private void ResetFailureCount(string endpoint)
    {
        lock (_consecutiveFailures)
        {
            _consecutiveFailures.Remove(endpoint);
        }
    }
    /// <summary>Flushes suppressed refusal warnings and releases their timers.</summary>
    public override void Dispose()
    {
        _refusalWarnings.Dispose();
        base.Dispose();
    }

}
#endif
