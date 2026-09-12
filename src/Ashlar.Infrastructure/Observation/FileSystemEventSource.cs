using System.IO;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Ashlar.Core.Application.Observation.Models;
using Ashlar.Core.Application.Observation.Ports;

namespace Ashlar.Infrastructure.Observation;

/// <summary>
/// Event source that watches the file system using FileSystemWatcher.
/// Emits NormalizedEvent with Category="file-paths", SourceId="file-system".
/// </summary>
/// <remarks>
/// <para><b>Nothing may throw on a watcher callback thread.</b> <see cref="FileSystemWatcher"/>
/// raises Created/Changed/Deleted/Renamed on a thread this class does not own and does not await,
/// so an exception escaping a handler is unhandled and takes the whole process down. On a test
/// runner that process is the test host, and it loses every other test's result with it. Both
/// handlers are therefore total: they log and return, never throw.
/// </para>
/// <para>That is also why the queue is a <see cref="Channel{T}"/> rather than a ConcurrentQueue
/// plus a SemaphoreSlim. SemaphoreSlim.Release throws <see cref="ObjectDisposedException"/> once
/// the semaphore is disposed, and disposal races an in-flight callback by construction;
/// <see cref="ChannelWriter{T}.TryWrite"/> returns false after completion instead. The previous
/// shape also threw <see cref="ObjectDisposedException"/> at the CONSUMER when
/// <see cref="SubscribeAsync"/> ran after <see cref="Dispose"/>.
/// </para>
/// <para><see cref="FileSystemWatcher.Error"/> is subscribed because it is the only notification
/// that the OS has stopped delivering: an inotify or FSEvents buffer overflow blinds the watcher
/// permanently and is otherwise completely silent.
/// </para>
/// </remarks>
public sealed class FileSystemEventSource : IObservableEventSource, IDisposable
{
    private const string SourceIdValue = "file-system";
    private const string CategoryValue = "file-paths";

    private readonly IReadOnlyList<string> _watchPaths;
    private readonly IReadOnlyList<string> _filters;
    private readonly string? _projectPath;
    private readonly ILogger<FileSystemEventSource>? _logger;
    private readonly Channel<NormalizedEvent> _events =
        Channel.CreateUnbounded<NormalizedEvent>(new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });
    private readonly object _watcherGate = new();
    private readonly List<FileSystemWatcher> _watchers = new();
    private bool _disposed;

    /// <summary>
    /// Creates a file system event source.
    /// </summary>
    /// <param name="watchPaths">Directories to watch (e.g. src/, .github/).</param>
    /// <param name="projectPath">Optional project path for per-project gating.</param>
    /// <param name="filters">Optional file filters (e.g. *.cs, *.csproj). Default: *.</param>
    /// <param name="logger">Optional logger.</param>
    public FileSystemEventSource(
        IEnumerable<string> watchPaths,
        string? projectPath = null,
        IEnumerable<string>? filters = null,
        ILogger<FileSystemEventSource>? logger = null)
    {
        _watchPaths = watchPaths?.Where(p => !string.IsNullOrWhiteSpace(p)).Select(Path.GetFullPath).Distinct().ToList()
            ?? throw new ArgumentNullException(nameof(watchPaths));
        _projectPath = projectPath;
        _filters = filters?.ToList() ?? new List<string> { "*" };
        _logger = logger;
    }

    /// <inheritdoc />
    public string SourceId => SourceIdValue;

    /// <inheritdoc />
    public async IAsyncEnumerable<NormalizedEvent> SubscribeAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (IsDisposed())
        {
            _logger?.LogWarning("SubscribeAsync called on a disposed FileSystemEventSource; no events will be produced.");
            yield break;
        }

        if (_watchPaths.Count == 0)
        {
            _logger?.LogWarning("No watch paths configured for FileSystemEventSource");
            yield break;
        }

        // Local to THIS subscription. A single shared List<FileSystemWatcher> field meant one
        // subscription's teardown disposed and cleared another's live watchers.
        var watchers = new List<FileSystemWatcher>();

        foreach (var watchPath in _watchPaths)
        {
            if (!Directory.Exists(watchPath))
            {
                _logger?.LogWarning("Watch path does not exist: {Path}", watchPath);
                continue;
            }

            foreach (var filter in _filters)
            {
                var watcher = new FileSystemWatcher(watchPath)
                {
                    Filter = filter,
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.DirectoryName,
                };
                watcher.Created += OnFileSystemEvent;
                watcher.Changed += OnFileSystemEvent;
                watcher.Deleted += OnFileSystemEvent;
                watcher.Renamed += OnRenamed;
                watcher.Error += OnWatcherError;
                watcher.EnableRaisingEvents = true;
                watchers.Add(watcher);
                Track(watcher);
            }
        }

        _logger?.LogInformation("FileSystemEventSource watching {Count} path(s)", _watchPaths.Count);

        var reader = _events.Reader;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (reader.TryRead(out var evt))
                {
                    yield return evt;
                    continue;
                }

                bool more;
                try
                {
                    more = await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (!more)
                    break;
            }
        }
        finally
        {
            foreach (var w in watchers)
            {
                Untrack(w);
                Quiesce(w);
            }
        }
    }

    private void OnFileSystemEvent(object sender, FileSystemEventArgs e)
    {
        // Total by contract: this runs on a FileSystemWatcher callback thread, where an escaping
        // exception is unhandled and kills the process.
        try
        {
            var changeType = e.ChangeType.ToString().ToLowerInvariant();
            var payload = JsonSerializer.SerializeToElement(new
            {
                changeType,
                fullPath = e.FullPath,
                name = e.Name,
            });
            Enqueue(CategoryValue, payload);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Dropped a file system event for {Path}", e.FullPath);
        }
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        try
        {
            var payload = JsonSerializer.SerializeToElement(new
            {
                changeType = "renamed",
                fullPath = e.FullPath,
                name = e.Name,
                oldFullPath = e.OldFullPath,
                oldName = e.OldName,
            });
            Enqueue(CategoryValue, payload);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Dropped a rename event for {Path}", e.FullPath);
        }
    }

    /// <summary>
    /// The watcher telling us it has stopped seeing the file system: buffer overflow, or the
    /// watched directory going away. Without this the source goes permanently blind in silence.
    /// </summary>
    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        try
        {
            _logger?.LogError(
                e.GetException(),
                "FileSystemWatcher reported an error; events under {Path} may have been lost.",
                (sender as FileSystemWatcher)?.Path);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "FileSystemWatcher error handler failed.");
        }
    }

    private void Enqueue(string category, JsonElement payload)
    {
        var evt = new NormalizedEvent
        {
            EventId = Guid.NewGuid().ToString("N"),
            Timestamp = DateTimeOffset.UtcNow,
            SourceId = SourceIdValue,
            Category = category,
            ProjectPath = _projectPath,
            Payload = payload,
        };

        // False once the source is disposed. Dropping an event on a dead source is correct;
        // throwing on this thread would not be.
        _events.Writer.TryWrite(evt);
    }

    private bool IsDisposed()
    {
        lock (_watcherGate)
        {
            return _disposed;
        }
    }

    private void Track(FileSystemWatcher watcher)
    {
        lock (_watcherGate)
        {
            _watchers.Add(watcher);
        }
    }

    private void Untrack(FileSystemWatcher watcher)
    {
        lock (_watcherGate)
        {
            _watchers.Remove(watcher);
        }
    }

    private void Quiesce(FileSystemWatcher watcher)
    {
        try
        {
            watcher.EnableRaisingEvents = false;
            watcher.Created -= OnFileSystemEvent;
            watcher.Changed -= OnFileSystemEvent;
            watcher.Deleted -= OnFileSystemEvent;
            watcher.Renamed -= OnRenamed;
            watcher.Error -= OnWatcherError;
            watcher.Dispose();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to tear down a FileSystemWatcher cleanly.");
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        List<FileSystemWatcher> live;
        lock (_watcherGate)
        {
            if (_disposed) return;
            _disposed = true;
            live = _watchers.ToList();
            _watchers.Clear();
        }

        foreach (var w in live)
            Quiesce(w);

        // Completing the channel is what ends a live subscription's read loop, and what makes a
        // callback still in flight a no-op instead of an ObjectDisposedException on a thread with
        // no handler above it.
        _events.Writer.TryComplete();
        GC.SuppressFinalize(this);
    }
}
