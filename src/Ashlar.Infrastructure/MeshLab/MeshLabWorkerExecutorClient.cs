using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ashlar.Infrastructure.MeshLab;

/// <summary>
/// Polls the mesh-lab director for assigned tasks and executes them against peer nodes.
/// Used by background worker hosts in mesh-lab integration scenarios.
/// </summary>
public sealed class MeshLabWorkerExecutorClient
{
    /// <summary>Named <see cref="IHttpClientFactory"/> client for mesh-lab HTTP calls.</summary>
    public const string HttpClientName = "mesh-lab-worker-executor";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptionsMonitor<MeshLabWorkerExecutorOptions> _options;
    private readonly IConfiguration _configuration;
    private readonly ILogger<MeshLabWorkerExecutorClient> _logger;

    /// <summary>Initializes a new mesh lab worker executor client.</summary>
    public MeshLabWorkerExecutorClient(
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<MeshLabWorkerExecutorOptions> options,
        IConfiguration configuration,
        ILogger<MeshLabWorkerExecutorClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>Try process one assigned task asynchronously.</summary>
    public async Task<int> TryProcessOneAssignedTaskAsync(CancellationToken cancellationToken)
    {
        var opts = _options.CurrentValue;
        var director = CreateDirectorClient(opts);
        var tasks = await ListTasksAsync(director, cancellationToken).ConfigureAwait(false);
        var candidate = PickCandidate(tasks, opts.TaskNamePrefix);
        if (candidate is null)
            return 0;

        return await ExecuteTaskAsync(director, candidate, opts, cancellationToken).ConfigureAwait(false)
            ? 1
            : 0;
    }

    /// <summary>Pick candidate.</summary>
    public static MeshLabTaskSnapshot? PickCandidate(IReadOnlyList<MeshLabTaskSnapshot> tasks, string prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix))
            return null;

        foreach (var task in tasks)
        {
            if (!IsStatus(task.Status, MeshLabTaskStatus.Assigned))
                continue;
            if (string.IsNullOrWhiteSpace(task.LeaseToken))
                continue;
            if (string.IsNullOrWhiteSpace(task.AssignedApiBaseUrl))
                continue;
            var name = task.Name ?? string.Empty;
            if (!name.StartsWith(prefix, StringComparison.Ordinal))
                continue;
            return task;
        }

        return null;
    }

    /// <summary>Whether status.</summary>
    public static bool IsStatus(string? status, MeshLabTaskStatus expected)
    {
        if (string.IsNullOrWhiteSpace(status))
            return false;
        if (int.TryParse(status, out var numeric) && numeric == (int)expected)
            return true;
        return string.Equals(status, expected.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> ExecuteTaskAsync(
        HttpClient director,
        MeshLabTaskSnapshot task,
        MeshLabWorkerExecutorOptions opts,
        CancellationToken cancellationToken)
    {
        var apiKey = ResolveApiKey(opts);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogWarning("mesh-lab-worker-executor: no API key configured; skipping task {TaskId}", task.TaskId);
            return false;
        }

        var runningBody = new
        {
            status = (int)MeshLabTaskStatus.Running,
            leaseToken = task.LeaseToken,
            reason = "mesh-lab-worker-executor-running",
        };
        if (!await PatchStatusAsync(director, task.TaskId, runningBody, apiKey, cancellationToken).ConfigureAwait(false))
            return false;

        byte[]? resultPayload = null;
        if (opts.ExecuteBrickOnAssignedPeer)
        {
            try
            {
                resultPayload = await ExecuteBrickOnAssignedPeerAsync(task.AssignedApiBaseUrl!, apiKey, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested &&
                ex is HttpRequestException or TaskCanceledException or JsonException or InvalidDataException or FormatException)
            {
                _logger.LogWarning(ex, "mesh-lab-worker-executor: brick execution failed for task {TaskId}", task.TaskId);
                return await PatchStatusAsync(director, task.TaskId, new
                {
                    status = (int)MeshLabTaskStatus.Failed,
                    leaseToken = task.LeaseToken,
                    resultSummary = "mesh-lab-worker-executor-failed",
                    reason = "mesh-lab-worker-executor-execution-failed",
                }, apiKey, cancellationToken).ConfigureAwait(false);
            }
        }

        var doneBody = new
        {
            status = (int)MeshLabTaskStatus.Succeeded,
            leaseToken = task.LeaseToken,
            resultSummary = opts.ResultSummary,
            resultPayload,
            reason = "mesh-lab-worker-executor-succeeded",
        };
        return await PatchStatusAsync(director, task.TaskId, doneBody, apiKey, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> PatchStatusAsync(
        HttpClient director,
        string taskId,
        object body,
        string apiKey,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"api/mesh/tasks/{Uri.EscapeDataString(taskId)}/status");
        request.Headers.TryAddWithoutValidation("X-Ashlar-Api-Key", apiKey);
        request.Content = JsonContent.Create(body);
        using var response = await director.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogWarning(
                "mesh-lab-worker-executor: PATCH {TaskId} failed {StatusCode}: {Detail}",
                taskId,
                (int)response.StatusCode,
                detail.Length > 300 ? detail[..300] : detail);
            return false;
        }

        return true;
    }

    private async Task<byte[]> ExecuteBrickOnAssignedPeerAsync(
        string assignedApiBaseUrl,
        string apiKey,
        CancellationToken cancellationToken)
    {
        // Matches the director's inline result limit. Do not send worker-local paths to the director.
        const int maxPayloadBytes = 256 * 1024;
        var peer = _httpClientFactory.CreateClient(HttpClientName);
        peer.BaseAddress = new Uri(assignedApiBaseUrl.TrimEnd('/') + "/");
        peer.DefaultRequestHeaders.TryAddWithoutValidation("X-Ashlar-Api-Key", apiKey);

        using var catalogResponse = await peer.GetAsync("api/bricks", cancellationToken).ConfigureAwait(false);
        catalogResponse.EnsureSuccessStatusCode();
        await using var catalogStream = await catalogResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var catalog = await JsonDocument.ParseAsync(catalogStream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var brickId = TryReadFirstBrickId(catalog.RootElement);
        if (string.IsNullOrWhiteSpace(brickId))
            throw new InvalidDataException("Assigned peer has no executable brick.");

        using var request = new HttpRequestMessage(HttpMethod.Post, $"api/bricks/{Uri.EscapeDataString(brickId)}/execute")
        {
            Content = JsonContent.Create(new { brickId, implementation = "Deterministic", input = new Dictionary<string, object?>() })
        };
        using var response = await peer.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        // Bound the JSON envelope as well as the decoded binary payload (base64 expands the bytes).
        await response.Content.LoadIntoBufferAsync(2 * maxPayloadBytes).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True ||
            !root.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Object ||
            (output.TryGetProperty("success", out var outputSuccess) && outputSuccess.ValueKind == JsonValueKind.False))
            throw new InvalidDataException("Assigned peer did not return successful brick output.");

        byte[] payload;
        if (output.TryGetProperty("payload", out var binary) && binary.ValueKind == JsonValueKind.Object &&
            binary.TryGetProperty("__type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "bytes")
        {
            if (!binary.TryGetProperty("base64", out var encoded) || encoded.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("Brick binary payload is missing base64 bytes.");
            payload = Convert.FromBase64String(encoded.GetString()!);
        }
        else
        {
            // Bricks without a binary payload still return their actual structured output.
            payload = System.Text.Encoding.UTF8.GetBytes(output.GetRawText());
        }
        if (payload.Length > maxPayloadBytes)
            throw new InvalidDataException("Brick result exceeds the 256 KiB inline result limit.");
        return payload;
    }

    private static string? TryReadFirstBrickId(JsonElement root)
    {
        if (!root.TryGetProperty("bricks", out var bricks) && !root.TryGetProperty("Bricks", out bricks))
            return null;
        if (bricks.ValueKind != JsonValueKind.Array || bricks.GetArrayLength() == 0)
            return null;

        var first = bricks[0];
        foreach (var prop in new[] { "id", "Id", "brickId", "BrickId" })
        {
            if (first.TryGetProperty(prop, out var id) && id.ValueKind == JsonValueKind.String)
            {
                var value = id.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }
        }

        return null;
    }

    private async Task<IReadOnlyList<MeshLabTaskSnapshot>> ListTasksAsync(HttpClient director, CancellationToken cancellationToken)
    {
        using var response = await director.GetAsync("api/mesh/tasks", cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var list = await JsonSerializer.DeserializeAsync<List<MeshLabTaskSnapshot>>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
        return list ?? [];
    }

    private HttpClient CreateDirectorClient(MeshLabWorkerExecutorOptions opts)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);
        var baseUrl = (opts.DirectorBaseUrl ?? string.Empty).TrimEnd('/') + "/";
        client.BaseAddress = new Uri(baseUrl);
        var apiKey = ResolveApiKey(opts);
        client.DefaultRequestHeaders.Remove("X-Ashlar-Api-Key");
        if (!string.IsNullOrWhiteSpace(apiKey))
            client.DefaultRequestHeaders.TryAddWithoutValidation("X-Ashlar-Api-Key", apiKey);
        return client;
    }

    private string? ResolveApiKey(MeshLabWorkerExecutorOptions opts) =>
        string.IsNullOrWhiteSpace(opts.ApiKey)
            ? _configuration["Ashlar:Security:ApiKey"]
            : opts.ApiKey.Trim();
}
