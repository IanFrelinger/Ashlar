namespace Ashlar.Core.Application.Execution.Ports;

// DEPRECATED: ModelUnavailableException has been moved to Application.Execution.Ports
// This type alias preserves backward compatibility.

/// <summary>
/// Thrown when no real LLM model is available (local or server).
/// DEPRECATED: Use Ashlar.Core.Application.Execution.Ports.ModelUnavailableException instead.
/// </summary>
[Obsolete("ModelUnavailableException has moved to Ashlar.Core.Application.Execution.Ports. Update your using statements.")]
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
