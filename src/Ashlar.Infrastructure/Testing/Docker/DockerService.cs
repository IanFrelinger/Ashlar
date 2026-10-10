using Ashlar.Abstractions.Security.Egress;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;
using System.Text;
using Ashlar.Infrastructure.HostProcess;

namespace Ashlar.Infrastructure.Testing.Docker;

/// <summary>
/// Implementation of IDockerService using Docker.DotNet library.
/// 
/// Provides portable Docker operations without command-line dependencies.
/// Works on Windows, Linux, and macOS through Docker API.
/// </summary>
public class DockerService : IDockerService, IDisposable
{
    private readonly ILogger<DockerService> _logger;
    private readonly DockerClient _dockerClient;
    private readonly bool _disposeClient;

    /// <summary>Initializes a new docker service.</summary>
    public DockerService(ILogger<DockerService> logger, DockerClient? dockerClient = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        
        if (dockerClient != null)
        {
            _dockerClient = dockerClient;
            _disposeClient = false;
        }
        else
        {
            // Create Docker client based on platform
            var dockerUri = GetDockerUri();
            _dockerClient = new DockerClientConfiguration(dockerUri).CreateClient();
            _disposeClient = true;
        }
    }

    /// <summary>Is docker available asynchronously.</summary>
    public async Task<bool> IsDockerAvailableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _dockerClient.System.PingAsync(cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Docker is not available");
            return false;
        }
    }

    /// <summary>Build image asynchronously.</summary>
    public async Task<DockerBuildResult> BuildImageAsync(
        string dockerfilePath,
        string imageTag,
        string contextPath,
        Dictionary<string, string>? buildArgs = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // The daemon is local, but build steps and container commands have unrestricted network access.
        EgressGuard.ProcessDefault.Evaluate(new EgressRequest(
            EgressFamilies.Process, "EG-PROC-10", "process:docker")).ThrowIfRefused();
        var startTime = DateTime.UtcNow;
        
        try
        {
            _logger.LogInformation("Building Docker image: {ImageTag} from {Dockerfile}", imageTag, dockerfilePath);

            var dockerfileRelativePath = Path.GetRelativePath(contextPath, dockerfilePath).Replace('\\', '/');
            
            var buildParameters = new ImageBuildParameters
            {
                Dockerfile = dockerfileRelativePath,
                Tags = new[] { imageTag },
                BuildArgs = buildArgs ?? new Dictionary<string, string>()
            };

            using var tarStream = CreateBuildContextTar(dockerfilePath, contextPath);
            
            var buildProgress = new Progress<JSONMessage>(message =>
            {
                var logMessage = message.Stream ?? message.Status ?? (message.Error != null ? message.Error.Message : null) ?? string.Empty;
                progress?.Report(logMessage);
                _logger.LogDebug("Docker build: {Message}", logMessage);
            });

            await _dockerClient.Images.BuildImageFromDockerfileAsync(
                buildParameters,
                tarStream,
                null,
                new Dictionary<string, string>(),
                buildProgress,
                cancellationToken);

            var duration = DateTime.UtcNow - startTime;
            _logger.LogInformation("Docker image built successfully: {ImageTag} in {Duration}ms", imageTag, duration.TotalMilliseconds);

            return new DockerBuildResult(true, null, duration);
        }
        catch (Exception ex)
        {
            var duration = DateTime.UtcNow - startTime;
            _logger.LogError(ex, "Failed to build Docker image: {ImageTag}", imageTag);
            return new DockerBuildResult(false, ex.Message, duration);
        }
    }

    /// <summary>Run container asynchronously.</summary>
    public async Task<DockerRunResult> RunContainerAsync(
        string imageTag,
        string[] command,
        Dictionary<string, string>? environmentVariables = null,
        Dictionary<string, string>? volumeMounts = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // The daemon is local, but build steps and container commands have unrestricted network access.
        EgressGuard.ProcessDefault.Evaluate(new EgressRequest(
            EgressFamilies.Process, "EG-PROC-10", "process:docker")).ThrowIfRefused();
        var startTime = DateTime.UtcNow;
        string? containerId = null;

        try
        {
            _logger.LogInformation("Running container: {ImageTag} with command: {Command}", imageTag, string.Join(" ", command));

            // Create container
            var createParams = new CreateContainerParameters
            {
                Image = imageTag,
                Cmd = command,
                Env = environmentVariables?.Select(kv => $"{kv.Key}={kv.Value}").ToList() ?? new List<string>(),
                HostConfig = new HostConfig
                {
                    Binds = volumeMounts?.Select(kv => $"{kv.Key}:{kv.Value}").ToList() ?? new List<string>(),
                    AutoRemove = true // Automatically remove container when it exits
                },
                AttachStdout = true,
                AttachStderr = true,
                Tty = false
            };

            var createResponse = await _dockerClient.Containers.CreateContainerAsync(createParams, cancellationToken);
            containerId = createResponse.ID;

            // Start container
            await _dockerClient.Containers.StartContainerAsync(containerId, new ContainerStartParameters(), cancellationToken);

            // Wait for container to finish
            using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            waitCts.CancelAfter(TimedProcess.DockerWaitTimeout);
            var waitResponse = await _dockerClient.Containers.WaitContainerAsync(containerId, waitCts.Token);
            var exitCode = (int)waitResponse.StatusCode;

            // Get logs
            var (stdout, stderr) = await GetContainerLogsAsync(containerId, cancellationToken);

            var duration = DateTime.UtcNow - startTime;
            var success = exitCode == 0;

            _logger.LogInformation(
                "Container finished: {ContainerId}, ExitCode: {ExitCode}, Duration: {Duration}ms",
                containerId, exitCode, duration.TotalMilliseconds);

            return new DockerRunResult(success, exitCode, stdout, stderr, containerId, duration);
        }
        catch (Exception ex)
        {
            var duration = DateTime.UtcNow - startTime;
            _logger.LogError(ex, "Failed to run container: {ImageTag}", imageTag);
            return new DockerRunResult(false, -1, string.Empty, ex.Message, containerId, duration);
        }
        finally
        {
            // Clean up container
            if (!string.IsNullOrEmpty(containerId))
            {
                try
                {
                    await _dockerClient.Containers.RemoveContainerAsync(
                        containerId,
                        new ContainerRemoveParameters { Force = true },
                        CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to remove container: {ContainerId}", containerId);
                }
            }
        }
    }


    /// <summary>Remove container asynchronously.</summary>
    public async Task RemoveContainerAsync(string containerId, CancellationToken cancellationToken = default)
    {
        try
        {
            await _dockerClient.Containers.RemoveContainerAsync(
                containerId,
                new ContainerRemoveParameters { Force = true },
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to remove container: {ContainerId}", containerId);
        }
    }

    /// <summary>Remove image asynchronously.</summary>
    public async Task RemoveImageAsync(string imageTag, CancellationToken cancellationToken = default)
    {
        try
        {
            await _dockerClient.Images.DeleteImageAsync(imageTag, new ImageDeleteParameters(), cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to remove image: {ImageTag}", imageTag);
        }
    }

    private static Uri GetDockerUri()
    {
        // Determine Docker socket location based on platform
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Windows: named pipe
            return new Uri("npipe://./pipe/docker_engine");
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            // Linux/macOS: Unix socket
            return new Uri("unix:///var/run/docker.sock");
        }
        else
        {
            throw new PlatformNotSupportedException("Unsupported platform for Docker");
        }
    }

    private static Stream CreateBuildContextTar(string dockerfilePath, string contextPath)
    {
        return TarArchiveHelper.CreateBuildContextTar(contextPath, dockerfilePath);
    }

    private async Task<(string stdout, string stderr)> GetContainerLogsAsync(
        string containerId,
        CancellationToken cancellationToken)
    {
        var logsParams = new ContainerLogsParameters
        {
            ShowStdout = true,
            ShowStderr = true,
            Follow = false
        };

        using var stream = await _dockerClient.Containers.GetContainerLogsAsync(containerId, logsParams, cancellationToken);
        
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var buffer = new byte[8192];

        while (true)
        {
            var result = await stream.ReadOutputAsync(buffer, 0, buffer.Length, cancellationToken);
            if (result.EOF)
            {
                break;
            }

            var data = Encoding.UTF8.GetString(buffer, 0, result.Count);
            if (result.Target == MultiplexedStream.TargetStream.StandardOut)
            {
                stdout.Append(data);
            }
            else if (result.Target == MultiplexedStream.TargetStream.StandardError)
            {
                stderr.Append(data);
            }
        }

        return (stdout.ToString(), stderr.ToString());
    }

    /// <summary>Releases managed resources.</summary>
    public void Dispose()
    {
        if (_disposeClient)
        {
            _dockerClient?.Dispose();
        }
    }
}
