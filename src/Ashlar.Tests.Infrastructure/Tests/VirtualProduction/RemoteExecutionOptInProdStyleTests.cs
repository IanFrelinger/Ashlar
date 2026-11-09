using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Ashlar.Contracts;
using Ashlar.Infrastructure.Testing.ExecutionPlatform;
using Ashlar.Tests.Infrastructure.Helpers;
using Ashlar.Tests.Infrastructure.Helpers.VirtualProduction;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.VirtualProduction;

/// <summary>
/// ProdStyle coverage for the security posture of the remote container-execution surface and the
/// exposure fail-closed rule, on the real Ashlar.API pipeline (auth middleware, endpoint mapping,
/// startup guard). <c>/api/execution/*</c> hands caller-chosen images and commands
/// to the Docker daemon, so it must be dark by default, refuse AuthorizationMode=None even when opted
/// in, and reject all host bind mounts, even with the legacy root configured. Off-loopback ExposureProfiles
/// with no auth must refuse to start unless the operator sets the documented escape hatch.
/// Configuration is injected with UseSetting only; no process environment variables are touched.
/// </summary>
[Collection("Integration")]
[Trait("Category", "Integration")]
[Trait("Category", "ProdStyle")]
public sealed class RemoteExecutionOptInProdStyleTests
{
    private const string ApiKey = "prodstyle-execution-key";
    private const string ServeRemoteExecutionKey = "Ashlar:Execution:ServeRemoteExecution";
    private const string AllowedVolumeMountRootKey = "Ashlar:Execution:AllowedVolumeMountRoot";

    /// <summary>Records what reached the platform instead of talking to Docker.</summary>
    private sealed class RecordingExecutionPlatform : IExecutionPlatform
    {
        public List<(string ImageTag, Dictionary<string, string>? VolumeMounts)> Runs { get; } = [];
        public List<string> Builds { get; } = [];

        public string PlatformName => "Recording";

        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<ExecutionBuildResult> BuildImageAsync(
            string dockerfilePath,
            string imageTag,
            string contextPath,
            Dictionary<string, string>? buildArgs = null,
            IProgress<string>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Builds.Add(imageTag);
            return Task.FromResult(new ExecutionBuildResult(true, null, TimeSpan.FromMilliseconds(1)));
        }

        public Task<ExecutionRunResult> RunContainerAsync(
            string imageTag,
            string[] command,
            Dictionary<string, string>? environmentVariables = null,
            Dictionary<string, string>? volumeMounts = null,
            string? workingDirectory = null,
            CancellationToken cancellationToken = default)
        {
            Runs.Add((imageTag, volumeMounts));
            return Task.FromResult(new ExecutionRunResult(true, 0, "ran", string.Empty, "container-1", TimeSpan.FromMilliseconds(1)));
        }

        public Task RemoveContainerAsync(string containerId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RemoveImageAsync(string imageTag, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private static WebApplicationFactory<Program> CreateFactory(
        IDictionary<string, string?>? settings = null,
        RecordingExecutionPlatform? platform = null)
        => new AshlarApiWebApplicationFactory().WithWebHostBuilder(builder =>
        {
            if (settings is not null)
            {
                foreach (var pair in settings)
                {
                    builder.UseSetting(pair.Key, pair.Value);
                }
            }

            if (platform is not null)
            {
                builder.ConfigureServices(services =>
                {
                    // Later registrations win for plain resolutions: the recorder replaces the
                    // kernel's DockerExecutionPlatform so the test never needs a Docker daemon.
                    services.AddSingleton<IExecutionPlatform>(platform);
                });
            }
        });

    private static Dictionary<string, string?> ApiKeyAuthSettings(params (string Key, string? Value)[] extra)
    {
        var settings = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Ashlar:Security:AuthorizationMode"] = "ApiKey",
            ["Ashlar:Security:ApiKey"] = ApiKey,
        };
        foreach (var (key, value) in extra)
        {
            settings[key] = value;
        }

        return settings;
    }

    private static HttpRequestMessage RunRequest(ExecutionRunRequest body, string? apiKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/execution/run")
        {
            Content = JsonContent.Create(body),
        };
        if (apiKey is not null)
        {
            request.Headers.Add("X-Ashlar-Api-Key", apiKey);
        }

        return request;
    }

    private static HttpRequestMessage BuildRequest(string? apiKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/execution/build")
        {
            Content = JsonContent.Create(new ExecutionBuildRequest("Dockerfile", "ashlar-test:prodstyle", "/ctx")),
        };
        if (apiKey is not null)
        {
            request.Headers.Add("X-Ashlar-Api-Key", apiKey);
        }

        return request;
    }

    private static ExecutionRunRequest SimpleRun(Dictionary<string, string>? volumeMounts = null)
        => new("ashlar-test:prodstyle", ["echo", "hi"], null, volumeMounts, "/workspace");

    [Fact(Timeout = TestTimeouts.HostTouching)]
    public async Task Execution_routes_are_unmapped_by_default()
    {
        var platform = new RecordingExecutionPlatform();
        using var factory = CreateFactory(ApiKeyAuthSettings(), platform);
        using var client = factory.CreateClient();

        var run = await client.SendAsync(RunRequest(SimpleRun(), ApiKey));
        var build = await client.SendAsync(BuildRequest(ApiKey));

        // Unmapped POSTs fall through to the SPA fallback route, which only accepts GET.
        run.StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed);
        build.StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed);
        platform.Runs.Should().BeEmpty();
        platform.Builds.Should().BeEmpty();
    }

    [Fact(Timeout = TestTimeouts.HostTouching)]
    public async Task Opted_in_without_auth_refuses_with_403_and_never_reaches_the_platform()
    {
        var platform = new RecordingExecutionPlatform();
        using var factory = CreateFactory(new Dictionary<string, string?>
        {
            [ServeRemoteExecutionKey] = "true",
        }, platform);
        using var client = factory.CreateClient();

        var run = await client.SendAsync(RunRequest(SimpleRun(), apiKey: null));
        var build = await client.SendAsync(BuildRequest(apiKey: null));

        run.StatusCode.Should().Be(HttpStatusCode.Forbidden, "AuthorizationMode=None must never serve container execution");
        build.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using var problem = JsonDocument.Parse(await run.Content.ReadAsStringAsync());
        problem.RootElement.GetProperty("detail").GetString().Should().Contain("AuthorizationMode");
        platform.Runs.Should().BeEmpty();
        platform.Builds.Should().BeEmpty();
    }

    [Fact(Timeout = TestTimeouts.HostTouching)]
    public async Task Opted_in_with_api_key_auth_reaches_the_platform_only_with_credentials()
    {
        var platform = new RecordingExecutionPlatform();
        using var factory = CreateFactory(ApiKeyAuthSettings((ServeRemoteExecutionKey, "true")), platform);
        using var client = factory.CreateClient();

        var withoutKey = await client.SendAsync(RunRequest(SimpleRun(), apiKey: null));
        withoutKey.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "the auth middleware guards the mapped route");
        platform.Runs.Should().BeEmpty();

        var run = await client.SendAsync(RunRequest(SimpleRun(), ApiKey));
        run.StatusCode.Should().Be(HttpStatusCode.OK);
        var runBody = await run.Content.ReadFromJsonAsync<ExecutionRunResponse>();
        runBody.Should().NotBeNull();
        runBody!.Success.Should().BeTrue();
        runBody.ContainerId.Should().Be("container-1");
        platform.Runs.Should().ContainSingle().Which.ImageTag.Should().Be("ashlar-test:prodstyle");

        var build = await client.SendAsync(BuildRequest(ApiKey));
        build.StatusCode.Should().Be(HttpStatusCode.OK);
        platform.Builds.Should().ContainSingle().Which.Should().Be("ashlar-test:prodstyle");
    }

    [Fact(Timeout = TestTimeouts.HostTouching)]
    public async Task Volume_mounts_are_rejected_when_no_root_is_configured()
    {
        var platform = new RecordingExecutionPlatform();
        using var factory = CreateFactory(ApiKeyAuthSettings((ServeRemoteExecutionKey, "true")), platform);
        using var client = factory.CreateClient();

        var hostPath = OperatingSystem.IsWindows() ? @"C:\Windows" : "/etc";
        var response = await client.SendAsync(RunRequest(
            SimpleRun(new Dictionary<string, string> { [hostPath] = "/mnt/host" }), ApiKey));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("AllowedVolumeMountRoot");
        platform.Runs.Should().BeEmpty("a rejected mount must never reach the Docker daemon");

        // RemoteExecutionPlatform sends an empty dictionary when the caller has no mounts; that must pass.
        var empty = await client.SendAsync(RunRequest(SimpleRun(new Dictionary<string, string>()), ApiKey));
        empty.StatusCode.Should().Be(HttpStatusCode.OK);
        platform.Runs.Should().ContainSingle();
    }

    [Fact(Timeout = TestTimeouts.HostTouching)]
    public async Task Volume_mounts_are_rejected_even_under_the_legacy_configured_root()
    {
        var platform = new RecordingExecutionPlatform();
        var root = Directory.CreateTempSubdirectory("ashlar-prodstyle-mount-").FullName;
        try
        {
            var inside = Directory.CreateDirectory(Path.Combine(root, "test-results")).FullName;
            var file = Path.Combine(inside, "result.txt");
            File.WriteAllText(file, "test result");
            using var factory = CreateFactory(
                ApiKeyAuthSettings((ServeRemoteExecutionKey, "true"), (AllowedVolumeMountRootKey, root)),
                platform);
            using var client = factory.CreateClient();

            // Even ordinary existing directories/files are refused. Checking a source first and
            // letting the daemon resolve it later would still leave a symlink-replacement window.
            foreach (var hostPath in new[]
            {
                root, inside, file, Path.Combine(root, "missing"),
                Path.Combine(root, "..", "escaped"), root + "-sibling", "test-results",
            })
            {
                var rejected = await client.SendAsync(RunRequest(
                    SimpleRun(new Dictionary<string, string> { [hostPath] = "/workspace/test-results" }), ApiKey));
                rejected.StatusCode.Should().Be(HttpStatusCode.BadRequest, "no caller-selected bind source is safe to hand to the daemon");
                (await rejected.Content.ReadAsStringAsync()).Should().Contain("Host bind mounts are disabled");
            }
            platform.Runs.Should().BeEmpty("a configured legacy root must not reopen bind mounts");

            // Both wire representations of a mount-free request still run with the same config.
            foreach (var mounts in new Dictionary<string, string>?[] { null, new() })
            {
                var accepted = await client.SendAsync(RunRequest(SimpleRun(mounts), ApiKey));
                accepted.StatusCode.Should().Be(HttpStatusCode.OK);
            }
            platform.Runs.Should().HaveCount(2);
            platform.Runs.Should().OnlyContain(run => run.VolumeMounts == null || run.VolumeMounts.Count == 0);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // Linux is the supported devtest container platform. Windows symlink creation can require
    // privileges; report an explicit skip there rather than silently treating a failed arrange as green.
    private sealed class UnixSymlinkFactAttribute : FactAttribute
    {
        public UnixSymlinkFactAttribute()
        {
            if (OperatingSystem.IsWindows())
                Skip = "This filesystem regression requires Unix symlink creation; run it in the Linux devtest container.";
        }
    }

    [UnixSymlinkFact(Timeout = TestTimeouts.HostTouching)]
    public async Task In_root_symlink_mount_sources_never_reach_the_platform()
    {
        var stem = Directory.CreateTempSubdirectory("ashlar-prodstyle-symlink-").FullName;
        try
        {
            var root = Directory.CreateDirectory(Path.Combine(stem, "allowed")).FullName;
            var outside = Directory.CreateDirectory(Path.Combine(stem, "outside")).FullName;
            File.WriteAllText(Path.Combine(outside, "private.txt"), "outside marker");
            var link = Path.Combine(root, "link");
            Directory.CreateSymbolicLink(link, outside);
            File.ReadAllText(Path.Combine(link, "private.txt")).Should().Be("outside marker",
                "the fixture must actually resolve outside the allowed root");

            var platform = new RecordingExecutionPlatform();
            using var factory = CreateFactory(
                ApiKeyAuthSettings((ServeRemoteExecutionKey, "true"), (AllowedVolumeMountRootKey, root)),
                platform);
            using var client = factory.CreateClient();
            foreach (var hostPath in new[] { link, Path.Combine(link, "private.txt"), Path.Combine(link, "missing") })
            {
                var rejected = await client.SendAsync(RunRequest(
                    SimpleRun(new Dictionary<string, string> { [hostPath] = "/mnt/host" }), ApiKey));
                rejected.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            }
            platform.Runs.Should().BeEmpty("no symlink spelling may be resolved later by Docker");
        }
        finally
        {
            Directory.Delete(stem, recursive: true);
        }
    }

    [Fact(Timeout = TestTimeouts.HostTouching)]
    public async Task Lan_exposure_without_auth_refuses_to_start()
    {
        using var factory = CreateFactory(new Dictionary<string, string?>
        {
            ["Ashlar:Security:ExposureProfile"] = "Lan",
        });

        var act = () =>
        {
            using var client = factory.CreateClient();
            return Task.CompletedTask;
        };

        (await act.Should().ThrowAsync<Exception>("an off-loopback profile with AuthorizationMode=None must fail closed"))
            .Which.ToString().Should().Contain("AllowUnauthenticatedNetworkExposure")
            .And.Contain("Lan");
    }

    [Fact(Timeout = TestTimeouts.HostTouching)]
    public async Task Lan_exposure_starts_with_the_escape_hatch_or_with_built_in_auth()
    {
        using (var factory = CreateFactory(new Dictionary<string, string?>
               {
                   ["Ashlar:Security:ExposureProfile"] = "Lan",
                   ["Ashlar:Security:AllowUnauthenticatedNetworkExposure"] = "true",
               }))
        using (var client = factory.CreateClient())
        {
            var health = await client.GetAsync("/health");
            health.StatusCode.Should().Be(HttpStatusCode.OK, "the explicit escape hatch turns the refusal into a warning");
        }

        using (var factory = CreateFactory(ApiKeyAuthSettings(("Ashlar:Security:ExposureProfile", "Tailnet"))))
        using (var client = factory.CreateClient())
        {
            var health = await client.GetAsync("/health");
            health.StatusCode.Should().Be(HttpStatusCode.OK, "off-loopback with built-in auth configured is the intended deployment shape");
        }
    }
}
