using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
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
/// </list>
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

    private static WebApplicationFactory<Program> CreateFactory(IDictionary<string, string?>? settings = null)
        => new AshlarApiWebApplicationFactory().WithWebHostBuilder(builder =>
        {
            if (settings is null)
            {
                return;
            }

            foreach (var pair in settings)
            {
                builder.UseSetting(pair.Key, pair.Value);
            }
        });

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
}
