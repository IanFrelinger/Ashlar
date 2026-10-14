using System.Net;
using System.Net.Sockets;
using Ashlar.Infrastructure;
using Ashlar.Tests.Infrastructure.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ashlar.Commercial.Tests.Fleet.Host;

[CollectionDefinition("InboundProfiles", DisableParallelization = true)]
public sealed class InboundProfilesCollection;

/// <summary>Real shipped entry points and real Kestrel sockets: Q6 applies to both inbound hosts.</summary>
[Collection("InboundProfiles")]
[Trait("Category", "ProdStyle")]
public sealed class InboundHostLoopbackProdStyleTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (var host in new[] { "grpc", "fleet" })
        foreach (var profile in new[] { "airgapped", "secure-workstation" })
        foreach (var binding in new[] { "urls", "endpoint", "code", "loopback" })
            yield return new object[] { host, profile, binding };
        foreach (var host in new[] { "grpc", "fleet" })
            yield return new object[] { host, "full", "code" };
    }

    [Theory(Timeout = 120000)]
    [MemberData(nameof(Cases))]
    public Task Shipped_host_enforces_loopback_before_and_after_bind(string host, string profile, string binding)
        => host == "grpc" ? Check<GrpcHostProgram>(profile, binding) : Check<FleetHostProgram>(profile, binding);

    private static async Task Check<T>(string profile, string binding) where T : class
    {
        using var state = new EgressProcessStateScope(reset: true);
        using var env = new EnvironmentVariableScope("ASHLAR_DEPLOYMENT_PROFILE", profile);
        using var mock = new EnvironmentVariableScope("ASHLAR_ALLOW_MOCK", "1");
        using var ports = new EnvironmentVariableScope("ASPNETCORE_HTTP_PORTS", null);
        await using var factory = new WebApplicationFactory<T>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("urls", binding == "urls" ? "http://0.0.0.0:0" : "http://127.0.0.1:0");
            builder.UseSetting("Ashlar:Security:ApiKey", "loopback-prodstyle-test-key");
            builder.UseSetting("Ashlar:RegisterBackgroundAgentHostedService", "false");
            builder.UseSetting("Ashlar:DisableObservationPipeline", "true");
            if (binding == "endpoint")
                builder.UseSetting("Kestrel:Endpoints:Lan:Url", "http://0.0.0.0:0");
            if (binding == "code")
                builder.ConfigureKestrel(k => k.ListenAnyIP(0));
        });
        factory.UseKestrel();
        var starts = profile == "full" || binding == "loopback";
        Action start = () => _ = factory.Services;
        if (!starts)
        {
            start.Should().Throw<Exception>().Which.ToString().Should().Contain("loopback");
            return;
        }
        start.Should().NotThrow();
        var addresses = factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
        addresses.Should().NotBeEmpty();
        if (profile != "full") addresses.Should().OnlyContain(a => AshlarInboundListenerPolicy.IsLoopbackEndpoint(a));
        var port = new Uri(addresses.First()).Port;
        using var socket = new TcpClient();
        await socket.ConnectAsync(IPAddress.Loopback, port);
        socket.Connected.Should().BeTrue("Kestrel must really accept connections");
    }
}
