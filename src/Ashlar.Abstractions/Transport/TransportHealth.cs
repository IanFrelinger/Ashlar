namespace Ashlar.Abstractions.Transport;

/// <summary>
/// Health status of an agent transport channel.
/// </summary>
/// <param name="IsHealthy">Whether the channel is currently healthy.</param>
/// <param name="TransportName">Logical transport name.</param>
/// <param name="Message">Optional detail message.</param>
/// <param name="TransportType">Optional transport type alias (e.g. "gRPC").</param>
/// <param name="DiagnosticMessage">Optional diagnostic detail.</param>
public sealed record TransportHealth(
    bool IsHealthy,
    string TransportName,
    string? Message = null,
    string? TransportType = null,
    string? DiagnosticMessage = null)
{
    // Operator-only local propagation. Never serialized across a remote health boundary.
    internal Security.Egress.EgressRefusedException? Refusal { get; init; }
}
