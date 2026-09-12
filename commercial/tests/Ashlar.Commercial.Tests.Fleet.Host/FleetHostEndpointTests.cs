using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Ashlar.Commercial.Tests.Fleet.Host;

/// <summary>Tests for fleet host endpoint.</summary>
[Trait("Category", "CommercialFleetHost")]
public sealed class FleetHostEndpointTests : IClassFixture<WebApplicationFactory<FleetHostProgram>>
{
    // The shipped appsettings.json no longer carries a literal key (operators supply one via
    // Ashlar__Security__ApiKey); the test injects its own so the ApiKey mode has a credential.
    private const string TestApiKey = "fleet-host-test-key";

    private readonly WebApplicationFactory<FleetHostProgram> _factory;

    /// <summary>Fleet host endpoint tests.</summary>
    /// <param name="factory">Factory.</param>
    public FleetHostEndpointTests(WebApplicationFactory<FleetHostProgram> factory) =>
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Ashlar:Security:ApiKey", TestApiKey);
        });

    [Fact]
    public async Task Health_returns_ok()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/health");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Commercial_fleet_nodes_list_returns_ok_without_auth()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/mesh/fleet/nodes");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var nodes = await response.Content.ReadFromJsonAsync<List<FleetNodeDto>>();
        nodes.Should().NotBeNull();
    }

    [Fact]
    public async Task Commercial_mesh_task_create_accepts_mutating_request_with_api_key()
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/mesh/tasks")
        {
            Content = JsonContent.Create(new { name = "commercial-fleet-host-test-task", steps = 1 })
        };
        request.Headers.Add("X-Ashlar-Api-Key", TestApiKey);

        var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Commercial_fleet_register_node_accepts_mutating_request_with_api_key()
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/mesh/fleet/nodes")
        {
            Content = JsonContent.Create(new
            {
                peerId = "commercial-fleet-host-test-peer",
                apiBaseUrl = "http://127.0.0.1:8080",
                trustTier = "Trusted"
            })
        };
        request.Headers.Add("X-Ashlar-Api-Key", TestApiKey);

        var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// Registration upserts the WHOLE node document, so anything the body leaves out is reset. A node
    /// re-registers on its normal reconnect cycle and sends no <c>drained</c> field; before
    /// <c>MeshFleetNodeRequest.Drained</c> became nullable that put an operator's drained node
    /// straight back into the placement pool — <c>MeshTaskPlacementService</c> selects on
    /// <c>Admitted &amp;&amp; !Drained</c> — with no concurrency involved at all.
    /// </summary>
    [Fact]
    public async Task Re_registering_a_node_does_not_clear_a_drain()
    {
        const string peerId = "commercial-fleet-host-drain-persistence-peer";
        using var client = _factory.CreateClient();

        await PostAsync(client, "/api/mesh/fleet/nodes", new
        {
            peerId,
            apiBaseUrl = "http://127.0.0.1:8080",
            trustTier = "Trusted",
        });

        await PostAsync(client, $"/api/mesh/fleet/nodes/{peerId}/drain", new { drained = true });

        // The reconnect: same body the node sent the first time, with no opinion about draining.
        await PostAsync(client, "/api/mesh/fleet/nodes", new
        {
            peerId,
            apiBaseUrl = "http://127.0.0.1:8080",
            trustTier = "Trusted",
        });

        var nodes = await client.GetFromJsonAsync<List<FleetNodeDto>>("/api/mesh/fleet/nodes");
        nodes.Should().NotBeNull();
        nodes!.Single(n => n.PeerId == peerId).Drained.Should().BeTrue(
            "a registration body that says nothing about draining must not un-drain the node. "
            + "Placement selects on Admitted && !Drained, so this is the node taking new work while "
            + "an operator believes it is being emptied.");
    }

    private static async Task PostAsync(HttpClient client, string path, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Add("X-Ashlar-Api-Key", TestApiKey);

        var response = await client.SendAsync(request);
        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NoContent);
    }

    /// <summary>Wire DTO for fleet node.</summary>
    /// <param name="PeerId">Peer id.</param>
    /// <param name="Drained">Whether the node is excluded from new placements.</param>
    private sealed record FleetNodeDto(string PeerId, bool Drained);
}
