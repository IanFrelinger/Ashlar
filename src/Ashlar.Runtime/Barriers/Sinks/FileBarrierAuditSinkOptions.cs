namespace Ashlar.Runtime.Barriers.Sinks;

/// <summary>Configuration for append-only file barrier audit sink behavior.</summary>
public sealed class FileBarrierAuditSinkOptions
{
    /// <summary>
    /// Directory where audit files are written. Must be writable.
    /// </summary>
    public string Directory { get; init; } = "audit";

    /// <summary>
    /// Base file name prefix.
    /// </summary>
    public string FilePrefix { get; init; } = "audit-barriers";

    /// <summary>
    /// Rotate when file exceeds this size. Default 10 MB.
    /// </summary>
    public long MaxFileSizeBytes { get; init; } = 10 * 1024 * 1024;

    /// <summary>
    /// Number of files with <see cref="FilePrefix"/> to retain in <see cref="Directory"/>; the oldest are deleted
    /// when a new file is opened. Every sink instance opens a new file, so this also counts process restarts.
    /// <c>0</c> keeps every file.
    /// </summary>
    public int MaxRotatedFiles { get; init; } = 10;

    /// <summary>
    /// Flush to disk after this interval. Default 5 seconds.
    /// </summary>
    public int FlushIntervalMs { get; init; } = 5_000;

    /// <summary>
    /// Flush after every event; recommended in high-integrity compliance environments.
    /// </summary>
    public bool FlushEveryEvent { get; init; } = false;

    /// <summary>
    /// Bounded channel capacity before events are dropped.
    /// </summary>
    public int ChannelCapacity { get; init; } = 4096;
}
