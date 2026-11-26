using Ashlar.Agents.TestKit;
using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Ashlar.Infrastructure.MeshLab;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.MeshLab;

/// <summary>Tests for mesh lab worker executor client gap coverage.</summary>
public sealed class MeshLabWorkerExecutorClientGapCoverageTests
{
    [Fact]
    public void PickCandidate_returns_null_for_blank_prefix()
    {
        var tasks = new[]
        {
            new MeshLabTaskSnapshot("a", "mesh-lab-worker-exec-x", "Assigned", "http://peer:8080", "tok"),
        };

        MeshLabWorkerExecutorClient.PickCandidate(tasks, "").Should().BeNull();
        MeshLabWorkerExecutorClient.PickCandidate(tasks, "   ").Should().BeNull();
    }

    [Fact]
    public void PickCandidate_skips_tasks_without_lease_or_peer_url()
    {
        var tasks = new[]
        {
            new MeshLabTaskSnapshot("no-lease", "mesh-lab-worker-exec-a", "Assigned", "http://peer:8080", null),
            new MeshLabTaskSnapshot("no-url", "mesh-lab-worker-exec-b", "Assigned", null, "tok"),
            new MeshLabTaskSnapshot("wrong-status", "mesh-lab-worker-exec-c", "Running", "http://peer:8080", "tok"),
        };

        MeshLabWorkerExecutorClient.PickCandidate(tasks, "mesh-lab-worker-exec").Should().BeNull();
    }

    [Fact]
    public void PickCandidate_skips_tasks_with_non_matching_name_prefix()
    {
        var tasks = new[]
        {
            new MeshLabTaskSnapshot("a", "other-prefix-task", "Assigned", "http://peer:8080", "tok"),
        };

        MeshLabWorkerExecutorClient.PickCandidate(tasks, "mesh-lab-worker-exec").Should().BeNull();
    }

    [Theory]
    [InlineData(null, MeshLabTaskStatus.Assigned, false)]
    [InlineData("", MeshLabTaskStatus.Assigned, false)]
    [InlineData("assigned", MeshLabTaskStatus.Assigned, true)]
    [InlineData("1", MeshLabTaskStatus.Assigned, true)]
    public void IsStatus_treats_blank_or_case_insensitive_values(string? status, MeshLabTaskStatus expected, bool match)
    {
        MeshLabWorkerExecutorClient.IsStatus(status, expected).Should().Be(match);
    }

    [Fact]
    public void PickCandidate_accepts_numeric_assigned_status()
    {
        var tasks = new[]
        {
            new MeshLabTaskSnapshot("numeric", "mesh-lab-worker-exec-numeric", "1", "http://peer:8080", "tok"),
        };

        MeshLabWorkerExecutorClient.PickCandidate(tasks, "mesh-lab-worker-exec")!.TaskId.Should().Be("numeric");
    }

    [Fact]
    public void PickCandidate_returns_first_matching_assigned_task()
    {
        var tasks = new[]
        {
            new MeshLabTaskSnapshot("first", "mesh-lab-worker-exec-a", "Assigned", "http://peer:8080", "tok1"),
            new MeshLabTaskSnapshot("second", "mesh-lab-worker-exec-b", "Assigned", "http://peer:8080", "tok2"),
        };

        MeshLabWorkerExecutorClient.PickCandidate(tasks, "mesh-lab-worker-exec")!.TaskId.Should().Be("first");
    }

    [Fact]
    public async Task TryProcessOneAssignedTaskAsync_returns_zero_when_director_lists_no_tasks()
    {
        var client = CreateClient(
            /// <summary>Fake fleet handler.</summary>
            StubHttpMessageHandler.FromSync((_, _) => Json(HttpStatusCode.OK, "[]")),
            new MeshLabWorkerExecutorOptions
            {
                ApiKey = "test-key",
                TaskNamePrefix = "mesh-lab-worker-exec",
            },
            new ConfigurationBuilder().Build());

        (await client.TryProcessOneAssignedTaskAsync(CancellationToken.None)).Should().Be(0);
    }

    [Fact]
    public async Task TryProcessOneAssignedTaskAsync_returns_zero_when_status_patch_fails()
    {
        var client = CreateClient(
            /// <summary>Fake fleet handler.</summary>
            StubHttpMessageHandler.FromSync((req, _) =>
            {
                if (req.Method == HttpMethod.Get)
                {
                    return Json(HttpStatusCode.OK, """
                        [{"taskId":"t1","name":"mesh-lab-worker-exec-job","status":"Assigned","assignedApiBaseUrl":"http://peer:8080","leaseToken":"tok"}]
                        """);
                }

                /// <summary>Json.</summary>
                /// <param name="failed"}"""">Failed"}""".</param>
                return Json(HttpStatusCode.BadRequest, """{"error":"patch failed"}""");
            }),
            new MeshLabWorkerExecutorOptions
            {
                ApiKey = "test-key",
                TaskNamePrefix = "mesh-lab-worker-exec",
                ExecuteBrickOnAssignedPeer = false,
            },
            new ConfigurationBuilder().Build());

        (await client.TryProcessOneAssignedTaskAsync(CancellationToken.None)).Should().Be(0);
    }

    [Fact]
    public async Task TryProcessOneAssignedTaskAsync_uses_configuration_api_key_fallback()
    {
        var patchCount = 0;
        var client = CreateClient(
            /// <summary>Fake fleet handler.</summary>
            StubHttpMessageHandler.FromSync((req, _) =>
            {
                if (req.Method == HttpMethod.Get)
                {
                    return Json(HttpStatusCode.OK, """
                        [{"taskId":"t2","name":"mesh-lab-worker-exec-fallback","status":"Assigned","assignedApiBaseUrl":"http://peer:8080","leaseToken":"tok"}]
                        """);
                }

                req.Headers.TryGetValues("X-Ashlar-Api-Key", out var keys).Should().BeTrue();
                keys!.Single().Should().Be("config-key");
                patchCount++;
                /// <summary>Json.</summary>
                return Json(HttpStatusCode.OK, "{}");
            }),
            new MeshLabWorkerExecutorOptions
            {
                ApiKey = null,
                TaskNamePrefix = "mesh-lab-worker-exec",
                ExecuteBrickOnAssignedPeer = false,
            },
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Ashlar:Security:ApiKey"] = "config-key" })
                .Build());

        var processed = await client.TryProcessOneAssignedTaskAsync(CancellationToken.None);

        processed.Should().Be(1);
        patchCount.Should().Be(2);
    }

    [Fact]
    public async Task TryProcessOneAssignedTaskAsync_returns_zero_when_no_api_key_configured()
    {
        var client = CreateClient(
            /// <summary>Fake fleet handler.</summary>
            StubHttpMessageHandler.FromSync((_, _) => Json(HttpStatusCode.OK, """
                [{"taskId":"t3","name":"mesh-lab-worker-exec-no-key","status":"Assigned","assignedApiBaseUrl":"http://peer:8080","leaseToken":"tok"}]
                """)),
            new MeshLabWorkerExecutorOptions
            {
                ApiKey = null,
                TaskNamePrefix = "mesh-lab-worker-exec",
            },
            new ConfigurationBuilder().Build());

        (await client.TryProcessOneAssignedTaskAsync(CancellationToken.None)).Should().Be(0);
    }

    [Fact]
    public async Task TryProcessOneAssignedTaskAsync_completes_task_without_peer_brick_execution()
    {
        var patchCount = 0;
        var client = CreateClient(
            /// <summary>Fake fleet handler.</summary>
            StubHttpMessageHandler.FromSync((req, _) =>
            {
                if (req.Method == HttpMethod.Get)
                {
                    return Json(HttpStatusCode.OK, """
                        [{"taskId":"t4","name":"mesh-lab-worker-exec-complete","status":"Assigned","assignedApiBaseUrl":"http://peer:8080","leaseToken":"tok"}]
                        """);
                }

                patchCount++;
                /// <summary>Json.</summary>
                return Json(HttpStatusCode.OK, "{}");
            }),
            new MeshLabWorkerExecutorOptions
            {
                ApiKey = "test-key",
                TaskNamePrefix = "mesh-lab-worker-exec",
                ExecuteBrickOnAssignedPeer = false,
            },
            new ConfigurationBuilder().Build());

        (await client.TryProcessOneAssignedTaskAsync(CancellationToken.None)).Should().Be(1);
        patchCount.Should().Be(2);
    }

    [Fact]
    public async Task TryProcessOneAssignedTaskAsync_reports_failure_when_peer_catalog_is_empty()
    {
        var patchCount = 0;
        var client = CreateClient(
            /// <summary>Fake fleet handler.</summary>
            StubHttpMessageHandler.FromSync((req, _) =>
            {
                var path = req.RequestUri!.AbsolutePath;
                if (path.EndsWith("/api/mesh/tasks", StringComparison.Ordinal))
                {
                    return Json(HttpStatusCode.OK, """
                        [{"taskId":"t5","name":"mesh-lab-worker-exec-empty-catalog","status":"Assigned","assignedApiBaseUrl":"http://peer:8080/","leaseToken":"tok"}]
                        """);
                }

                if (path.EndsWith("/api/bricks", StringComparison.Ordinal))
                    /// <summary>Json.</summary>
                    return Json(HttpStatusCode.OK, """{"bricks":[]}""");

                if (patchCount == 1)
                {
                    using var terminal = JsonDocument.Parse(req.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                    terminal.RootElement.GetProperty("status").GetInt32().Should().Be((int)MeshLabTaskStatus.Failed);
                }
                patchCount++;
                /// <summary>Json.</summary>
                return Json(HttpStatusCode.OK, "{}");
            }),
            new MeshLabWorkerExecutorOptions
            {
                ApiKey = "test-key",
                TaskNamePrefix = "mesh-lab-worker-exec",
                ExecuteBrickOnAssignedPeer = true,
            },
            new ConfigurationBuilder().Build());

        (await client.TryProcessOneAssignedTaskAsync(CancellationToken.None)).Should().Be(1);
        patchCount.Should().Be(2);
    }

    [Fact]
    public async Task TryProcessOneAssignedTaskAsync_reads_brick_id_from_lowercase_id_property()
    {
        var executed = false;
        var client = CreateClient(
            /// <summary>Fake fleet handler.</summary>
            StubHttpMessageHandler.FromSync((req, _) =>
            {
                var path = req.RequestUri!.AbsolutePath;
                if (path.EndsWith("/api/mesh/tasks", StringComparison.Ordinal))
                {
                    return Json(HttpStatusCode.OK, """
                        [{"taskId":"t6b","name":"mesh-lab-worker-exec-lowercase-id","status":"Assigned","assignedApiBaseUrl":"http://peer:8080/","leaseToken":"tok"}]
                        """);
                }

                if (path.EndsWith("/api/bricks", StringComparison.Ordinal))
                    /// <summary>Json.</summary>
                    return Json(HttpStatusCode.OK, """{"bricks":[{"id":"lowercase-brick"}]}""");

                if (path.Contains("/execute", StringComparison.Ordinal))
                {
                    executed = true;
                    /// <summary>Json.</summary>
                    return Json(HttpStatusCode.OK, """{"success":true}""");
                }

                /// <summary>Json.</summary>
                return Json(HttpStatusCode.OK, "{}");
            }),
            new MeshLabWorkerExecutorOptions
            {
                ApiKey = "test-key",
                TaskNamePrefix = "mesh-lab-worker-exec",
                ExecuteBrickOnAssignedPeer = true,
            },
            new ConfigurationBuilder().Build());

        (await client.TryProcessOneAssignedTaskAsync(CancellationToken.None)).Should().Be(1);
        executed.Should().BeTrue();
    }

    [Fact]
    public async Task TryProcessOneAssignedTaskAsync_skips_brick_when_catalog_request_fails()
    {
        var patchCount = 0;
        var client = CreateClient(
            /// <summary>Fake fleet handler.</summary>
            StubHttpMessageHandler.FromSync((req, _) =>
            {
                var path = req.RequestUri!.AbsolutePath;
                if (path.EndsWith("/api/mesh/tasks", StringComparison.Ordinal) && req.Method == HttpMethod.Get)
                {
                    return Json(HttpStatusCode.OK, """
                        [{"taskId":"t7","name":"mesh-lab-worker-exec-catalog-fail","status":"Assigned","assignedApiBaseUrl":"http://peer:8080/","leaseToken":"tok"}]
                        """);
                }

                if (path.EndsWith("/api/bricks", StringComparison.Ordinal))
                    /// <summary>Json.</summary>
                    /// <param name="down"}"""">Down"}""".</param>
                    return Json(HttpStatusCode.ServiceUnavailable, """{"error":"catalog down"}""");

                patchCount++;
                /// <summary>Json.</summary>
                return Json(HttpStatusCode.OK, "{}");
            }),
            new MeshLabWorkerExecutorOptions
            {
                ApiKey = "test-key",
                TaskNamePrefix = "mesh-lab-worker-exec",
                ExecuteBrickOnAssignedPeer = true,
            },
            new ConfigurationBuilder().Build());

        (await client.TryProcessOneAssignedTaskAsync(CancellationToken.None)).Should().Be(1);
        patchCount.Should().Be(2);
    }

    [Fact]
    public async Task TryProcessOneAssignedTaskAsync_tolerates_peer_http_errors_during_brick_execute()
    {
        var client = CreateClient(
            /// <summary>Fake fleet handler.</summary>
            StubHttpMessageHandler.FromSync((req, _) =>
            {
                var path = req.RequestUri!.AbsolutePath;
                if (path.EndsWith("/api/mesh/tasks", StringComparison.Ordinal))
                    return Json(HttpStatusCode.OK, req.Method == HttpMethod.Get
                        ? """
                          [{"taskId":"t8","name":"mesh-lab-worker-exec-peer-error","status":"Assigned","assignedApiBaseUrl":"http://peer:8080/","leaseToken":"tok"}]
                          """
                        : "{}");

                if (path.EndsWith("/api/bricks", StringComparison.Ordinal))
                    /// <summary>Http request exception.</summary>
                    /// <param name="unreachable"">Unreachable".</param>
                    throw new HttpRequestException("peer unreachable");

                /// <summary>Json.</summary>
                return Json(HttpStatusCode.OK, "{}");
            }),
            new MeshLabWorkerExecutorOptions
            {
                ApiKey = "test-key",
                TaskNamePrefix = "mesh-lab-worker-exec",
                ExecuteBrickOnAssignedPeer = true,
            },
            new ConfigurationBuilder().Build());

        (await client.TryProcessOneAssignedTaskAsync(CancellationToken.None)).Should().Be(1);
    }

    [Fact]
    public async Task TryProcessOneAssignedTaskAsync_reads_brick_id_from_pascal_case_property()
    {
        var executed = false;
        var client = CreateClient(
            /// <summary>Fake fleet handler.</summary>
            StubHttpMessageHandler.FromSync((req, _) =>
            {
                var path = req.RequestUri!.AbsolutePath;
                if (path.EndsWith("/api/mesh/tasks", StringComparison.Ordinal))
                {
                    return Json(HttpStatusCode.OK, """
                        [{"taskId":"t6","name":"mesh-lab-worker-exec-brick-id","status":"Assigned","assignedApiBaseUrl":"http://peer:8080/","leaseToken":"tok"}]
                        """);
                }

                if (path.EndsWith("/api/bricks", StringComparison.Ordinal))
                    /// <summary>Json.</summary>
                    return Json(HttpStatusCode.OK, """{"Bricks":[{"BrickId":"pascal-brick"}]}""");

                if (path.Contains("/execute", StringComparison.Ordinal))
                {
                    executed = true;
                    /// <summary>Json.</summary>
                    return Json(HttpStatusCode.BadRequest, """{"error":"ignored"}""");
                }

                /// <summary>Json.</summary>
                return Json(HttpStatusCode.OK, "{}");
            }),
            new MeshLabWorkerExecutorOptions
            {
                ApiKey = "test-key",
                TaskNamePrefix = "mesh-lab-worker-exec",
                ExecuteBrickOnAssignedPeer = true,
            },
            new ConfigurationBuilder().Build());

        (await client.TryProcessOneAssignedTaskAsync(CancellationToken.None)).Should().Be(1);
        executed.Should().BeTrue();
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
