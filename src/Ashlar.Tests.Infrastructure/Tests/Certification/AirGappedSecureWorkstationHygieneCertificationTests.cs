using Ashlar.Abstractions;
using Ashlar.AI.Pipeline;
using Ashlar.Core.Application.Execution.Ports;
using Ashlar.Core.Application.Execution.Routing;
using Ashlar.Hosting;
using Ashlar.Infrastructure;
using Ashlar.Infrastructure.Execution;
using Ashlar.Infrastructure.Execution.LoadPolicy;
using Ashlar.Infrastructure.MeshLab;
using Ashlar.Infrastructure.ModelArtifacts;
using Ashlar.Mcp.Server;
using Ashlar.Tests.Infrastructure.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using Xunit;
using InfraProviderFactory = Ashlar.Infrastructure.Execution.IProviderFactory;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>
/// SPEC-007 PR 4.10. AirGapped keeps remote execution local, does not escalate providers to OpenAI or Azure,
/// refuses the opt-in network paths at boot, and disables the ollama.com catalog. SecureWorkstation refuses
/// MCP over HTTP and still boots stdio. Both profiles refuse a non-loopback inbound listener. Full is unchanged.
/// Responses on inbound connections are not mediated here; that is PR 5.
/// </summary>
[Trait("Category", "Certification")]
[Collection("EnvironmentVariables")]
public sealed class AirGappedSecureWorkstationHygieneCertificationTests : IDisposable
{
    private readonly EgressProcessStateScope _egressState = new();

    /// <inheritdoc />
    public void Dispose() => _egressState.Dispose();

    [Fact]
    public void Overnight_routing_stays_local_on_air_gapped_and_can_select_remote_on_full()
    {
        const string reason =
            "AirGapped: remote execution unavailable; running locally (Overnight/background job forces remote execution.)";

        using (var airGapped = Compose(AshlarDeploymentProfile.AirGapped))
        {
            var target = airGapped.GetRequiredService<ICapabilityRouter>()
                .ResolveExecutionTarget(new JobRequirements { IsOvernightOrBackground = true });

            target.Should().BeOfType<ExecutionTarget.Local>().Which.Reason.Should().Be(reason);
        }

        using (var full = Compose(AshlarDeploymentProfile.Full))
        {
            var target = full.GetRequiredService<ICapabilityRouter>()
                .ResolveExecutionTarget(new JobRequirements { IsOvernightOrBackground = true });

            target.Should().BeOfType<ExecutionTarget.Remote>();
        }
    }

    [Fact]
    public void Peer_network_only_on_air_gapped_is_refused()
    {
        using var airGapped = Compose(AshlarDeploymentProfile.AirGapped);
        var router = airGapped.GetRequiredService<ICapabilityRouter>();

        var act = () => router.ResolveExecutionTarget(new JobRequirements
        {
            RemoteExecutionPreference = RemoteExecutionPreference.PeerNetworkOnly,
        });

        act.Should().Throw<InvalidOperationException>().WithMessage("*AirGapped*peer-network*");
    }

    [Fact]
    public async Task Vision_on_air_gapped_never_tries_openai_or_azure_and_full_still_does()
    {
        using (var airGapped = Compose(AshlarDeploymentProfile.AirGapped))
        {
            var calls = await VisionCalls(airGapped, "ollama");
            calls.Should().NotContain("openai");
            calls.Should().NotContain("azure");
            calls.Should().Contain("ollama");
        }

        using (var full = Compose(AshlarDeploymentProfile.Full))
        {
            var calls = await VisionCalls(full, "ollama");
            calls.Should().Contain("openai");
            calls.Should().Contain("azure");
        }
    }

    [Fact]
    public async Task Llm_on_air_gapped_does_not_escalate_to_openai_or_azure()
    {
        using (var airGapped = Compose(AshlarDeploymentProfile.AirGapped))
        {
            var local = await LlmCalls(airGapped, "ollama");
            local.Should().Equal("ollama");

            var cloudResolved = await LlmCalls(airGapped, "openai");
            cloudResolved.Should().BeEmpty();
        }

        using (var full = Compose(AshlarDeploymentProfile.Full))
        {
            var calls = await LlmCalls(full, "ollama");
            calls.Should().Contain("openai");
            calls.Should().Contain("azure");
        }
    }

    [Fact]
    public void Air_gapped_boot_allows_the_opt_in_paths_while_they_stay_off()
    {
        using var airGapped = Compose(AshlarDeploymentProfile.AirGapped);

        airGapped.GetRequiredService<IOptions<RunPodBrickConfig>>().Value.EnablePeerNetworkRouting.Should().BeFalse();
        airGapped.GetRequiredService<IOptions<BrickHostOptions>>().Value.RemoteCatalogBaseUrls.Should().BeEmpty();
        airGapped.GetRequiredService<IOptions<MeshLabWorkerExecutorOptions>>().Value.Enabled.Should().BeFalse();
        airGapped.GetRequiredService<IOptions<MeaiPipelineOptions>>().Value.Bedrock.Enabled.Should().BeFalse();
    }

    [Fact]
    public void Opt_in_remote_paths_fail_boot_on_air_gapped_and_stay_allowed_on_full()
    {
        ExpectRefusal<RunPodBrickConfig>(
            AshlarDeploymentProfile.AirGapped,
            services => services.Configure<RunPodBrickConfig>(options => options.EnablePeerNetworkRouting = true),
            "EnablePeerNetworkRouting");
        ExpectAllowed<RunPodBrickConfig>(
            AshlarDeploymentProfile.Full,
            services => services.Configure<RunPodBrickConfig>(options => options.EnablePeerNetworkRouting = true))
            .EnablePeerNetworkRouting.Should().BeTrue();

        ExpectRefusal<BrickHostOptions>(
            AshlarDeploymentProfile.AirGapped,
            services => services.Configure<BrickHostOptions>(options =>
                options.RemoteCatalogBaseUrls = new[] { "https://catalog.example" }),
            "RemoteCatalogBaseUrls");
        ExpectAllowed<BrickHostOptions>(
            AshlarDeploymentProfile.Full,
            services => services.Configure<BrickHostOptions>(options =>
                options.RemoteCatalogBaseUrls = new[] { "https://catalog.example" }))
            .RemoteCatalogBaseUrls.Should().Contain("https://catalog.example");

        ExpectRefusal<MeshLabWorkerExecutorOptions>(
            AshlarDeploymentProfile.AirGapped,
            services => services.Configure<MeshLabWorkerExecutorOptions>(options => options.Enabled = true),
            "WorkerExecutor");
        ExpectAllowed<MeshLabWorkerExecutorOptions>(
            AshlarDeploymentProfile.Full,
            services => services.Configure<MeshLabWorkerExecutorOptions>(options => options.Enabled = true))
            .Enabled.Should().BeTrue();

        ExpectRefusal<MeaiPipelineOptions>(
            AshlarDeploymentProfile.AirGapped,
            services => services.Configure<MeaiPipelineOptions>(options => options.Bedrock.Enabled = true),
            "Bedrock");
        ExpectAllowed<MeaiPipelineOptions>(
            AshlarDeploymentProfile.Full,
            services => services.Configure<MeaiPipelineOptions>(options => options.Bedrock.Enabled = true))
            .Bedrock.Enabled.Should().BeTrue();
    }

    [Fact]
    public void Ollama_com_catalog_is_disabled_on_air_gapped_and_stays_enabled_on_full()
    {
        using (var airGapped = Compose(AshlarDeploymentProfile.AirGapped))
        {
            airGapped.GetRequiredService<IOptions<OllamaRemoteLibraryCatalogOptions>>().Value.Enabled.Should().BeFalse();
        }

        using (var full = Compose(AshlarDeploymentProfile.Full))
        {
            full.GetRequiredService<IOptions<OllamaRemoteLibraryCatalogOptions>>().Value.Enabled.Should().BeTrue();
        }
    }

    [Fact]
    public void Mcp_over_http_fails_boot_on_secure_workstation_and_stdio_boots()
    {
        using (var http = Compose(AshlarDeploymentProfile.SecureWorkstation, services =>
            services.AddAshlarMcpServer(McpEnabled()).WithAshlarHttpTransport()))
        {
            var act = () => http.GetRequiredService<IOptions<AshlarMcpServerOptions>>().Value;
            act.Should().Throw<OptionsValidationException>().WithMessage("*SecureWorkstation*").WithMessage("*HTTP*");
        }

        using (var stdio = Compose(AshlarDeploymentProfile.SecureWorkstation, services =>
            services.AddAshlarMcpServer(McpEnabled()).WithStdioServerTransport()))
        {
            stdio.GetRequiredService<IOptions<AshlarMcpServerOptions>>().Value.Enabled.Should().BeTrue();
        }

        using (var airGapped = Compose(AshlarDeploymentProfile.AirGapped, services =>
            services.AddAshlarMcpServer(McpEnabled())))
        {
            var act = () => airGapped.GetRequiredService<IOptions<AshlarMcpServerOptions>>().Value;
            act.Should().Throw<OptionsValidationException>().WithMessage("*AirGapped*");
        }
    }

    [Fact]
    public void Non_loopback_listeners_fail_boot_on_air_gapped_and_secure_workstation_and_loopback_boots()
    {
        var mixed = AshlarInboundListenerPolicy.CollectEndpoints(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Kestrel:Endpoints:Http:Url"] = "http://0.0.0.0:8080",
            }).Build(),
            "http://127.0.0.1:5000;http://+:8080");
        mixed.Should().Contain("http://0.0.0.0:8080").And.Contain("http://+:8080");

        ExpectListenerRefusal(AshlarDeploymentProfile.AirGapped, mixed);
        ExpectListenerRefusal(AshlarDeploymentProfile.SecureWorkstation, new[] { "http://10.1.2.3:8080" });
        ExpectListenerRefusal(AshlarDeploymentProfile.AirGapped, new[] { "http://*:8080" });

        ExpectListenerAllowed(AshlarDeploymentProfile.AirGapped, new[] { "http://127.0.0.1:5000" });
        ExpectListenerAllowed(AshlarDeploymentProfile.SecureWorkstation, new[] { "http://localhost:5000" });
        ExpectListenerAllowed(AshlarDeploymentProfile.AirGapped, new[] { "http://[::1]:5000" });
        ExpectListenerAllowed(AshlarDeploymentProfile.SecureWorkstation, Array.Empty<string>());
        ExpectListenerAllowed(AshlarDeploymentProfile.Full, new[] { "http://+:8080" });

        using var airGapped = Compose(AshlarDeploymentProfile.AirGapped);
        var profile = airGapped.GetRequiredService<IOptions<AshlarResolvedDeploymentProfileOptions>>().Value;
        AshlarInboundListenerPolicy.IsLoopbackEndpoint("0.0.0.0").Should().BeFalse();
        AshlarInboundListenerPolicy.Refusal(profile, new[] { "0.0.0.0" })
            .Should().NotBeNull().And.Contain("loopback");
    }

    [Theory]
    [InlineData("::1", true)]
    [InlineData("::ffff:127.0.0.1", true)]
    [InlineData("[::1]", true)]
    [InlineData("http://[::1]:5000", true)]
    [InlineData("localhost", true)]
    [InlineData("127.0.0.2", true)]
    [InlineData("::", false)]
    [InlineData("http://[::]:5000", false)]
    [InlineData("unix:///tmp/ashlar.sock", false)]
    [InlineData("unix://localhost/tmp/ashlar.sock", false)]
    [InlineData("npipe://localhost/pipe/ashlar", false)]
    [InlineData("npipe://./pipe/ashlar", false)]
    [InlineData("https://example.com", false)]
    public void Loopback_spellings_are_classified(string endpoint, bool expected)
        => AshlarInboundListenerPolicy.IsLoopbackEndpoint(endpoint).Should().Be(expected);

    [Fact]
    public void Port_only_configuration_is_non_loopback_but_urls_take_precedence()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["http_ports"] = "8080; 8081",
            ["https_ports"] = "8443",
        }).Build();
        var endpoints = AshlarInboundListenerPolicy.CollectEndpoints(config, null);
        endpoints.Should().Equal("http://*:8080", "http://*:8081", "https://*:8443");
        ExpectListenerRefusal(AshlarDeploymentProfile.AirGapped, endpoints);
        AshlarInboundListenerPolicy.CollectEndpoints(config, "http://localhost:5000")
            .Should().Equal("http://localhost:5000");
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("azure")]
    public async Task Multi_frame_vision_on_AirGapped_refuses_a_cloud_resolve(string resolved)
    {
        foreach (var profile in new[] { AshlarDeploymentProfile.AirGapped, AshlarDeploymentProfile.Full })
        {
            using var provider = Compose(profile);
            var fake = new RecordingFactory();
            var factory = new AdaptiveProviderFactory(fake, new FixedLoadPolicy(resolved), deploymentProfile:
                provider.GetRequiredService<IOptions<AshlarResolvedDeploymentProfileOptions>>());
            var act = () => factory.ExecuteVisionMultiFrameAsync("ignored", "system", "user", new[] { new byte[] { 1 } }, new object());
            await act.Should().ThrowAsync<ModelUnavailableException>();
            if (profile == AshlarDeploymentProfile.AirGapped)
                fake.MultiFrame.Should().BeEmpty("AirGapped must refuse before sending frames to the provider");
            else
                fake.MultiFrame.Should().Equal(resolved);
        }
    }

    [Theory]
    [InlineData("ollama")]
    [InlineData("local")]
    public async Task Multi_frame_vision_on_AirGapped_keeps_local_resolution(string resolved)
    {
        using var provider = Compose(AshlarDeploymentProfile.AirGapped);
        var fake = new RecordingFactory();
        var factory = new AdaptiveProviderFactory(fake, new FixedLoadPolicy(resolved), deploymentProfile:
            provider.GetRequiredService<IOptions<AshlarResolvedDeploymentProfileOptions>>());
        var act = () => factory.ExecuteVisionMultiFrameAsync("ignored", "system", "user", new[] { new byte[] { 1 } }, new object());
        await act.Should().ThrowAsync<ModelUnavailableException>();
        fake.MultiFrame.Should().Equal(resolved);
    }

    [Fact]
    public void Host_configuration_after_AddAshlar_cannot_weaken_the_noted_profile()
    {
        using var provider = Compose(AshlarDeploymentProfile.AirGapped, services =>
            services.Configure<AshlarResolvedDeploymentProfileOptions>(o => o.Profile = "full"));
        provider.GetRequiredService<IOptions<AshlarResolvedDeploymentProfileOptions>>().Value.IsAirGapped.Should().BeTrue();
        provider.GetRequiredService<ICapabilityRouter>()
            .ResolveExecutionTarget(new JobRequirements { IsOvernightOrBackground = true })
            .Should().BeOfType<ExecutionTarget.Local>();
    }

    [Fact]
    public void A_later_weaker_AddAshlar_keeps_the_AirGapped_routing()
    {
        EgressProcessStateScope.Reset();
        using var airgapped = Compose(AshlarDeploymentProfile.AirGapped);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAshlar(o => o.DeploymentProfile = AshlarDeploymentProfile.Full);
        using var full = services.BuildServiceProvider();
        full.GetRequiredService<IOptions<AshlarResolvedDeploymentProfileOptions>>().Value.IsAirGapped.Should().BeTrue();
        full.GetRequiredService<ICapabilityRouter>()
            .ResolveExecutionTarget(new JobRequirements { IsOvernightOrBackground = true })
            .Should().BeOfType<ExecutionTarget.Local>();
    }

    [Fact]
    public void A_container_composed_Full_before_AirGapped_was_noted_keeps_Full()
    {
        using var full = Compose(AshlarDeploymentProfile.Full);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAshlar(o => o.DeploymentProfile = AshlarDeploymentProfile.AirGapped);
        using var airgapped = services.BuildServiceProvider();
        full.GetRequiredService<IOptions<AshlarResolvedDeploymentProfileOptions>>().Value.IsAirGapped.Should().BeFalse();
        airgapped.GetRequiredService<IOptions<AshlarResolvedDeploymentProfileOptions>>().Value.IsAirGapped.Should().BeTrue();
    }

    [Fact]
    public void Mcp_direct_HTTP_transport_also_fails_boot_on_SecureWorkstation()
    {
        using var provider = Compose(AshlarDeploymentProfile.SecureWorkstation, services =>
            services.AddAshlarMcpServer(McpEnabled()).WithHttpTransport());
        var act = () => provider.GetRequiredService<IOptions<AshlarMcpServerOptions>>().Value;
        act.Should().Throw<OptionsValidationException>().WithMessage("*SecureWorkstation*HTTP*");
    }

    [Fact]
    public void The_legacy_MCP_validator_constructor_keeps_its_marker_only_contract()
    {
        using var provider = Compose(AshlarDeploymentProfile.SecureWorkstation);
        var options = new AshlarMcpServerOptions { Enabled = true, ServerName = "legacy-stdio" };
        new ValidateAshlarMcpServerOptions().Validate(null, options).Succeeded.Should().BeTrue();
        new ValidateAshlarMcpServerOptions(new[] { new AshlarMcpHttpTransportMarker() })
            .Validate(null, options).Failed.Should().BeTrue();
    }

    private static ServiceProvider Compose(AshlarDeploymentProfile profile, Action<IServiceCollection>? after = null)
    {
        EgressProcessStateScope.Reset();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAshlar(options => options.DeploymentProfile = profile);
        after?.Invoke(services);
        return services.BuildServiceProvider();
    }

    private static void ExpectRefusal<TOptions>(
        AshlarDeploymentProfile profile,
        Action<IServiceCollection> configure,
        string messageFragment)
        where TOptions : class
    {
        using var provider = Compose(profile, configure);
        var act = () => provider.GetRequiredService<IOptions<TOptions>>().Value;
        act.Should().Throw<OptionsValidationException>().WithMessage("*" + messageFragment + "*");
    }

    private static TOptions ExpectAllowed<TOptions>(
        AshlarDeploymentProfile profile,
        Action<IServiceCollection> configure)
        where TOptions : class
    {
        using var provider = Compose(profile, configure);
        return provider.GetRequiredService<IOptions<TOptions>>().Value;
    }

    private static void ExpectListenerRefusal(AshlarDeploymentProfile profile, IReadOnlyList<string> endpoints)
    {
        using var provider = Compose(profile, services => services.AddAshlarInboundListenerValidation(endpoints));
        var act = () => provider.GetRequiredService<IOptions<AshlarInboundListenerOptions>>().Value;
        act.Should().Throw<OptionsValidationException>().WithMessage("*loopback*");
    }

    private static void ExpectListenerAllowed(AshlarDeploymentProfile profile, IReadOnlyList<string> endpoints)
    {
        using var provider = Compose(profile, services => services.AddAshlarInboundListenerValidation(endpoints));
        provider.GetRequiredService<IOptions<AshlarInboundListenerOptions>>().Value.Endpoints
            .Should().BeEquivalentTo(endpoints);
    }

    private static async Task<IReadOnlyList<string>> VisionCalls(ServiceProvider provider, string resolved)
    {
        var fake = new RecordingFactory();
        var factory = new AdaptiveProviderFactory(
            fake,
            new FixedLoadPolicy(resolved),
            logger: null,
            provider.GetRequiredService<IOptions<AshlarResolvedDeploymentProfileOptions>>());
        var act = async () => await factory.ExecuteVisionAsync("ignored", "system", "user", new byte[] { 1 }, new object());
        await act.Should().ThrowAsync<ModelUnavailableException>();
        return fake.Vision;
    }

    private static async Task<IReadOnlyList<string>> LlmCalls(ServiceProvider provider, string resolved)
    {
        var fake = new RecordingFactory();
        var factory = new AdaptiveProviderFactory(
            fake,
            new FixedLoadPolicy(resolved),
            logger: null,
            provider.GetRequiredService<IOptions<AshlarResolvedDeploymentProfileOptions>>());
        var act = async () => await factory.ExecuteLLMAsync("ignored", "system", "user", new object());
        await act.Should().ThrowAsync<ModelUnavailableException>();
        return fake.Llm;
    }

    private static IConfiguration McpEnabled() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"{AshlarMcpServerOptions.SectionPath}:Enabled"] = "true",
            [$"{AshlarMcpServerOptions.SectionPath}:ServerName"] = "ashlar-hygiene",
        }).Build();

    private sealed class FixedLoadPolicy : ILoadPolicy
    {
        private readonly string _resolved;

        public FixedLoadPolicy(string resolved) => _resolved = resolved;

        public LoadPreference GetPreference() => LoadPreference.Edge;

        public string? ResolveProvider(InfraProviderFactory providerFactory) => _resolved;
    }

    private sealed class RecordingFactory : InfraProviderFactory
    {
        public List<string> Llm { get; } = new();

        public List<string> Vision { get; } = new();

        public List<string> MultiFrame { get; } = new();

        public bool IsProviderAvailable(string provider) => true;

        public Task<string> ExecuteLLMAsync(
            string provider, string systemPrompt, string userPrompt, object config, CancellationToken cancellationToken = default)
        {
            Llm.Add(provider);
            throw new InvalidOperationException(provider);
        }

        public Task<string> ExecuteVisionAsync(
            string provider, string systemPrompt, string userPrompt, byte[] imageBytes, object config,
            CancellationToken cancellationToken = default)
        {
            Vision.Add(provider);
            throw new InvalidOperationException(provider);
        }

        public Task<string> ExecuteVisionMultiFrameAsync(
            string provider, string systemPrompt, string userPrompt, IReadOnlyList<byte[]> frameBytes, object config,
            CancellationToken cancellationToken = default)
        {
            MultiFrame.Add(provider);
            throw new InvalidOperationException(provider);
        }

        public Task<string> ExecuteVideoAsync(
            string systemPrompt, string userPrompt, IReadOnlyList<byte[]> frameBytes, object config,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("video");

        public Task EnsureOllamaReachableAsync(bool requireVisionModel, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
