using Ashlar.Abstractions.Security.Egress;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Logging;
using Ashlar.Core.Application.Ephemeral.Ports;
using System.Runtime.InteropServices;

namespace Ashlar.Infrastructure.Execution.Ephemeral;

/// <summary>
/// Ephemeral Ollama lifecycle. Starts an ollama/ollama container per session;
/// stops and removes it when the session scope is disposed.
/// </summary>
public sealed class OllamaEphemeralLifecycle : IEphemeralModelLifecycle
{
    private const string Image = "ollama/ollama";
    private const int ContainerPort = 11434;

    private readonly ILogger<OllamaEphemeralLifecycle> _logger;
    private readonly DockerClient _dockerClient;
    private readonly AsyncLocal<string?> _sessionUrl = new();

    /// <summary>Initializes a new ollama ephemeral lifecycle.</summary>
    public OllamaEphemeralLifecycle(ILogger<OllamaEphemeralLifecycle> logger, DockerClient? dockerClient = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        var uri = GetDockerUri();
        _dockerClient = dockerClient ?? new DockerClientConfiguration(uri).CreateClient();
    }

    /// <summary>Start session asynchronously.</summary>
    public async Task<IAsyncDisposable> StartSessionAsync(string? model = null, CancellationToken cancellationToken = default)
    {
        if (_sessionUrl.Value != null)
        {
            _logger.LogWarning("Session already active; returning existing scope");
            return new SessionScope(this, null);
        }

        // No network-off guarantee exists for this container.
        EgressGuard.ProcessDefault.Evaluate(new EgressRequest(
            EgressFamilies.Process, "EG-PROC-11", "process:docker")).ThrowIfRefused();

        var createParams = new CreateContainerParameters
        {
            Image = Image,
            HostConfig = new HostConfig
            {
                PortBindings = new Dictionary<string, IList<PortBinding>>
                {
                    [ContainerPort + "/tcp"] = new List<PortBinding> { new() { HostPort = "0" } }
                },
                AutoRemove = true
            }
        };

        var createResponse = await _dockerClient.Containers.CreateContainerAsync(createParams, cancellationToken);
        var containerId = createResponse.ID;
        _logger.LogInformation("Created ephemeral Ollama container: {ContainerId}", containerId);

        await _dockerClient.Containers.StartContainerAsync(containerId, null, cancellationToken);

        // Wait for Ollama to be ready
        await Task.Delay(2000, cancellationToken);

        var port = await GetHostPortAsync(containerId, cancellationToken);
        var baseUrl = $"http://localhost:{port}";
        _sessionUrl.Value = baseUrl;

        _logger.LogInformation("Ephemeral Ollama ready at {BaseUrl}", baseUrl);
        return new SessionScope(this, containerId);
    }

    /// <summary>Get base url asynchronously.</summary>
    public Task<string> GetBaseUrlAsync(CancellationToken cancellationToken = default)
    {
        var url = _sessionUrl.Value;
        if (string.IsNullOrEmpty(url))
            throw new InvalidOperationException("No ephemeral Ollama session active. Call StartSessionAsync first, or use OLLAMA_BASE_URL for non-ephemeral.");
        return Task.FromResult(url);
    }

    internal async Task StopSessionAsync(string? containerId, bool isNoOp = false)
    {
        _sessionUrl.Value = null;
        if (isNoOp || string.IsNullOrEmpty(containerId)) return;

        try
        {
            await _dockerClient.Containers.StopContainerAsync(
                containerId,
                new ContainerStopParameters { WaitBeforeKillSeconds = 5 },
                CancellationToken.None);
            _logger.LogInformation("Stopped ephemeral Ollama container: {ContainerId}", containerId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to stop ephemeral Ollama container: {ContainerId}", containerId);
        }
    }

    private async Task<int> GetHostPortAsync(string containerId, CancellationToken ct)
    {
        var inspect = await _dockerClient.Containers.InspectContainerAsync(containerId, ct);
        var bindings = inspect.NetworkSettings?.Ports;
        if (bindings == null) return ContainerPort;

        var key = ContainerPort + "/tcp";
        if (bindings.TryGetValue(key, out var list) && list != null && list.Count > 0)
        {
            var hostPort = list[0].HostPort;
            if (!string.IsNullOrEmpty(hostPort) && int.TryParse(hostPort, out var port))
                return port;
        }

        return ContainerPort;
    }

    private static Uri GetDockerUri()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return new Uri("npipe://./pipe/docker_engine");
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return new Uri("unix:///var/run/docker.sock");
        throw new PlatformNotSupportedException("Unsupported operating system");
    }

    private sealed class SessionScope : IAsyncDisposable
    {
        private readonly OllamaEphemeralLifecycle _lifecycle;
        private readonly string? _containerId;

        /// <summary>Initializes a new session scope.</summary>
        public SessionScope(OllamaEphemeralLifecycle lifecycle, string? containerId)
        {
            _lifecycle = lifecycle;
            _containerId = containerId;
        }

        /// <summary>Dispose asynchronously.</summary>
        public async ValueTask DisposeAsync()
        {
            await _lifecycle.StopSessionAsync(_containerId, _containerId == null);
        }
    }
}
