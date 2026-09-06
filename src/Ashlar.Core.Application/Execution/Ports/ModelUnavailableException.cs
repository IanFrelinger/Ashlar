namespace Ashlar.Core.Application.Execution.Ports;

/// <summary>
/// Thrown when no real LLM model is available (local or server).
/// Used when mock/offline fallback is disabled and both edge and server providers fail.
/// </summary>
public sealed class ModelUnavailableException : InvalidOperationException
{
    /// <summary>Initializes a new model unavailable exception.</summary>
    public ModelUnavailableException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new model unavailable exception.</summary>
    public ModelUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
