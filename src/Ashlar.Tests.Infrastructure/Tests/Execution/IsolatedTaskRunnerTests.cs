using Ashlar.Core.Application.Execution.Ports;
using Ashlar.Infrastructure.Autonomy;
using Ashlar.Infrastructure.Execution.Sandbox;
using Ashlar.Infrastructure.Scaling;
using Ashlar.Tests.Infrastructure.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Execution;

public sealed class IsolatedTaskRunnerTests
{
    private static IsolatedTaskRequest Request() => new("task-image:local", new[] { "parser", "a document; $(literal)" });

    [Fact]
    public async Task Task_returns_output_and_observed_environment_then_disposes_its_session()
    {
        var backend = new RecordingBackend();
        var result = await new DockerIsolatedTaskRunner(backend).RunAsync(Request());

        Assert.Equal(new ProcessCommandResult(0, "parsed", "diagnostic"), result.Command);
        Assert.Equal(backend.Session.SessionId, result.Environment.SessionId);
        Assert.Equal("sha256:verified", result.Environment.ImageDigest);
        Assert.Equal(new[] { "start", "attest", "exec", "dispose" }, backend.Events);
        Assert.Equal(Request().Command, backend.Session.ExecutedCommand);
        Assert.Equal(1024 * 1024, backend.Session.ExecutedMaxOutputBytes);
        var spec = Assert.IsType<SandboxSpec>(backend.Spec);
        Assert.Equal(NetworkAccess.None, spec.Network);
        Assert.Empty(spec.Mounts);
        Assert.Equal(new[] { "/tmp" }, spec.ScratchPaths);
        Assert.Equal(new ResourceLimits("268435456", 64, "1", TimeSpan.FromMinutes(1)), spec.Limits);
    }

    [Fact]
    public async Task Nonzero_program_exit_is_returned_and_does_not_skip_cleanup()
    {
        var backend = new RecordingBackend();
        backend.Session.Execute = (_, _) => Task.FromResult(new ProcessCommandResult(17, "partial", "parse failed"));

        var result = await new DockerIsolatedTaskRunner(backend).RunAsync(Request());

        Assert.False(result.Command.Succeeded);
        Assert.Equal(17, result.Command.ExitCode);
        Assert.Equal("parse failed", result.Command.StdErr);
        Assert.Equal("dispose", backend.Events.Last());
    }

    [Fact]
    public async Task Explicit_resource_budget_is_applied_and_stricter_effective_limits_are_accepted()
    {
        var backend = new RecordingBackend();
        backend.Session.Inspect = _ => Task.FromResult(backend.Session.ValidAttestation() with
        {
            EffectiveMemoryBytes = 64L * 1024 * 1024,
            EffectiveNanoCpus = 250_000_000,
            EffectivePidsLimit = 16,
        });
        var request = Request() with
        {
            MemoryBytes = 128L * 1024 * 1024,
            Cpus = 0.5m,
            Pids = 32,
            Timeout = TimeSpan.FromSeconds(5),
            MaxOutputBytes = 8192,
        };

        var result = await new DockerIsolatedTaskRunner(backend).RunAsync(request);

        Assert.True(result.Command.Succeeded);
        var expected = new ResourceLimits("134217728", 32, "0.5", TimeSpan.FromSeconds(5));
        Assert.Equal(expected, backend.Spec!.Limits);
        Assert.Equal(expected, result.Environment.Requested);
        Assert.Equal(8192, backend.Session.ExecutedMaxOutputBytes);
    }

    [Fact]
    public async Task Output_limit_failure_preserves_the_exception_and_disposes_the_session()
    {
        var backend = new RecordingBackend();
        var failure = new ProcessOutputLimitExceededException(4096);
        backend.Session.Execute = (_, _) => throw failure;

        var actual = await Assert.ThrowsAsync<ProcessOutputLimitExceededException>(() =>
            new DockerIsolatedTaskRunner(backend).RunAsync(Request() with { MaxOutputBytes = 4096 }));

        Assert.Same(failure, actual);
        Assert.Equal(4096, actual.MaxOutputBytes);
        Assert.Equal(4096, backend.Session.ExecutedMaxOutputBytes);
        Assert.Equal(new[] { "start", "attest", "exec", "dispose" }, backend.Events);
    }

    [Fact]
    public async Task Backend_without_bounded_output_support_refuses_work_and_disposes()
    {
        var backend = new RecordingBackend { SupportsBoundedOutput = false };

        await Assert.ThrowsAsync<NotSupportedException>(() => new DockerIsolatedTaskRunner(backend).RunAsync(Request()));

        Assert.DoesNotContain("unbounded-exec", backend.Events);
        Assert.DoesNotContain("exec", backend.Events);
        Assert.Null(backend.Session.ExecutedCommand);
        Assert.Equal("dispose", backend.Events.Last());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(16 * 1024 * 1024)]
    public async Task Output_budget_boundaries_are_accepted_and_forwarded(int maxOutputBytes)
    {
        var backend = new RecordingBackend();
        backend.Session.Execute = (_, _) => Task.FromResult(new ProcessCommandResult(0, "", ""));

        var result = await new DockerIsolatedTaskRunner(backend).RunAsync(Request() with { MaxOutputBytes = maxOutputBytes });

        Assert.True(result.Command.Succeeded);
        Assert.Equal(maxOutputBytes, backend.Session.ExecutedMaxOutputBytes);
    }

    [Fact]
    public async Task Program_exception_preserves_failure_and_disposes_session()
    {
        var backend = new RecordingBackend();
        var failure = new IOException("execution transport lost");
        backend.Session.Execute = (_, _) => throw failure;

        var actual = await Assert.ThrowsAsync<IOException>(() => new DockerIsolatedTaskRunner(backend).RunAsync(Request()));

        Assert.Same(failure, actual);
        Assert.Equal("dispose", backend.Events.Last());
    }

    [Fact]
    public async Task Inspection_failure_disposes_without_executing_program()
    {
        var backend = new RecordingBackend();
        backend.Session.Inspect = _ => throw new InvalidOperationException("inspection unavailable");

        await Assert.ThrowsAsync<InvalidOperationException>(() => new DockerIsolatedTaskRunner(backend).RunAsync(Request()));

        Assert.Equal(new[] { "start", "attest", "dispose" }, backend.Events);
    }

    [Theory]
    [InlineData("memory")]
    [InlineData("pids")]
    [InlineData("cpu")]
    [InlineData("network")]
    [InlineData("rootfs")]
    [InlineData("capabilities")]
    [InlineData("privileges")]
    [InlineData("digest")]
    [InlineData("session")]
    public async Task Unverified_or_weaker_environment_refuses_work_and_disposes(string defect)
    {
        var backend = new RecordingBackend();
        var attestation = backend.Session.ValidAttestation();
        attestation = defect switch
        {
            "memory" => attestation with { EffectiveMemoryBytes = 0 },
            "pids" => attestation with { EffectivePidsLimit = 65 },
            "cpu" => attestation with { EffectiveNanoCpus = null },
            "network" => attestation with { EffectiveNetworkMode = "bridge" },
            "rootfs" => attestation with { EffectiveReadOnlyRootFilesystem = null },
            "capabilities" => attestation with { EffectiveDroppedCapabilities = Array.Empty<string>() },
            "privileges" => attestation with { EffectiveSecurityOptions = new[] { "no-new-privileges=false" } },
            "digest" => attestation with { ImageDigest = null },
            "session" => attestation with { SessionId = "some-other-session" },
            _ => throw new ArgumentOutOfRangeException(nameof(defect)),
        };
        backend.Session.Inspect = _ => Task.FromResult(attestation);

        await Assert.ThrowsAsync<InvalidOperationException>(() => new DockerIsolatedTaskRunner(backend).RunAsync(Request()));

        Assert.Equal(new[] { "start", "attest", "dispose" }, backend.Events);
    }

    [Fact]
    public async Task Backend_cannot_weaken_the_limits_by_echoing_a_different_request()
    {
        var backend = new RecordingBackend();
        backend.Session.Inspect = _ => Task.FromResult(backend.Session.ValidAttestation() with
        {
            Requested = new ResourceLimits(),
            EffectiveMemoryBytes = 512L * 1024 * 1024,
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => new DockerIsolatedTaskRunner(backend).RunAsync(Request()));

        Assert.DoesNotContain("exec", backend.Events);
        Assert.Equal("dispose", backend.Events.Last());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Timeout_covers_inspection_and_execution_and_disposes(bool duringInspection)
    {
        var backend = new RecordingBackend();
        if (duringInspection)
        {
            backend.Session.Inspect = async token =>
            {
                await Task.Delay(Timeout.Infinite, token);
                return backend.Session.ValidAttestation();
            };
        }
        else
        {
            backend.Session.Execute = async (_, token) =>
            {
                await Task.Delay(Timeout.Infinite, token);
                return new ProcessCommandResult(0, "", "");
            };
        }

        await Assert.ThrowsAsync<TimeoutException>(() => new DockerIsolatedTaskRunner(backend).RunAsync(
            Request() with { Timeout = TimeSpan.FromMilliseconds(100) }));

        Assert.Equal("dispose", backend.Events.Last());
        if (duringInspection)
            Assert.DoesNotContain("exec", backend.Events);
    }

    [Fact]
    public async Task Timeout_also_covers_session_startup()
    {
        var backend = new RecordingBackend
        {
            BeforeStart = token => Task.Delay(Timeout.Infinite, token),
        };

        await Assert.ThrowsAsync<TimeoutException>(() => new DockerIsolatedTaskRunner(backend).RunAsync(
            Request() with { Timeout = TimeSpan.FromMilliseconds(100) }));

        Assert.Equal(new[] { "start" }, backend.Events);
    }

    [Fact]
    public async Task Caller_cancellation_remains_cancellation_and_disposes()
    {
        var backend = new RecordingBackend();
        using var caller = new CancellationTokenSource();
        backend.Session.Execute = (_, token) =>
        {
            caller.Cancel();
            token.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Cancellation was not propagated.");
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new DockerIsolatedTaskRunner(backend).RunAsync(Request(), caller.Token));

        Assert.Equal("dispose", backend.Events.Last());
    }

    [Fact]
    public async Task Already_cancelled_task_does_not_allocate_a_session()
    {
        var backend = new RecordingBackend();
        using var caller = new CancellationTokenSource();
        caller.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new DockerIsolatedTaskRunner(backend).RunAsync(Request(), caller.Token));

        Assert.Empty(backend.Events);
    }

    [Fact]
    public async Task Command_is_snapshotted_before_asynchronous_start()
    {
        var backend = new RecordingBackend();
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        backend.BeforeStart = _ => started.Task;
        var argv = new[] { "parser", "original" };
        var execution = new DockerIsolatedTaskRunner(backend).RunAsync(new IsolatedTaskRequest("task-image:local", argv));
        argv[0] = "different-program";
        argv[1] = "changed";
        started.SetResult(true);

        await execution;

        Assert.Equal(new[] { "parser", "original" }, backend.Session.ExecutedCommand);
    }

    public static IEnumerable<object[]> InvalidRequests()
    {
        yield return new object[] { Request() with { Image = "--privileged" } };
        yield return new object[] { Request() with { Image = "image with spaces" } };
        yield return new object[] { Request() with { Image = "image\0suffix" } };
        yield return new object[] { Request() with { Image = null! } };
        yield return new object[] { Request() with { Command = Array.Empty<string>() } };
        yield return new object[] { Request() with { Command = new[] { " " } } };
        yield return new object[] { Request() with { Command = new[] { "parser", null! } } };
        yield return new object[] { Request() with { Command = new[] { "parser", "a\0b" } } };
        yield return new object[] { Request() with { MemoryBytes = 0 } };
        yield return new object[] { Request() with { Pids = 0 } };
        yield return new object[] { Request() with { Cpus = 0m } };
        yield return new object[] { Request() with { Cpus = 1025m } };
        yield return new object[] { Request() with { Timeout = TimeSpan.Zero } };
        yield return new object[] { Request() with { Timeout = TimeSpan.FromDays(2) } };
        yield return new object[] { Request() with { MaxOutputBytes = 0 } };
        yield return new object[] { Request() with { MaxOutputBytes = -1 } };
        yield return new object[] { Request() with { MaxOutputBytes = 16 * 1024 * 1024 + 1 } };
    }

    [Theory]
    [MemberData(nameof(InvalidRequests))]
    public async Task Invalid_task_is_refused_before_allocating_any_environment(IsolatedTaskRequest request)
    {
        var backend = new RecordingBackend();

        await Assert.ThrowsAnyAsync<ArgumentException>(() => new DockerIsolatedTaskRunner(backend).RunAsync(request));

        Assert.Empty(backend.Events);
    }

    [Fact]
    [Trait("Category", "ProdStyle")]
    public async Task Opt_in_composition_runs_real_session_lifecycle_with_fresh_container_per_task()
    {
        var docker = new RecordingProcessRunner();
        var services = new ServiceCollection();
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IIsolatedTaskRunner));
        services.AddSingleton<IProcessCommandRunner>(docker);
        services.AddAshlarIsolatedTasks();
        services.AddAshlarIsolatedTasks();
        using var provider = services.BuildServiceProvider();
        var runner = provider.GetRequiredService<IIsolatedTaskRunner>();
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(ISandboxedSessionRunner));
        Assert.Same(docker, provider.GetRequiredService<IProcessCommandRunner>());
        Assert.Single(services, d => d.ServiceType == typeof(IIsolatedTaskRunner));
        Assert.Empty(docker.Calls);

        var first = await runner.RunAsync(Request());
        var second = await runner.RunAsync(Request());

        Assert.NotEqual(first.Environment.SessionId, second.Environment.SessionId);
        Assert.Equal("parsed", first.Command.StdOut);
        Assert.Equal(new[] { 1024 * 1024, 1024 * 1024 }, docker.OutputCaps);
        Assert.Equal(new[] { "run", "inspect", "version", "exec", "rm", "run", "inspect", "version", "exec", "rm" },
            docker.Calls.Select(call => call[0]));
        foreach (var start in docker.Calls.Where(call => call[0] == "run"))
        {
            Assert.Contains("--network=none", start);
            Assert.Contains("--read-only", start);
            Assert.Contains("--tmpfs", start);
            Assert.DoesNotContain("-v", start);
            Assert.DoesNotContain("--env", start);
            Assert.Contains("268435456", start);
            Assert.Contains("64", start);
        }
        foreach (var execution in docker.Calls.Where(call => call[0] == "exec"))
            Assert.Equal(Request().Command, execution.Skip(2));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "ProdStyle")]
    public async Task Opt_in_preserves_host_supplied_session_runner_and_provenance_sink(bool registerHostFirst)
    {
        var backend = new RecordingBackend();
        var provenance = new RecordingProvenance();
        var services = new ServiceCollection();
        if (!registerHostFirst)
            services.AddAshlarIsolatedTasks();
        services.AddSingleton<ISandboxedSessionRunner>(backend);
        services.AddSingleton<ISessionProvenanceSink>(provenance);
        if (registerHostFirst)
            services.AddAshlarIsolatedTasks();
        using var provider = services.BuildServiceProvider();

        Assert.Same(backend, provider.GetRequiredService<ISandboxedSessionRunner>());
        Assert.Same(provenance, provider.GetRequiredService<ISessionProvenanceSink>());
        Assert.IsType<DockerIsolatedTaskRunner>(provider.GetRequiredService<IIsolatedTaskRunner>());
        Assert.Empty(backend.Events);

        await provider.GetRequiredService<IIsolatedTaskRunner>().RunAsync(Request());

        Assert.Equal(new[] { "start", "attest", "exec", "dispose" }, backend.Events);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "ProdStyle")]
    public async Task Opt_in_cannot_suppress_autonomy_image_pin_in_either_registration_order(bool tasksFirst)
    {
        var docker = new RecordingProcessRunner();
        var services = new ServiceCollection();
        services.AddSingleton<IProcessCommandRunner>(docker);
        services.Configure<AshlarAutonomyOptions>(options => options.SessionImageDigest = "sha256:pinned");
        if (tasksFirst)
            services.AddAshlarIsolatedTasks();
        services.AddAshlarAutonomy();
        if (!tasksFirst)
            services.AddAshlarIsolatedTasks();
        using var provider = services.BuildServiceProvider();
        var sessionRunner = Assert.IsType<DockerSandboxedSessionRunner>(provider.GetRequiredService<ISandboxedSessionRunner>());
        Assert.Equal("sha256:pinned", sessionRunner.ExpectedImageDigest);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.GetRequiredService<IIsolatedTaskRunner>().RunAsync(Request()));

        Assert.Single(docker.Calls);
        Assert.Equal("image", docker.Calls[0][0]);
        Assert.DoesNotContain(docker.Calls, call => call[0] == "run");
    }

    [OptInFact("ASHLAR_RUN_ISOLATED_TASK_DOCKER", "Linux Docker with a preloaded shell/sleep image",
        RequiredEnvironmentVariables = new[] { "ASHLAR_ISOLATED_TASK_TEST_IMAGE" }, Timeout = 60_000)]
    [Trait("Category", "External")]
    public async Task Real_Docker_task_has_ephemeral_scratch_readonly_root_and_removes_container()
    {
        var image = Environment.GetEnvironmentVariable("ASHLAR_ISOLATED_TASK_TEST_IMAGE")!;
        var process = new ProcessCommandRunner();
        var runner = new DockerIsolatedTaskRunner(new DockerSandboxedSessionRunner(process));
        var request = new IsolatedTaskRequest(image, new[]
        {
            "/bin/sh", "-c",
            "test ! -e /tmp/ashlar-task-marker || exit 41; "
            + "echo ephemeral > /tmp/ashlar-task-marker; "
            + "if touch /ashlar-task-forbidden 2>/dev/null; then exit 42; fi; cat /tmp/ashlar-task-marker",
        }) { Timeout = TimeSpan.FromSeconds(15) };

        var first = await runner.RunAsync(request);
        var second = await runner.RunAsync(request);

        Assert.True(first.Command.Succeeded, first.Command.StdErr);
        Assert.True(second.Command.Succeeded, second.Command.StdErr);
        Assert.Equal("ephemeral", first.Command.StdOut.Trim());
        Assert.NotEqual(first.Environment.SessionId, second.Environment.SessionId);
        using var inspectionBudget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        foreach (var result in new[] { first, second })
        {
            var inspection = await process.RunAsync("docker", new[] { "inspect", result.Environment.SessionId }, inspectionBudget.Token);
            Assert.False(inspection.Succeeded, "The temporary container must be removed before RunAsync returns.");
        }
    }

    [OptInFact("ASHLAR_RUN_ISOLATED_TASK_DOCKER", "Linux Docker with a preloaded shell/sleep image",
        RequiredEnvironmentVariables = new[] { "ASHLAR_ISOLATED_TASK_TEST_IMAGE" }, Timeout = 60_000)]
    [Trait("Category", "External")]
    public async Task Real_Docker_output_flood_is_bounded_and_the_container_is_removed()
    {
        var image = Environment.GetEnvironmentVariable("ASHLAR_ISOLATED_TASK_TEST_IMAGE")!;
        var process = new ProcessCommandRunner();
        var sessions = new ObservedSessionRunner(new DockerSandboxedSessionRunner(process));
        var runner = new DockerIsolatedTaskRunner(sessions);
        var request = new IsolatedTaskRequest(image, new[]
        {
            "/bin/sh", "-c",
            "while :; do printf 'stdout flood 0123456789\\n'; printf 'stderr flood 0123456789\\n' >&2; done",
        }) { MaxOutputBytes = 4096, Timeout = TimeSpan.FromSeconds(15) };

        var failure = await Assert.ThrowsAsync<ProcessOutputLimitExceededException>(() => runner.RunAsync(request));

        Assert.Equal(4096, failure.MaxOutputBytes);
        var sessionId = Assert.Single(sessions.SessionIds);
        using var inspectionBudget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var listing = await process.RunAsync("docker", new[]
        {
            "ps", "--all", "--filter", $"name={sessionId}", "--format", "{{.Names}}",
        }, inspectionBudget.Token);
        Assert.True(listing.Succeeded, listing.StdErr);
        Assert.DoesNotContain(sessionId, listing.StdOut);
        var inspection = await process.RunAsync("docker", new[] { "inspect", sessionId }, inspectionBudget.Token);
        Assert.False(inspection.Succeeded, "Output overflow must remove the temporary container before returning.");
    }

    private sealed class ObservedSessionRunner(ISandboxedSessionRunner inner) : ISandboxedSessionRunner
    {
        public List<string> SessionIds { get; } = new();

        public async Task<ISandboxedSession> StartAsync(SandboxSpec spec, CancellationToken cancellationToken = default)
        {
            var session = await inner.StartAsync(spec, cancellationToken);
            SessionIds.Add(session.SessionId);
            return session;
        }
    }

    private sealed class RecordingBackend : ISandboxedSessionRunner
    {
        public List<string> Events { get; } = new();
        public RecordingSession Session { get; }
        public SandboxSpec? Spec { get; private set; }
        public Func<CancellationToken, Task>? BeforeStart { get; set; }
        public bool SupportsBoundedOutput { get; init; } = true;

        public RecordingBackend() => Session = new RecordingSession(Events);

        public async Task<ISandboxedSession> StartAsync(SandboxSpec spec, CancellationToken cancellationToken = default)
        {
            Events.Add("start");
            Spec = spec;
            if (BeforeStart is not null)
                await BeforeStart(cancellationToken);
            return SupportsBoundedOutput ? Session : new UnboundedSession(Session);
        }
    }

    private sealed class UnboundedSession(RecordingSession inner) : ISandboxedSession
    {
        public string SessionId => inner.SessionId;
        public Task<ProcessCommandResult> ExecAsync(IReadOnlyList<string> command, CancellationToken cancellationToken = default) =>
            inner.ExecAsync(command, cancellationToken);
        public Task<SessionAttestation> AttestAsync(CancellationToken cancellationToken = default) => inner.AttestAsync(cancellationToken);
        public Task StopAsync(CancellationToken cancellationToken = default) => inner.StopAsync(cancellationToken);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed class RecordingSession(List<string> events) : IBoundedOutputSandboxedSession
    {
        public string SessionId { get; } = "session-for-task";
        public IReadOnlyList<string>? ExecutedCommand { get; private set; }
        public int? ExecutedMaxOutputBytes { get; private set; }
        public Func<CancellationToken, Task<SessionAttestation>>? Inspect { get; set; }
        public Func<IReadOnlyList<string>, CancellationToken, Task<ProcessCommandResult>>? Execute { get; set; }

        public SessionAttestation ValidAttestation() => new()
        {
            SessionId = SessionId,
            Image = "task-image:local",
            ImageDigest = "sha256:verified",
            Requested = new ResourceLimits("268435456", 64, "1", TimeSpan.FromMinutes(1)),
            EffectiveMemoryBytes = 256L * 1024 * 1024,
            EffectivePidsLimit = 64,
            EffectiveNanoCpus = 1_000_000_000,
            EffectiveNetworkMode = "none",
            EffectiveReadOnlyRootFilesystem = true,
            EffectiveDroppedCapabilities = new[] { "ALL" },
            EffectiveSecurityOptions = new[] { "no-new-privileges:true" },
            AttestedAt = DateTimeOffset.UnixEpoch,
        };

        public Task<SessionAttestation> AttestAsync(CancellationToken cancellationToken = default)
        {
            events.Add("attest");
            return Inspect?.Invoke(cancellationToken) ?? Task.FromResult(ValidAttestation());
        }

        public Task<ProcessCommandResult> ExecAsync(IReadOnlyList<string> command, CancellationToken cancellationToken = default)
        {
            events.Add("unbounded-exec");
            return ExecuteCommand(command, cancellationToken);
        }

        public Task<ProcessCommandResult> ExecBoundedAsync(IReadOnlyList<string> command, int maxOutputBytes,
            CancellationToken cancellationToken = default)
        {
            events.Add("exec");
            ExecutedMaxOutputBytes = maxOutputBytes;
            return ExecuteCommand(command, cancellationToken);
        }

        private Task<ProcessCommandResult> ExecuteCommand(IReadOnlyList<string> command, CancellationToken cancellationToken)
        {
            ExecutedCommand = command.ToArray();
            return Execute?.Invoke(command, cancellationToken)
                ?? Task.FromResult(new ProcessCommandResult(0, "parsed", "diagnostic"));
        }

        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask DisposeAsync()
        {
            events.Add("dispose");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingProcessRunner : IBoundedOutputProcessCommandRunner
    {
        public List<IReadOnlyList<string>> Calls { get; } = new();
        public List<int> OutputCaps { get; } = new();

        public Task<ProcessCommandResult> RunBoundedAsync(string fileName, IReadOnlyList<string> arguments,
            int maxOutputBytes, CancellationToken cancellationToken = default)
        {
            OutputCaps.Add(maxOutputBytes);
            return RunAsync(fileName, arguments, cancellationToken);
        }

        public Task<ProcessCommandResult> RunAsync(string fileName, IReadOnlyList<string> arguments,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal("docker", fileName);
            Calls.Add(arguments.ToArray());
            var output = arguments[0] switch
            {
                "inspect" => "sha256:verified\t268435456\t64\t1000000000\tnone\ttrue\t[ALL]\t[no-new-privileges:true]",
                "version" => "29.0.0",
                "exec" => "parsed",
                _ => "",
            };
            return Task.FromResult(new ProcessCommandResult(0, output, ""));
        }
    }

    private sealed class RecordingProvenance : ISessionProvenanceSink
    {
        public void Record(SessionProvenanceEvent provenanceEvent) { }
    }
}
