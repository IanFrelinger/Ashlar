using Ashlar.Abstractions.Security.Egress;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Ashlar.Infrastructure.Testing.ExecutionPlatform;

/// <summary>
/// IExecutionPlatform that delegates BuildImageAsync and RunContainerAsync to a remote Ashlar API.
/// Use when Docker is not available (e.g. mobile, CI without Docker) but a Ashlar API server with Docker is reachable.
/// Config: ASHLAR_EXECUTION_REMOTE_URL (required).
/// </summary>
public sealed class RemoteExecutionPlatform : IExecutionPlatform, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly EgressRefusalWarnings _refusalWarnings;
    private readonly ILogger<RemoteExecutionPlatform>? _logger;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>Platform name.</summary>
    public string PlatformName => "Remote";

    /// <summary>Initializes a new remote execution platform.</summary>
    public RemoteExecutionPlatform(HttpClient httpClient, ILogger<RemoteExecutionPlatform>? logger = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger;
        _refusalWarnings = new EgressRefusalWarnings((refusal, suppressed, summary) =>
            _logger?.LogWarning(new EventId(7307, "EgressRefusalHandled"),
                "Egress operation refused. Site={Site} Reason={Reason} Ref={Ref} Suppressed={Suppressed} Summary={Summary}",
                refusal.Site, refusal.Reason, refusal.Ref, suppressed, summary));
    }

    /// <inheritdoc />
    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var resp = await _httpClient.GetAsync("api/status", cancellationToken);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) when (EgressRefusal.Find(ex) is { } refusal)
        {
            _refusalWarnings.Report(refusal);
            return false;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Remote Ashlar API is not reachable");
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<ExecutionBuildResult> BuildImageAsync(
        string dockerfilePath,
        string imageTag,
        string contextPath,
        Dictionary<string, string>? buildArgs = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var startTime = DateTime.UtcNow;
        try
        {
            var request = new { dockerfilePath, imageTag, contextPath, buildArgs = buildArgs ?? new Dictionary<string, string>() };
            var response = await _httpClient.PostAsJsonAsync("api/execution/build", request, JsonOptions, cancellationToken);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadFromJsonAsync<RemoteBuildResponse>(JsonOptions, cancellationToken);
            var duration = DateTime.UtcNow - startTime;
            return new ExecutionBuildResult(
                body?.Success ?? false,
                body?.ErrorMessage,
                TimeSpan.FromMilliseconds(body?.DurationMs ?? duration.TotalMilliseconds));
        }
        catch (Exception ex) when (EgressRefusal.Find(ex) is { } refusal)
        {
            _refusalWarnings.Report(refusal);
            return new ExecutionBuildResult(false, refusal.Message, DateTime.UtcNow - startTime);
        }
        catch (Exception ex)
        {
            var duration = DateTime.UtcNow - startTime;
            _logger?.LogError(ex, "Remote build failed for {ImageTag}", imageTag);
            return new ExecutionBuildResult(false, ex.Message, duration);
        }
    }

    /// <inheritdoc />
    public async Task<ExecutionRunResult> RunContainerAsync(
        string imageTag,
        string[] command,
        Dictionary<string, string>? environmentVariables = null,
        Dictionary<string, string>? volumeMounts = null,
        string? workingDirectory = null,
        CancellationToken cancellationToken = default)
    {
        var startTime = DateTime.UtcNow;
        try
        {
            var request = new
            {
                imageTag,
                command,
                environmentVariables = environmentVariables ?? new Dictionary<string, string>(),
                volumeMounts = volumeMounts ?? new Dictionary<string, string>(),
                workingDirectory
            };
            var response = await _httpClient.PostAsJsonAsync("api/execution/run", request, JsonOptions, cancellationToken);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadFromJsonAsync<RemoteRunResponse>(JsonOptions, cancellationToken);
            var duration = DateTime.UtcNow - startTime;
            return new ExecutionRunResult(
                body?.Success ?? false,
                body?.ExitCode ?? -1,
                body?.StandardOutput ?? "",
                body?.StandardError ?? "",
                body?.ContainerId,
                TimeSpan.FromMilliseconds(body?.DurationMs ?? duration.TotalMilliseconds));
        }
        catch (Exception ex) when (EgressRefusal.Find(ex) is { } refusal)
        {
            _refusalWarnings.Report(refusal);
            return new ExecutionRunResult(false, -1, "", refusal.Message, null, DateTime.UtcNow - startTime);
        }
        catch (Exception ex)
        {
            var duration = DateTime.UtcNow - startTime;
            _logger?.LogError(ex, "Remote run failed for {ImageTag}", imageTag);
            return new ExecutionRunResult(false, -1, "", ex.Message, null, duration);
        }
    }

    /// <inheritdoc />
    public Task RemoveContainerAsync(string containerId, CancellationToken cancellationToken = default)
    {
        _logger?.LogWarning("RemoveContainerAsync is not supported by RemoteExecutionPlatform");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RemoveImageAsync(string imageTag, CancellationToken cancellationToken = default)
    {
        _logger?.LogWarning("RemoveImageAsync is not supported by RemoteExecutionPlatform");
        return Task.CompletedTask;
    }

    private sealed record RemoteBuildResponse(bool Success, string? ErrorMessage, double DurationMs);
    private sealed record RemoteRunResponse(bool Success, int ExitCode, string StandardOutput, string StandardError, string? ContainerId, double DurationMs);
    /// <summary>Flushes suppressed refusal warnings and releases their timers.</summary>
    public void Dispose()
    {
        _refusalWarnings.Dispose();
    }

}
