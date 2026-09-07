namespace Ashlar.Orchestration.Agents;

/// <summary>
/// What <see cref="GenericAgent"/> returns when a domain had no specialized agent: a marker
/// saying <em>no work was performed</em>.
///
/// <para>This used to be an anonymous object carrying <c>Placeholder = true</c>. The flag was
/// added so the case would be "detectable" — but nothing detected it, and an orchestration whose
/// every result was one of these still reached the CLI as <c>Success = true</c> and printed
/// "Orchestration completed successfully". A marker nobody can check is a comment.</para>
///
/// <para>Making it a NAMED TYPE is the whole point: callers now test <c>is
/// PlaceholderAgentResult</c> rather than reflecting on a property name that a refactor could
/// rename without breaking anything. The property names and shape are unchanged, so serialized
/// output (including the CLI's <c>--format-json</c>) is byte-identical to before.</para>
/// </summary>
/// <param name="AgentId">The agent that produced no work.</param>
/// <param name="Domain">The domain that had no specialized agent.</param>
/// <param name="Goal">The goal that went unaddressed.</param>
/// <param name="Placeholder">Always true. Retained so existing consumers reading the flag still work.</param>
/// <param name="Output">Human-readable statement that nothing was done.</param>
public sealed record PlaceholderAgentResult(
    string AgentId,
    string Domain,
    string Goal,
    bool Placeholder,
    string Output);
