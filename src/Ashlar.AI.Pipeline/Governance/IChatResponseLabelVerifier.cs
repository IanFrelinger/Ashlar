using Microsoft.Extensions.AI;

namespace Ashlar.AI.Pipeline.Governance;

/// <summary>Trusted host adapter for per-response classification attestations.</summary>
/// <remarks>
/// Register only an operator-trusted implementation. Verify the authenticated peer identity, target,
/// canonical label and exact response payload (all messages, contents and metadata) as one attested unit;
/// The trusted transport must retain association with its in-flight invocation; a valid old signature alone
/// does not establish freshness. Consume each attestation once; stream attestations additionally bind the
/// invocation and update sequence to prevent replay or substitution.
/// Never accept an unsigned label from AdditionalProperties, a target name, or the request.
/// Return null when absent, malformed, stale, unverifiable, or not bound to this exact object payload.
/// There is no default signed transport implementation: absent verification means SystemHigh.
/// The verifier must not mutate the supplied response or retain it for later mutation.
/// </remarks>
public interface IChatResponseLabelVerifier
{
    /// <summary>Verifies a complete response and returns its canonical attested label, or null.</summary>
    string? Verify(string? targetKey, ChatResponse response);

    /// <summary>Verifies this individual streamed update, including invocation and sequence binding, or returns null.</summary>
    string? Verify(string? targetKey, ChatResponseUpdate update);
}
