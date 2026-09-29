using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ashlar.Core.Application.Scaling.Models;
using Ashlar.Core.Application.Scaling.Ports;

namespace Ashlar.Infrastructure.Scaling;

/// <summary>
/// Scales Docker Compose services via <c>docker compose up -d --scale</c>.
/// Useful locally; swap to <see cref="KubernetesWorkloadScaler"/> for clusters.
/// </summary>
internal sealed class ComposeWorkloadScaler : IWorkloadScaler
{
    private readonly WorkloadScalerOptions _options;
    private readonly IProcessCommandRunner _runner;
    private readonly ILogger<ComposeWorkloadScaler> _logger;

    /// <summary>Creates a Compose workload scaler.</summary>
    public ComposeWorkloadScaler(
        IOptions<WorkloadScalerOptions> options,
        IProcessCommandRunner runner,
        ILogger<ComposeWorkloadScaler> logger)
    {
        _options = options.Value;
        _runner = runner;
        _logger = logger;
    }

    /// <inheritdoc />
    public string ProviderName => "compose";

    /// <inheritdoc />
    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        var result = await _runner.RunAsync(
                _options.Compose.DockerPath,
                ["compose", "version"],
                cancellationToken)
            .ConfigureAwait(false);
        return result.Succeeded;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<WorkloadDescriptor>> ListWorkloadsAsync(CancellationToken cancellationToken = default)
    {
        var list = _options.Compose.Workloads
            .Select(kv => new WorkloadDescriptor(
                kv.Key,
                kv.Value.DisplayName ?? kv.Value.Service,
                kv.Value.MinReplicas,
                kv.Value.MaxReplicas,
                ProviderResourceHint: $"compose:{kv.Value.Service}"))
            .ToList();
        return Task.FromResult<IReadOnlyList<WorkloadDescriptor>>(list);
    }

    /// <inheritdoc />
    public async Task<WorkloadReplicaSnapshot> GetReplicasAsync(
        string workloadId,
        CancellationToken cancellationToken = default)
    {
        var binding = RequireBinding(workloadId);
        var args = BuildComposeArgs("ps", "-q", "--status", "running", binding.Service);
        var result = await _runner.RunAsync(_options.Compose.DockerPath, args, cancellationToken)
            .ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"docker compose ps failed for '{workloadId}': {result.StdErr}");
        }

        var count = string.IsNullOrWhiteSpace(result.StdOut)
            ? 0
            : result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;

        return new WorkloadReplicaSnapshot(
            workloadId,
            DesiredReplicas: count,
            CurrentReplicas: count,
            ReadyReplicas: count,
            ObservedAtUtc: DateTimeOffset.UtcNow,
            ProviderDetail: $"compose service={binding.Service}");
    }

    /// <inheritdoc />
    public async Task<WorkloadScaleResult> ScaleAsync(
        WorkloadScaleRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            return new WorkloadScaleResult(
                false,
                request.WorkloadId,
                0,
                request.DesiredReplicas,
                "Workload scaling is disabled (Ashlar:WorkloadScaling:Enabled=false).");
        }

        var binding = RequireBinding(request.WorkloadId);
        var previous = 0;
        try
        {
            previous = (await GetReplicasAsync(request.WorkloadId, cancellationToken).ConfigureAwait(false))
                .DesiredReplicas;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read previous replicas for {WorkloadId}", request.WorkloadId);
        }

        var desired = Math.Clamp(request.DesiredReplicas, binding.MinReplicas, binding.MaxReplicas);
        var args = BuildComposeArgs("up", "-d", "--scale", $"{binding.Service}={desired}", "--no-recreate", binding.Service);
        var result = await _runner.RunAsync(_options.Compose.DockerPath, args, cancellationToken)
            .ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return new WorkloadScaleResult(
                false,
                request.WorkloadId,
                previous,
                desired,
                $"docker compose scale failed: {result.StdErr}");
        }

        _logger.LogInformation(
            "Scaled Compose workload {WorkloadId} service/{Service} {Previous} -> {Desired} ({Reason})",
            request.WorkloadId,
            binding.Service,
            previous,
            desired,
            request.Reason ?? "manual");

        return new WorkloadScaleResult(true, request.WorkloadId, previous, desired, result.StdOut);
    }

    private ComposeWorkloadBinding RequireBinding(string workloadId)
    {
        if (!_options.Compose.Workloads.TryGetValue(workloadId, out var binding) ||
            string.IsNullOrWhiteSpace(binding.Service))
        {
            throw new KeyNotFoundException(
                $"Unknown Compose workload '{workloadId}'. Configure Ashlar:WorkloadScaling:Compose:Workloads.");
        }

        return binding;
    }

    private List<string> BuildComposeArgs(params string[] command)
    {
        var args = new List<string> { "compose" };
        if (!string.IsNullOrWhiteSpace(_options.Compose.ProjectDirectory))
        {
            args.Add("--project-directory");
            args.Add(_options.Compose.ProjectDirectory!);
        }

        args.Add("-f");
        args.Add(_options.Compose.ComposeFile);
        args.AddRange(command);
        return args;
    }
}
