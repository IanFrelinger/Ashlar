using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Ashlar.Abstractions;
using Ashlar.Core.Application.Execution.Ports;
using Ashlar.Runtime;

namespace Ashlar.BackgroundAgents.Agents;

/// <summary>
/// IAgent that uses an LLM to drive tool calls.
///
/// <para>Two execution modes are supported:</para>
/// <list type="bullet">
///   <item><b>Single-turn</b> via <see cref="ThinkAsync"/> (the legacy <see cref="IAgent"/>
///         contract): one LLM round-trip → return tool calls → host executes them.</item>
///   <item><b>Multi-turn ReAct</b> via <see cref="RunCycleAsync"/>: the agent loops up to
///         <c>maxIterations</c> times, executing tool calls inline through an injected
///         <see cref="PolicyEngine"/> + <see cref="IToolbox"/>, feeding tool results back into
///         the conversation so the model can reason over observations and refine its plan.
///         A per-cycle deadline guards against runaway loops.</item>
/// </list>
///
/// <para>The multi-turn path is what unlocks the planner from "list once, write blind" into
/// genuine <c>list → read → propose → write</c> chains.</para>
/// </summary>
public sealed class ToolCallingAgent : IAgent
{
    /// <summary>
    /// Default ReAct iteration cap. Five rounds is enough for list → read → read → write → reflect
    /// without letting a degenerate model burn through dozens of tokens-per-cycle round-trips.
    /// </summary>
    public const int DefaultMaxIterations = 5;

    /// <summary>
    /// Default per-cycle wall-clock deadline. Sized for a multi-turn ReAct loop on local
    /// hardware: a cold Ollama load can eat 30–60s on first request, then each turn adds
    /// 10–30s for an 8B model on GPU, so 5 minutes leaves enough headroom for a full
    /// list → read → write → reflect cycle without bumping into the scheduler interval.
    /// Override per-agent via the <c>perCycleDeadline</c> constructor arg if you have
    /// stricter timing requirements.
    /// </summary>
    public static readonly TimeSpan DefaultPerCycleDeadline = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Cap on serialized tool-result payload appended back into the conversation.
    /// Keeps prompts bounded on long file reads / wide directory listings.
    /// </summary>
    private const int ToolResultPreviewBytes = 4 * 1024;

    private readonly IModel _model;
    private readonly ILogger<ToolCallingAgent>? _logger;
    private readonly string? _objective;
    private readonly string? _modelProvider;
    private readonly string? _modelName;
    private readonly int _maxIterations;
    private readonly TimeSpan _perCycleDeadline;

    public string Name { get; }

    public ToolCallingAgent(
        string name,
        IModel model,
        ILogger<ToolCallingAgent>? logger = null,
        string? objective = null,
        string? modelProvider = null,
        string? modelName = null,
        int? maxIterations = null,
        TimeSpan? perCycleDeadline = null)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _logger = logger;
        _objective = objective;
        _modelProvider = NormalizeOrNull(modelProvider);
        _modelName = NormalizeOrNull(modelName);
        _maxIterations = maxIterations is > 0 ? maxIterations.Value : DefaultMaxIterations;
        _perCycleDeadline = perCycleDeadline is { TotalMilliseconds: > 0 } d ? d : DefaultPerCycleDeadline;
    }

    private static string? NormalizeOrNull(string? s)
        => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <inheritdoc />
    /// <remarks>
    /// Single-turn entrypoint kept for the legacy <see cref="IAgent"/> contract. Multi-turn
    /// callers should use <see cref="RunCycleAsync"/> instead so tool results can flow back
    /// into the conversation history.
    /// </remarks>
    public async Task<AgentActions> ThinkAsync(AgentObservation obs, IToolbox tools, IAgentMemory mem, CancellationToken ct)
    {
        var snapshot = obs.Snapshot;
        var schemas = tools.Schemas().ToList();
        if (schemas.Count == 0)
        {
            _logger?.LogDebug("No tools available; returning no actions.");
            return AgentActions.None;
        }

        var systemPrompt = BuildSystemPrompt(snapshot, schemas);
        var messages = new List<(string role, string content)>
        {
            ("system", systemPrompt),
            ("user", BuildUserPrompt(_objective))
        };

        try
        {
            var output = await _model.CompleteAsync(new ModelInput(messages), ct).ConfigureAwait(false);
            var text = output?.Text?.Trim() ?? "";
            var (toolCalls, rationale) = ParseResponse(text);
            LogModelTurn(text, toolCalls, rationale);
            return toolCalls.Count == 0 ? AgentActions.None : new AgentActions(toolCalls);
        }
        catch (ModelUnavailableException)
        {
            // A genuinely unavailable model is a hard infra failure, not "the model chose to do
            // nothing." Propagate it so the run fails honestly instead of reporting no actions.
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "ThinkAsync failed; returning no actions.");
            mem.Write(new EventRecord(DateTimeOffset.UtcNow, Name, "think.error", ex.Message));
            return AgentActions.None;
        }
    }

    /// <summary>
    /// Run a multi-turn ReAct cycle. The agent thinks (LLM) → acts (tool call through
    /// <paramref name="policies"/>) → observes (tool payload appended to history) → repeats,
    /// up to <see cref="DefaultMaxIterations"/> rounds or until <see cref="DefaultPerCycleDeadline"/>
    /// elapses (whichever fires first), or until the model returns an empty
    /// <c>tool_calls</c> array signalling "I'm done".
    ///
    /// <para>Denied tool calls are reported back to the model as a "user" message so it can
    /// adjust on the next iteration (e.g. retry with an allowed write path) instead of
    /// silently giving up.</para>
    /// </summary>
    public async Task<AgentCycleResult> RunCycleAsync(
        WorldSnapshot snapshot,
        IToolbox tools,
        PolicyEngine policies,
        ChainRejectionCallback? onRejected,
        IAgentMemory? memory,
        CancellationToken ct)
    {
        if (tools is null) throw new ArgumentNullException(nameof(tools));
        if (policies is null) throw new ArgumentNullException(nameof(policies));

        var schemas = tools.Schemas().ToList();
        if (schemas.Count == 0)
        {
            _logger?.LogDebug("No tools available; returning empty cycle.");
            return new AgentCycleResult(null, 0, 0, 0, null, "empty");
        }

        var systemPrompt = BuildSystemPrompt(snapshot, schemas);
        var messages = new List<(string role, string content)>(_maxIterations * 2 + 2)
        {
            ("system", systemPrompt),
            ("user", BuildUserPrompt(_objective))
        };

        var deltas = new List<IActionDelta>();
        var executed = 0;
        var denied = 0;
        var iterations = 0;
        string? lastRationale = null;
        string stoppedReason = "max_iterations";

        var stopwatch = Stopwatch.StartNew();
        using var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadlineCts.CancelAfter(_perCycleDeadline);
        var loopCt = deadlineCts.Token;

        try
        {
            for (var iter = 0; iter < _maxIterations; iter++)
            {
                iterations = iter + 1;
                if (loopCt.IsCancellationRequested)
                {
                    stoppedReason = ct.IsCancellationRequested ? "cancelled" : "deadline";
                    break;
                }

                ModelOutput? output;
                try
                {
                    output = await _model.CompleteAsync(new ModelInput(messages), loopCt).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    stoppedReason = "deadline";
                    break;
                }

                var text = output?.Text?.Trim() ?? "";
                messages.Add(("assistant", text));

                var (toolCalls, rationale) = ParseResponse(text);
                if (!string.IsNullOrWhiteSpace(rationale))
                    lastRationale = rationale;
                LogModelTurn(text, toolCalls, rationale, iter + 1);

                if (toolCalls.Count == 0)
                {
                    stoppedReason = "empty";
                    break;
                }

                // Execute calls one by one so tool results can be threaded back into the
                // conversation in order. A denial does NOT abort the whole cycle — we feed
                // the rejection back so the model can correct on the next iteration.
                var anyDeniedThisIter = false;
                foreach (var call in toolCalls)
                {
                    loopCt.ThrowIfCancellationRequested();

                    if (!policies.Approve(call, snapshot, out var reason))
                    {
                        denied++;
                        anyDeniedThisIter = true;
                        memory?.Write(new EventRecord(DateTimeOffset.UtcNow, Name, "policy.denied", $"{call.Id}: {reason}"));
                        onRejected?.Invoke(toolCalls, toolCalls.IndexOf(call), reason);
                        messages.Add(("user", $"tool {call.Id}: DENIED ({reason}). Adjust your plan and try again."));
                        // Per AgentHost contract, a denial in a chain stops the rest of the chain.
                        break;
                    }

                    ToolResult result;
                    try
                    {
                        result = await tools.InvokeAsync(call, snapshot, loopCt).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        stoppedReason = "deadline";
                        throw;
                    }

                    deltas.Add(policies.Sign(result.Delta));
                    executed++;
                    snapshot = AdvanceTick(snapshot, result.Delta.TickTo);
                    messages.Add(("user", FormatToolObservation(call.Id, result)));
                }

                // If the chain self-aborted on denial we still loop — the model has the rejection
                // message and can retry with a different plan in the next iteration.
                _ = anyDeniedThisIter;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            stoppedReason = "deadline";
        }
        catch (OperationCanceledException)
        {
            stoppedReason = "cancelled";
            throw;
        }
        catch (ModelUnavailableException)
        {
            // The model was genuinely unreachable/unconfigured. Do NOT fall through to a returned
            // AgentCycleResult with executed=0 (which reads as a successful empty cycle) — let it
            // propagate so the runner marks the cycle FAILED and the caller exits non-zero.
            throw;
        }
        catch (Exception ex)
        {
            stoppedReason = "error";
            _logger?.LogWarning(ex, "RunCycleAsync failed mid-cycle ({Iterations} iterations, {Executed} executed)", iterations, executed);
            memory?.Write(new EventRecord(DateTimeOffset.UtcNow, Name, "react.error", ex.Message));
        }

        stopwatch.Stop();
        _logger?.LogInformation(
            "Agent {Agent} ReAct cycle complete: {Iterations} iter, {Executed} exec, {Denied} denied, stopped={Reason}, took {Elapsed}ms",
            Name, iterations, executed, denied, stoppedReason, stopwatch.ElapsedMilliseconds);

        var merged = deltas.Count == 0 ? null : ActionDelta.Merge(deltas);
        return new AgentCycleResult(merged, iterations, executed, denied, lastRationale, stoppedReason);
    }

    private void LogModelTurn(string rawText, IReadOnlyList<ToolCall> calls, string? rationale, int? iter = null)
    {
        var preview = rawText.Length > 600 ? rawText[..600] + "…" : rawText;
        var iterTag = iter is null ? "" : $" iter={iter}";
        if (calls.Count == 0)
        {
            if (!string.IsNullOrWhiteSpace(rationale))
                _logger?.LogInformation("Agent {Agent}{Iter} returned no tool calls. Rationale: {Rationale}. Raw: {Raw}",
                    Name, iterTag, rationale, preview);
            else
                _logger?.LogInformation("Agent {Agent}{Iter} returned no tool calls (no rationale provided). Raw: {Raw}",
                    Name, iterTag, preview);
        }
        else if (!string.IsNullOrWhiteSpace(rationale))
        {
            _logger?.LogInformation("Agent {Agent}{Iter} proposed {Count} tool call(s). Rationale: {Rationale}",
                Name, iterTag, calls.Count, rationale);
        }
        else
        {
            _logger?.LogInformation("Agent {Agent}{Iter} proposed {Count} tool call(s).", Name, iterTag, calls.Count);
        }
    }

    private static WorldSnapshot AdvanceTick(WorldSnapshot prev, int newTick)
        => new(Math.Max(prev.Tick, newTick), prev.Data);

    private static string FormatToolObservation(string toolId, ToolResult result)
    {
        string payloadJson;
        try
        {
            payloadJson = result.Payload is null
                ? "{}"
                : JsonSerializer.Serialize(result.Payload);
        }
        catch
        {
            payloadJson = "{\"error\":\"payload_serialization_failed\"}";
        }

        if (payloadJson.Length > ToolResultPreviewBytes)
            payloadJson = payloadJson[..ToolResultPreviewBytes] + "…";

        return $"tool {toolId}: {payloadJson}";
    }

    private string BuildSystemPrompt(WorldSnapshot snapshot, IReadOnlyList<ToolSchema> schemas)
    {
        var stateJson = JsonSerializer.Serialize(snapshot.Data, new JsonSerializerOptions
        {
            WriteIndented = false
        });
        var toolDescriptions = string.Join("\n", schemas.Select(s =>
            $"- {s.Id}: {s.Description} (args: {s.InputJsonSchema})"));

        snapshot.Data.TryGetValue("RepoRoot", out var repoRootObj);
        var repoRoot = repoRootObj as string ?? ".";

        var directiveLines = new List<string>();
        if (_modelProvider is not null)
            directiveLines.Add($"ashlar.model.provider={_modelProvider}");
        if (_modelName is not null)
            directiveLines.Add($"ashlar.model.name={_modelName}");
        var directives = directiveLines.Count == 0
            ? string.Empty
            : string.Join('\n', directiveLines) + "\n";

        return $@"{directives}You are a self-extending code agent operating against a real repository through tools.

World state (JSON): {stateJson}

Available tools:
{toolDescriptions}

Operating rules:
- Use ""{repoRoot}"" as the ""root"" argument for every repo.fs.* call (it matches RepoRoot above).
- Iterate: each turn you may emit one or more tool_calls; their results will appear back as ""user"" messages prefixed with ""tool <id>: ..."".
- When you do not yet know enough to act, START by calling repo.fs.list and/or repo.fs.read to gather context — do NOT return an empty tool_calls array just because the world state looks sparse.
- Reserve an empty tool_calls array for the case when you have inspected the repository and concluded that no further action is needed this cycle. Always include a ""rationale"" string in that case.
- Prefer small, reversible steps: list → read → propose a single write or search_replace.
- If a previous call was DENIED, read the reason and choose a different path or argument; do not repeat the same call.
- If ""RecentNotes"" is present in the world state, treat it as the planner's prior cycle log: continue from where you left off, do NOT redo work already noted there.
- All write paths must be relative to root and live under one of: src/, tests/, docs/, .ashlar/.

Response format: a single JSON object, no markdown, with this shape:
{{""tool_calls"": [{{""id"": ""<tool>"", ""arguments"": {{...}}}}], ""rationale"": ""<short reason>""}}";
    }

    private static (List<ToolCall> Calls, string? Rationale) ParseResponse(string text)
    {
        var result = new List<ToolCall>();
        string? rationale = null;
        var json = ExtractJson(text);
        if (string.IsNullOrWhiteSpace(json))
            return (result, rationale);

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("rationale", out var rEl) && rEl.ValueKind == JsonValueKind.String)
                rationale = rEl.GetString();
            if (root.TryGetProperty("tool_calls", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    if (item.TryGetProperty("id", out var idEl) && item.TryGetProperty("arguments", out var argsEl))
                    {
                        var id = idEl.GetString();
                        if (string.IsNullOrWhiteSpace(id))
                            continue;
                        var argsJson = argsEl.GetRawText();
                        var argsCopy = JsonSerializer.Deserialize<JsonElement>(argsJson);
                        result.Add(new ToolCall(id, argsCopy));
                    }
                }
            }
        }
        catch
        {
            // Return what we have on parse error.
        }

        return (result, rationale);
    }

    private static string? ExtractJson(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        text = text.Trim();
        var match = Regex.Match(text, @"```(?:json)?\s*([\s\S]*?)```", RegexOptions.IgnoreCase);
        if (match.Success)
            return match.Groups[1].Value.Trim();
        if (text.StartsWith("{", StringComparison.Ordinal))
            return text;
        return null;
    }

    private static string BuildUserPrompt(string? objective)
    {
        if (string.IsNullOrWhiteSpace(objective))
            return "Decide which tools to call based on the current state. Respond with JSON only.";

        return $"Objective:\n{objective.Trim()}\n\nUse available tools to complete the objective. Respond with JSON only.";
    }
}
