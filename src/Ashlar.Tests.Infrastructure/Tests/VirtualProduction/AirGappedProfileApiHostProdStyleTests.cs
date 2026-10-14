using System.Net;
using Ashlar.Infrastructure;
using Ashlar.Mcp.Server;
using Microsoft.Extensions.Logging;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Ashlar.API.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Ashlar.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Ashlar.Runtime.Routing;
using Ashlar.Tests.Infrastructure.Helpers;
using Ashlar.Tests.Infrastructure.Helpers.VirtualProduction;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.VirtualProduction;

/// <summary>
/// ProdStyle coverage for the real Ashlar.API host under <c>ASHLAR_DEPLOYMENT_PROFILE=airgapped</c>.
/// Two facts, both read from the process environment by production code, so the class lives in
/// the serialized "EnvironmentVariables" collection (xunit runs collections in parallel and a
/// profile leaking into a concurrent host-composition test is a readiness flake, not a signal):
/// <list type="bullet">
///   <item>The host must build AND start: <c>AddAshlarRuntimeRouting</c> registers the endpoint
///   health monitor unconditionally while the kernel registers its <c>GrpcAgentTransport</c>
///   dependency only for profiles with runtime transport, so start-up used to fail DI under
///   airgapped/edge/system.</item>
///   <item>Enabling a protocol surface (MCP) under the air-gapped profile is refused at boot.</item>
///   <item>SPEC-007 PR 4.10 (owner decision Q6): on AirGapped and SecureWorkstation the API's listeners bind loopback or
///   the host does not boot, and on SecureWorkstation MCP over HTTP is refused at boot.</item>
/// </list>
/// The devtest image sets <c>ASPNETCORE_HTTP_PORTS=8080</c>, which binds every interface, so a test that expects the
/// host to start on those profiles sets <c>urls</c> to a loopback address, as an operator must.
/// </summary>
[Collection("EnvironmentVariables")]
[Trait("Category", "Integration")]
[Trait("Category", "ProdStyle")]
public sealed class AirGappedProfileApiHostProdStyleTests : IDisposable
{
    // SPEC-007 PR 4.6: the host composes AirGapped from the variable, which notes a profile no later AddAshlar lowers,
    // so each test restores the process egress state (the noted profile and the mode latch) through the reset seam.
    private readonly EgressProcessStateScope _egressState = new();

    public void Dispose() => _egressState.Dispose();

    private const string LoopbackUrls = "http://localhost:5000";

    private static WebApplicationFactory<Program> CreateFactory(IDictionary<string, string?>? settings = null, StartupFailureRecorder? startup = null, bool forceDisposalRace = false)
        => (startup is null ? new AshlarApiWebApplicationFactory() : (WebApplicationFactory<Program>)new ObservedFactory<Program>(startup, forceDisposalRace)).WithWebHostBuilder(builder =>
        {
            var all = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["urls"] = LoopbackUrls };
            foreach (var pair in settings ?? new Dictionary<string, string?>())
            {
                all[pair.Key] = pair.Value;
            }

            foreach (var pair in all)
            {
                builder.UseSetting(pair.Key, pair.Value);
            }
        });

    private static async Task<Exception> BootFailureAsync(WebApplicationFactory<Program> factory, StartupFailureRecorder startup, Type optionsType, bool forceDisposalRace = false)
    {
        var act = () =>
        {
            using var client = factory.CreateClient();
            return Task.CompletedTask;
        };

        var observed = (await act.Should().ThrowAsync<Exception>("the host must not boot")).Which;
        if (forceDisposalRace) observed.Should().BeOfType<ObjectDisposedException>();
        var recorded = startup.Failure.Task.IsCompletedSuccessfully ? startup.Failure.Task.Result : null;
        var failure = AuthenticOptionsFailure(observed, recorded, optionsType);
        failure.Should().NotBeNull("this host must supply its genuine typed startup validation failure; observed: {0}; recorded: {1}", observed, recorded);
        return failure!;
    }

    [Fact(Timeout = TestTimeouts.HostTouching)]
    public async Task Airgapped_profile_api_host_starts_with_an_idle_endpoint_health_monitor()
    {
        using var profile = new EnvironmentVariableScope("ASHLAR_DEPLOYMENT_PROFILE", "airgapped");
        using var factory = CreateFactory();

        // CreateClient starts the host, which resolves every IHostedService — that is where the
        // monitor's unresolvable transport dependency used to surface.
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        factory.Services.GetServices<IHostedService>().Should().ContainSingle(
            s => s.GetType().FullName == "Ashlar.API.Security.LoopbackListenerVerifier",
            "the production API must register the post-bind verifier");
        factory.Services.GetServices<IHostedService>()
            .Should().ContainSingle(s => s is EndpointHealthMonitor,
                "the routing registration is profile-independent; only the transport is gated");
        factory.Services.GetService<Ashlar.Transport.Grpc.GrpcAgentTransport>()
            .Should().BeNull("the air-gapped profile excludes runtime transport (no gRPC egress)");
    }

    [Fact(Timeout = TestTimeouts.HostTouching)]
    public async Task Airgapped_profile_refuses_protocol_enablement_at_boot()
    {
        using var profile = new EnvironmentVariableScope("ASHLAR_DEPLOYMENT_PROFILE", "airgapped");
        var startup = new StartupFailureRecorder();
        using var factory = CreateFactory(new Dictionary<string, string?>
        {
            ["Ashlar:Mcp:Server:Enabled"] = "true",
        }, startup);

        // Match MCP's own validator, so an unrelated listener refusal cannot pass this test.
        var failure = await BootFailureAsync(factory, startup, typeof(AshlarMcpServerOptions));
        failure.ToString().Should().Contain("is not permitted under the AirGapped deployment profile");
    }
    [Theory(Timeout = TestTimeouts.HostTouching)]
    [InlineData("airgapped", "urls", "http://0.0.0.0:5000", "AirGapped")]
    [InlineData("airgapped", "urls", "http://+:80", "AirGapped")]
    [InlineData("secure-workstation", "urls", "http://[::]:5000", "SecureWorkstation")]
    [InlineData("secure-workstation", "Kestrel:Endpoints:Lan:Url", "http://192.168.1.5:5000", "SecureWorkstation")]
    public async Task A_non_loopback_listener_fails_boot(string profileValue, string key, string value, string display)
    {
        using var profile = new EnvironmentVariableScope("ASHLAR_DEPLOYMENT_PROFILE", profileValue);
        var startup = new StartupFailureRecorder();
        using var factory = CreateFactory(new Dictionary<string, string?> { [key] = value }, startup);

        var failure = await BootFailureAsync(factory, startup, typeof(AshlarInboundListenerOptions));

        failure.ToString().Should().Contain("loopback").And.Contain(display).And.Contain(value);
    }

    [Fact(Timeout = TestTimeouts.HostTouching)]
    public async Task The_port_only_binding_fails_boot_on_AirGapped()
    {
        using var profile = new EnvironmentVariableScope("ASHLAR_DEPLOYMENT_PROFILE", "airgapped");
        // http_ports applies only while urls is unset, and binds every interface. A port the devtest image's
        // ASPNETCORE_HTTP_PORTS=8080 cannot supply, so the refusal is this setting's, not the environment's.
        var startup = new StartupFailureRecorder();
        using var factory = CreateFactory(new Dictionary<string, string?> { ["urls"] = null, ["http_ports"] = "18080" }, startup);

        var failure = await BootFailureAsync(factory, startup, typeof(AshlarInboundListenerOptions));

        failure.ToString().Should().Contain("http://*:18080").And.Contain("AirGapped");
    }

    [Fact(Timeout = TestTimeouts.HostTouching)]
    public async Task SecureWorkstation_api_host_starts_on_loopback()
    {
        using var profile = new EnvironmentVariableScope("ASHLAR_DEPLOYMENT_PROFILE", "secure-workstation");
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact(Timeout = TestTimeouts.HostTouching)]
    public async Task SecureWorkstation_refuses_MCP_over_HTTP_at_boot()
    {
        using var profile = new EnvironmentVariableScope("ASHLAR_DEPLOYMENT_PROFILE", "secure-workstation");
        var startup = new StartupFailureRecorder();
        using var factory = CreateFactory(new Dictionary<string, string?>
        {
            ["Ashlar:Mcp:Server:Enabled"] = "true",
        }, startup);

        var failure = await BootFailureAsync(factory, startup, typeof(AshlarMcpServerOptions));

        failure.ToString().Should().Contain("MCP over HTTP").And.Contain("SecureWorkstation");
    }

    [Theory(Timeout = TestTimeouts.HostTouching)]
    [InlineData("air-gapped", false, false)]
    [InlineData("secure-workstation", false, false)]
    [InlineData("air-gapped", true, true)]
    [InlineData("full", false, true)]
    public async Task What_Kestrel_bound_is_checked_after_the_server_starts(string profile, bool loopback, bool starts)
    {
        // A listener added in code never reaches configuration; the verifier reads what Kestrel actually bound.
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(k =>
        {
            if (loopback)
                k.Listen(IPAddress.Loopback, 0);
            else
                k.ListenAnyIP(0);
        });
        builder.Services.AddSingleton(Options.Create(new AshlarResolvedDeploymentProfileOptions { Profile = profile }));
        var verifier = typeof(Program).Assembly.GetType("Ashlar.API.Security.LoopbackListenerVerifier");
        verifier.Should().NotBeNull("the API must verify actual bound addresses after start");
        builder.Services.AddSingleton(typeof(IHostedService), verifier!);
        await using var app = builder.Build();

        var act = () => app.StartAsync();

        if (starts)
        {
            await act.Should().NotThrowAsync();
            await app.StopAsync();
        }
        else
        {
            (await act.Should().ThrowAsync<Exception>()).Which.ToString().Should().Contain("loopback");
        }
    }
    [Theory(Timeout = TestTimeouts.HostTouching)]
    [InlineData("air-gapped", false, false)]
    [InlineData("air-gapped", true, false)]
    [InlineData("secure-workstation", false, false)]
    [InlineData("secure-workstation", true, false)]
    [InlineData("full", false, true)]
    [InlineData("full", true, true)]
    public async Task A_restricted_server_without_published_addresses_fails_closed(string profile, bool featurePresent, bool succeeds)
    {
        using var server = new UnreportedServer(featurePresent);
        var verifier = new LoopbackListenerVerifier(server,
            Options.Create(new AshlarResolvedDeploymentProfileOptions { Profile = profile }));
        var act = () => verifier.StartedAsync(CancellationToken.None);
        if (succeeds)
            await act.Should().NotThrowAsync();
        else
            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*no bound addresses*");
    }

    private sealed class UnreportedServer : IServer
    {
        public UnreportedServer(bool featurePresent)
        {
            if (featurePresent)
                Features.Set<IServerAddressesFeature>(new ServerAddressesFeature());
        }

        public IFeatureCollection Features { get; } = new FeatureCollection();
        public Task StartAsync<TContext>(IHttpApplication<TContext> application, CancellationToken cancellationToken) where TContext : notnull
            => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public void Dispose() { }
    }

    [Theory(Timeout = TestTimeouts.HostTouching)]
    [InlineData("airgapped", "ports", "http://*:18080")]
    public async Task Api_startup_refusal_survives_deferred_observer_disposal(string profileValue, string kind, string expected)
    {
        using var profile = new EnvironmentVariableScope("ASHLAR_DEPLOYMENT_PROFILE", profileValue);
        var startup = new StartupFailureRecorder();
        var settings = kind == "ports"
            ? new Dictionary<string, string?> { ["urls"] = null, ["http_ports"] = "18080" }
            : new Dictionary<string, string?> { ["Ashlar:Mcp:Server:Enabled"] = "true" };
        using var factory = CreateFactory(settings, startup, forceDisposalRace: true);
        var failure = await BootFailureAsync(factory, startup, kind == "ports" ? typeof(AshlarInboundListenerOptions) : typeof(AshlarMcpServerOptions), forceDisposalRace: true);
        failure.Should().BeOfType<OptionsValidationException>();
        failure.ToString().Should().Contain(expected);
    }

    private static OptionsValidationException? AuthenticOptionsFailure(Exception observed, Exception? recorded, Type optionsType)
    {
        // Recover only WAF's disposal mask, using this host's actual startup-fault event.
        var candidate = observed is ObjectDisposedException ? recorded : observed;
        return candidate is OptionsValidationException validation && validation.OptionsType == optionsType
            ? validation : null;
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("wrong-type")]
    [InlineData("wrong-options")]
    [InlineData("unrelated-observed")]
    public void Api_disposal_without_authentic_options_failure_is_not_a_refusal(string invalid)
    {
        Exception observed = invalid == "unrelated-observed" ? new IOException("unrelated") : new ObjectDisposedException("IServiceProvider");
        Exception? recorded = invalid switch
        {
            "missing" => null,
            "wrong-type" => new IOException("loopback"),
            "wrong-options" => new OptionsValidationException(Options.DefaultName, typeof(string), ["loopback"]),
            _ => new OptionsValidationException(Options.DefaultName, typeof(AshlarInboundListenerOptions), ["loopback"]),
        };
        AuthenticOptionsFailure(observed, recorded, typeof(AshlarInboundListenerOptions)).Should().BeNull();
    }
    private sealed class ObservedFactory<T>(StartupFailureRecorder startup, bool forceDisposalRace)
        : WebApplicationFactory<T> where T : class
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services => services.AddSingleton<ILoggerProvider>(_ => startup));
            builder.ConfigureLogging(logging => logging.AddFilter<StartupFailureRecorder>(level => level >= LogLevel.Debug));
        }

        protected override IHost CreateHost(IHostBuilder builder)
        {
            if (!forceDisposalRace) return base.CreateHost(builder);
            var host = builder.Build();
            try
            {
                // Force the real RunAsync failure/disposal to win before WAF observes startup.
                // This deterministically reproduces the macOS DeferredHost.StartAsync race.
                // A broken guard can instead start successfully; observe that too so the assertion
                // fails promptly and the factory still owns and disposes the running host.
                Task.WhenAny(startup.Disposed.Task, startup.Started.Task)
                    .WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
                host.Start();
                return host;
            }
            catch
            {
                host.Dispose(); // WAF cannot own a host that CreateHost did not return.
                throw;
            }
        }
    }

    private sealed class StartupFailureRecorder : ILoggerProvider
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<Exception> Failure { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ILogger CreateLogger(string categoryName) => new RecorderLogger(this, categoryName);
        public void Dispose() => Disposed.TrySetResult();

        private sealed class RecorderLogger(StartupFailureRecorder owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel is LogLevel.Debug or LogLevel.Error;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (category == "Microsoft.Extensions.Hosting.Internal.Host" && logLevel == LogLevel.Error
                    && eventId.Id == 11 && eventId.Name == "HostedServiceStartupFaulted" && exception is not null)
                    owner.Failure.TrySetResult(exception);
                if (category == "Microsoft.Extensions.Hosting.Internal.Host" && logLevel == LogLevel.Debug
                    && eventId.Id == 2 && eventId.Name == "Started")
                    owner.Started.TrySetResult();
            }
        }
    }

}
