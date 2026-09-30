using System.Collections.Generic;
using Ashlar.Abstractions;
using Ashlar.BackgroundAgents.Configuration;
using Ashlar.BackgroundAgents.DataSensitivity;
using Ashlar.BackgroundAgents.Registry;

namespace Ashlar.BackgroundAgents.Security;

/// <summary>
/// Policy that enforces exfiltration prevention for background agent tool calls.
///
/// Uses agent id from WorldSnapshot.Data["agentId"] to look up BackgroundAgentConfig,
/// then applies ExfiltrationPolicy to block tool calls that would send data to
/// external LLMs, web search, or network exports when disallowed.
/// Implements IPolicy for use with PolicyEngine.
///
/// <para><b>An agent it cannot identify gets the MOST restrictive policy, not none.</b> When the
/// snapshot carries no usable <c>agentId</c>, or names an agent the registry does not hold, every
/// block this policy knows applies (external LLM, web search, network export, local-only). It used
/// to APPROVE such a call with reason "OK", so a caller could escape every exfiltration rule by
/// omitting or misspelling its own id. The scope is unchanged: this policy only ever refuses the
/// exfiltration-class tool ids below, so a local tool (a file read, a build) is still approved
/// for an unidentified agent exactly as for the most restricted registered one. That the tool
/// lists are deny-lists -- an exfiltrating tool with an id not on them passes -- is a separate,
/// known gap.</para>
/// </summary>
public sealed class DataExfiltrationPolicy : IPolicy
{
    private static readonly HashSet<string> DefaultExternalLlmToolIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "complete", "chat", "llm_complete", "LlmComplete", "openai_complete", "ollama_complete"
    };

    private static readonly HashSet<string> DefaultWebSearchToolIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "web_search", "WebSearch", "search", "Search", "bing_search", "duckduckgo_search"
    };

    private static readonly HashSet<string> DefaultNetworkExportToolIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "export", "Export", "upload", "Upload", "send_to_network", "SendToNetwork", "http_post", "HttpPost"
    };

    /// <summary>What an agent that cannot be identified is held to: every block at once.</summary>
    private static readonly ExfiltrationPolicy MostRestrictivePolicy = new()
    {
        BlockExternalLLMs = true,
        BlockWebSearch = true,
        BlockNetworkExports = true,
        RequireLocalOnly = true,
        MaxAllowedLevel = DataSensitivityLevels.Public.Value,
    };

    private readonly IBackgroundAgentRegistry _registry;
    private readonly IDataSensitivityRegistry _sensitivityRegistry;
    private readonly IReadOnlySet<string> _externalLlmToolIds;
    private readonly IReadOnlySet<string> _webSearchToolIds;
    private readonly IReadOnlySet<string> _networkExportToolIds;

    /// <summary>
    /// Initializes a new instance of the <see cref="DataExfiltrationPolicy"/> class.
    /// </summary>
    /// <param name="registry">Background agent registry to resolve agent config from snapshot.</param>
    /// <param name="sensitivityRegistry">Sensitivity registry to resolve MaxAllowedLevel.</param>
    /// <param name="externalLlmToolIds">Tool ids treated as external LLM (optional; uses defaults if null).</param>
    /// <param name="webSearchToolIds">Tool ids treated as web search (optional; uses defaults if null).</param>
    /// <param name="networkExportToolIds">Tool ids treated as network export (optional; uses defaults if null).</param>
    public DataExfiltrationPolicy(
        IBackgroundAgentRegistry registry,
        IDataSensitivityRegistry sensitivityRegistry,
        IEnumerable<string>? externalLlmToolIds = null,
        IEnumerable<string>? webSearchToolIds = null,
        IEnumerable<string>? networkExportToolIds = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _sensitivityRegistry = sensitivityRegistry ?? throw new ArgumentNullException(nameof(sensitivityRegistry));
        _externalLlmToolIds = externalLlmToolIds != null
            ? new HashSet<string>(externalLlmToolIds, StringComparer.OrdinalIgnoreCase)
            : DefaultExternalLlmToolIds;
        _webSearchToolIds = webSearchToolIds != null
            ? new HashSet<string>(webSearchToolIds, StringComparer.OrdinalIgnoreCase)
            : DefaultWebSearchToolIds;
        _networkExportToolIds = networkExportToolIds != null
            ? new HashSet<string>(networkExportToolIds, StringComparer.OrdinalIgnoreCase)
            : DefaultNetworkExportToolIds;
    }

    /// <inheritdoc />
    public bool Approve(ToolCall call, WorldSnapshot s, out string reason)
    {
        reason = string.Empty;

        // Resolve whose policy applies. Every branch that cannot name a registered agent falls to
        // the most restrictive policy, with the reason saying why, so a refusal of an unidentified
        // caller reads differently from a refusal of a known one.
        ExfiltrationPolicy policy;
        var basis = "Exfiltration policy";
        if (!s.Data.TryGetValue("agentId", out var agentIdObj)
            || agentIdObj is not string agentId
            || string.IsNullOrWhiteSpace(agentId))
        {
            policy = MostRestrictivePolicy;
            basis = "Exfiltration policy (no agent id in the snapshot; most restrictive policy applied)";
        }
        else if (_registry.GetAgent(agentId) is not { } instance)
        {
            policy = MostRestrictivePolicy;
            basis = $"Exfiltration policy (agent '{agentId}' is not registered; most restrictive policy applied)";
        }
        else if (instance.Config?.ExfiltrationPolicy is not { } configured)
        {
            // A registered agent with no config, or a config whose policy was nulled after loading
            // (BackgroundAgentConfig.ExfiltrationPolicy is non-nullable but settable, and not every
            // registration goes through the loader). Before this class failed closed, this line
            // threw a NullReferenceException; the permissive `new ExfiltrationPolicy()` is the
            // tempting fix and the wrong one.
            policy = MostRestrictivePolicy;
            basis = $"Exfiltration policy (agent '{agentId}' has no exfiltration policy; most restrictive policy applied)";
        }
        else
        {
            policy = configured;
        }

        var toolId = call.Id ?? string.Empty;

        if (policy.BlockExternalLLMs && _externalLlmToolIds.Contains(toolId))
        {
            reason = $"{basis} blocks external LLM tool: {toolId}";
            return false;
        }

        if (policy.BlockWebSearch && _webSearchToolIds.Contains(toolId))
        {
            reason = $"{basis} blocks web search tool: {toolId}";
            return false;
        }

        if (policy.BlockNetworkExports && _networkExportToolIds.Contains(toolId))
        {
            reason = $"{basis} blocks network export tool: {toolId}";
            return false;
        }

        if (policy.RequireLocalOnly)
        {
            if (_externalLlmToolIds.Contains(toolId) || _webSearchToolIds.Contains(toolId) || _networkExportToolIds.Contains(toolId))
            {
                reason = $"{basis} requires local-only; tool {toolId} is not local";
                return false;
            }
        }

        reason = "OK";
        return true;
    }
}
