#if NET8_0_OR_GREATER
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Ashlar.Abstractions.Barriers;

namespace Ashlar.Runtime.Barriers.Sinks;

/// <summary>Append-only file sink for barrier audit events with rotation and async draining.</summary>
public sealed class FileBarrierAuditSink : IBarrierAuditSink, IAsyncDisposable
{
    private readonly FileBarrierAuditSinkOptions _options;
    private readonly ILogger<FileBarrierAuditSink> _logger;
    private readonly Channel<BarrierAuditEvent> _channel;
    private readonly Task _drainTask;
    private readonly JsonSerializerOptions _jsonSerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly TimeSpan _flushInterval;

    private StreamWriter? _writer;
    private long _currentFileSizeBytes;
    private DateTimeOffset _nextFlushUtc;
    private int _disposeRequested;

    public FileBarrierAuditSink(
        FileBarrierAuditSinkOptions options,
        ILogger<FileBarrierAuditSink> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = Normalize(options);
        _flushInterval = TimeSpan.FromMilliseconds(_options.FlushIntervalMs);

        _channel = Channel.CreateBounded<BarrierAuditEvent>(new BoundedChannelOptions(_options.ChannelCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });

        _drainTask = Task.Run(DrainAsync);
    }

    /// <summary>Enqueues a barrier audit event for asynchronous file append.</summary>
    public ValueTask WriteAsync(
        BarrierAuditEvent auditEvent,
        CancellationToken cancellationToken = default)
    {
        if (auditEvent is null)
        {
            _logger.LogWarning("Discarding null barrier audit event.");
            return ValueTask.CompletedTask;
        }

        try
        {
            if (!_channel.Writer.TryWrite(auditEvent))
            {
                _logger.LogWarning(
                    "Barrier audit channel full; dropping event. EventType={EventType} CorrelationId={CorrelationId} AgentName={AgentName}",
                    auditEvent.EventType,
                    auditEvent.CorrelationId,
                    auditEvent.AgentName);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to enqueue barrier audit event. EventType={EventType} CorrelationId={CorrelationId}",
                auditEvent.EventType,
                auditEvent.CorrelationId);
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Flushes pending events and releases file writer resources.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeRequested, 1) != 0)
        {
            return;
        }

        _channel.Writer.TryComplete();

        try
        {
            await _drainTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Barrier audit sink drain failed during shutdown.");
        }
    }

    private async Task DrainAsync()
    {
        try
        {
            while (await _channel.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (_channel.Reader.TryRead(out var auditEvent))
                {
                    await AppendEventAsync(auditEvent).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Barrier audit sink drain loop terminated unexpectedly.");
        }
        finally
        {
            await FlushAndCloseWriterAsync().ConfigureAwait(false);
        }
    }

    private async Task AppendEventAsync(BarrierAuditEvent auditEvent)
    {
        try
        {
            await EnsureWriterAsync().ConfigureAwait(false);
            var line = SerializeLine(auditEvent);
            await _writer!.WriteLineAsync(line).ConfigureAwait(false);
            _currentFileSizeBytes += Utf8ByteCount(line) + Utf8ByteCount(Environment.NewLine);

            if (_options.FlushEveryEvent)
            {
                await _writer.FlushAsync().ConfigureAwait(false);
                _nextFlushUtc = DateTimeOffset.UtcNow.Add(_flushInterval);
            }
            else if (DateTimeOffset.UtcNow >= _nextFlushUtc)
            {
                await _writer.FlushAsync().ConfigureAwait(false);
                _nextFlushUtc = DateTimeOffset.UtcNow.Add(_flushInterval);
            }

            if (_currentFileSizeBytes >= _options.MaxFileSizeBytes)
            {
                await RotateFileAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to append barrier audit event. EventType={EventType} CorrelationId={CorrelationId}",
                auditEvent.EventType,
                auditEvent.CorrelationId);
        }
    }

    private async Task EnsureWriterAsync()
    {
        if (_writer is not null)
        {
            return;
        }

        Directory.CreateDirectory(_options.Directory);

        var filePath = BuildUniqueFilePath();
        var stream = new FileStream(
            filePath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read);

        _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        _currentFileSizeBytes = 0;
        _nextFlushUtc = DateTimeOffset.UtcNow.Add(_flushInterval);

        await PruneRotatedFilesAsync().ConfigureAwait(false);
    }

    private async Task RotateFileAsync()
    {
        await FlushAndCloseWriterAsync().ConfigureAwait(false);
        await EnsureWriterAsync().ConfigureAwait(false);
    }

    private async Task FlushAndCloseWriterAsync()
    {
        if (_writer is null)
        {
            return;
        }

        try
        {
            await _writer.FlushAsync().ConfigureAwait(false);
            await _writer.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to flush or dispose barrier audit file writer.");
        }
        finally
        {
            _writer = null;
            _currentFileSizeBytes = 0;
        }
    }

    private async Task PruneRotatedFilesAsync()
    {
        try
        {
            var pattern = $"{_options.FilePrefix}-*.ndjson";
            var files = Directory.GetFiles(_options.Directory, pattern)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToList();

            while (files.Count > _options.MaxRotatedFiles)
            {
                var oldest = files[0];
                files.RemoveAt(0);
                File.Delete(oldest);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to prune rotated barrier audit files.");
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private string BuildUniqueFilePath() => BuildUniqueFilePath(DateTimeOffset.UtcNow);

    private string BuildUniqueFilePath(DateTimeOffset now)
    {
        var timestamp = now.ToString("yyyyMMddTHHmmssfffZ");
        var basePath = Path.Combine(_options.Directory, $"{_options.FilePrefix}-{timestamp}.ndjson");
        if (!File.Exists(basePath))
        {
            return basePath;
        }

        var counter = 1;
        while (true)
        {
            var candidate = Path.Combine(
                _options.Directory,
                $"{_options.FilePrefix}-{timestamp}-{counter}.ndjson");
            if (!File.Exists(candidate))
            {
                return candidate;
            }

            counter++;
        }
    }

    private string SerializeLine(BarrierAuditEvent auditEvent)
    {
        var payload = new SerializableAuditLine(
            Timestamp: auditEvent.OccurredAt.UtcDateTime,
            EventType: auditEvent.EventType,
            BarrierLevel: auditEvent.BarrierLevel,
            AuthoritySource: auditEvent.AuthoritySource,
            AgentName: auditEvent.AgentName,
            CorrelationId: auditEvent.CorrelationId,
            SpanId: auditEvent.SpanId,
            Detail: auditEvent.Detail);

        return JsonSerializer.Serialize(payload, _jsonSerializerOptions);
    }

    private static int Utf8ByteCount(string value) => Encoding.UTF8.GetByteCount(value);

    private static FileBarrierAuditSinkOptions Normalize(FileBarrierAuditSinkOptions options)
    {
        return new FileBarrierAuditSinkOptions
        {
            Directory = string.IsNullOrWhiteSpace(options.Directory) ? "audit" : options.Directory.Trim(),
            FilePrefix = string.IsNullOrWhiteSpace(options.FilePrefix) ? "audit-barriers" : options.FilePrefix.Trim(),
            MaxFileSizeBytes = Math.Max(1, options.MaxFileSizeBytes),
            MaxRotatedFiles = Math.Max(1, options.MaxRotatedFiles),
            FlushIntervalMs = Math.Max(1, options.FlushIntervalMs),
            FlushEveryEvent = options.FlushEveryEvent,
            ChannelCapacity = Math.Max(1, options.ChannelCapacity)
        };
    }

    private sealed record SerializableAuditLine(
        DateTime Timestamp,
        string EventType,
        string BarrierLevel,
        string AuthoritySource,
        string AgentName,
        string CorrelationId,
        string SpanId,
        string? Detail);
}
#endif
