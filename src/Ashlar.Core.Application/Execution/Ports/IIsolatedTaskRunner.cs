namespace Ashlar.Core.Application.Execution.Ports;

/// <summary>
/// Runs one explicitly opted-in code task in a fresh temporary environment. This is an
/// execution boundary, not transparent serialization of a CLR object or a security
/// guarantee for hostile code. Failure to isolate must never fall back to host execution.
/// </summary>
public interface IIsolatedTaskRunner
{
    /// <summary>
    /// Runs an image-packaged program with explicit argv inputs and captured stdout/stderr
    /// outputs. Timeout throws <see cref="TimeoutException"/>; caller cancellation remains
    /// cancellation. Excess output throws <see cref="ProcessOutputLimitExceededException"/>.
    /// Every exit attempts teardown before returning.
    /// </summary>
    Task<IsolatedTaskResult> RunAsync(IsolatedTaskRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// One code invocation. Image selection and resource budgets are trusted host configuration,
/// not authority granted to generated code. No host mounts, network, or inherited host
/// environment are exposed by this API. Package dependencies and large inputs in the image.
/// </summary>
public sealed record IsolatedTaskRequest(string Image, IReadOnlyList<string> Command)
{
    /// <summary>Memory ceiling, in bytes (default 256 MiB).</summary>
    public long MemoryBytes { get; init; } = 256L * 1024 * 1024;

    /// <summary>CPU quota (default one CPU).</summary>
    public decimal Cpus { get; init; } = 1m;

    /// <summary>Maximum process/thread IDs (default 64, including the keepalive).</summary>
    public int Pids { get; init; } = 64;

    /// <summary>Total startup, inspection and execution budget (default one minute).</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>Combined stdout/stderr byte ceiling (default 1 MiB; maximum 16 MiB).</summary>
    public int MaxOutputBytes { get; init; } = 1024 * 1024;
}

/// <summary>Command output and the environment observed before invoking the program.</summary>
public sealed record IsolatedTaskResult(ProcessCommandResult Command, SessionAttestation Environment);
