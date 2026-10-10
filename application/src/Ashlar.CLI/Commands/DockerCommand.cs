using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text.Json;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ashlar.CLI.Output;
using Ashlar.Infrastructure.Testing.Docker;

namespace Ashlar.CLI.Commands;

/// <summary>
/// Command for Docker operations (build, run, clean, ps, images).
/// Replaces direct Docker CLI calls in scripts.
/// </summary>
public class DockerCommand : Command
{
    /// <summary>Creates a new DockerCommand instance.</summary>
    public DockerCommand() : base("docker", "Docker operations (build, run, clean, ps, images)")
    {
        // ashlar docker build
        var buildCmd = new Command("build", "Build a Docker image from a Dockerfile");
        var dockerfileOpt = new Option<FileInfo>(
            "--dockerfile",
            "Path to Dockerfile"
        ) { IsRequired = true };
        var tagOpt = new Option<string>(
            "--tag",
            "Image tag (e.g., 'myimage:latest')"
        ) { IsRequired = true };
        var contextOpt = new Option<DirectoryInfo>(
            "--context",
            () => new DirectoryInfo(Environment.CurrentDirectory),
            "Build context path"
        );
        var buildArgOpt = new Option<string[]>(
            "--build-arg",
            "Build arguments (format: KEY=VALUE)"
        )
        {
            AllowMultipleArgumentsPerToken = true
        };

        buildCmd.AddOption(dockerfileOpt);
        buildCmd.AddOption(tagOpt);
        buildCmd.AddOption(contextOpt);
        buildCmd.AddOption(buildArgOpt);

        buildCmd.SetHandler(async (InvocationContext ctx) =>
        {
            var dockerfile = ctx.ParseResult.GetValueForOption(dockerfileOpt) ?? throw new InvalidOperationException("--dockerfile is required");
            var tag = ctx.ParseResult.GetValueForOption(tagOpt) ?? throw new InvalidOperationException("--tag is required");
            var context = ctx.ParseResult.GetValueForOption(contextOpt) ?? new DirectoryInfo(Environment.CurrentDirectory);
            var buildArgs = ctx.ParseResult.GetValueForOption(buildArgOpt) ?? Array.Empty<string>();
            var rootCommand = ctx.ParseResult.RootCommandResult.Command;
            var jsonOpt = rootCommand.Options.OfType<Option<bool>>().FirstOrDefault(o => o.Name == "--format-json");
            var verboseOpt = rootCommand.Options.OfType<Option<bool>>().FirstOrDefault(o => o.Name == "--verbose");
            var json = jsonOpt != null ? ctx.ParseResult.GetValueForOption(jsonOpt) : false;
            var verbose = verboseOpt != null ? ctx.ParseResult.GetValueForOption(verboseOpt) : false;
            ctx.ExitCode = await BuildAsync(dockerfile, tag, context, buildArgs, json, verbose);
        });

        // ashlar docker run
        var runCmd = new Command("run", "Run a container");
        var imageOpt = new Option<string>(
            "--image",
            "Image tag to run"
        ) { IsRequired = true };
        var commandOpt = new Option<string[]>(
            "--command",
            "Command to execute in container"
        )
        {
            AllowMultipleArgumentsPerToken = true
        };
        var envOpt = new Option<string[]>(
            "--env",
            "Environment variables (format: KEY=VALUE)"
        )
        {
            AllowMultipleArgumentsPerToken = true
        };
        var volumeOpt = new Option<string[]>(
            "--volume",
            "Volume mounts (format: HOST_PATH:CONTAINER_PATH)"
        )
        {
            AllowMultipleArgumentsPerToken = true
        };
        var workdirOpt = new Option<string?>(
            "--workdir",
            "Working directory in container"
        );
        var rmImageOpt = new Option<bool>(
            "--rm-image",
            () => false,
            "Remove the image after the container exits"
        );

        runCmd.AddOption(imageOpt);
        runCmd.AddOption(commandOpt);
        runCmd.AddOption(envOpt);
        runCmd.AddOption(volumeOpt);
        runCmd.AddOption(workdirOpt);
        runCmd.AddOption(rmImageOpt);

        runCmd.SetHandler(async (InvocationContext ctx) =>
        {
            var image = ctx.ParseResult.GetValueForOption(imageOpt) ?? throw new InvalidOperationException("--image is required");
            var command = ctx.ParseResult.GetValueForOption(commandOpt) ?? Array.Empty<string>();
            var env = ctx.ParseResult.GetValueForOption(envOpt) ?? Array.Empty<string>();
            var volume = ctx.ParseResult.GetValueForOption(volumeOpt) ?? Array.Empty<string>();
            var workdir = ctx.ParseResult.GetValueForOption(workdirOpt);
            var rmImage = ctx.ParseResult.GetValueForOption(rmImageOpt);
            var rootCommand = ctx.ParseResult.RootCommandResult.Command;
            var jsonOpt = rootCommand.Options.OfType<Option<bool>>().FirstOrDefault(o => o.Name == "--format-json");
            var verboseOpt = rootCommand.Options.OfType<Option<bool>>().FirstOrDefault(o => o.Name == "--verbose");
            var json = jsonOpt != null ? ctx.ParseResult.GetValueForOption(jsonOpt) : false;
            var verbose = verboseOpt != null ? ctx.ParseResult.GetValueForOption(verboseOpt) : false;
            ctx.ExitCode = await RunAsync(image, command, env, volume, workdir, rmImage, json, verbose);
        });

        // ashlar docker clean
        var cleanCmd = new Command("clean", "Clean up Docker resources");
        var imageTagOpt = new Option<string?>(
            "--image",
            "Image tag to remove (if not specified, removes all unused images)"
        );
        var containerIdOpt = new Option<string?>(
            "--container",
            "Container ID to remove"
        );
        var allOpt = new Option<bool>(
            "--all",
            () => false,
            "Remove all unused images and containers"
        );

        cleanCmd.AddOption(imageTagOpt);
        cleanCmd.AddOption(containerIdOpt);
        cleanCmd.AddOption(allOpt);

        cleanCmd.SetHandler(async (InvocationContext ctx) =>
        {
            var imageTag = ctx.ParseResult.GetValueForOption(imageTagOpt);
            var containerId = ctx.ParseResult.GetValueForOption(containerIdOpt);
            var all = ctx.ParseResult.GetValueForOption(allOpt);
            var rootCommand = ctx.ParseResult.RootCommandResult.Command;
            var jsonOpt = rootCommand.Options.OfType<Option<bool>>().FirstOrDefault(o => o.Name == "--format-json");
            var verboseOpt = rootCommand.Options.OfType<Option<bool>>().FirstOrDefault(o => o.Name == "--verbose");
            var json = jsonOpt != null ? ctx.ParseResult.GetValueForOption(jsonOpt) : false;
            var verbose = verboseOpt != null ? ctx.ParseResult.GetValueForOption(verboseOpt) : false;
            ctx.ExitCode = await CleanAsync(imageTag, containerId, all, json, verbose);
        });

        // ashlar docker ps
        var psCmd = new Command("ps", "List containers");
        var allContainersOpt = new Option<bool>(
            "--all",
            () => false,
            "Show all containers (including stopped)"
        );

        psCmd.AddOption(allContainersOpt);

        psCmd.SetHandler(async (InvocationContext ctx) =>
        {
            var all = ctx.ParseResult.GetValueForOption(allContainersOpt);
            var rootCommand = ctx.ParseResult.RootCommandResult.Command;
            var jsonOpt = rootCommand.Options.OfType<Option<bool>>().FirstOrDefault(o => o.Name == "--format-json");
            var verboseOpt = rootCommand.Options.OfType<Option<bool>>().FirstOrDefault(o => o.Name == "--verbose");
            var json = jsonOpt != null ? ctx.ParseResult.GetValueForOption(jsonOpt) : false;
            var verbose = verboseOpt != null ? ctx.ParseResult.GetValueForOption(verboseOpt) : false;
            ctx.ExitCode = await ListContainersAsync(all, json, verbose);
        });

        // ashlar docker images
        var imagesCmd = new Command("images", "List images");

        imagesCmd.SetHandler(async (InvocationContext ctx) =>
        {
            var rootCommand = ctx.ParseResult.RootCommandResult.Command;
            var jsonOpt = rootCommand.Options.OfType<Option<bool>>().FirstOrDefault(o => o.Name == "--format-json");
            var verboseOpt = rootCommand.Options.OfType<Option<bool>>().FirstOrDefault(o => o.Name == "--verbose");
            var json = jsonOpt != null ? ctx.ParseResult.GetValueForOption(jsonOpt) : false;
            var verbose = verboseOpt != null ? ctx.ParseResult.GetValueForOption(verboseOpt) : false;
            ctx.ExitCode = await ListImagesAsync(json, verbose);
        });

        AddCommand(buildCmd);
        AddCommand(runCmd);
        AddCommand(cleanCmd);
        AddCommand(psCmd);
        AddCommand(imagesCmd);
    }

    private static async Task<int> BuildAsync(
        FileInfo dockerfile,
        string tag,
        DirectoryInfo context,
        string[] buildArgs,
        bool json,
        bool verbose)
    {
        var console = json ? null : new CliConsole(verbose);
        var services = CreateServiceProvider();
        var loggerFactory = services.GetRequiredService<ILoggerFactory>();
        var dockerLogger = loggerFactory.CreateLogger<DockerService>();
        var dockerService = new DockerService(dockerLogger);

        try
        {
            if (!json && console != null)
            {
                console.WriteHeader("🐳 Building Docker Image");
                console.WritePair("Dockerfile", dockerfile.FullName);
                console.WritePair("Tag", tag);
                console.WritePair("Context", context.FullName);
                if (buildArgs.Length > 0)
                {
                    console.WritePair("Build Args", string.Join(", ", buildArgs));
                }
                console.WriteLine();
            }

            // Check if Docker is available
            if (!await dockerService.IsDockerAvailableAsync())
            {
                if (json)
                {
                    Console.WriteLine(JsonSerializer.Serialize(new { error = "Docker is not available. Is Docker running?" }));
                }
                else if (console != null)
                {
                    console.WriteError("Docker is not available. Is Docker running?");
                }
                return 1;
            }

            // Parse build args
            var buildArgsDict = new Dictionary<string, string>();
            foreach (var arg in buildArgs)
            {
                var parts = arg.Split('=', 2);
                if (parts.Length == 2)
                {
                    buildArgsDict[parts[0]] = parts[1];
                }
            }

            var progress = new Progress<string>(message =>
            {
                if (verbose && !json && console != null)
                {
                    console.WriteLine($"  {message}");
                }
            });

            var result = await dockerService.BuildImageAsync(
                dockerfile.FullName,
                tag,
                context.FullName,
                buildArgsDict.Count > 0 ? buildArgsDict : null,
                progress);

            if (result.Success)
            {
                if (!json && console != null)
                {
                    console.WriteSuccess($"✅ Image built successfully: {tag}");
                    console.WritePair("Duration", $"{result.Duration.TotalSeconds:F1}s");
                }
                else if (json)
                {
                    Console.WriteLine(JsonSerializer.Serialize(new
                    {
                        success = true,
                        imageTag = tag,
                        duration = result.Duration.TotalSeconds
                    }));
                }
                return 0;
            }
            else
            {
                if (json)
                {
                    Console.WriteLine(JsonSerializer.Serialize(new
                    {
                        success = false,
                        error = result.ErrorMessage
                    }));
                }
                else if (console != null)
                {
                    console.WriteError($"❌ Build failed: {result.ErrorMessage}");
                }
                return 1;
            }
        }
        catch (Exception ex)
        {
            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(new { error = ex.Message }));
            }
            else if (console != null)
            {
                console.WriteError($"Error: {ex.Message}");
            }
            return 1;
        }
        finally
        {
            dockerService.Dispose();
        }
    }

    private static async Task<int> RunAsync(
        string image,
        string[] command,
        string[] env,
        string[] volume,
        string? workdir,
        bool rmImage,
        bool json,
        bool verbose)
    {
        var console = json ? null : new CliConsole(verbose);
        var services = CreateServiceProvider();
        var loggerFactory = services.GetRequiredService<ILoggerFactory>();
        var dockerLogger = loggerFactory.CreateLogger<DockerService>();
        var dockerService = new DockerService(dockerLogger);

        try
        {
            if (!json && console != null)
            {
                console.WriteHeader("🐳 Running Docker Container");
                console.WritePair("Image", image);
                if (command.Length > 0)
                {
                    console.WritePair("Command", string.Join(" ", command));
                }
                if (env.Length > 0)
                {
                    console.WritePair("Environment", string.Join(", ", env));
                }
                if (volume.Length > 0)
                {
                    console.WritePair("Volumes", string.Join(", ", volume));
                }
                console.WriteLine();
            }

            // Check if Docker is available
            if (!await dockerService.IsDockerAvailableAsync())
            {
                if (json)
                {
                    Console.WriteLine(JsonSerializer.Serialize(new { error = "Docker is not available. Is Docker running?" }));
                }
                else if (console != null)
                {
                    console.WriteError("Docker is not available. Is Docker running?");
                }
                return 1;
            }

            // Parse environment variables
            var envDict = new Dictionary<string, string>();
            foreach (var e in env)
            {
                var parts = e.Split('=', 2);
                if (parts.Length == 2)
                {
                    envDict[parts[0]] = parts[1];
                }
            }

            // Parse volume mounts
            var volumeDict = new Dictionary<string, string>();
            foreach (var v in volume)
            {
                var parts = v.Split(':', 2);
                if (parts.Length == 2)
                {
                    volumeDict[parts[0]] = parts[1];
                }
            }

            var result = await dockerService.RunContainerAsync(
                image,
                command.Length > 0 ? command : Array.Empty<string>(),
                envDict.Count > 0 ? envDict : null,
                volumeDict.Count > 0 ? volumeDict : null);

            if (rmImage)
            {
                try
                {
                    await dockerService.RemoveImageAsync(image);
                    if (!json && console != null)
                    {
                        console.WriteSuccess($"Removed image: {image}");
                    }
                }
                catch (Exception ex)
                {
                    if (!json && console != null)
                    {
                        console.WriteError($"Failed to remove image: {ex.Message}");
                    }
                }
            }

            if (result.Success)
            {
                if (!json && console != null)
                {
                    if (!string.IsNullOrWhiteSpace(result.StandardOutput))
                    {
                        console.WriteLine(result.StandardOutput);
                    }
                    console.WriteSuccess($"✅ Container finished successfully (exit code: {result.ExitCode})");
                }
                else if (json)
                {
                    Console.WriteLine(JsonSerializer.Serialize(new
                    {
                        success = true,
                        exitCode = result.ExitCode,
                        stdout = result.StandardOutput,
                        stderr = result.StandardError,
                        duration = result.Duration.TotalSeconds
                    }));
                }
                return result.ExitCode;
            }
            else
            {
                if (json)
                {
                    Console.WriteLine(JsonSerializer.Serialize(new
                    {
                        success = false,
                        exitCode = result.ExitCode,
                        stdout = result.StandardOutput,
                        stderr = result.StandardError,
                        error = result.StandardError
                    }));
                }
                else if (console != null)
                {
                    if (!string.IsNullOrWhiteSpace(result.StandardError))
                    {
                        console.WriteError(result.StandardError);
                    }
                    console.WriteError($"❌ Container failed (exit code: {result.ExitCode})");
                }
                return result.ExitCode != 0 ? result.ExitCode : 1;
            }
        }
        catch (Exception ex)
        {
            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(new { error = ex.Message }));
            }
            else if (console != null)
            {
                console.WriteError($"Error: {ex.Message}");
            }
            return 1;
        }
        finally
        {
            dockerService.Dispose();
        }
    }

    private static async Task<int> CleanAsync(
        string? imageTag,
        string? containerId,
        bool all,
        bool json,
        bool verbose)
    {
        var console = json ? null : new CliConsole(verbose);
        var services = CreateServiceProvider();
        var loggerFactory = services.GetRequiredService<ILoggerFactory>();
        var dockerLogger = loggerFactory.CreateLogger<DockerService>();
        var dockerService = new DockerService(dockerLogger);

        try
        {
            if (!json && console != null)
            {
                console.WriteHeader("🧹 Cleaning Docker Resources");
                console.WriteLine();
            }

            // Check if Docker is available
            if (!await dockerService.IsDockerAvailableAsync())
            {
                if (json)
                {
                    Console.WriteLine(JsonSerializer.Serialize(new { error = "Docker is not available. Is Docker running?" }));
                }
                else if (console != null)
                {
                    console.WriteError("Docker is not available. Is Docker running?");
                }
                return 1;
            }

            var cleaned = new List<string>();

            if (!string.IsNullOrEmpty(containerId))
            {
                await dockerService.RemoveContainerAsync(containerId);
                cleaned.Add($"Container: {containerId}");
            }

            if (!string.IsNullOrEmpty(imageTag))
            {
                await dockerService.RemoveImageAsync(imageTag);
                cleaned.Add($"Image: {imageTag}");
            }

            if (all)
            {
                // List and remove all unused images
                var dockerClient = new DockerClientConfiguration().CreateClient();
                try
                {
                    var images = await dockerClient.Images.ListImagesAsync(new ImagesListParameters { All = false });
                    foreach (var image in images)
                    {
                        if (image.RepoTags != null && image.RepoTags.Count > 0)
                        {
                            var tag = image.RepoTags[0];
                            if (!tag.Contains("<none>"))
                            {
                                try
                                {
                                    await dockerService.RemoveImageAsync(tag);
                                    cleaned.Add($"Image: {tag}");
                                }
                                catch
                                {
                                    // Ignore errors for images in use
                                }
                            }
                        }
                    }
                }
                finally
                {
                    dockerClient.Dispose();
                }
            }

            if (!json && console != null)
            {
                if (cleaned.Count > 0)
                {
                    console.WriteSuccess($"✅ Cleaned {cleaned.Count} resource(s):");
                    foreach (var item in cleaned)
                    {
                        console.WriteLine($"  - {item}");
                    }
                }
                else
                {
                    console.WriteLine("No resources to clean");
                }
            }
            else if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    success = true,
                    cleaned = cleaned
                }));
            }

            return 0;
        }
        catch (Exception ex)
        {
            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(new { error = ex.Message }));
            }
            else if (console != null)
            {
                console.WriteError($"Error: {ex.Message}");
            }
            return 1;
        }
        finally
        {
            dockerService.Dispose();
        }
    }

    private static async Task<int> ListContainersAsync(bool all, bool json, bool verbose)
    {
        var console = json ? null : new CliConsole(verbose);

        try
        {
            var dockerClient = new DockerClientConfiguration().CreateClient();
            try
            {
                var containers = await dockerClient.Containers.ListContainersAsync(
                    new ContainersListParameters { All = all });

                if (json)
                {
                    var containerList = containers.Select(c => new
                    {
                        id = c.ID,
                        image = c.Image,
                        status = c.Status,
                        names = c.Names,
                        created = c.Created
                    }).ToArray();
                    Console.WriteLine(JsonSerializer.Serialize(containerList, new JsonSerializerOptions { WriteIndented = true }));
                }
                else if (console != null)
                {
                    console.WriteHeader("📦 Containers");
                    if (containers.Count == 0)
                    {
                        console.WriteLine("No containers found");
                    }
                    else
                    {
                        foreach (var container in containers)
                        {
                            console.WriteLine($"  {container.ID[..12]}  {container.Image}  {container.Status}");
                        }
                    }
                }
            }
            finally
            {
                dockerClient.Dispose();
            }

            return 0;
        }
        catch (Exception ex)
        {
            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(new { error = ex.Message }));
            }
            else if (console != null)
            {
                console.WriteError($"Error: {ex.Message}");
            }
            return 1;
        }
    }

    private static async Task<int> ListImagesAsync(bool json, bool verbose)
    {
        var console = json ? null : new CliConsole(verbose);

        try
        {
            var dockerClient = new DockerClientConfiguration().CreateClient();
            try
            {
                var images = await dockerClient.Images.ListImagesAsync(new ImagesListParameters { All = false });

                if (json)
                {
                    var imageList = images.Select(i => new
                    {
                        id = i.ID,
                        tags = i.RepoTags,
                        size = i.Size,
                        created = i.Created
                    }).ToArray();
                    Console.WriteLine(JsonSerializer.Serialize(imageList, new JsonSerializerOptions { WriteIndented = true }));
                }
                else if (console != null)
                {
                    console.WriteHeader("🖼️  Images");
                    if (images.Count == 0)
                    {
                        console.WriteLine("No images found");
                    }
                    else
                    {
                        foreach (var image in images)
                        {
                            var tags = image.RepoTags != null && image.RepoTags.Count > 0
                                ? string.Join(", ", image.RepoTags)
                                : "<none>";
                            console.WriteLine($"  {image.ID[..12]}  {tags}  {FormatSize(image.Size)}");
                        }
                    }
                }
            }
            finally
            {
                dockerClient.Dispose();
            }

            return 0;
        }
        catch (Exception ex)
        {
            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(new { error = ex.Message }));
            }
            else if (console != null)
            {
                console.WriteError($"Error: {ex.Message}");
            }
            return 1;
        }
    }

    private static string FormatSize(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB" };
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len = len / 1024;
        }
        return $"{len:0.##} {sizes[order]}";
    }

    private static IServiceProvider CreateServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddConsole());
        return services.BuildServiceProvider();
    }
}
