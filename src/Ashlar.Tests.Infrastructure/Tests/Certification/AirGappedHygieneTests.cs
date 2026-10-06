using Ashlar.AI.Pipeline;
using Ashlar.Core.Application.Execution.Routing;
using Ashlar.Core.Application.NodeCapabilityRuntime.Ports;
using Ashlar.Hosting;
using Ashlar.Infrastructure.Deployment;
using Ashlar.Infrastructure.Execution;
using Ashlar.Infrastructure.Execution.LoadPolicy;
using Ashlar.Infrastructure.Execution.Routing;
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

    /// <summary>Every remote reason the router knows, and a preferred peer, stay local on AirGapped (design §2.7).</summary>
    [Theory(Timeout = TestTimeouts.E2E)]
    [InlineData("overnight", RemoteExecutionPreference.UseSystemDefault, "Overnight/background job forces remote execution.")]
    [InlineData("vram", RemoteExecutionPreference.UseSystemDefault, "Insufficient VRAM: available=0, required=1.")]
    [InlineData("compute", RemoteExecutionPreference.UseSystemDefault, "Insufficient compute class: available=None, required=High.")]
    [InlineData("queue", RemoteExecutionPreference.UseSystemDefault, "Local queue depth threshold exceeded: depth=1000, threshold=")]
    [InlineData("overnight", RemoteExecutionPreference.PreferPeerNetwork, "Overnight/background job forces remote execution.")]
    [InlineData("overnight", RemoteExecutionPreference.CloudOnly, "Overnight/background job forces remote execution.")]
    public async Task AirGapped_keeps_every_remote_reason_local(string why, RemoteExecutionPreference preference, string reason)
    {
        await Task.CompletedTask;
        // A snapshot that satisfies everything but the one reason under test.
        var snapshot = why switch
        {
            "vram" => new FixedSnapshot(0, GpuComputeClass.Extreme, 0),
            "compute" => new FixedSnapshot(long.MaxValue, GpuComputeClass.None, 0),
            "queue" => new FixedSnapshot(long.MaxValue, GpuComputeClass.Extreme, 1000),
            _ => new FixedSnapshot(long.MaxValue, GpuComputeClass.Extreme, 0),
        };
        using var sp = Compose(AshlarDeploymentProfile.AirGapped, before: s => s.AddSingleton<INCRCapabilitySnapshot>(snapshot))
            .BuildServiceProvider();
        var requirements = new JobRequirements
        {
            IsOvernightOrBackground = why == "overnight",
            MinimumVramBytes = why == "vram" ? 1 : 0,
            ComputeClass = why == "compute" ? GpuComputeClass.High : GpuComputeClass.None,
            RemoteExecutionPreference = preference,
        };

        var target = sp.GetRequiredService<ICapabilityRouter>().ResolveExecutionTarget(requirements);

        target.Should().BeOfType<ExecutionTarget.Local>()
            .Which.Reason.Should().StartWith($"{NcrCapabilityRouter.AirGappedLocalReasonPrefix} (").And.Contain(reason);
    }

    /// <summary>
    /// A ready-made <c>IOptions</c> instance that host code registers after <c>AddAshlar</c> is outside the options
    /// pipeline, so the validator never sees it (a known limit, pinned here); the routing rule holds on its own.
    /// </summary>
    [Fact(Timeout = TestTimeouts.E2E)]
    public async Task AirGapped_routes_locally_even_when_peer_routing_is_forced_past_the_validator()
    {
        await Task.CompletedTask;
        var services = Compose(AshlarDeploymentProfile.AirGapped);
        services.AddSingleton(Options.Create(new RunPodBrickConfig { EnablePeerNetworkRouting = true, PreferPeerNetworkOverCloud = true }));
        using var sp = services.BuildServiceProvider();

        var validate = () => ValidateOnStart(sp);
        validate.Should().NotThrow("the options pipeline validates the instance it builds, not a ready-made one host code registered (known limit)");
        Overnight(sp, RemoteExecutionPreference.PreferPeerNetwork).Should().BeOfType<ExecutionTarget.Local>(
                "the router's AirGapped rule does not depend on the validator")
            .Which.Reason.Should().StartWith(NcrCapabilityRouter.AirGappedLocalReasonPrefix);
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

        // A blank entry (the mesh ignores it) and a null list (a host's) are not opt-ins and do not fault the validator.
        foreach (var list in new IReadOnlyList<string>?[] { new[] { "  " }, null })
        {
            var services = Compose(AshlarDeploymentProfile.AirGapped);
            services.Configure<BrickHostOptions>(o => o.RemoteCatalogBaseUrls = list!);
            using var provider = services.BuildServiceProvider();
            var boot = () => ValidateOnStart(provider);
            boot.Should().NotThrow();
        }
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

        // A host's setting registered before AddAshlar also wins: the default is inserted first in the collection.
        using (var sp = Compose(AshlarDeploymentProfile.AirGapped,
                   before: s => s.Configure<OllamaRemoteLibraryCatalogOptions>(o => o.Enabled = true)).BuildServiceProvider())
        {
            sp.GetRequiredService<IOptions<OllamaRemoteLibraryCatalogOptions>>().Value.Enabled
                .Should().BeTrue("the default runs before every binding, whichever side of AddAshlar the binding is on");
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
        Overnight(sp).Should().BeOfType<ExecutionTarget.Local>().Which.Reason.Should().StartWith(NcrCapabilityRouter.AirGappedLocalReasonPrefix);
        // The carry-in for 4.11: the later AddAshlar still selects its own (Full) module set; only the value is AirGapped.
        sp.GetService<Ashlar.Transport.Grpc.IGrpcChannelFactory>().Should().NotBeNull(
            "a later AddAshlar still selects its own module set (4.6 carry-in, 4.11 territory); AirGapped itself omits runtime transport");
        var act = () => ValidateOnStart(sp);
        act.Should().Throw<OptionsValidationException>().WithMessage("*AirGapped*");
        // The validator also refuses at the first resolution of the options, outside a host start.
        var resolve = () => sp.GetRequiredService<IOptions<RunPodBrickConfig>>().Value;
        resolve.Should().Throw<OptionsValidationException>().WithMessage("*EnablePeerNetworkRouting*AirGapped*");
    }

    /// <summary>The value is captured when the container is composed (deviation 4): the reverse order keeps Full.</summary>
    [Fact(Timeout = TestTimeouts.E2E)]
    public async Task A_container_composed_Full_before_AirGapped_was_noted_keeps_Full()
    {
        await Task.CompletedTask;
        using var full = Compose(AshlarDeploymentProfile.Full).BuildServiceProvider();
        using var airGapped = Compose(AshlarDeploymentProfile.AirGapped).BuildServiceProvider();

        full.GetRequiredService<ResolvedDeploymentProfile>().IsAirGapped.Should().BeFalse("the value is the profile noted when that container was composed");
        Overnight(full).Should().BeOfType<ExecutionTarget.Remote>();
        airGapped.GetRequiredService<ResolvedDeploymentProfile>().IsAirGapped.Should().BeTrue();
    }

    /// <summary>Known limit: host code can replace the value after <c>AddAshlar</c>, as it can replace any service.</summary>
    [Fact(Timeout = TestTimeouts.E2E)]
    public async Task Host_code_that_registers_its_own_profile_after_AddAshlar_replaces_the_value()
    {
        await Task.CompletedTask;
        var services = Compose(AshlarDeploymentProfile.AirGapped);
        services.AddSingleton(new ResolvedDeploymentProfile("full"));
        using var sp = services.BuildServiceProvider();

        Overnight(sp).Should().BeOfType<ExecutionTarget.Remote>(
            "last registration wins: a known limit recorded in docs/EgressInventory.md; the egress guard reads the process profile and is the backstop");
    }

    /// <summary>Defect 5 is scoped to AirGapped (design §2.7): SecureWorkstation's outbound paths are unchanged by 4.10.</summary>
    [Fact(Timeout = TestTimeouts.E2E)]
    public async Task SecureWorkstation_outbound_paths_are_unchanged()
    {
        using (var sp = Compose(AshlarDeploymentProfile.SecureWorkstation).BuildServiceProvider())
        {
            Overnight(sp).Should().BeOfType<ExecutionTarget.Remote>("SecureWorkstation reaches RunPod by default until the switch (4.11)")
                .Which.Reason.Should().Be(OvernightReason);
            sp.GetRequiredService<IOptions<OllamaRemoteLibraryCatalogOptions>>().Value.Enabled.Should().BeTrue();
        }

        foreach (var setting in OptInSettings)
        {
            var services = Compose(AshlarDeploymentProfile.SecureWorkstation);
            OptIn(services, setting);
            using var sp = services.BuildServiceProvider();
            var act = () => ValidateOnStart(sp);
            act.Should().NotThrow($"{setting}: the four validators are AirGapped-only (design §2.7)");
        }

        var (recorded, recorder) = ComposeRecording(AshlarDeploymentProfile.SecureWorkstation, "ollama");
        using (recorded)
        {
            var factory = recorded.GetRequiredService<IProviderFactory>();
            var act = () => factory.ExecuteLLMAsync("any", "system", "user", new object());
            await act.Should().ThrowAsync<CorePorts.ModelUnavailableException>();
            recorder.Tried.Should().Equal("ollama", "openai", "azure");
        }
    }

    /// <summary>
    /// The Bedrock check also runs when <c>AddAshlar</c> composes, for a process that never starts a host (the CLI):
    /// a tier already registered, by the kernel from configuration or by host code before <c>AddAshlar</c>, fails the
    /// composition. Full composes with it.
    /// </summary>
    [Fact(Timeout = TestTimeouts.E2E)]
    public async Task AirGapped_refuses_a_Bedrock_tier_already_registered_when_AddAshlar_composes()
    {
        await Task.CompletedTask;
        Action<IServiceCollection> bedrock = s => s.AddSingleton(Options.Create(new MeaiPipelineOptions { Bedrock = { Enabled = true } }));

        var airGapped = () => Compose(AshlarDeploymentProfile.AirGapped, before: bedrock);
        airGapped.Should().Throw<InvalidOperationException>().WithMessage("*Ashlar:Meai:Bedrock:Enabled*AirGapped*");

        var full = () => Compose(AshlarDeploymentProfile.Full, before: bedrock);
        full.Should().NotThrow();
    }

    [Fact(Timeout = TestTimeouts.E2E)]
    public async Task AirGapped_refuses_the_configured_Bedrock_tier_when_AddAshlar_composes()
    {
        await Task.CompletedTask;
        using var enabled = new EnvironmentVariableScope("Ashlar__Meai__Bedrock__Enabled", "true");
        using var region = new EnvironmentVariableScope("Ashlar__Meai__Bedrock__Region", "us-east-1");

        var airGapped = () => Compose(AshlarDeploymentProfile.AirGapped);
        airGapped.Should().Throw<InvalidOperationException>().WithMessage("*Ashlar:Meai:Bedrock:Enabled*AirGapped*");

        var full = () => Compose(AshlarDeploymentProfile.Full);
        full.Should().NotThrow();
    }

    /// <summary>
    /// The real listener checks run only on net10.0 (Ashlar.API and Ashlar.CLI), outside this gate, so this gate pins
    /// their call sites by reading the source: a required check goes red when either host stops making the check.
    /// </summary>
    [Fact]
    public void The_API_and_mesh_serve_call_the_listener_checks()
    {
        var root = TestPaths.FindRepoRoot();
        var program = File.ReadAllText(Path.Combine(root, "application", "src", "Ashlar.API", "Program.cs"));
        var built = program.IndexOf("var app = builder.Build();", StringComparison.Ordinal);
        var check = program.IndexOf("LoopbackListenerPolicy.Violation(", StringComparison.Ordinal);
        var refusal = program.IndexOf("throw new InvalidOperationException(listenerViolation);", StringComparison.Ordinal);
        built.Should().BePositive();
        check.Should().BeGreaterThan(built, "the pre-bind check reads the built app's configuration");
        refusal.Should().BeGreaterThan(check, "a violation refuses the start");
        program.Should().Contain("builder.Services.AddHostedService<LoopbackListenerVerifier>();");

        var verifier = File.ReadAllText(Path.Combine(root, "application", "src", "Ashlar.API", "Security", "LoopbackListenerVerifier.cs"));
        verifier.Should().Contain("LoopbackListenerPolicy.Violation(").And.Contain("throw new InvalidOperationException(violation)");

        var mesh = File.ReadAllText(Path.Combine(root, "application", "src", "Ashlar.CLI", "Commands", "BackgroundAgent", "MeshServeService.cs"));
        var profileCheck = mesh.IndexOf("var profileError = ProfileError(_settings, _deploymentProfile);", StringComparison.Ordinal);
        var buildApp = mesh.IndexOf("app = BuildApp();", StringComparison.Ordinal);
        profileCheck.Should().BePositive();
        buildApp.Should().BeGreaterThan(profileCheck, "the refusal comes before anything binds");
        mesh[profileCheck..buildApp].Should().Contain("return;", "a refused profile returns without building the app");
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
    [InlineData("http://api.localhost:5000", false)]
    [InlineData("http://localhost.:5000", false)]
    [InlineData("http://ashlar.internal:5000", false)]
    [InlineData("https://0.0.0.0:0", false)]
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

    private sealed class FixedSnapshot(long vram, GpuComputeClass compute, int queue) : INCRCapabilitySnapshot
    {
        public long AvailableVramBytes => vram;

        public GpuComputeClass ComputeClass => compute;

        public int CurrentQueueDepth => queue;

        public DateTimeOffset CapturedAt => DateTimeOffset.UtcNow;
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
