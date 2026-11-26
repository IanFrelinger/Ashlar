using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ashlar.Commercial.Fleet.Contracts.Ports;
using Ashlar.Commercial.Fleet.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using OpenWorker = Ashlar.Infrastructure.MeshLab;
using CommercialWorker = Ashlar.Commercial.Fleet.Infrastructure.MeshLab;

namespace Ashlar.Commercial.Tests.Fleet.Host;

/// <summary>Worker completion must deliver actual bytes through the director, including after reopening its store.</summary>
public sealed class FleetResultDeliveryTests
{
    private const string ApiKey = "local-result-delivery-test-key";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Automatic_worker_payload_is_downloadable_after_director_recreation(bool commercial)
    {
        var directory = Path.Combine(Path.GetTempPath(), "fleet-results-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var database = Path.Combine(directory, "tasks.db");
        try
        {
            string taskId;
            byte[] expected = [0, 1, 2, 255, 42];
            using (var host = CreateHost(database))
            using (var client = CreateClient(host))
            {
                var task = await ScheduleAsync(client);
                taskId = task.GetProperty("taskId").GetString()!;
                var response = JsonSerializer.Serialize(new
                {
                    success = true,
                    output = new { success = true, payload = new { __type = "bytes", base64 = Convert.ToBase64String(expected) } }
                });
                await RunWorkerAsync(client, commercial, response);
                using var download = await client.GetAsync($"/api/mesh/tasks/{taskId}/result");
                Assert.Equal(HttpStatusCode.OK, download.StatusCode);
                Assert.Equal(expected, await download.Content.ReadAsByteArrayAsync());
                // The existing operator metadata path must not erase the newly persisted result.
                using var annotation = await client.PatchAsJsonAsync($"/api/mesh/tasks/{taskId}/status",
                    new { status = 3, resultSummary = "reviewed result" });
                annotation.EnsureSuccessStatusCode();
            }
            using var reopened = CreateHost(database);
            using var reopenedClient = CreateClient(reopened);
            using var retained = await reopenedClient.GetAsync($"/api/mesh/tasks/{taskId}/result");
            Assert.Equal(HttpStatusCode.OK, retained.StatusCode);
            Assert.Equal(expected, await retained.Content.ReadAsByteArrayAsync());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData(false, "{\"success\":false,\"error\":\"execution failed\"}")]
    [InlineData(true, "{\"success\":false,\"error\":\"execution failed\"}")]
    [InlineData(false, "{\"success\":true,\"output\":{\"success\":false,\"errorCode\":\"fixture.failed\"}}")]
    [InlineData(true, "{\"success\":true,\"output\":{\"success\":false,\"errorCode\":\"fixture.failed\"}}")]
    public async Task Unsuccessful_brick_response_is_not_reported_as_success(bool commercial, string response)
    {
        using var host = CreateHost();
        using var client = CreateClient(host);
        var task = await ScheduleAsync(client);
        var id = task.GetProperty("taskId").GetString();
        await RunWorkerAsync(client, commercial, response);
        var final = await client.GetFromJsonAsync<JsonElement>($"/api/mesh/tasks/{id}");
        Assert.Equal("Failed", final.GetProperty("status").GetString());
        using var download = await client.GetAsync($"/api/mesh/tasks/{id}/result");
        Assert.Equal(HttpStatusCode.BadRequest, download.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("different-lease")]
    public async Task Payload_completion_requires_the_current_lease(string? offeredLease)
    {
        using var host = CreateHost();
        using var client = CreateClient(host);
        var task = await ScheduleAsync(client);
        var id = task.GetProperty("taskId").GetString();
        using var completion = await client.PatchAsJsonAsync($"/api/mesh/tasks/{id}/status",
            new { status = 3, leaseToken = offeredLease, resultPayload = new byte[] { 42 } });
        Assert.Equal(HttpStatusCode.Conflict, completion.StatusCode);
        var final = await client.GetFromJsonAsync<JsonElement>($"/api/mesh/tasks/{id}");
        Assert.Equal("Assigned", final.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Oversized_payload_is_rejected_without_changing_the_task()
    {
        using var host = CreateHost();
        using var client = CreateClient(host);
        var task = await ScheduleAsync(client);
        var id = task.GetProperty("taskId").GetString();
        using var completion = await client.PatchAsJsonAsync($"/api/mesh/tasks/{id}/status", new
        {
            status = 3, leaseToken = task.GetProperty("leaseToken").GetString(), resultPayload = new byte[256 * 1024 + 1]
        });
        Assert.Equal(HttpStatusCode.BadRequest, completion.StatusCode);
        var final = await client.GetFromJsonAsync<JsonElement>($"/api/mesh/tasks/{id}");
        Assert.Equal("Assigned", final.GetProperty("status").GetString());
    }

    private static WebApplicationFactory<FleetHostProgram> CreateHost(string? database = null) =>
        new WebApplicationFactory<FleetHostProgram>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Ashlar:Security:ApiKey", ApiKey);
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IMeshTaskRegistry>(database is null
                    ? new InMemoryMeshTaskRegistry() : new LiteDbMeshTaskRegistry(database));
                services.AddSingleton<IFleetNodeRegistry>(new InMemoryFleetNodeRegistry());
            });
        });

    private static HttpClient CreateClient(WebApplicationFactory<FleetHostProgram> host)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Ashlar-Api-Key", ApiKey);
        return client;
    }

    private static async Task<JsonElement> ScheduleAsync(HttpClient client)
    {
        using var registration = await client.PostAsJsonAsync("/api/mesh/fleet/nodes", new
        {
            peerId = "result-worker", apiBaseUrl = "http://peer", trustTier = "Trusted",
            peerRegistrationKey = "local-peer-registration-test-key"
        });
        registration.EnsureSuccessStatusCode();
        using var creation = await client.PostAsJsonAsync("/api/mesh/tasks", new { name = "mesh-lab-worker-exec-result", steps = 1 });
        creation.EnsureSuccessStatusCode();
        var created = await creation.Content.ReadFromJsonAsync<JsonElement>();
        using var scheduled = await client.PostAsJsonAsync($"/api/mesh/tasks/{created.GetProperty("taskId").GetString()}/schedule", new { });
        scheduled.EnsureSuccessStatusCode();
        return await scheduled.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task RunWorkerAsync(HttpClient director, bool commercial, string brickResponse)
    {
        using var handler = new WorkerTransport(director, brickResponse);
        var services = new ServiceCollection();
        services.AddHttpClient("mesh-lab-worker-executor").ConfigurePrimaryHttpMessageHandler(() => handler);
        services.Configure<OpenWorker.MeshLabWorkerExecutorOptions>(options =>
        { options.ApiKey = ApiKey; options.DirectorBaseUrl = "http://localhost"; });
        services.Configure<CommercialWorker.MeshLabWorkerExecutorOptions>(options =>
        { options.ApiKey = ApiKey; options.DirectorBaseUrl = "http://localhost"; });
        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();
        var config = new ConfigurationBuilder().Build();
        var count = commercial
            ? await new CommercialWorker.MeshLabWorkerExecutorClient(factory,
                provider.GetRequiredService<IOptionsMonitor<CommercialWorker.MeshLabWorkerExecutorOptions>>(), config,
                NullLogger<CommercialWorker.MeshLabWorkerExecutorClient>.Instance).TryProcessOneAssignedTaskAsync(CancellationToken.None)
            : await new OpenWorker.MeshLabWorkerExecutorClient(factory,
                provider.GetRequiredService<IOptionsMonitor<OpenWorker.MeshLabWorkerExecutorOptions>>(), config,
                NullLogger<OpenWorker.MeshLabWorkerExecutorClient>.Instance).TryProcessOneAssignedTaskAsync(CancellationToken.None);
        Assert.Equal(1, count);
    }

    private sealed class WorkerTransport(HttpClient director, string brickResponse) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.RequestUri!.Host == "peer")
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(request.Method == HttpMethod.Get
                        ? "{\"bricks\":[{\"id\":\"benign-fixture\"}]}" : brickResponse,
                        System.Text.Encoding.UTF8, "application/json")
                };
            using var forwarded = new HttpRequestMessage(request.Method, request.RequestUri);
            foreach (var header in request.Headers)
                forwarded.Headers.TryAddWithoutValidation(header.Key, header.Value);
            if (request.Content is not null)
                forwarded.Content = new StringContent(await request.Content.ReadAsStringAsync(token), System.Text.Encoding.UTF8, "application/json");
            return await director.SendAsync(forwarded, token);
        }
    }
}
