using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Ashlar.API.Security;
using Ashlar.Infrastructure.Deployment;
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

    private static WebApplicationFactory<Program> CreateFactory(IDictionary<string, string?>? settings = null)
        => new AshlarApiWebApplicationFactory().WithWebHostBuilder(builder =>
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

    private static async Task<Exception> BootFailureAsync(WebApplicationFactory<Program> factory)
    {
        var act = () =>
        {
            using var client = factory.CreateClient();
            return Task.CompletedTask;
        };

        return (await act.Should().ThrowAsync<Exception>("the host must not boot")).Which;
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
        using var factory = CreateFactory(new Dictionary<string, string?>
        {
            ["Ashlar:Mcp:Server:Enabled"] = "true",
        });

        var act = () =>
        {
            using var client = factory.CreateClient();
            return Task.CompletedTask;
        };

        (await act.Should().ThrowAsync<Exception>("ValidateOnStart must stop the host"))
            .Which.ToString().Should().Contain("AirGapped");
    }

    [Theory(Timeout = TestTimeouts.HostTouching)]
    [InlineData("airgapped", "urls", "http://0.0.0.0:5000", "AirGapped")]
    [InlineData("airgapped", "urls", "http://+:80", "AirGapped")]
    [InlineData("secure-workstation", "urls", "http://[::]:5000", "SecureWorkstation")]
    [InlineData("secure-workstation", "Kestrel:Endpoints:Lan:Url", "http://192.168.1.5:5000", "SecureWorkstation")]
    public async Task A_non_loopback_listener_fails_boot(string profileValue, string key, string value, string display)
    {
        using var profile = new EnvironmentVariableScope("ASHLAR_DEPLOYMENT_PROFILE", profileValue);
        using var factory = CreateFactory(new Dictionary<string, string?> { [key] = value });

        var failure = await BootFailureAsync(factory);

        failure.ToString().Should().Contain("not loopback").And.Contain(display).And.Contain(value);
    }

    [Fact(Timeout = TestTimeouts.HostTouching)]
    public async Task The_port_only_binding_fails_boot_on_AirGapped()
    {
        using var profile = new EnvironmentVariableScope("ASHLAR_DEPLOYMENT_PROFILE", "airgapped");
        // http_ports applies only while urls is unset, and binds every interface.
        using var factory = CreateFactory(new Dictionary<string, string?> { ["urls"] = null, ["http_ports"] = "8080" });

        var failure = await BootFailureAsync(factory);

        failure.ToString().Should().Contain("http://*:8080").And.Contain("AirGapped");
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
        using var factory = CreateFactory(new Dictionary<string, string?>
        {
            ["Ashlar:Mcp:Server:Enabled"] = "true",
        });

        var failure = await BootFailureAsync(factory);

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
        builder.Services.AddSingleton(new ResolvedDeploymentProfile(profile));
        builder.Services.AddHostedService<LoopbackListenerVerifier>();
        await using var app = builder.Build();

        var act = () => app.StartAsync();

        if (starts)
        {
            await act.Should().NotThrowAsync();
            await app.StopAsync();
        }
        else
        {
            (await act.Should().ThrowAsync<Exception>()).Which.ToString().Should().Contain("not loopback");
        }
    }
}
