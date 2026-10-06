using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Ashlar.CLI.Commands.BackgroundAgent;
using Ashlar.Infrastructure.Deployment;
using Xunit;

namespace Ashlar.Tests.CLI.Tests.Commands;

/// <summary>
/// SPEC-007 PR 4.10 (owner decision Q6 as recorded: "mesh serve must bind loopback"): on AirGapped and
/// SecureWorkstation every Ashlar inbound listener binds loopback, so mesh serve binds <c>localhost</c> there instead
/// of every interface and keeps serving; a peer on another host cannot reach it. Full serves on every interface as
/// before. The profile is passed as the value the hosting layer registers, so no process-global state is touched.
/// </summary>
[Xunit.Collection("MeshIntegration")]
public sealed class MeshServeLoopbackProfileTests : IDisposable
{
    private readonly string _published;

    public MeshServeLoopbackProfileTests()
    {
        _published = Path.Combine(Path.GetTempPath(), "ashlar-mesh-q6-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_published);
    }

    public void Dispose()
    {
        try { Directory.Delete(_published, recursive: true); } catch (IOException) { /* best effort */ }
    }

    [Theory]
    [InlineData("air-gapped", false, "http://localhost:7420")]
    [InlineData("secure-workstation", false, "http://localhost:7420")]
    [InlineData("air-gapped", true, "https://localhost:7420")]
    public void The_bind_address_is_loopback_on_loopback_only_profiles(string profile, bool tls, string expected)
    {
        var settings = tls
            ? new MeshServeSettings(7420, _published, "n", TlsCertPath: "cert.pem", TlsKeyPath: "key.pem")
            : new MeshServeSettings(7420, _published, "n");

        var address = MeshServeService.BindAddress(settings, new ResolvedDeploymentProfile(profile));

        address.Should().Be(expected);
        LoopbackListenerPolicy.IsLoopback(address).Should().BeTrue();
        MeshServeService.BindsLoopbackOnly(new ResolvedDeploymentProfile(profile)).Should().BeTrue();
    }

    [Theory]
    [InlineData("full")]
    [InlineData("server")]
    [InlineData(null)]
    public void The_bind_address_is_every_interface_elsewhere(string? profile)
    {
        var resolved = profile is null ? null : new ResolvedDeploymentProfile(profile);

        MeshServeService.BindAddress(new MeshServeSettings(7420, _published, "n"), resolved).Should().Be("http://*:7420");
        MeshServeService.BindsLoopbackOnly(resolved).Should().BeFalse();
    }

    [Theory]
    [InlineData("air-gapped")]
    [InlineData("secure-workstation")]
    public async Task On_a_loopback_only_profile_mesh_serve_serves_on_loopback_and_nowhere_else(string profile)
    {
        var port = FreePort();
        var service = new MeshServeService(
            new MeshServeSettings(port, _published, "q6-node"),
            NullLogger<MeshServeService>.Instance,
            new ResolvedDeploymentProfile(profile));

        await service.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilListeningAsync(IPAddress.Loopback, port);
            IsListening(IPAddress.Loopback, port).Should().BeTrue("mesh serve keeps serving, on loopback");
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
            using var hello = await client.GetAsync("/mesh/v1/hello");
            hello.StatusCode.Should().Be(HttpStatusCode.OK);

            // The point of Q6: a peer on another host cannot reach it. Every non-loopback address of this machine refuses.
            var lan = NonLoopbackIPv4Addresses();
            lan.Should().NotBeEmpty("the twin needs a non-loopback address to show the listener is not on it");
            foreach (var address in lan)
                IsListening(address, port).Should().BeFalse($"mesh serve must not listen on {address} under {profile}");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task On_Full_mesh_serve_still_serves_on_every_interface()
    {
        var port = FreePort();
        var service = new MeshServeService(
            new MeshServeSettings(port, _published, "q6-node"),
            NullLogger<MeshServeService>.Instance,
            new ResolvedDeploymentProfile("full"));

        await service.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilListeningAsync(IPAddress.Loopback, port);
            IsListening(IPAddress.Loopback, port).Should().BeTrue();
            foreach (var address in NonLoopbackIPv4Addresses())
                IsListening(address, port).Should().BeTrue($"Full is unchanged: mesh serve listens on {address} too");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task WaitUntilListeningAsync(IPAddress address, int port)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (!IsListening(address, port) && DateTime.UtcNow < deadline)
            await Task.Delay(100);
    }

    private static bool IsListening(IPAddress address, int port)
    {
        try
        {
            using var client = new TcpClient(address.AddressFamily);
            client.Connect(address, port);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static IReadOnlyList<IPAddress> NonLoopbackIPv4Addresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(u => u.Address)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
            .Distinct()
            .ToList();
}
