using System.Text.Json;
using Ashlar.Abstractions;
using Ashlar.BackgroundAgents.Configuration;
using Ashlar.BackgroundAgents.Registry;
using Ashlar.Core.Application.Orchestration.Ports;

namespace Ashlar.BackgroundAgents.Tools;

/// <summary>
/// ITool that updates (re-registers) a background agent from configuration. Id: "update_agent_config".
/// Reloads config, rebuilds spec, and re-registers the agent so it picks up config changes; does not start/stop.
/// </summary>
public sealed class UpdateAgentConfigTool : ITool
{
    /// <summary>Tool id.</summary>
    public const string DefaultId = "update_agent_config";

    private static readonly ToolSchema SchemaInstance = new(
        DefaultId,
        "Reload a background agent's configuration and re-register it. Does not start or stop the agent. Use restart_agent to apply and run with new config.",
        """{"type":"object","properties":{"agentId":{"type":"string","description":"Background agent ID to update from config"}},"required":["agentId"]}""");

    private readonly IBackgroundAgentRegistry _registry;
    private readonly BackgroundAgentConfigLoader _configLoader;
    private readonly BackgroundAgentSpecBuilder _specBuilder;
    private readonly IAgentCreator _agentCreator;

    /// <inheritdoc />
    public string Id => DefaultId;

    /// <inheritdoc />
    public ToolSchema Schema => SchemaInstance;

    /// <summary>
    /// Initializes a new instance of the <see cref="UpdateAgentConfigTool"/> class.
    /// </summary>
    /// <param name="registry">Background agent registry.</param>
    /// <param name="configLoader">Config loader.</param>
    /// <param name="specBuilder">Spec builder.</param>
    /// <param name="agentCreator">Agent creator.</param>
    public UpdateAgentConfigTool(
        IBackgroundAgentRegistry registry,
        BackgroundAgentConfigLoader configLoader,
        BackgroundAgentSpecBuilder specBuilder,
        IAgentCreator agentCreator)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _configLoader = configLoader ?? throw new ArgumentNullException(nameof(configLoader));
        _specBuilder = specBuilder ?? throw new ArgumentNullException(nameof(specBuilder));
        _agentCreator = agentCreator ?? throw new ArgumentNullException(nameof(agentCreator));
    }

    /// <inheritdoc />
    public async Task<ToolResult> InvokeAsync(ToolCall toolCall, WorldSnapshot s, CancellationToken ct)
    {
        var args = ParseArgs(toolCall);
        var agentId = args.AgentId ?? string.Empty;
        if (string.IsNullOrWhiteSpace(agentId))
        {
            var err = new ActionDelta(s.Tick, s.Tick + 1, new[] { "update_agent_config: agentId is required" });
            return new ToolResult(err, new { ok = false, error = "agentId is required" });
        }
        try
        {
            var configs = await _configLoader.LoadAsync(ct).ConfigureAwait(false);
            var config = configs.FirstOrDefault(c => string.Equals(c.Id, agentId, StringComparison.OrdinalIgnoreCase));
            if (config == null)
            {
                var log = new[] { $"update_agent_config: agent '{agentId}' not found in configuration" };
                var delta = new ActionDelta(s.Tick, s.Tick + 1, log);
                return new ToolResult(delta, new { ok = false, agentId, error = "not found in config" });
            }
            var spec = _specBuilder.BuildSpec(config);
            var agent = _agentCreator.CreateAgent(spec);
            await _registry.RegisterAsync(agent, config, AgentRegistrationOrigin.Authored, ct).ConfigureAwait(false);
            var okLog = new[] { $"Re-registered background agent from config: {agentId}" };
            var okDelta = new ActionDelta(s.Tick, s.Tick + 1, okLog);
            return new ToolResult(okDelta, new { ok = true, agentId, action = "updated" });
        }
        catch (Exception ex)
        {
            var log = new[] { $"update_agent_config failed: {ex.Message}" };
            var delta = new ActionDelta(s.Tick, s.Tick + 1, log);
            return new ToolResult(delta, new { ok = false, agentId, error = ex.Message });
        }
    }

    private static UpdateAgentConfigArgs ParseArgs(ToolCall call)
    {
        try
        {
            var json = call.Arguments.GetRawText();
            return JsonSerializer.Deserialize<UpdateAgentConfigArgs>(json) ?? new UpdateAgentConfigArgs();
        }
        catch
        {
            return new UpdateAgentConfigArgs();
        }
    }

    private sealed class UpdateAgentConfigArgs
    {
        [System.Text.Json.Serialization.JsonPropertyName("agentId")]
        public string? AgentId { get; set; }
    }
}
