using System.Net.Http.Json;
using FluentAssertions;
using Ashlar.Client;
using Ashlar.Contracts;
using Ashlar.Tests.Infrastructure.Helpers.VirtualProduction;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.VirtualProduction;

/// <summary>
/// End-to-end: real Kestrel pipeline + <see cref="IAshlarClient"/> over HTTP, the path the HTTP client
/// demos used (parked on <c>archive/parked-2026-10-03</c>).
/// Complements NCR routing harness tests (which isolate cloud/peers with test doubles).
/// </summary>
[Collection("Integration")]
[Trait("Category", "Integration")]
[Trait("Category", "ProdStyle")]
public sealed class FrameworkVirtualProdDemosTests : IClassFixture<AshlarApiWebApplicationFactory>
{
    private readonly AshlarApiWebApplicationFactory _factory;

    public FrameworkVirtualProdDemosTests(AshlarApiWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact(Timeout = 120000)]
    public async Task Virtual_prod_GET_api_status_full_HTTP_stack_matches_demo_contract()
    {
        var client = _factory.CreateClient();
        using var resp = await client.GetAsync(new Uri("api/status", UriKind.Relative));
        resp.EnsureSuccessStatusCode();
        var dto = await resp.Content.ReadFromJsonAsync<StatusResponse>();
        dto.Should().NotBeNull();
        dto!.Mode.Should().NotBeNullOrWhiteSpace();
        dto.Message.Should().NotBeNull();
        Assert.True(dto.TotalAgents >= 0);
        Assert.True(dto.ActiveAgents >= 0);
    }

    [Fact(Timeout = 120000)]
    public async Task Virtual_prod_IAshlarClient_GetStatusAsync_matches_console_and_blazor_demos()
    {
        var http = _factory.CreateClient();
        var ashlar = new AshlarClient(http);
        var status = await ashlar.GetStatusAsync();
        status.Mode.Should().NotBeNullOrWhiteSpace();
        status.Message.Should().NotBeNull();
    }

    [Fact(Timeout = 60000)]
    public async Task Virtual_prod_health_and_root_static_pipeline_ready()
    {
        var client = _factory.CreateClient();
        using var health = await client.GetAsync(new Uri("health", UriKind.Relative));
        health.EnsureSuccessStatusCode();

        using var root = await client.GetAsync(new Uri("/", UriKind.Relative));
        root.IsSuccessStatusCode.Should().BeTrue();
        var html = await root.Content.ReadAsStringAsync();
        html.Should().Contain("<html");
    }
}
