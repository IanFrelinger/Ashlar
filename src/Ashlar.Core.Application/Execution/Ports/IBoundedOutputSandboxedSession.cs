namespace Ashlar.Core.Application.Execution.Ports;

/// <summary>
/// A session that can bound host-side output capture while the command is running.
/// Isolated tasks require this capability; buffering first and truncating later does not
/// satisfy the contract. Existing session callers may still use the unbounded ExecAsync API.
/// </summary>
public interface IBoundedOutputSandboxedSession : ISandboxedSession
{
    /// <summary>
    /// Captures at most <paramref name="maxOutputBytes"/> combined raw stdout/stderr bytes.
    /// Exceeding the ceiling aborts capture and throws <see cref="ProcessOutputLimitExceededException"/>.
    /// The caller still owns session disposal to terminate the isolated workload.
    /// </summary>
    Task<ProcessCommandResult> ExecBoundedAsync(
        IReadOnlyList<string> command,
        int maxOutputBytes,
        CancellationToken cancellationToken = default);
}
