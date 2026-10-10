using Ashlar.Abstractions;

namespace Ashlar.BackgroundAgents.Agents;

/// <summary>
/// Outcome of a single multi-turn ReAct cycle run by <see cref="ToolCallingAgent.RunCycleAsync"/>.
/// </summary>
/// <param name="MergedDelta">Aggregate <see cref="IActionDelta"/> from every executed tool call,
/// or <c>null</c> if no calls were executed.</param>
/// <param name="Iterations">Number of LLM round-trips actually performed (1..maxIterations).</param>
/// <param name="ToolCallsExecuted">Total tool calls that ran successfully through policy.</param>
/// <param name="ToolCallsDenied">Total tool calls rejected by the policy chain.</param>
/// <param name="FinalRationale">Last <c>rationale</c> string emitted by the model, if any.</param>
/// <param name="StoppedReason">Why the loop terminated: <c>"empty"</c>, <c>"max_iterations"</c>,
/// <c>"deadline"</c>, <c>"cancelled"</c>, <c>"egress_refused"</c>, or <c>"error"</c>.</param>
public sealed record AgentCycleResult(
    IActionDelta? MergedDelta,
    int Iterations,
    int ToolCallsExecuted,
    int ToolCallsDenied,
    string? FinalRationale,
    string StoppedReason)
{
    /// <summary>Number of tool or model egress refusals observed during this cycle.</summary>
    public int EgressRefusals { get; init; }
}
