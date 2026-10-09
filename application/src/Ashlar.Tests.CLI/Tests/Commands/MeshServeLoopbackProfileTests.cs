using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Ashlar.CLI.Commands.BackgroundAgent;
using Ashlar.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Ashlar.Tests.CLI.Tests.Commands;

/// <summary>
/// SPEC-007 PR 4.10b: AG/SW refuse a missing or non-loopback bind and serve only with an explicit loopback bind.
/// Full retains its listener on every interface.
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
    [InlineData("air-gapped", null)]
    [InlineData("secure-workstation", null)]
    [InlineData("air-gapped", "0.0.0.0")]
    [InlineData("secure-workstation", "192.0.2.1")]
    public async Task Missing_or_non_loopback_bind_faults_mesh_serve(string profile, string? bind)
    {
        using var service = new MeshServeService(new MeshServeSettings(FreePort(), _published, "q6-node", BindAddress: bind),
            NullLogger<MeshServeService>.Instance, Options.Create(new AshlarResolvedDeploymentProfileOptions { Profile = profile }));
        var act = async () =>
        {
            await service.StartAsync(CancellationToken.None);
            await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(3));
        };
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*loopback*");
    }

    [Theory]
    [InlineData("air-gapped", "127.0.0.1")]
    [InlineData("secure-workstation", "127.0.0.1")]
    [InlineData("air-gapped", "::1")]
    [InlineData("secure-workstation", "::ffff:127.0.0.1")]
    public async Task On_a_loopback_only_profile_mesh_serve_serves_on_loopback_and_nowhere_else(string profile, string bind)
    {
        var port = FreePort();
        var service = new MeshServeService(
            new MeshServeSettings(port, _published, "q6-node", BindAddress: bind),
            NullLogger<MeshServeService>.Instance,
            Options.Create(new AshlarResolvedDeploymentProfileOptions { Profile = profile }));

        await service.StartAsync(CancellationToken.None);
        try
        {
            var address = IPAddress.Parse(bind);
            if (address.IsIPv4MappedToIPv6)
                address = address.MapToIPv4();
            await WaitUntilListeningAsync(address, port);
            IsListening(address, port).Should().BeTrue("mesh serve keeps serving, on loopback");
            var host = address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{address}]" : address.ToString();
            using var client = new HttpClient { BaseAddress = new Uri($"http://{host}:{port}/") };
            using var hello = await client.GetAsync("/mesh/v1/hello");
            hello.StatusCode.Should().Be(HttpStatusCode.OK);

            // The point of Q6: a peer on another host cannot reach it. Every non-loopback address of this machine refuses.
            var lan = NonLoopbackIPv4Addresses();
            lan.Should().NotBeEmpty("the twin needs a non-loopback address to show the listener is not on it");
            foreach (var lanAddress in lan)
                IsListening(lanAddress, port).Should().BeFalse($"mesh serve must not listen on {lanAddress} under {profile}");
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
            Options.Create(new AshlarResolvedDeploymentProfileOptions { Profile = "full" }));

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
