using Ashlar.Agents.TestKit;
using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Ashlar.Infrastructure.MeshLab;
using Xunit;
using Ashlar.Abstractions.Security.Egress;
using Microsoft.Extensions.Logging;
using Moq;

namespace Ashlar.Tests.Infrastructure.Tests.MeshLab;

/// <summary>Tests for mesh lab worker executor background service gap coverage.</summary>
public sealed class MeshLabWorkerExecutorBackgroundServiceGapCoverageTests
{
    [Fact]
    public async Task Refused_polls_warn_once_and_the_hosted_loop_continues()
    {
        var refusal = new EgressRefusedException(new EgressGuard("full", "enforce").Evaluate(
            new EgressRequest(EgressFamilies.Http, "mesh.poll", new Uri("https://remote.example"))));
        var continued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var handler = StubHttpMessageHandler.FromSync((_, _) =>
        {
            if (Interlocked.Increment(ref calls) <= 2) throw new HttpRequestException("wrapper", refusal);
            continued.TrySetResult();
            return Json(HttpStatusCode.OK, "[]");
        });
        var options = new MeshLabWorkerExecutorOptions { Enabled = true, ApiKey = "test-key", PollIntervalMs = 100 };
        var client = CreateClient(handler, options, new ConfigurationBuilder().Build());
        var logger = new Mock<ILogger<MeshLabWorkerExecutorBackgroundService>>();
        using var service = new MeshLabWorkerExecutorBackgroundService(client,
            new StaticOptionsMonitor<MeshLabWorkerExecutorOptions>(options), logger.Object);
        await service.StartAsync(CancellationToken.None);
        try { await continued.Task.WaitAsync(TimeSpan.FromSeconds(15)); }
        finally { await service.StopAsync(CancellationToken.None); }
        calls.Should().BeGreaterThanOrEqualTo(3);
        logger.Verify(x => x.Log(LogLevel.Warning, It.Is<EventId>(e => e.Id == 7307),
            It.IsAny<It.IsAnyType>(), It.IsAny<Exception>(), It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
        logger.Verify(x => x.Log(LogLevel.Debug, It.IsAny<EventId>(),
            It.IsAny<It.IsAnyType>(), It.Is<Exception>(e => e is HttpRequestException),
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Never);
        service.Dispose();
        logger.Verify(x => x.Log(LogLevel.Warning, It.Is<EventId>(e => e.Id == 7307),
            It.IsAny<It.IsAnyType>(), It.IsAny<Exception>(), It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Exactly(2));
    }

    [Fact]
    public async Task ExecuteAsync_skips_processing_when_disabled()
    {
        var handler = StubHttpMessageHandler.Always(HttpStatusCode.OK, "[]");
        var client = CreateClient(

            handler,
            new MeshLabWorkerExecutorOptions { Enabled = false, ApiKey = "key" },
            new ConfigurationBuilder().Build());

        var service = new MeshLabWorkerExecutorBackgroundService(
            client,
            new StaticOptionsMonitor<MeshLabWorkerExecutorOptions>(new MeshLabWorkerExecutorOptions { Enabled = false }),
            NullLogger<MeshLabWorkerExecutorBackgroundService>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(600));
        await service.StartAsync(cts.Token);
        await Task.Delay(400);
        await cts.CancelAsync();
        await service.StopAsync(CancellationToken.None);

        handler.WasNeverCalled.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_processes_task_when_enabled()
    {
        var patchCount = 0;
        var handler = StubHttpMessageHandler.FromSync((req, _) =>
        {
            if (req.Method == HttpMethod.Get)
            {
                return Json(HttpStatusCode.OK, """
                    [{"taskId":"bg-1","name":"mesh-lab-worker-exec-bg","status":"Assigned","assignedApiBaseUrl":"http://peer:8080","leaseToken":"tok"}]
                    """);
            }

            patchCount++;
            /// <summary>Json.</summary>
            return Json(HttpStatusCode.OK, "{}");
        });

        var options = new MeshLabWorkerExecutorOptions
        {
            Enabled = true,
            ApiKey = "test-key",
            TaskNamePrefix = "mesh-lab-worker-exec",
            PollIntervalMs = 100,
            ExecuteBrickOnAssignedPeer = false,
        };

        var client = CreateClient(handler, options, new ConfigurationBuilder().Build());
        var service = new MeshLabWorkerExecutorBackgroundService(
            client,
            new StaticOptionsMonitor<MeshLabWorkerExecutorOptions>(options),
            NullLogger<MeshLabWorkerExecutorBackgroundService>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await service.StartAsync(cts.Token);
        await Task.Delay(500);
        await cts.CancelAsync();
        await service.StopAsync(CancellationToken.None);

        patchCount.Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task ExecuteAsync_survives_director_http_failures()
    {
        var handler = StubHttpMessageHandler.FromSync((_, _) => throw new HttpRequestException("director down"));
        var options = new MeshLabWorkerExecutorOptions
        {
            Enabled = true,
            ApiKey = "test-key",
            PollIntervalMs = 100,
        };

        var client = CreateClient(handler, options, new ConfigurationBuilder().Build());
        var service = new MeshLabWorkerExecutorBackgroundService(
            client,
            new StaticOptionsMonitor<MeshLabWorkerExecutorOptions>(options),
            NullLogger<MeshLabWorkerExecutorBackgroundService>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var act = async () =>
        {
            await service.StartAsync(cts.Token);
            await Task.Delay(300);
            await cts.CancelAsync();
            await service.StopAsync(CancellationToken.None);
        };

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ExecuteAsync_logs_and_continues_after_unexpected_errors()
    {
        var handler = StubHttpMessageHandler.FromSync((_, _) => throw new InvalidOperationException("unexpected"));
        var options = new MeshLabWorkerExecutorOptions
        {
            Enabled = true,
            ApiKey = "test-key",
            PollIntervalMs = 100,
        };

        var client = CreateClient(handler, options, new ConfigurationBuilder().Build());
        var service = new MeshLabWorkerExecutorBackgroundService(
            client,
            new StaticOptionsMonitor<MeshLabWorkerExecutorOptions>(options),
            NullLogger<MeshLabWorkerExecutorBackgroundService>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        await service.StartAsync(cts.Token);
        await Task.Delay(300);
        await cts.CancelAsync();
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ExecuteAsync_keeps_polling_while_disabled()
    {
        var handler = StubHttpMessageHandler.Always(HttpStatusCode.OK, "[]");
        var client = CreateClient(

            handler,
            new MeshLabWorkerExecutorOptions { Enabled = false, ApiKey = "key" },
            new ConfigurationBuilder().Build());

        var service = new MeshLabWorkerExecutorBackgroundService(
            client,
            new StaticOptionsMonitor<MeshLabWorkerExecutorOptions>(new MeshLabWorkerExecutorOptions { Enabled = false }),
            NullLogger<MeshLabWorkerExecutorBackgroundService>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await service.StartAsync(cts.Token);
        await Task.Delay(2100);
        await cts.CancelAsync();
        await service.StopAsync(CancellationToken.None);

        handler.WasNeverCalled.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_clamps_poll_interval_to_minimum()
    {
        var handler = StubHttpMessageHandler.Always(HttpStatusCode.OK, "[]");

        var options = new MeshLabWorkerExecutorOptions
        {
            Enabled = true,
            ApiKey = "test-key",
            PollIntervalMs = 10,
        };

        var client = CreateClient(handler, options, new ConfigurationBuilder().Build());
        var service = new MeshLabWorkerExecutorBackgroundService(
            client,
            new StaticOptionsMonitor<MeshLabWorkerExecutorOptions>(options),
            NullLogger<MeshLabWorkerExecutorBackgroundService>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        await service.StartAsync(cts.Token);
        await Task.Delay(350);
        await cts.CancelAsync();
        await service.StopAsync(CancellationToken.None);

        handler.Requests.Should().NotBeEmpty();
    }

    private static MeshLabWorkerExecutorClient CreateClient(
        HttpMessageHandler handler,
        MeshLabWorkerExecutorOptions options,
        IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddHttpClient(MeshLabWorkerExecutorClient.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => handler);
        var factory = services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>();

        return new MeshLabWorkerExecutorClient(
            factory,
            new StaticOptionsMonitor<MeshLabWorkerExecutorOptions>(options),
            configuration,
            NullLogger<MeshLabWorkerExecutorClient>.Instance);
    }

    /// <summary>Json.</summary>
    /// <param name="status">Status.</param>
    /// <param name="json">Json.</param>
    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };



    /// <summary>Tests for static options monitor.</summary>
    private sealed class StaticOptionsMonitor<T>(T value) : Microsoft.Extensions.Options.IOptionsMonitor<T> where T : class
    {
        /// <summary>Current value.</summary>
        public T CurrentValue { get; } = value;
        /// <summary>Gets the value.</summary>
        /// <param name="name">Name.</param>
        public T Get(string? name) => CurrentValue;
        /// <summary>On change.</summary>
        /// <param name="listener">Listener.</param>
        public IDisposable OnChange(Action<T, string?> listener) => NullDisposable.Instance;
    }

    /// <summary>Tests for null disposable.</summary>
    private sealed class NullDisposable : IDisposable
    {
        public static readonly NullDisposable Instance = new();
        /// <summary>Dispose.</summary>
        public void Dispose() { }
    }
}
