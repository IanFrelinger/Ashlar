namespace Ashlar.Core.Application.Execution.Ports;

/// <summary>Execution stopped because combined stdout/stderr exceeded its byte budget.</summary>
public sealed class ProcessOutputLimitExceededException : Exception
{
    /// <summary>Creates an output-limit failure without retaining partial command output.</summary>
    public ProcessOutputLimitExceededException(int maxOutputBytes)
        : base($"Process output exceeded the combined stdout/stderr limit of {maxOutputBytes} bytes.")
    {
        MaxOutputBytes = maxOutputBytes;
    }

    /// <summary>The configured combined raw output byte ceiling.</summary>
    public int MaxOutputBytes { get; }
}
