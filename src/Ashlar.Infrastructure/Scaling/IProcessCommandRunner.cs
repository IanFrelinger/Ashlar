using Ashlar.Core.Application.Execution.Ports;

namespace Ashlar.Infrastructure.Scaling;

/// <summary>
/// Thin process runner used by kubectl/compose adapters so unit tests can stub shell I/O.
/// </summary>
public interface IProcessCommandRunner
{
    /// <summary>Runs a process and captures stdout/stderr/exit code.</summary>
    Task<ProcessCommandResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Optional process-runner capability that enforces a combined raw stdout/stderr byte limit.
/// Callers that require bounded output must reject runners without this capability.
/// </summary>
public interface IBoundedOutputProcessCommandRunner : IProcessCommandRunner
{
    /// <summary>
    /// Runs a process with bounded output capture. Exceeding <paramref name="maxOutputBytes"/>
    /// kills the local process tree and throws <see cref="ProcessOutputLimitExceededException"/>.
    /// The limit must be between one byte and 16 MiB; it is not a character count.
    /// </summary>
    Task<ProcessCommandResult> RunBoundedAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        int maxOutputBytes,
        CancellationToken cancellationToken = default);
}
