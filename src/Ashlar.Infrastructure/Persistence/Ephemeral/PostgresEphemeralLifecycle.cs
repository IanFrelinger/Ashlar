using Ashlar.Abstractions.Security.Egress;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Logging;
using Ashlar.Core.Application.Persistence.Ports;
using System.Runtime.InteropServices;

namespace Ashlar.Infrastructure.Persistence.Ephemeral;

/// <summary>
/// Ephemeral PostgreSQL lifecycle. Starts a postgres container per session;
/// stops and removes it when StopAsync is called.
/// </summary>
public sealed class PostgresEphemeralLifecycle : IEphemeralDatabaseLifecycle
{
    private const string DefaultImage = "postgres:16-alpine";
    private const int ContainerPort = 5432;

    private readonly ILogger<PostgresEphemeralLifecycle> _logger;
    private readonly DockerClient _dockerClient;

    /// <summary>Initializes a new postgres ephemeral lifecycle.</summary>
    public PostgresEphemeralLifecycle(ILogger<PostgresEphemeralLifecycle> logger, DockerClient? dockerClient = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        var uri = GetDockerUri();
        _dockerClient = dockerClient ?? new DockerClientConfiguration(uri).CreateClient();
    }

    /// <summary>Start asynchronously.</summary>
    public async Task<EphemeralDbResult> StartAsync(EphemeralDbOptions options, CancellationToken cancellationToken = default)
    {
        if (!string.Equals(options.Engine, "postgres", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Engine must be 'postgres', got '{options.Engine}'", nameof(options));

        var image = options.ImageTag ?? DefaultImage;
        var password = options.Password ?? "ashlar_ephemeral_" + Guid.NewGuid().ToString("N")[..12];
        var database = options.Database ?? "ashlar";

        // No network-off guarantee exists for this container.
        EgressGuard.ProcessDefault.Evaluate(new EgressRequest(
            EgressFamilies.Process, "EG-PROC-11", "process:docker")).ThrowIfRefused();

        var createParams = new CreateContainerParameters
        {
            Image = image,
            Env = new List<string>
            {
                $"POSTGRES_PASSWORD={password}",
                $"POSTGRES_DB={database}"
            },
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
        _logger.LogInformation("Created ephemeral Postgres container: {ContainerId}", containerId);

        await _dockerClient.Containers.StartContainerAsync(containerId, null, cancellationToken);

        // Wait for Postgres to accept connections
        await Task.Delay(3000, cancellationToken);

        var hostPort = await GetHostPortAsync(containerId, cancellationToken);
        var connectionString = $"Host=localhost;Port={hostPort};Database={database};Username=postgres;Password={password}";

        _logger.LogInformation("Ephemeral Postgres ready at localhost:{Port}", hostPort);
        return new EphemeralDbResult(connectionString, containerId, hostPort);
    }

    /// <summary>Stop asynchronously.</summary>
    public async Task StopAsync(string containerId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(containerId)) return;

        try
        {
            await _dockerClient.Containers.StopContainerAsync(
                containerId,
                new ContainerStopParameters { WaitBeforeKillSeconds = 5 },
                cancellationToken);
            _logger.LogInformation("Stopped ephemeral Postgres container: {ContainerId}", containerId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to stop ephemeral Postgres container: {ContainerId}", containerId);
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
            var hostPortStr = list[0].HostPort;
            if (!string.IsNullOrEmpty(hostPortStr) && int.TryParse(hostPortStr, out var port))
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
}
