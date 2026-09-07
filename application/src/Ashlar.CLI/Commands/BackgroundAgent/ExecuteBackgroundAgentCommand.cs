using System.Text.Json;
using Microsoft.Extensions.Logging;
using Ashlar.BackgroundAgents.Configuration;
using Ashlar.BackgroundAgents.Registry;
using Ashlar.Orchestration.Agents;

namespace Ashlar.CLI.Commands.BackgroundAgent;

/// <summary>
/// CLI command to manually trigger one execution of a background agent (for testing).
/// </summary>
public class ExecuteBackgroundAgentCommand
{
    private readonly BackgroundAgentConfigLoader _configLoader;
    private readonly IBackgroundAgentRegistry _registry;
    private readonly BackgroundAgentSpecBuilder _specBuilder;
    private readonly AgentFactory _agentFactory;
    private readonly ILogger<ExecuteBackgroundAgentCommand> _logger;

    /// <summary>Creates a new ExecuteBackgroundAgentCommand instance.</summary>
    public ExecuteBackgroundAgentCommand(
        BackgroundAgentConfigLoader configLoader,
        IBackgroundAgentRegistry registry,
        BackgroundAgentSpecBuilder specBuilder,
        AgentFactory agentFactory,
        ILogger<ExecuteBackgroundAgentCommand> logger)
    {
        _configLoader = configLoader ?? throw new ArgumentNullException(nameof(configLoader));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _specBuilder = specBuilder ?? throw new ArgumentNullException(nameof(specBuilder));
        _agentFactory = agentFactory ?? throw new ArgumentNullException(nameof(agentFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Ensure agent is registered, then run one execution cycle.
    /// </summary>
    public async Task<int> ExecuteAsync(string id, bool runAsync, bool formatJson, CancellationToken ct = default)
    {
        try
        {
            await EnsureAgentRegisteredAsync(id, ct).ConfigureAwait(false);
            if (runAsync)
            {
                _ = Task.Run(() => _registry.ExecuteOnceAsync(id, ct), ct);
                if (formatJson)
                    Console.Out.WriteLine(JsonSerializer.Serialize(new { ok = true, id, action = "execute-async-started" }));
                else
                    Console.Out.WriteLine($"Started one-off execution for agent: {id}");
                return 0;
            }
            await _registry.ExecuteOnceAsync(id, ct).ConfigureAwait(false);
            if (formatJson)
                Console.Out.WriteLine(JsonSerializer.Serialize(new { ok = true, id, action = "executed" }));
            else
                Console.Out.WriteLine($"Executed agent: {id}");
            return 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Execute background agent failed");
            if (formatJson)
                Console.Out.WriteLine(JsonSerializer.Serialize(new { ok = false, error = ex.Message }));
            else
                Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private async Task EnsureAgentRegisteredAsync(string id, CancellationToken ct)
    {
        if (_registry.GetAgent(id) != null)
            return;
        var configs = await _configLoader.LoadAsync(ct).ConfigureAwait(false);
        var config = configs.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Agent '{id}' not found in configuration");
        var spec = _specBuilder.BuildSpec(config);
        var agent = _agentFactory.CreateAgent(spec);
        await _registry.RegisterAsync(agent, config, AgentRegistrationOrigin.Authored, ct).ConfigureAwait(false);
    }
}
