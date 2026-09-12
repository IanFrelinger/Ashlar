namespace Ashlar.Infrastructure.Certification;

/// <summary>
/// Thrown when a canonical brick-output payload is not the shape the output it came from declares.
/// </summary>
/// <remarks>
/// Distinct from <c>CanonicalPayloadException</c>, which covers the certification record's signed
/// bytes. Both mean the same thing — reflection-based serialization degraded and produced a payload
/// nobody can reason about — but they arise on different paths and an operator reading a log should
/// not have to work out which one they are looking at.
/// </remarks>
public sealed class CanonicalOutputException : InvalidOperationException
{
    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">What was expected and what was observed.</param>
    public CanonicalOutputException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and the error underneath it.</summary>
    /// <param name="message">What was expected and what was observed.</param>
    /// <param name="innerException">The underlying failure.</param>
    public CanonicalOutputException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
