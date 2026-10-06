using Ashlar.Abstractions;

namespace Ashlar.Runtime;

/// <summary>
/// Registry for tools and agent memory, implementing IToolbox.
/// 
/// Responsibilities:
/// - Registers ITool instances for agent use
/// - Provides tool schemas for agent discovery
/// - Manages per-agent memory instances
/// - Invokes tools on behalf of agents
/// 
/// Implements IToolbox interface for agent tool access.
/// Used by AgentHost for tool execution.
/// </summary>
public sealed class CapabilityRegistry : IToolbox
{
    private readonly Dictionary<string, ITool> _tools = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, InMemoryAgentMemory> _mem = new();

    /// <summary>Registers a tool for agent invocation.</summary>
    public void Register(ITool tool) => _tools[tool.Id] = tool;

    /// <summary>Returns schemas for all registered tools.</summary>
    public IEnumerable<ToolSchema> Schemas() => _tools.Values.Select(t => t.Schema);

    /// <summary>Returns or creates in-memory storage for <paramref name="agent"/>.</summary>
    public IAgentMemory MemoryFor(IAgent agent)
    {
        if (!_mem.TryGetValue(agent.Name, out var m))
        {
            m = new InMemoryAgentMemory();
            _mem[agent.Name] = m;
        }
        return m;
    }

    /// <summary>
    /// The tool registered under <paramref name="id"/>, the one <see cref="InvokeAsync"/> would invoke for a call with
    /// that id, or <see langword="null"/> when none is.
    /// </summary>
    public ITool? Find(string id) => _tools.TryGetValue(id, out var tool) ? tool : null;

    /// <summary>Invokes a registered tool on behalf of an agent.</summary>
    public Task<ToolResult> InvokeAsync(ToolCall call, WorldSnapshot s, CancellationToken ct)
    {
        if (!_tools.TryGetValue(call.Id, out var tool))
            throw new InvalidOperationException($"Tool '{call.Id}' is not registered.");
        return tool.InvokeAsync(call, s, ct);
    }
}
