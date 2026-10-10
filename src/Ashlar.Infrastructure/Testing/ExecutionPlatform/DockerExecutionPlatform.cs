using Ashlar.Abstractions.Security.Egress;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Logging;
using Ashlar.Infrastructure.HostProcess;
using Ashlar.Infrastructure.Testing.Docker;
using System.Runtime.InteropServices;
using System.Text;

// Note: TarArchiveHelper is in Ashlar.Infrastructure.Testing.Docker namespace
// This is intentional - it's a shared utility for Docker operations

namespace Ashlar.Infrastructure.Testing.ExecutionPlatform;

/// <summary>
/// Docker implementation of IExecutionPlatform using Docker.DotNet.
/// 
/// Provides portable Docker operations without command-line dependencies.
/// Works on Windows, Linux, and macOS through Docker API.
/// </summary>
public class DockerExecutionPlatform : IExecutionPlatform, IDisposable
{
    private readonly ILogger<DockerExecutionPlatform> _logger;
    private readonly DockerClient _dockerClient;
    private readonly bool _disposeClient;

    /// <summary>Platform name.</summary>
    public string PlatformName => "Docker";

    /// <summary>Initializes a new docker execution platform.</summary>
    public DockerExecutionPlatform(ILogger<DockerExecutionPlatform> logger, DockerClient? dockerClient = null)
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

    /// <summary>Is available asynchronously.</summary>
    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
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
    public async Task<ExecutionBuildResult> BuildImageAsync(
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
            var dockerfileRelativePath = Path.GetRelativePath(contextPath, dockerfilePath).Replace('\\', '/');
            var buildParameters = new ImageBuildParameters
            {
                Dockerfile = dockerfileRelativePath,
                Tags = new[] { imageTag },
                BuildArgs = buildArgs ?? new Dictionary<string, string>()
            };

            using var tarStream = TarArchiveHelper.CreateBuildContextTar(contextPath, dockerfilePath);
            
            var buildProgress = new Progress<JSONMessage>(message =>
            {
                if (message.Error != null)
                {
                    _logger.LogError("Docker build error: {Error}", message.Error);
                }
                else if (message.Stream != null)
                {
                    progress?.Report(message.Stream);
                }
            });

            await _dockerClient.Images.BuildImageFromDockerfileAsync(
                buildParameters,
                tarStream,
                null,
                new Dictionary<string, string>(),
                buildProgress,
                cancellationToken);

            var duration = DateTime.UtcNow - startTime;
            return new ExecutionBuildResult(true, null, duration);
        }
        catch (Exception ex)
        {
            var duration = DateTime.UtcNow - startTime;
            _logger.LogError(ex, "Failed to build Docker image: {ImageTag}", imageTag);
            return new ExecutionBuildResult(false, ex.Message, duration);
        }
    }

    /// <summary>Run container asynchronously.</summary>
    public async Task<ExecutionRunResult> RunContainerAsync(
        string imageTag,
        string[] command,
        Dictionary<string, string>? environmentVariables = null,
        Dictionary<string, string>? volumeMounts = null,
        string? workingDirectory = null,
        CancellationToken cancellationToken = default)
    {
        // The daemon is local, but build steps and container commands have unrestricted network access.
        EgressGuard.ProcessDefault.Evaluate(new EgressRequest(
            EgressFamilies.Process, "EG-PROC-10", "process:docker")).ThrowIfRefused();
        var startTime = DateTime.UtcNow;
        string? containerId = null;

        try
        {
            var createParams = new CreateContainerParameters
            {
                Image = imageTag,
                Cmd = command.ToList(),
                Env = environmentVariables?.Select(kv => $"{kv.Key}={kv.Value}").ToList() ?? new List<string>(),
                WorkingDir = workingDirectory,
                HostConfig = new HostConfig
                {
                    Binds = volumeMounts?.Select(kv => $"{kv.Key}:{kv.Value}").ToList() ?? new List<string>(),
                    AutoRemove = true
                },
                AttachStdout = true,
                AttachStderr = true,
                Tty = false
            };

            var createResponse = await _dockerClient.Containers.CreateContainerAsync(createParams, cancellationToken);
            containerId = createResponse.ID;

            await _dockerClient.Containers.StartContainerAsync(containerId, null, cancellationToken);

            using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            waitCts.CancelAfter(TimedProcess.DockerWaitTimeout);
            var waitResponse = await _dockerClient.Containers.WaitContainerAsync(containerId, waitCts.Token);
            var exitCode = (int)waitResponse.StatusCode;

            var (stdout, stderr) = await GetContainerLogsAsync(containerId, cancellationToken);

            var duration = DateTime.UtcNow - startTime;
            return new ExecutionRunResult(
                exitCode == 0,
                exitCode,
                stdout,
                stderr,
                containerId,
                duration);
        }
        catch (Exception ex)
        {
            var duration = DateTime.UtcNow - startTime;
            _logger.LogError(ex, "Failed to run Docker container: {ImageTag}", imageTag);
            return new ExecutionRunResult(false, -1, string.Empty, ex.Message, containerId, duration);
        }
        finally
        {
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
            await _dockerClient.Containers.RemoveContainerAsync(containerId, new ContainerRemoveParameters(), cancellationToken);
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

    private async Task<(string stdout, string stderr)> GetContainerLogsAsync(string containerId, CancellationToken cancellationToken)
    {
        var logsParams = new ContainerLogsParameters
        {
            ShowStdout = true,
            ShowStderr = true
        };

        using var stream = await _dockerClient.Containers.GetContainerLogsAsync(containerId, logsParams, cancellationToken);
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var buffer = new byte[8192];

        while (true)
        {
            var result = await stream.ReadOutputAsync(buffer, 0, buffer.Length, cancellationToken);
            if (result.EOF) break;
            var data = Encoding.UTF8.GetString(buffer, 0, result.Count);
            if (result.Target == MultiplexedStream.TargetStream.StandardOut) stdout.Append(data);
            else if (result.Target == MultiplexedStream.TargetStream.StandardError) stderr.Append(data);
        }

        return (stdout.ToString(), stderr.ToString());
    }

    private static Uri GetDockerUri()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return new Uri("npipe://./pipe/docker_engine");
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return new Uri("unix:///var/run/docker.sock");
        }
        else
        {
            throw new PlatformNotSupportedException("Unsupported operating system");
        }
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
