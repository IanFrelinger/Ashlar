using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Ashlar.CLI.Commands.BackgroundAgent;
using Ashlar.Infrastructure.Deployment;
using Xunit;

namespace Ashlar.Tests.CLI.Tests.Commands;

/// <summary>
/// SPEC-007 PR 4.10 (owner decision Q6): on AirGapped and SecureWorkstation every Ashlar inbound listener binds
/// loopback, and mesh serve listens on every interface, so it refuses to serve there and binds nothing. Full serves as
/// before. The profile is passed as the value <c>AddAshlar</c> registers, so no process-global state is touched.
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
    [InlineData("air-gapped", "AirGapped")]
    [InlineData("secure-workstation", "SecureWorkstation")]
    public void ProfileError_explains_the_refusal_on_loopback_only_profiles(string profile, string display)
    {
        var error = MeshServeService.ProfileError(
            new MeshServeSettings(7420, _published, "n"), new ResolvedDeploymentProfile(profile));

        error.Should().NotBeNull()
            .And.Contain("Mesh serve would listen on http://*:7420")
            .And.Contain(display)
            .And.Contain("ASHLAR_MESH_SERVE_PORT");
    }

    [Theory]
    [InlineData("full")]
    [InlineData("server")]
    [InlineData(null)]
    public void ProfileError_is_null_elsewhere(string? profile) =>
        MeshServeService.ProfileError(
                new MeshServeSettings(7420, _published, "n"),
                profile is null ? null : new ResolvedDeploymentProfile(profile))
            .Should().BeNull();

    [Theory]
    [InlineData("air-gapped")]
    [InlineData("secure-workstation")]
    public async Task On_a_loopback_only_profile_mesh_serve_binds_nothing(string profile)
    {
        var port = FreePort();
        var service = new MeshServeService(
            new MeshServeSettings(port, _published, "q6-node"),
            NullLogger<MeshServeService>.Instance,
            new ResolvedDeploymentProfile(profile));

        await service.StartAsync(CancellationToken.None);
        try
        {
            // The refusal returns before anything binds; give a wrongly started Kestrel the time it would need.
            await Task.Delay(TimeSpan.FromSeconds(1));
            IsListening(port).Should().BeFalse("mesh serve listens on every interface, which Q6 forbids here");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task On_Full_mesh_serve_still_serves()
    {
        var port = FreePort();
        var service = new MeshServeService(
            new MeshServeSettings(port, _published, "q6-node"),
            NullLogger<MeshServeService>.Instance,
            new ResolvedDeploymentProfile("full"));

        await service.StartAsync(CancellationToken.None);
        try
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            while (!IsListening(port) && DateTime.UtcNow < deadline)
                await Task.Delay(100);
            IsListening(port).Should().BeTrue();
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

    private static bool IsListening(int port)
    {
        try
        {
            using var client = new TcpClient();
            client.Connect(IPAddress.Loopback, port);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}
