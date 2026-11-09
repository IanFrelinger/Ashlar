using FluentAssertions;
using Ashlar.Core.Application.Execution.Ports;
using Ashlar.Infrastructure.Certification.HotSwap;
using Ashlar.Infrastructure.Execution.Sandbox;
using Ashlar.Infrastructure.Scaling;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Execution;

/// <summary>
/// Docker backend for session sandboxes (extension spec Part B): a session is started
/// detached with name + deadline labels, work goes through <c>docker exec</c>, teardown is
/// guaranteed on every disposal path and idempotent, use-after-teardown is loud, and the
/// reaper sweeps leaked containers by deadline — fail-closed for sessions whose deadline
/// is unreadable. (The backend-neutral port and TestKit fake contracts are pinned in
/// <c>Ashlar.Tests.Application</c>; this suite exercises the Infrastructure backend.)
/// </summary>
public sealed class DockerSandboxSessionTests
{
    private static SandboxSpec KeepaliveSpec(TimeSpan? timeout = null) => new(
        Image: "proposer:latest",
        Mounts: new[] { new Mount("/host/work", "/work", ReadOnly: false) },
        Network: NetworkAccess.None,
        Command: new[] { "sleep", "infinity" },
        Limits: new ResourceLimits(Memory: "2g", Timeout: timeout));

    // --- start ---------------------------------------------------------------------------

    [Fact]
    public void BuildStartArguments_RunsDetached_WithNameAndSessionAndDeadlineLabels()
    {
        var args = DockerSandboxedSessionRunner.BuildStartArguments(
            KeepaliveSpec(), "ashlar-session-abc", deadlineUnixSeconds: 1234567890);

        args.Should().ContainInOrder("run", "-d", "--rm", "--name", "ashlar-session-abc");
        args.Should().ContainInOrder("--label", DockerSandboxedSessionRunner.SessionLabel);
        args.Should().ContainInOrder("--label", $"{DockerSandboxedSessionRunner.DeadlineLabelKey}=1234567890");
        args.Should().Contain("--network=none");
        args.Should().Contain("/host/work:/work", "the session workspace mount is read-write");
        args.Should().ContainInOrder("proposer:latest", "sleep", "infinity");
    }

    [Fact]
    public void BuildStartArguments_HardensTheSessionContainer_ExactlyLikeOneShotRuns()
    {
        // Sessions and one-shot runs share one translation; the hardening flags ride it,
        // so a session container is never a softer container than a one-shot one.
        var spec = KeepaliveSpec() with { ScratchPaths = new[] { "/ashlar-candidate", "/tmp" } };

        var args = DockerSandboxedSessionRunner.BuildStartArguments(spec, "ashlar-session-abc", 0);

        args.Should().ContainInOrder("--cap-drop", "ALL");
        args.Should().ContainInOrder("--security-opt", "no-new-privileges");
        args.Should().ContainInOrder(new[] { "--pull", "never" },
            "the image must already be present and attested — a session never fetches one");
        args.Should().Contain("--read-only");
        args.Should().ContainInOrder("--tmpfs", $"/ashlar-candidate:{DockerSandboxedCommandRunner.ScratchPathMountOptions}");
        args.Should().ContainInOrder("--tmpfs", $"/tmp:{DockerSandboxedCommandRunner.ScratchPathMountOptions}");
        var ordered = args.ToList();
        ordered.IndexOf("--read-only").Should().BeLessThan(ordered.IndexOf("proposer:latest"));
        ordered.IndexOf("--pull").Should().BeLessThan(ordered.IndexOf("proposer:latest"));
    }

    [Fact]
    public void BuildStartArguments_Throws_WithoutKeepaliveCommand()
    {
        var spec = KeepaliveSpec() with { Command = Array.Empty<string>() };

        var act = () => DockerSandboxedSessionRunner.BuildStartArguments(spec, "s", 0);

        act.Should().Throw<ArgumentException>().WithMessage("*keepalive*");
    }

    [Fact]
    public async Task StartAsync_StampsTheDeadline_FromTheSpecTimeout()
    {
        var now = new DateTimeOffset(2026, 8, 13, 12, 0, 0, TimeSpan.Zero);
        IReadOnlyList<string>? startArgs = null;
        var docker = new FakeProcessRunner((_, args) =>
        {
            startArgs ??= args.ToArray();
            return Task.FromResult(new ProcessCommandResult(0, "container-id", ""));
        });
        var runner = new DockerSandboxedSessionRunner(docker, new FrozenClock(now));

        await using var session = await runner.StartAsync(KeepaliveSpec(TimeSpan.FromMinutes(10)));

        var expected = now.AddMinutes(10).ToUnixTimeSeconds();
        startArgs.Should().Contain($"{DockerSandboxedSessionRunner.DeadlineLabelKey}={expected}");
        session.SessionId.Should().StartWith(DockerSandboxedSessionRunner.NamePrefix);
    }

    [Fact]
    public async Task StartAsync_FailsClosed_WhenTheContainerCannotStart()
    {
        var docker = new FakeProcessRunner((_, _) =>
            Task.FromResult(new ProcessCommandResult(125, "", "no such image")));
        var runner = new DockerSandboxedSessionRunner(docker, new FrozenClock(DateTimeOffset.UnixEpoch));

        var act = () => runner.StartAsync(KeepaliveSpec());

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*no such image*");
    }

    [Fact]
    public async Task FailedStart_RemovesTheKnownContainerName_AndPreservesTheLaunchFailure()
    {
        var calls = new List<IReadOnlyList<string>>();
        var docker = new FakeProcessRunner((_, args) =>
        {
            calls.Add(args.ToArray());
            return Task.FromResult(args[0] == "run"
                ? new ProcessCommandResult(125, "", "launch failed after create")
                : new ProcessCommandResult(1, "", "cleanup also failed"));
        });
        var runner = new DockerSandboxedSessionRunner(docker);

        var act = () => runner.StartAsync(KeepaliveSpec());

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*launch failed after create*");
        var name = calls[0][calls[0].ToList().IndexOf("--name") + 1];
        calls[1].Should().Equal("rm", "-f", name);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledStart_CleansUpWithAnIndependentToken_EvenAfterSuccessfulResponse(bool returnSuccess)
    {
        using var caller = new CancellationTokenSource();
        var calls = new List<IReadOnlyList<string>>();
        var docker = new TokenAwareProcessRunner((args, token) =>
        {
            calls.Add(args.ToArray());
            if (args[0] == "run")
            {
                caller.Cancel();
                if (!returnSuccess)
                    throw new OperationCanceledException(token);
                return Task.FromResult(new ProcessCommandResult(0, "created", ""));
            }

            token.IsCancellationRequested.Should().BeFalse("launch cancellation must not prevent removal");
            return Task.FromResult(new ProcessCommandResult(0, "", ""));
        });
        var runner = new DockerSandboxedSessionRunner(docker);

        var act = () => runner.StartAsync(KeepaliveSpec(), caller.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        calls.Should().HaveCount(2);
        var name = calls[0][calls[0].ToList().IndexOf("--name") + 1];
        calls[1].Should().Equal("rm", "-f", name);
    }

    // --- exec + teardown -----------------------------------------------------------------

    [Fact]
    public async Task Exec_RoutesThroughDockerExec_AndTeardownIsIdempotentRmForce()
    {
        var calls = new List<IReadOnlyList<string>>();
        var docker = new FakeProcessRunner((_, args) =>
        {
            calls.Add(args.ToArray());
            return Task.FromResult(new ProcessCommandResult(0, "ok", ""));
        });
        var runner = new DockerSandboxedSessionRunner(docker, new FrozenClock(DateTimeOffset.UnixEpoch));

        var session = await runner.StartAsync(KeepaliveSpec());
        await session.ExecAsync(new[] { "dotnet", "build" });
        await session.StopAsync();
        await session.DisposeAsync();

        calls[1].Should().ContainInOrder("exec", session.SessionId, "dotnet", "build");
        calls.Count(a => a.Take(2).SequenceEqual(new[] { "rm", "-f" }))
            .Should().Be(1, "teardown is idempotent — stop plus dispose must not double-remove");
        calls[2].Should().ContainInOrder("rm", "-f", session.SessionId);
    }

    [Fact]
    public async Task ExecAfterTeardown_IsLoud_NeverSilentlyRecreated()
    {
        var docker = new FakeProcessRunner((_, _) => Task.FromResult(new ProcessCommandResult(0, "", "")));
        var runner = new DockerSandboxedSessionRunner(docker, new FrozenClock(DateTimeOffset.UnixEpoch));
        var session = await runner.StartAsync(KeepaliveSpec());
        await session.DisposeAsync();

        var act = () => session.ExecAsync(new[] { "dotnet", "test" });

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*stopped*");
    }

    [Fact]
    public async Task BoundedExec_ForwardsLiteralArgvOutputBudgetAndCancellation_ToTheProcessRunner()
    {
        var docker = new TokenAwareProcessRunner((args, _) => Task.FromResult(
            args[0] == "exec" ? new ProcessCommandResult(17, "partial", "diagnostic") : new ProcessCommandResult(0, "", "")));
        var runner = new DockerSandboxedSessionRunner(docker);
        await using var session = await runner.StartAsync(KeepaliveSpec());
        var bounded = Assert.IsAssignableFrom<IBoundedOutputSandboxedSession>(session);
        using var caller = new CancellationTokenSource();

        var result = await bounded.ExecBoundedAsync(new[] { "parser", "literal; $(document)" }, 8192, caller.Token);

        Assert.Equal(new ProcessCommandResult(17, "partial", "diagnostic"), result);
        var execution = Assert.Single(docker.BoundedCalls);
        Assert.Equal(new[] { "exec", session.SessionId, "parser", "literal; $(document)" }, execution.Arguments);
        Assert.Equal(8192, execution.MaxOutputBytes);
        Assert.Equal(caller.Token, execution.Token);
    }

    [Fact]
    public async Task BoundedExec_RefusesALegacyProcessRunner_WithoutExecutingTheProgram()
    {
        var calls = new List<IReadOnlyList<string>>();
        var docker = new FakeProcessRunner((_, args) =>
        {
            calls.Add(args.ToArray());
            return Task.FromResult(new ProcessCommandResult(0, "", ""));
        });
        var runner = new DockerSandboxedSessionRunner(docker);
        await using var session = await runner.StartAsync(KeepaliveSpec());
        var bounded = Assert.IsAssignableFrom<IBoundedOutputSandboxedSession>(session);

        await Assert.ThrowsAsync<NotSupportedException>(() => bounded.ExecBoundedAsync(new[] { "parser" }, 4096));

        Assert.DoesNotContain(calls, call => call[0] == "exec");
    }

    [Fact]
    public async Task BoundedExecAfterTeardown_IsRefusedBeforeCallingTheProcessRunner()
    {
        var docker = new TokenAwareProcessRunner((_, _) => Task.FromResult(new ProcessCommandResult(0, "", "")));
        var runner = new DockerSandboxedSessionRunner(docker);
        var session = await runner.StartAsync(KeepaliveSpec());
        var bounded = Assert.IsAssignableFrom<IBoundedOutputSandboxedSession>(session);
        await session.DisposeAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => bounded.ExecBoundedAsync(new[] { "parser" }, 4096));

        Assert.Empty(docker.BoundedCalls);
    }

    [Fact]
    public async Task BoundedExec_OutputOverflowIsPreserved_AndTheSessionCanStillBeDisposed()
    {
        var failure = new ProcessOutputLimitExceededException(4096);
        var calls = new List<IReadOnlyList<string>>();
        var docker = new TokenAwareProcessRunner((args, _) =>
        {
            calls.Add(args.ToArray());
            return args[0] == "exec" ? throw failure : Task.FromResult(new ProcessCommandResult(0, "", ""));
        });
        var runner = new DockerSandboxedSessionRunner(docker);
        var session = await runner.StartAsync(KeepaliveSpec());
        var bounded = Assert.IsAssignableFrom<IBoundedOutputSandboxedSession>(session);

        var actual = await Assert.ThrowsAsync<ProcessOutputLimitExceededException>(() =>
            bounded.ExecBoundedAsync(new[] { "parser" }, 4096));
        await session.DisposeAsync();

        Assert.Same(failure, actual);
        Assert.Equal(new[] { "run", "exec", "rm" }, calls.Select(call => call[0]));
        Assert.Equal(new[] { "rm", "-f", session.SessionId }, calls.Last());
    }

    [Fact]
    public async Task TeardownFailure_NeverThrows_TheReaperIsTheBackstop()
    {
        var docker = new FakeProcessRunner((_, args) =>
            Task.FromResult(args[0] == "rm"
                ? new ProcessCommandResult(1, "", "removal already in progress")
                : new ProcessCommandResult(0, "", "")));
        var runner = new DockerSandboxedSessionRunner(docker, new FrozenClock(DateTimeOffset.UnixEpoch));
        var session = await runner.StartAsync(KeepaliveSpec());

        var act = () => session.DisposeAsync().AsTask();

        await act.Should().NotThrowAsync("a failed teardown must not mask the work's own result");
    }

    [Fact]
    public async Task Teardown_StopsWaitingAtItsOwnDeadline_WithoutMaskingTheWorkResult()
    {
        var clock = new ControlledTimerClock();
        var docker = new TokenAwareProcessRunner(async (args, token) =>
        {
            if (args[0] == "rm")
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new ProcessCommandResult(0, "", "");
        });
        var runner = new DockerSandboxedSessionRunner(docker, clock);
        var session = await runner.StartAsync(KeepaliveSpec());

        var cleanup = session.DisposeAsync().AsTask();
        cleanup.IsCompleted.Should().BeFalse();
        clock.DueTime.Should().Be(DockerSandboxedSessionRunner.TeardownTimeout);
        clock.Fire();
        var act = () => cleanup.WaitAsync(TimeSpan.FromSeconds(5));

        await act.Should().NotThrowAsync("the reaper handles removal after the bounded attempt");
        var exec = () => session.ExecAsync(new[] { "echo", "after-timeout" });
        await exec.Should().ThrowAsync<InvalidOperationException>().WithMessage("*stopped*");
    }

    // --- reaper --------------------------------------------------------------------------

    [Fact]
    public void ParseSessionLine_ReadsNameAndDeadline_AndTreatsGarbageDeadlineAsMissing()
    {
        DockerSandboxSessionReaper.ParseSessionLine("ashlar-session-a\t1000")
            .Should().Be(("ashlar-session-a", (long?)1000L));
        DockerSandboxSessionReaper.ParseSessionLine("ashlar-session-b\tnot-a-number")
            .Should().Be(("ashlar-session-b", (long?)null));
        DockerSandboxSessionReaper.ParseSessionLine("   ").Should().BeNull();
    }

    [Theory]
    [InlineData(999, true)]   // deadline in the past
    [InlineData(1000, true)]  // deadline exactly now
    [InlineData(1001, false)] // still alive
    public void ShouldReap_ComparesDeadlineToNow(long deadline, bool expected)
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1000);

        DockerSandboxSessionReaper.ShouldReap(deadline, now).Should().Be(expected);
    }

    [Fact]
    public void ShouldReap_TreatsMissingDeadlineAsExpired()
    {
        DockerSandboxSessionReaper.ShouldReap(null, DateTimeOffset.UnixEpoch)
            .Should().BeTrue("a session container without a readable deadline is a leak by definition");
    }

    [Fact]
    public async Task Sweep_RemovesExpiredAndUnlabeledSessions_LeavesLiveOnesAlone()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(2000);
        var removed = new List<string>();
        var docker = new FakeProcessRunner((_, args) =>
        {
            if (args[0] == "ps")
            {
                return Task.FromResult(new ProcessCommandResult(
                    0,
                    "ashlar-session-expired\t1000\nashlar-session-live\t3000\nashlar-session-unlabeled\t\n",
                    ""));
            }

            removed.Add(args[2]);
            return Task.FromResult(new ProcessCommandResult(0, "", ""));
        });
        var reaper = new DockerSandboxSessionReaper(docker, new FrozenClock(now));

        var reaped = await reaper.SweepAsync();

        reaped.Should().BeEquivalentTo("ashlar-session-expired", "ashlar-session-unlabeled");
        removed.Should().BeEquivalentTo("ashlar-session-expired", "ashlar-session-unlabeled");
    }

    [Fact]
    public async Task Sweep_ReapsNothing_WhenTheListingFails()
    {
        var rmCalls = 0;
        var docker = new FakeProcessRunner((_, args) =>
        {
            if (args[0] == "ps")
                return Task.FromResult(new ProcessCommandResult(1, "", "daemon unreachable"));
            rmCalls++;
            return Task.FromResult(new ProcessCommandResult(0, "", ""));
        });
        var reaper = new DockerSandboxSessionReaper(docker, new FrozenClock(DateTimeOffset.UnixEpoch));

        var reaped = await reaper.SweepAsync();

        reaped.Should().BeEmpty();
        rmCalls.Should().Be(0, "an unreadable listing must not trigger blind removals — the next sweep retries");
    }

    // --- confinement + network modes (extension spec Part B) -----------------------------

    [Fact]
    public void DockerBackends_RefuseHostServicesOnly_FailClosed()
    {
        var spec = new SandboxSpec(
            "img", Array.Empty<Mount>(), NetworkAccess.HostServicesOnly, new[] { "sleep", "infinity" });

        var oneShot = () => DockerSandboxedCommandRunner.BuildDockerArguments(spec);
        var session = () => DockerSandboxedSessionRunner.BuildStartArguments(spec, "ashlar-session-x", 0);

        oneShot.Should().Throw<NotSupportedException>().WithMessage("*fail-closed*");
        session.Should().Throw<NotSupportedException>().WithMessage("*fail-closed*",
            "a session believing itself contained while holding open egress is worse than no session");
    }

    [Fact]
    public void ConfinementMounts_FlowIntoASessionSpec_Unchanged()
    {
        var confinement = new ProposerConfinement { WritablePrefixes = new[] { "src/" } };
        var spec = new SandboxSpec(
            "proposer:latest",
            confinement.ToMounts("/repo"),
            NetworkAccess.None,
            new[] { "sleep", "infinity" });

        var args = DockerSandboxedSessionRunner.BuildStartArguments(spec, "ashlar-session-x", 42);

        args.Should().Contain("/repo:/workspace:ro", "context is read-only");
        args.Should().Contain("/repo/src:/workspace/src", "the declared surface is the only writable mount");
    }

    // --- write surface under a sealed rootfs -------------------------------------------

    [Fact]
    public void HarnessScratchPaths_CoverEveryDirectoryTheSessionLegsWrite_AndBecomeScratchMounts()
    {
        // The rootfs is sealed read-only; the legs can only work if the paths they write to
        // are exactly the paths the spec declares. One catalog, checked against the legs'
        // own constants so they cannot drift apart.
        SessionScratchPaths.Default.Should().Contain(SessionCandidateBuild.WorkDir);
        SessionScratchPaths.Default.Should().Contain(SessionExecutionBackend.ExecDir);
        SessionScratchPaths.Default.Should().Contain("/tmp", "MSBuild and the compiler server keep pipes and temp files there");
        SessionScratchPaths.Default.Should().Contain("/root", "the SDK image's toolchain writes under $HOME even for an offline restore");
        SessionScratchPaths.Default.Should().OnlyHaveUniqueItems();

        var spec = KeepaliveSpec() with { Mounts = Array.Empty<Mount>(), ScratchPaths = SessionScratchPaths.Default };
        var args = DockerSandboxedSessionRunner.BuildStartArguments(spec, "ashlar-session-x", 0);

        args.Should().Contain("--read-only");
        foreach (var path in SessionScratchPaths.Default)
            args.Should().Contain($"{path}:{DockerSandboxedCommandRunner.ScratchPathMountOptions}");
    }

    // --- helpers -------------------------------------------------------------------------

    private sealed class ControlledTimerClock : TimeProvider
    {
        private TimerCallback? _callback;
        private object? _state;
        public TimeSpan DueTime { get; private set; }
        public void Fire() => _callback!(_state);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _callback = callback;
            _state = state;
            DueTime = dueTime;
            return new NoopTimer();
        }

        private sealed class NoopTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class TokenAwareProcessRunner : IBoundedOutputProcessCommandRunner
    {
        private readonly Func<IReadOnlyList<string>, CancellationToken, Task<ProcessCommandResult>> _handler;
        public List<(IReadOnlyList<string> Arguments, int MaxOutputBytes, CancellationToken Token)> BoundedCalls { get; } = new();
        public TokenAwareProcessRunner(Func<IReadOnlyList<string>, CancellationToken, Task<ProcessCommandResult>> handler) =>
            _handler = handler;
        public Task<ProcessCommandResult> RunAsync(string fileName, IReadOnlyList<string> arguments,
            CancellationToken cancellationToken = default)
        {
            fileName.Should().Be("docker");
            return _handler(arguments, cancellationToken);
        }

        public Task<ProcessCommandResult> RunBoundedAsync(string fileName, IReadOnlyList<string> arguments,
            int maxOutputBytes, CancellationToken cancellationToken = default)
        {
            BoundedCalls.Add((arguments.ToArray(), maxOutputBytes, cancellationToken));
            return RunAsync(fileName, arguments, cancellationToken);
        }
    }

    private sealed class FrozenClock : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FrozenClock(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class FakeProcessRunner : IProcessCommandRunner
    {
        private readonly Func<string, IReadOnlyList<string>, Task<ProcessCommandResult>> _handler;

        public FakeProcessRunner(Func<string, IReadOnlyList<string>, Task<ProcessCommandResult>> handler) =>
            _handler = handler;

        public Task<ProcessCommandResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken = default)
        {
            fileName.Should().Be("docker");
            return _handler(fileName, arguments);
        }
    }
}
