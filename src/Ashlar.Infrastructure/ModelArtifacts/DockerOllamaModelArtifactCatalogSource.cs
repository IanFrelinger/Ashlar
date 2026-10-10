using System.Text.Json;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ashlar.Core.Application.ModelArtifacts;
using Ashlar.Core.Application.ModelArtifacts.Ports;
using Ashlar.Infrastructure.Execution.Ollama;

namespace Ashlar.Infrastructure.ModelArtifacts;

/// <summary>
/// Lists Ollama models by probing running Ollama Docker containers on the local engine
/// (<c>GET http://127.0.0.1:&lt;mapped-port&gt;/api/tags</c> per container).
/// </summary>
internal sealed class DockerOllamaModelArtifactCatalogSource : IModelArtifactCatalogSource, IDisposable
{
    /// <summary>http client name constant.</summary>
    public const string HttpClientName = "Ashlar.ModelArtifactCatalog.DockerOllamaProbe";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly DockerClient _dockerClient;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptions<DockerOllamaModelArtifactCatalogOptions> _options;
    private readonly ILogger<DockerOllamaModelArtifactCatalogSource> _logger;
    private readonly bool _disposeDockerClient;

    /// <summary>Initializes a new docker ollama model artifact catalog source.</summary>
    public DockerOllamaModelArtifactCatalogSource(
        IHttpClientFactory httpClientFactory,
        IOptions<DockerOllamaModelArtifactCatalogOptions> options,
        ILogger<DockerOllamaModelArtifactCatalogSource> logger,
        DockerClient? dockerClient = null)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        if (dockerClient != null)
        {
            _dockerClient = dockerClient;
            _disposeDockerClient = false;
        }
        else
        {
            _dockerClient = new DockerClientConfiguration(DockerEngineUriResolver.Resolve()).CreateClient();
            _disposeDockerClient = true;
        }
    }

    /// <summary>Source id.</summary>
    public string SourceId => "docker-ollama-tags";

    /// <summary>Is available asynchronously.</summary>
    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.Value.Enabled)
        {
            return false;
        }

        try
        {
            await _dockerClient.System.PingAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Docker engine not reachable for model artifact catalog");
            return false;
        }
    }

    /// <summary>List asynchronously.</summary>
    public async Task<IReadOnlyList<ModelArtifactRecord>> ListAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.Value.Enabled)
        {
            return [];
        }

        try
        {
            await _dockerClient.System.PingAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Skipping Docker Ollama catalog: engine unavailable");
            return [];
        }

        var prefix = _options.Value.OllamaImagePrefix ?? string.Empty;
        if (prefix.Length == 0)
        {
            return [];
        }

        var privatePort = _options.Value.OllamaContainerPort;
        var containers = await _dockerClient.Containers.ListContainersAsync(
                new ContainersListParameters { All = true },
                cancellationToken)
            .ConfigureAwait(false);

        var http = _httpClientFactory.CreateClient(HttpClientName);
        var merged = new Dictionary<string, ModelArtifactRecord>(StringComparer.OrdinalIgnoreCase);

        foreach (var c in containers)
        {
            if (string.IsNullOrEmpty(c.Image) ||
                !c.Image.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var hostPort = ResolveHostPort(c.Ports, privatePort);
            if (hostPort is null or <= 0)
            {
                continue;
            }

            var url = $"http://127.0.0.1:{hostPort.Value}/api/tags";
            try
            {
                using var response = await http.GetAsync(new Uri(url), cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }

                OllamaTagsResponse? body;
                await using (var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
                {
                    body = await JsonSerializer.DeserializeAsync<OllamaTagsResponse>(stream, JsonOptions, cancellationToken)
                        .ConfigureAwait(false);
                }
                if (body?.Models is null)
                {
                    continue;
                }

                foreach (var m in body.Models)
                {
                    if (string.IsNullOrWhiteSpace(m.Name))
                    {
                        continue;
                    }

                    var id = m.Name.Trim();
                    var meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["docker_container_id"] = c.ID ?? string.Empty,
                        ["docker_image"] = c.Image,
                        ["host_port"] = hostPort.Value.ToString(),
                        ["modified_at"] = m.ModifiedAt?.ToString("O") ?? string.Empty
                    };

                    if (!merged.TryGetValue(id, out var existing) || existing.SizeHintBytes < m.Size)
                    {
                        merged[id] = new ModelArtifactRecord(
                            id,
                            SourceId,
                            ModelArtifactKind.OllamaModel,
                            m.Size,
                            meta);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to read Ollama tags from Docker container {ContainerId} at {Url}", c.ID, url);
            }
        }

        return merged.Values.OrderBy(r => r.Id, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static int? ResolveHostPort(IList<PortSummary>? ports, int privatePort)
    {
        if (ports is null || ports.Count == 0)
        {
            return null;
        }

        foreach (var p in ports)
        {
            if (p.PrivatePort == privatePort && p.PublicPort > 0)
            {
                return p.PublicPort;
            }
        }

        return null;
    }

    /// <summary>Releases managed resources.</summary>
    public void Dispose()
    {
        if (_disposeDockerClient)
        {
            _dockerClient.Dispose();
        }
    }
}
