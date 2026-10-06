using Ashlar.AI.Pipeline;
using Ashlar.Core.Application.Execution.Routing;
using Ashlar.Hosting;
using Ashlar.Infrastructure.Deployment;
using Ashlar.Infrastructure.Execution;
using Ashlar.Infrastructure.Execution.LoadPolicy;
using Ashlar.Infrastructure.MeshLab;
using Ashlar.Infrastructure.ModelArtifacts;
using Ashlar.Mcp.Server;
using Ashlar.Tests.Infrastructure.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using CorePorts = Ashlar.Core.Application.Execution.Ports;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>
/// SPEC-007 PR 4.10: AirGapped and SecureWorkstation hygiene, composed through <c>AddAshlar</c> (design §2.7, default
/// D35, owner decision Q6). On AirGapped, overnight routing runs locally with the AirGapped reason and an explicit
/// peer-network-only job is refused; the LLM and vision paths never try <c>openai</c> or <c>azure</c>; the four
/// opt-ins that leave the node fail boot; the ollama.com catalog defaults to off. Full is unchanged. On
/// SecureWorkstation, MCP over HTTP fails boot and stdio still boots.
/// </summary>
/// <remarks>
/// Composing AirGapped or SecureWorkstation notes a profile no later <c>AddAshlar</c> lowers (default D5), so the class
/// runs in the serialized <c>EnvironmentVariables</c> collection and each test starts from, and restores, the process
/// egress state through the reset seam (default D41, design §2.10).
/// </remarks>
[Trait("Category", "Certification")]
[Collection("EnvironmentVariables")]
public sealed class AirGappedHygieneTests : IDisposable
{
    private const string OvernightReason = "Overnight/background job forces remote execution.";

    private readonly EgressProcessStateScope _egressState = new(reset: true);

    public void Dispose() => _egressState.Dispose();

    private static ServiceCollection Compose(
        AshlarDeploymentProfile profile,
        Action<IServiceCollection>? before = null,
        Action<AshlarHostingOptions>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        before?.Invoke(services);
        services.AddAshlar(options =>
        {
            options.DeploymentProfile = profile;
            configure?.Invoke(options);
        });
        return services;
    }

    private static ExecutionTarget Overnight(IServiceProvider sp, RemoteExecutionPreference preference = RemoteExecutionPreference.UseSystemDefault) =>
        sp.GetRequiredService<ICapabilityRouter>().ResolveExecutionTarget(new JobRequirements
        {
            IsOvernightOrBackground = true,
            RemoteExecutionPreference = preference,
        });

    private static void ValidateOnStart(IServiceProvider sp) => sp.GetRequiredService<IStartupValidator>().Validate();

    [Fact(Timeout = TestTimeouts.E2E)]
    public async Task AirGapped_overnight_routing_runs_locally_with_the_AirGapped_reason()
    {
        await Task.CompletedTask;
        using var sp = Compose(AshlarDeploymentProfile.AirGapped).BuildServiceProvider();

        var target = Overnight(sp);

        target.Should().BeOfType<ExecutionTarget.Local>("AirGapped never routes a job off the node")
            .Which.Reason.Should().Be($"AirGapped: remote execution unavailable; running locally ({OvernightReason})");
    }

    [Fact(Timeout = TestTimeouts.E2E)]
    public async Task AirGapped_refuses_an_explicit_peer_network_only_job_with_an_explained_failure()
    {
        await Task.CompletedTask;
        using var sp = Compose(AshlarDeploymentProfile.AirGapped).BuildServiceProvider();

        var act = () => Overnight(sp, RemoteExecutionPreference.PeerNetworkOnly);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*AirGapped*PeerNetworkOnly*refused*");
    }

    [Fact(Timeout = TestTimeouts.E2E)]
    public async Task A_later_weaker_AddAshlar_keeps_the_AirGapped_routing()
    {
        await Task.CompletedTask;
        var services = Compose(AshlarDeploymentProfile.AirGapped);
        services.AddAshlar(options => options.DeploymentProfile = AshlarDeploymentProfile.Full);
        using var sp = services.BuildServiceProvider();

        Overnight(sp).Should().BeOfType<ExecutionTarget.Local>(
            "the strictest profile noted in the process wins (default D5), so a later Full AddAshlar does not reopen RunPod");
    }

    [Theory(Timeout = TestTimeouts.E2E)]
    [InlineData("ollama", new[] { "ollama" })]
    [InlineData("openai", new[] { "ollama", "local" })]
    public async Task AirGapped_LLM_never_tries_openai_or_azure(string resolved, string[] expected)
    {
        var (sp, recorder) = ComposeRecording(AshlarDeploymentProfile.AirGapped, resolved);
        using (sp)
        {
            var factory = sp.GetRequiredService<IProviderFactory>();

            var act = () => factory.ExecuteLLMAsync("any", "system", "user", new object());

            await act.Should().ThrowAsync<CorePorts.ModelUnavailableException>();
            recorder.Tried.Should().Equal(expected);
        }
    }

    [Theory(Timeout = TestTimeouts.E2E)]
    [InlineData("ollama", new[] { "ollama" })]
    [InlineData("openai", new[] { "ollama" })]
    public async Task AirGapped_vision_never_tries_openai_or_azure(string resolved, string[] expected)
    {
        var (sp, recorder) = ComposeRecording(AshlarDeploymentProfile.AirGapped, resolved);
        using (sp)
        {
            var factory = sp.GetRequiredService<IProviderFactory>();

            var act = () => factory.ExecuteVisionAsync("any", "system", "user", [1, 2, 3], new object());

            await act.Should().ThrowAsync<CorePorts.ModelUnavailableException>();
            recorder.Tried.Should().Equal(expected);
        }
    }

    [Theory(Timeout = TestTimeouts.E2E)]
    [InlineData(AshlarDeploymentProfile.AirGapped, "ollama", new[] { "ollama" })]
    [InlineData(AshlarDeploymentProfile.AirGapped, "openai", new string[0])]
    [InlineData(AshlarDeploymentProfile.AirGapped, "azure", new string[0])]
    [InlineData(AshlarDeploymentProfile.Full, "openai", new[] { "openai" })]
    public async Task Multi_frame_vision_on_AirGapped_refuses_a_cloud_resolve(
        AshlarDeploymentProfile profile, string resolved, string[] expected)
    {
        var (sp, recorder) = ComposeRecording(profile, resolved);
        using (sp)
        {
            var factory = sp.GetRequiredService<IProviderFactory>();

            var act = () => factory.ExecuteVisionMultiFrameAsync("any", "system", "user", [new byte[] { 1 }], new object());

            await act.Should().ThrowAsync<CorePorts.ModelUnavailableException>();
            recorder.Tried.Should().Equal(expected);
        }
    }

    /// <summary>The four opt-ins, by the setting an operator would write.</summary>
    private static readonly string[] OptInSettings =
    [
        "BrickHost:RemoteCatalogBaseUrls",
        "Ashlar:RunPod:EnablePeerNetworkRouting",
        "Ashlar:MeshLab:WorkerExecutor:Enabled",
        "Ashlar:Meai:Bedrock:Enabled",
    ];

    private static void OptIn(IServiceCollection services, string setting)
    {
        switch (setting)
        {
            case "BrickHost:RemoteCatalogBaseUrls":
                services.Configure<BrickHostOptions>(o => o.RemoteCatalogBaseUrls = ["https://peer.example/"]);
                break;
            case "Ashlar:RunPod:EnablePeerNetworkRouting":
                services.Configure<RunPodBrickConfig>(o => o.EnablePeerNetworkRouting = true);
                break;
            case "Ashlar:MeshLab:WorkerExecutor:Enabled":
                services.Configure<MeshLabWorkerExecutorOptions>(o => o.Enabled = true);
                break;
            case "Ashlar:Meai:Bedrock:Enabled":
                services.AddSingleton(Options.Create(new MeaiPipelineOptions { Bedrock = { Enabled = true } }));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(setting), setting, null);
        }
    }

    [Theory(Timeout = TestTimeouts.E2E)]
    [InlineData("BrickHost:RemoteCatalogBaseUrls")]
    [InlineData("Ashlar:RunPod:EnablePeerNetworkRouting")]
    [InlineData("Ashlar:MeshLab:WorkerExecutor:Enabled")]
    [InlineData("Ashlar:Meai:Bedrock:Enabled")]
    public async Task AirGapped_opt_in_fails_boot(string setting)
    {
        await Task.CompletedTask;
        var services = Compose(AshlarDeploymentProfile.AirGapped);
        OptIn(services, setting);
        using var sp = services.BuildServiceProvider();

        var act = () => ValidateOnStart(sp);

        act.Should().Throw<OptionsValidationException>()
            .WithMessage($"*{setting}*AirGapped*");

        if (setting != "Ashlar:Meai:Bedrock:Enabled")
        {
            // The three options-bound validators also refuse at the first resolution of their options, in a process
            // that never starts a host; the Bedrock check reads a ready-made instance, so it runs at start only.
            var resolve = () => ResolveOptIn(sp, setting);
            resolve.Should().Throw<OptionsValidationException>().WithMessage($"*{setting}*AirGapped*");
        }
    }

    private static object ResolveOptIn(IServiceProvider sp, string setting) => setting switch
    {
        "BrickHost:RemoteCatalogBaseUrls" => sp.GetRequiredService<IOptions<BrickHostOptions>>().Value,
        "Ashlar:RunPod:EnablePeerNetworkRouting" => sp.GetRequiredService<IOptions<RunPodBrickConfig>>().Value,
        "Ashlar:MeshLab:WorkerExecutor:Enabled" => sp.GetRequiredService<IOptions<MeshLabWorkerExecutorOptions>>().Value,
        _ => throw new ArgumentOutOfRangeException(nameof(setting), setting, null),
    };

    [Fact(Timeout = TestTimeouts.E2E)]
    public async Task AirGapped_without_an_opt_in_boots()
    {
        await Task.CompletedTask;
        using var sp = Compose(AshlarDeploymentProfile.AirGapped).BuildServiceProvider();

        var act = () => ValidateOnStart(sp);

        act.Should().NotThrow();
    }

    [Fact(Timeout = TestTimeouts.E2E)]
    public async Task AirGapped_ollama_com_catalog_defaults_off_and_an_explicit_setting_is_honoured()
    {
        await Task.CompletedTask;
        using (var sp = Compose(AshlarDeploymentProfile.AirGapped).BuildServiceProvider())
        {
            sp.GetRequiredService<IOptions<OllamaRemoteLibraryCatalogOptions>>().Value.Enabled
                .Should().BeFalse("the ollama.com catalog defaults to off on AirGapped");
        }

        var explicitly = Compose(AshlarDeploymentProfile.AirGapped);
        explicitly.Configure<OllamaRemoteLibraryCatalogOptions>(o => o.Enabled = true);
        using (var sp = explicitly.BuildServiceProvider())
        {
            sp.GetRequiredService<IOptions<OllamaRemoteLibraryCatalogOptions>>().Value.Enabled
                .Should().BeTrue("it is a default; an explicit setting still turns it on");
        }
    }

    [Fact(Timeout = TestTimeouts.E2E)]
    public async Task Full_is_unchanged()
    {
        using (var sp = Compose(AshlarDeploymentProfile.Full).BuildServiceProvider())
        {
            Overnight(sp).Should().BeOfType<ExecutionTarget.Remote>()
                .Which.Reason.Should().Be(OvernightReason);
            sp.GetRequiredService<IOptions<OllamaRemoteLibraryCatalogOptions>>().Value.Enabled.Should().BeTrue();
        }

        foreach (var setting in OptInSettings)
        {
            var services = Compose(AshlarDeploymentProfile.Full);
            OptIn(services, setting);
            using var sp = services.BuildServiceProvider();
            var act = () => ValidateOnStart(sp);
            act.Should().NotThrow($"{setting} is an opt-in Full allows");
        }

        var (recorded, recorder) = ComposeRecording(AshlarDeploymentProfile.Full, "ollama");
        using (recorded)
        {
            var factory = recorded.GetRequiredService<IProviderFactory>();
            var act = () => factory.ExecuteVisionAsync("any", "system", "user", [1, 2, 3], new object());
            await act.Should().ThrowAsync<CorePorts.ModelUnavailableException>();
            recorder.Tried.Should().Equal("ollama", "openai", "azure");
        }
    }

    [Fact(Timeout = TestTimeouts.E2E)]
    public async Task SecureWorkstation_MCP_over_HTTP_fails_boot()
    {
        await Task.CompletedTask;
        var services = Compose(AshlarDeploymentProfile.SecureWorkstation);
        services.AddAshlarMcpServer(McpEnabled()).WithHttpTransport();
        using var sp = services.BuildServiceProvider();

        var act = () => ValidateOnStart(sp);

        act.Should().Throw<OptionsValidationException>()
            .WithMessage("*MCP over HTTP*SecureWorkstation*");
    }

    [Fact(Timeout = TestTimeouts.E2E)]
    public async Task SecureWorkstation_MCP_over_stdio_boots()
    {
        await Task.CompletedTask;
        var services = Compose(AshlarDeploymentProfile.SecureWorkstation);
        services.AddAshlarMcpServer(McpEnabled()).WithStdioServerTransport();
        using var sp = services.BuildServiceProvider();

        var act = () => ValidateOnStart(sp);

        act.Should().NotThrow("a local IDE spawns the stdio server; only MCP over HTTP is inbound");
    }

    [Fact(Timeout = TestTimeouts.E2E)]
    public async Task A_composition_after_AirGapped_was_noted_registers_AirGapped_and_refuses_the_opt_ins()
    {
        await Task.CompletedTask;
        using (Compose(AshlarDeploymentProfile.AirGapped).BuildServiceProvider())
        {
        }

        // A separate, later, weaker composition in the same process (default D5).
        var services = Compose(AshlarDeploymentProfile.Full);
        OptIn(services, "Ashlar:RunPod:EnablePeerNetworkRouting");
        using var sp = services.BuildServiceProvider();

        sp.GetRequiredService<ResolvedDeploymentProfile>().IsAirGapped.Should().BeTrue();
        var act = () => ValidateOnStart(sp);
        act.Should().Throw<OptionsValidationException>().WithMessage("*AirGapped*");
        // The validator also refuses at the first resolution of the options, outside a host start.
        var resolve = () => sp.GetRequiredService<IOptions<RunPodBrickConfig>>().Value;
        resolve.Should().Throw<OptionsValidationException>().WithMessage("*EnablePeerNetworkRouting*AirGapped*");
    }

    [Theory(Timeout = TestTimeouts.E2E)]
    [InlineData(AshlarDeploymentProfile.AirGapped, "urls", "http://0.0.0.0:5000")]
    [InlineData(AshlarDeploymentProfile.AirGapped, "urls", "http://localhost:5000;http://+:80")]
    [InlineData(AshlarDeploymentProfile.AirGapped, "http_ports", "8080")]
    [InlineData(AshlarDeploymentProfile.AirGapped, "https_ports", "8443")]
    [InlineData(AshlarDeploymentProfile.AirGapped, "Kestrel:Endpoints:Http:Url", "http://192.168.1.5:5000")]
    [InlineData(AshlarDeploymentProfile.SecureWorkstation, "urls", "http://[::]:5000")]
    [InlineData(AshlarDeploymentProfile.SecureWorkstation, "urls", "http://ashlar.lan:5000")]
    [InlineData(AshlarDeploymentProfile.SecureWorkstation, "http_ports", "8080")]
    public async Task A_non_loopback_listener_fails_boot_on_AirGapped_and_SecureWorkstation(
        AshlarDeploymentProfile profile, string key, string value)
    {
        await Task.CompletedTask;
        using var sp = Compose(profile).BuildServiceProvider();
        var configuration = Configuration((key, value));

        var violation = LoopbackListenerPolicy.Violation(
            sp.GetRequiredService<ResolvedDeploymentProfile>(),
            LoopbackListenerPolicy.ConfiguredAddresses(configuration),
            "Ashlar.API");

        violation.Should().NotBeNull().And.Contain("not loopback")
            .And.Contain(profile == AshlarDeploymentProfile.AirGapped ? "AirGapped" : "SecureWorkstation");
    }

    [Theory(Timeout = TestTimeouts.E2E)]
    [InlineData(AshlarDeploymentProfile.AirGapped, "http://localhost:5000;http://127.0.0.1:5001;https://[::1]:5002")]
    [InlineData(AshlarDeploymentProfile.SecureWorkstation, "http://unix:/run/ashlar.sock")]
    [InlineData(AshlarDeploymentProfile.Full, "http://0.0.0.0:5000")]
    public async Task A_loopback_listener_boots_and_Full_is_unchanged(AshlarDeploymentProfile profile, string urls)
    {
        await Task.CompletedTask;
        using var sp = Compose(profile).BuildServiceProvider();
        // http_ports binds every interface, but Kestrel ignores it once urls is set.
        var configuration = Configuration(("urls", urls), ("http_ports", "8080"));

        LoopbackListenerPolicy.Violation(
                sp.GetRequiredService<ResolvedDeploymentProfile>(),
                LoopbackListenerPolicy.ConfiguredAddresses(configuration),
                "Ashlar.API")
            .Should().BeNull();
    }

    [Theory]
    [InlineData("http://localhost:5000", true)]
    [InlineData("https://LOCALHOST", true)]
    [InlineData("http://127.0.0.1:5000", true)]
    [InlineData("http://127.8.9.10:5000/base", true)]
    [InlineData("http://[::1]:5000", true)]
    [InlineData("http://unix:/tmp/ashlar.sock", true)]
    [InlineData("http://pipe:/ashlar", true)]
    [InlineData("http://*:5000", false)]
    [InlineData("http://+:5000", false)]
    [InlineData("http://0.0.0.0:5000", false)]
    [InlineData("http://[::]:5000", false)]
    [InlineData("http://10.0.0.1:5000", false)]
    [InlineData("http://localhost.example:5000", false)]
    [InlineData("localhost:5000", false)]
    [InlineData("http://[::1:5000", false)]
    [InlineData("", false)]
    public void Loopback_is_localhost_a_loopback_address_a_unix_socket_or_a_pipe(string address, bool loopback) =>
        LoopbackListenerPolicy.IsLoopback(address).Should().Be(loopback);

    private static IConfiguration Configuration(params (string Key, string Value)[] pairs) => new ConfigurationBuilder()
        .AddInMemoryCollection(pairs.ToDictionary(p => p.Key, p => (string?)p.Value))
        .Build();

    private static IConfiguration McpEnabled() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"{AshlarMcpServerOptions.SectionPath}:Enabled"] = "true",
        })
        .Build();

    private static (ServiceProvider Provider, RecordingProviderFactory Recorder) ComposeRecording(
        AshlarDeploymentProfile profile,
        string resolved)
    {
        var recorder = new RecordingProviderFactory();
        var services = Compose(
            profile,
            before: s => s.AddSingleton<ILoadPolicy>(new FixedLoadPolicy(resolved)),
            configure: o =>
            {
                o.UseAdaptiveLoadBalancing = true;
                o.TrustEnabled = false;
            });
        // Last wins: the adaptive factory wraps this one.
        services.AddSingleton<ProviderFactory>(recorder);
        return (services.BuildServiceProvider(), recorder);
    }

    private sealed class FixedLoadPolicy(string provider) : ILoadPolicy
    {
        public LoadPreference GetPreference() => LoadPreference.Auto;

        public string? ResolveProvider(IProviderFactory providerFactory) => provider;
    }

    /// <summary>Every provider is available, every call is recorded and fails, so the factory tries them all.</summary>
    private sealed class RecordingProviderFactory() : ProviderFactory(NullLogger<ProviderFactory>.Instance), IProviderFactory
    {
        private readonly List<string> _tried = [];

        public IReadOnlyList<string> Tried
        {
            get
            {
                lock (_tried)
                    return _tried.ToArray();
            }
        }

        bool CorePorts.IProviderFactory.IsProviderAvailable(string provider) => true;

        Task<string> CorePorts.IProviderFactory.ExecuteLLMAsync(
            string provider, string systemPrompt, string userPrompt, object config, CancellationToken cancellationToken) =>
            Record(provider);

        Task<string> CorePorts.IProviderFactory.ExecuteVisionMultiFrameAsync(
            string provider, string systemPrompt, string userPrompt, IReadOnlyList<byte[]> frameBytes, object config,
            CancellationToken cancellationToken) =>
            Record(provider);

        Task<string> CorePorts.IProviderFactory.ExecuteVisionAsync(
            string provider, string systemPrompt, string userPrompt, byte[] imageBytes, object config,
            CancellationToken cancellationToken) =>
            Record(provider);

        private Task<string> Record(string provider)
        {
            lock (_tried)
                _tried.Add(provider);
            return Task.FromException<string>(new InvalidOperationException($"recorded {provider}"));
        }
    }
}
