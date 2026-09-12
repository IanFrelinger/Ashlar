using FluentAssertions;
using Ashlar.Core.Application.Observation.Models;
using Ashlar.Infrastructure.Observation;
using Ashlar.Tests.Infrastructure.Helpers;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Observation;

/// <summary>Tests for file system event source.</summary>
public class FileSystemEventSourceTests : IDisposable
{
    private readonly string _tempDir;

    public FileSystemEventSourceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ashlar_observe_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    /// <summary>Dispose.</summary>
    public void Dispose() => Dispose(true);
    protected virtual void Dispose(bool disposing)
    {
        if (disposing && Directory.Exists(_tempDir))
        {
            try
            {
                Directory.Delete(_tempDir, true);
            }
            catch
            {
            }
        }
    }

    [Fact]
    public async Task SubscribeAsync_FileCreated_EmitsEvent()
    {
        var source = new FileSystemEventSource(new[] { _tempDir }, _tempDir, new[] { "*" }, null);
        var received = new TaskCompletionSource<NormalizedEvent>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Hang net, not a performance budget: healthy runs finish in under a second, but
        // under kernel-coverage-gate instrumentation this test's file write once executed
        // 6m17s late and a 20s token turned that stall into a red build (see
        // docs/production-readiness/KernelCoverageGate-Findings.md).
        var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(TestTimeouts.HostTouching));

        var consumeTask = Task.Run(async () =>
        {
            await foreach (var evt in source.SubscribeAsync(cts.Token))
            {
                received.TrySetResult(evt);
                break;
            }
        }, cts.Token);

        // The watchers are armed inside the FIRST MoveNextAsync, which runs on whichever
        // thread-pool thread eventually dequeues the Task.Run above -- and inotify/FSEvents only
        // deliver events raised AFTER arming. A fixed sleep here is not a synchronization
        // primitive: Task.Delay's continuation is dispatched on the pool's high-priority path and
        // routinely overtakes an ordinary work item queued before it, so "the delay elapsed" says
        // nothing about whether the watcher is watching. Touch the file on a short cadence
        // instead, until the event lands or the hang net trips -- a polled condition with a
        // timeout, which is a real primitive, and which also tolerates FSEvents coalescing.
        var filePath = Path.Combine(_tempDir, "test.txt");
        while (!received.Task.IsCompleted)
        {
            cts.Token.ThrowIfCancellationRequested();
            await File.WriteAllTextAsync(filePath, "hello", cts.Token);
            var settled = await Task.WhenAny(received.Task, Task.Delay(50, cts.Token));
            if (settled == received.Task)
                break;
        }

        var first = await received.Task.WaitAsync(cts.Token);
        await consumeTask;

        // Deliberately not ContainSingle: one WriteAllText already raises Created AND Changed on
        // most platforms, and the re-touch loop above can raise more. The claim under test is what
        // the source emits, not how many times the OS noticed.
        first.SourceId.Should().Be("file-system");
        first.Category.Should().Be("file-paths");
        first.ProjectPath.Should().Be(_tempDir);
    }

    /// <summary>
    /// Subscribing to a disposed source ends cleanly instead of throwing.
    ///
    /// <para>This is the safe, observable half of a hazard whose other half is fatal. The source
    /// used to pair a ConcurrentQueue with a SemaphoreSlim that Dispose disposed. SemaphoreSlim
    /// throws <see cref="ObjectDisposedException"/> from both ends of that pair: from WaitAsync,
    /// which surfaces here as a test failure, and from Release on a FileSystemWatcher callback
    /// thread, where there is no handler above it and the whole process dies -- taking every other
    /// test in the host with it. A completed Channel has no disposed state to throw from: the
    /// reader ends, and a callback still in flight is a TryWrite that returns false.</para>
    ///
    /// <para>The fatal half is deliberately not exercised by a test. Provoking it means racing a
    /// live watcher callback against Dispose, which on the unfixed source kills the test host
    /// rather than failing a test -- the exact outcome this change exists to prevent.</para>
    /// </summary>
    [Fact]
    public async Task SubscribeAsync_AfterDispose_EndsWithoutThrowing()
    {
        var source = new FileSystemEventSource(new[] { _tempDir }, _tempDir, new[] { "*" }, null);
        source.Dispose();

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(TestTimeouts.Quick));
        var collected = new List<NormalizedEvent>();

        var enumerate = async () =>
        {
            await foreach (var evt in source.SubscribeAsync(cts.Token).WithCancellation(cts.Token))
                collected.Add(evt);
        };

        await enumerate.Should().NotThrowAsync(
            "a disposed source must be inert, not a source of exceptions on whichever thread "
            + "happens to touch it next");
        collected.Should().BeEmpty();
    }

    /// <summary>Disposing twice, and disposing without ever subscribing, are both no-ops.</summary>
    [Fact]
    public void Dispose_IsIdempotent()
    {
        var source = new FileSystemEventSource(new[] { _tempDir }, _tempDir, new[] { "*" }, null);

        var act = () =>
        {
            source.Dispose();
            source.Dispose();
        };

        act.Should().NotThrow();
    }

    [Fact]
    public void Ctor_EmptyWatchPaths_DoesNotThrow()
    {
        var source = new FileSystemEventSource(Array.Empty<string>(), null);
        source.SourceId.Should().Be("file-system");
    }
}
