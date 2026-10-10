using Ashlar.Abstractions.Security.Egress;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ashlar.Core.Application.Execution.Routing;

namespace Ashlar.Infrastructure.Execution.Routing;

/// <summary>
/// HTTP implementation of RunPod REST API wrapper.
/// </summary>
public sealed class RunPodHttpClient : IRunPodClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<RunPodHttpClient> _logger;
    private readonly IOptions<RunPodBrickConfig> _options;

    /// <summary>Initializes a new run pod http client.</summary>
    public RunPodHttpClient(
        HttpClient httpClient,
        IOptions<RunPodBrickConfig> options,
        ILogger<RunPodHttpClient> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Spin up instance.</summary>
    public async Task<Result<RunPodInstance>> SpinUpInstance(
        string modelId,
        string gpuType,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = CreateRequest(HttpMethod.Post, "/v2/instances");
            request.Content = JsonContent.Create(new
            {
                modelId,
                gpuType
            });
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return HttpFailure<RunPodInstance>("runpod.spinup_failed", response.StatusCode);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            var instanceId = ReadText(doc.RootElement, "instanceId") ??
                             ReadText(doc.RootElement, "id") ??
                             string.Empty;
            if (string.IsNullOrWhiteSpace(instanceId))
            {
                return Result<RunPodInstance>.Failure("runpod.spinup_invalid_response", "RunPod did not return an instance id.");
            }

            return Result<RunPodInstance>.Success(new RunPodInstance
            {
                InstanceId = instanceId,
                ModelId = modelId,
                GpuType = gpuType,
                StartedAt = DateTimeOffset.UtcNow
            });
        }
        catch (Exception ex) when (EgressRefusal.Find(ex) is { } refusal)
        {
            return Result<RunPodInstance>.Failure("runpod.egress_refused", refusal.Message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RunPod spin up request failed");
            return Result<RunPodInstance>.Failure("runpod.spinup_exception", "RunPod spin up failed.", ex.Message);
        }
    }

    /// <summary>Dispatch job.</summary>
    public async Task<Result<JobHandle>> DispatchJob(
        string instanceId,
        RunPodJobPayload payload,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = CreateRequest(HttpMethod.Post, $"/v2/instances/{Uri.EscapeDataString(instanceId)}/jobs");
            request.Content = JsonContent.Create(new
            {
                modelId = payload.ModelId,
                prompt = payload.Prompt,
                generationParameters = payload.GenerationParameters,
                outputFormat = payload.OutputFormat,
                priority = payload.IsPriority
            });
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return HttpFailure<JobHandle>("runpod.dispatch_failed", response.StatusCode);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            var jobId = ReadText(doc.RootElement, "jobId") ??
                        ReadText(doc.RootElement, "id") ??
                        string.Empty;
            if (string.IsNullOrWhiteSpace(jobId))
            {
                return Result<JobHandle>.Failure("runpod.dispatch_invalid_response", "RunPod did not return a job id.");
            }

            return Result<JobHandle>.Success(new JobHandle
            {
                InstanceId = instanceId,
                JobId = jobId
            });
        }
        catch (Exception ex) when (EgressRefusal.Find(ex) is { } refusal)
        {
            return Result<JobHandle>.Failure("runpod.egress_refused", refusal.Message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RunPod dispatch request failed for instance={InstanceId}", instanceId);
            return Result<JobHandle>.Failure("runpod.dispatch_exception", "RunPod dispatch failed.", ex.Message);
        }
    }

    /// <summary>Poll job status.</summary>
    public async Task<Result<JobStatus>> PollJobStatus(
        JobHandle jobHandle,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = CreateRequest(HttpMethod.Get, $"/v2/jobs/{Uri.EscapeDataString(jobHandle.JobId)}");
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return HttpFailure<JobStatus>("runpod.poll_failed", response.StatusCode);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            var statusRaw = ReadText(doc.RootElement, "status") ?? string.Empty;
            var message = ReadText(doc.RootElement, "message");

            return Result<JobStatus>.Success(new JobStatus
            {
                State = ParseState(statusRaw),
                Message = message
            });
        }
        catch (Exception ex) when (EgressRefusal.Find(ex) is { } refusal)
        {
            return Result<JobStatus>.Failure("runpod.egress_refused", refusal.Message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RunPod poll request failed for job={JobId}", jobHandle.JobId);
            return Result<JobStatus>.Failure("runpod.poll_exception", "RunPod polling failed.", ex.Message);
        }
    }

    /// <summary>Pull results.</summary>
    public async Task<Result<byte[]>> PullResults(
        JobHandle jobHandle,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = CreateRequest(HttpMethod.Get, $"/v2/jobs/{Uri.EscapeDataString(jobHandle.JobId)}/result");
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return HttpFailure<byte[]>("runpod.pull_failed", response.StatusCode);
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            return Result<byte[]>.Success(bytes);
        }
        catch (Exception ex) when (EgressRefusal.Find(ex) is { } refusal)
        {
            return Result<byte[]>.Failure("runpod.egress_refused", refusal.Message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RunPod result pull failed for job={JobId}", jobHandle.JobId);
            return Result<byte[]>.Failure("runpod.pull_exception", "RunPod result pull failed.", ex.Message);
        }
    }

    /// <summary>Terminate instance.</summary>
    public async Task<Result<Unit>> TerminateInstance(
        string instanceId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = CreateRequest(HttpMethod.Delete, $"/v2/instances/{Uri.EscapeDataString(instanceId)}");
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return HttpFailure<Unit>("runpod.terminate_failed", response.StatusCode);
            }

            return Result<Unit>.Success(Unit.Value);
        }
        catch (Exception ex) when (EgressRefusal.Find(ex) is { } refusal)
        {
            return Result<Unit>.Failure("runpod.egress_refused", refusal.Message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RunPod terminate failed for instance={InstanceId}", instanceId);
            return Result<Unit>.Failure("runpod.terminate_exception", "RunPod instance termination failed.", ex.Message);
        }
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        var options = _options.Value;
        var request = new HttpRequestMessage(method, path);
        if (!string.IsNullOrWhiteSpace(options.ApiKey))
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", options.ApiKey);
        }

        return request;
    }

    private static Result<T> HttpFailure<T>(string code, System.Net.HttpStatusCode status)
        => Result<T>.Failure(code, $"RunPod request failed with HTTP {(int)status} ({status}).");

    private static string? ReadText(JsonElement root, string name)
    {
        if (TryGetPropertyIgnoreCase(root, name, out var property))
        {
            return property.ValueKind switch
            {
                JsonValueKind.String => property.GetString(),
                JsonValueKind.Number => property.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => null
            };
        }

        return null;
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement root, string propertyName, out JsonElement value)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static RunPodJobState ParseState(string raw)
    {
        if (string.Equals(raw, "completed", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(raw, "succeeded", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(raw, "success", StringComparison.OrdinalIgnoreCase))
        {
            return RunPodJobState.Completed;
        }

        if (string.Equals(raw, "failed", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(raw, "error", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(raw, "cancelled", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(raw, "canceled", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(raw, "aborted", StringComparison.OrdinalIgnoreCase))
        {
            return RunPodJobState.Failed;
        }

        if (string.Equals(raw, "queued", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(raw, "pending", StringComparison.OrdinalIgnoreCase))
        {
            return RunPodJobState.Queued;
        }

        if (string.Equals(raw, "running", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(raw, "processing", StringComparison.OrdinalIgnoreCase))
        {
            return RunPodJobState.Running;
        }

        return RunPodJobState.Unknown;
    }
}
