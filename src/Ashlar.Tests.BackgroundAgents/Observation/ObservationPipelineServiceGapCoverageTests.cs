using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Ashlar.BackgroundAgents.Observation;
using Ashlar.Core.Application.Observation.Models;
using Ashlar.Core.Application.Observation.Ports;
using Ashlar.Core.Application.Trust.Ports;
using Ashlar.Infrastructure.Observation;
using Xunit;

namespace Ashlar.Tests.BackgroundAgents.Observation;

/// <summary>Tests for observation pipeline service gap coverage.</summary>
public sealed class ObservationPipelineServiceGapCoverageTests
{
    /// <summary>
    /// Upper bound on waiting for a file-system notification to reach the pipeline. A HANG NET,
    /// not a performance budget: healthy runs signal in milliseconds, but this repository has
    /// measured a queued thread-pool work item running 6m17s late under coverage instrumentation
    /// (see <c>TestTimeouts.HostTouching</c> in Ashlar.Tests.Infrastructure, which this assembly
    /// does not reference). Sized to match <c>TestTimeouts.FileSystemPipelineIntegration</c>.
    /// </summary>
    private const int FileSystemPipelineTimeoutMs = 240_000;

    /// <summary>
    /// The SourceId <see cref="FileSystemEventSource"/> stamps on every event it emits. The
    /// composite also carries a <c>ProcessEventSource</c>, whose events reach the gate too, so a
    /// test that counts gate verdicts has to count the ones it actually provoked.
    /// </summary>
    private const string FileSystemSourceId = "file-system";

    /// <summary>
    /// Re-touch <paramref name="path"/> on a short cadence until <paramref name="signal"/>
    /// completes, then return.
    ///
    /// <para>A polled condition with a timeout, not a sleep. Two things make a single write
    /// insufficient: the FileSystemWatcher arms on a thread-pool thread we do not control, and
    /// inotify/FSEvents only deliver what was raised after arming, so a write that loses the race
    /// is simply never seen. Re-touching gives a late-arming watcher another chance while a
    /// genuinely broken pipeline still fails at the hang net rather than passing by luck.</para>
    /// </summary>
    private static async Task TouchUntilAsync(string path, Task signal, CancellationToken cancellationToken)
    {
        var edit = 0;
        while (!signal.IsCompleted)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await File.WriteAllTextAsync(path, $"// edit {edit++}", cancellationToken);
            var settled = await Task.WhenAny(signal, Task.Delay(80, cancellationToken)).ConfigureAwait(false);
            if (settled == signal)
                break;
        }

        await signal.WaitAsync(cancellationToken);
    }

    [Fact]
    public void Constructor_rejects_null_dependencies()
    {
        var options = Options.Create(new ObservationPipelineOptions());
        var store = new LiteDbPatternStore(Path.Combine(Path.GetTempPath(), $"ashlar_obs_gap_{Guid.NewGuid():N}.db"));

        var act1 = () => new ObservationPipelineService(null!, store, NullLogger<ObservationPipelineService>.Instance, NullLoggerFactory.Instance);
        act1.Should().Throw<ArgumentNullException>();

        var act2 = () => new ObservationPipelineService(options, null!, NullLogger<ObservationPipelineService>.Instance, NullLoggerFactory.Instance);
        act2.Should().Throw<ArgumentNullException>();

        var act3 = () => new ObservationPipelineService(options, store, null!, NullLoggerFactory.Instance);
        act3.Should().Throw<ArgumentNullException>();

        var act4 = () => new ObservationPipelineService(options, store, NullLogger<ObservationPipelineService>.Instance, null!);
        act4.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task ExecuteAsync_with_no_existing_watch_paths_idles_until_cancelled()
    {
        var root = Path.Combine(Path.GetTempPath(), "ashlar-obs-idle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var options = Options.Create(new ObservationPipelineOptions
            {
                RepoRoot = root,
                StorePath = $"ashlar_test_{Guid.NewGuid():N}.db",
                WatchPaths = new[] { "missing-dir" },
            });
            var storePath = Path.Combine(root, options.Value.StorePath);
            var service = new ObservationPipelineService(
                options,
                new LiteDbPatternStore(storePath),
                NullLogger<ObservationPipelineService>.Instance,
                NullLoggerFactory.Instance);

            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
            await service.StartAsync(cts.Token);
            await Task.Delay(50);
            await service.StopAsync(CancellationToken.None);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_processes_events_when_watch_path_exists()
    {
        var root = Path.Combine(Path.GetTempPath(), "ashlar-obs-watch-" + Guid.NewGuid().ToString("N"));
        var watchDir = Path.Combine(root, "src");
        Directory.CreateDirectory(watchDir);
        try
        {
            var options = Options.Create(new ObservationPipelineOptions
            {
                RepoRoot = root,
                StorePath = $"ashlar_test_{Guid.NewGuid():N}.db",
                WatchPaths = new[] { "src" },
                PatternWindowSeconds = 5,
                RepeatedEditThreshold = 1,
            });
            var storePath = Path.Combine(root, options.Value.StorePath);
            var service = new ObservationPipelineService(
                options,
                new LiteDbPatternStore(storePath),
                NullLogger<ObservationPipelineService>.Instance,
                NullLoggerFactory.Instance);

            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(800));
            await service.StartAsync(cts.Token);
            await File.WriteAllTextAsync(Path.Combine(watchDir, "touch.cs"), "// ping");
            await Task.Delay(200);
            await service.StopAsync(CancellationToken.None);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_skips_events_blocked_by_observation_gate()
    {
        var root = Path.Combine(Path.GetTempPath(), "ashlar-obs-gate-" + Guid.NewGuid().ToString("N"));
        var watchDir = Path.Combine(root, "src");
        Directory.CreateDirectory(watchDir);
        try
        {
            var store = new Mock<IPatternStore>();
            var gate = new Mock<IObservationGate>();

            // The SECOND file-system verdict is the signal, and the second one is what makes the
            // Times.Never below an assertion rather than a statement about how quickly this test
            // reached StopAsync.
            //
            // ObservationPipelineService's consume loop is strictly sequential: ask the gate,
            // then -- only if allowed through -- `await patternDetector.ProcessAsync(evt)`. So
            // verdict N+1 cannot be asked for until event N has finished being processed. With
            // RepeatedEditThreshold = 1 a single unblocked file event is already a
            // "repeated-edits" pattern, so a pipeline that ignored a `false` verdict would have
            // reached store.AddAsync before this signal could fire. Waiting for the FIRST verdict
            // proved only that the gate was consulted: that event was still in flight when the
            // service was torn down, so ignoring the verdict changed nothing the test could see.
            var fileVerdicts = 0;
            var secondFileVerdict = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            gate.Setup(g => g.ShouldObserve(It.IsAny<string>(), FileSystemSourceId, It.IsAny<string?>()))
                .Callback(() =>
                {
                    if (Interlocked.Increment(ref fileVerdicts) >= 2)
                        secondFileVerdict.TrySetResult();
                })
                .Returns(false);
            gate.Setup(g => g.ShouldObserve(It.IsAny<string>(), It.Is<string>(s => s != FileSystemSourceId), It.IsAny<string?>()))
                .Returns(false);

            var options = Options.Create(new ObservationPipelineOptions
            {
                RepoRoot = root,
                StorePath = $"ashlar_test_{Guid.NewGuid():N}.db",
                WatchPaths = new[] { "src" },
                PatternWindowSeconds = 30,

                // One edit is a pattern. At the default of 3, a pipeline that ignored the gate
                // would still store nothing, for a reason that has nothing to do with the gate.
                RepeatedEditThreshold = 1,
            });

            var service = new ObservationPipelineService(
                options,
                store.Object,
                NullLogger<ObservationPipelineService>.Instance,
                NullLoggerFactory.Instance,
                gate.Object);

            // Hang net, not a budget. At 800 ms this token was a second racing deadline that
            // cancelled the pipeline out from under the assertion whenever the runner was loaded.
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(FileSystemPipelineTimeoutMs));
            await service.StartAsync(cts.Token);
            await TouchUntilAsync(Path.Combine(watchDir, "blocked.cs"), secondFileVerdict.Task, cts.Token);
            await service.StopAsync(CancellationToken.None);

            store.Verify(
                s => s.AddAsync(It.IsAny<ObservedPattern>(), It.IsAny<CancellationToken>()),
                Times.Never);
            gate.Verify(
                g => g.ShouldObserve(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()),
                Times.AtLeastOnce);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_degrades_gracefully_on_storage_io_errors()
    {
        var root = Path.Combine(Path.GetTempPath(), "ashlar-obs-io-" + Guid.NewGuid().ToString("N"));
        var watchDir = Path.Combine(root, "src");
        Directory.CreateDirectory(watchDir);
        try
        {
            var store = new Mock<IPatternStore>();

            // Moq runs the Callback before the Throws, so the signal fires on the one and only
            // AddAsync this test ever gets: the IOException makes the pipeline return, so
            // Times.AtLeastOnce here is really exactly-once and the old ~440 ms budget in front of
            // it was the whole synchronization.
            var addAttempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            store.Setup(s => s.AddAsync(It.IsAny<ObservedPattern>(), It.IsAny<CancellationToken>()))
                .Callback(() => addAttempted.TrySetResult())
                .ThrowsAsync(new IOException("store unavailable"));

            var options = Options.Create(new ObservationPipelineOptions
            {
                RepoRoot = root,
                StorePath = $"ashlar_test_{Guid.NewGuid():N}.db",
                WatchPaths = new[] { "src" },
                PatternWindowSeconds = 30,
                RepeatedEditThreshold = 1,
            });

            var service = new ObservationPipelineService(
                options,
                store.Object,
                NullLogger<ObservationPipelineService>.Instance,
                NullLoggerFactory.Instance);

            // Hang net, not a budget.
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(FileSystemPipelineTimeoutMs));
            await service.StartAsync(cts.Token);

            await TouchUntilAsync(Path.Combine(watchDir, "repeat.cs"), addAttempted.Task, cts.Token);

            // StopAsync joins ExecuteAsync but never OBSERVES it: BackgroundService.StopAsync
            // awaits Task.WhenAny(ExecuteTask, Task.Delay(Timeout.Infinite, cancellationToken)),
            // which with CancellationToken.None waits for ExecuteTask to complete and then
            // returns, discarding whatever it completed with. "StopAsync did not throw" is
            // therefore structurally true and says nothing at all about degradation. ExecuteTask
            // is where the verdict actually lives -- and under a host a faulted one stops the host
            // (BackgroundServiceExceptionBehavior.StopHost is the .NET 6+ default), which is
            // precisely the outcome this test exists to prevent.
            await service.StopAsync(CancellationToken.None);

            service.ExecuteTask.Should().NotBeNull("StartAsync must have begun ExecuteAsync");
            service.ExecuteTask!.IsCompletedSuccessfully.Should().BeTrue(
                "a storage IO error must be swallowed and the service left standing, but "
                + "ExecuteAsync ended as {0}: {1}",
                service.ExecuteTask.Status,
                service.ExecuteTask.Exception?.GetBaseException().ToString() ?? "<no exception>");

            store.Verify(
                s => s.AddAsync(It.IsAny<ObservedPattern>(), It.IsAny<CancellationToken>()),
                Times.AtLeastOnce);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_rethrows_unexpected_pipeline_failures()
    {
        var root = Path.Combine(Path.GetTempPath(), "ashlar-obs-fail-" + Guid.NewGuid().ToString("N"));
        var watchDir = Path.Combine(root, "src");
        Directory.CreateDirectory(watchDir);
        try
        {
            var store = new Mock<IPatternStore>();

            // Without this signal the assertion below was vacuous: on a loaded runner the
            // pipeline was routinely cancelled before AddAsync was ever reached, so "StopAsync
            // does not throw" was measured on a pipeline that never hit the failure path at all.
            var addAttempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            store.Setup(s => s.AddAsync(It.IsAny<ObservedPattern>(), It.IsAny<CancellationToken>()))
                .Callback(() => addAttempted.TrySetResult())
                .ThrowsAsync(new InvalidOperationException("unexpected store failure"));

            var options = Options.Create(new ObservationPipelineOptions
            {
                RepoRoot = root,
                StorePath = $"ashlar_test_{Guid.NewGuid():N}.db",
                WatchPaths = new[] { "src" },
                PatternWindowSeconds = 30,
                RepeatedEditThreshold = 1,
            });

            var service = new ObservationPipelineService(
                options,
                store.Object,
                NullLogger<ObservationPipelineService>.Instance,
                NullLoggerFactory.Instance);

            // Hang net, not a budget.
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(FileSystemPipelineTimeoutMs));
            await service.StartAsync(cts.Token);

            await TouchUntilAsync(Path.Combine(watchDir, "boom.cs"), addAttempted.Task, cts.Token);

            // Not "StopAsync should not throw": that was structurally true (see the sibling test
            // above -- StopAsync joins ExecuteTask with Task.WhenAny and never observes its
            // result), so it held whether ExecuteAsync rethrew or swallowed. The rethrow lands on
            // ExecuteTask, and reading .Exception here is also what keeps it from resurfacing as
            // an unobserved task exception at finalization.
            await service.StopAsync(CancellationToken.None);

            service.ExecuteTask.Should().NotBeNull("StartAsync must have begun ExecuteAsync");
            service.ExecuteTask!.IsFaulted.Should().BeTrue(
                "an unexpected store failure must propagate out of ExecuteAsync rather than be "
                + "degraded away, but ExecuteAsync ended as {0}",
                service.ExecuteTask.Status);
            service.ExecuteTask.Exception!.InnerExceptions.Should().ContainSingle()
                .Which.Should().BeOfType<InvalidOperationException>()
                .Which.Message.Should().Be("unexpected store failure");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
