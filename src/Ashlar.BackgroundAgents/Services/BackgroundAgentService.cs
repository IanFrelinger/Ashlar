using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Ashlar.BackgroundAgents.Configuration;
using Ashlar.BackgroundAgents.DataSensitivity;
using Ashlar.BackgroundAgents.Registry;
using Ashlar.Core.Application.Orchestration.Ports;

namespace Ashlar.BackgroundAgents.Services;

/// <summary>
/// Background service that manages and executes background agents.
/// 
/// Responsibilities:
/// - Loads agent configurations on startup
/// - Creates agents from configurations
/// - Registers agents with the registry
/// - Starts agent execution loops
/// - Manages agent lifecycle
/// 
/// Runs as a hosted service, starting automatically when the application starts.
/// </summary>
public class BackgroundAgentService : BackgroundService
{
    private readonly BackgroundAgentConfigLoader _configLoader;
    private readonly BackgroundAgentSpecBuilder _specBuilder;
    private readonly IAgentCreator _agentCreator;
    private readonly IBackgroundAgentRegistry _registry;
    private readonly IDataSensitivityRegistry _sensitivityRegistry;
    private readonly ILogger<BackgroundAgentService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="BackgroundAgentService"/> class.
    /// </summary>
    /// <param name="configLoader">The configuration loader.</param>
    /// <param name="specBuilder">The spec builder for creating AgentSpawnSpecDto.</param>
    /// <param name="agentCreator">The agent creator for creating agents.</param>
    /// <param name="registry">The agent registry.</param>
    /// <param name="sensitivityRegistry">The sensitivity level registry.</param>
    /// <param name="logger">The logger.</param>
    public BackgroundAgentService(
        BackgroundAgentConfigLoader configLoader,
        BackgroundAgentSpecBuilder specBuilder,
        IAgentCreator agentCreator,
        IBackgroundAgentRegistry registry,
        IDataSensitivityRegistry sensitivityRegistry,
        ILogger<BackgroundAgentService> logger)
    {
        _configLoader = configLoader ?? throw new ArgumentNullException(nameof(configLoader));
        _specBuilder = specBuilder ?? throw new ArgumentNullException(nameof(specBuilder));
        _agentCreator = agentCreator ?? throw new ArgumentNullException(nameof(agentCreator));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _sensitivityRegistry = sensitivityRegistry ?? throw new ArgumentNullException(nameof(sensitivityRegistry));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Executes the background service.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Background agent service starting");

        try
        {
            // Load configurations
            var configs = await _configLoader.LoadAsync(stoppingToken);
            _logger.LogInformation("Loaded {Count} background agent configurations", configs.Count);

            // Create and register agents
            var enabledConfigs = configs.Where(c => c.Enabled).ToList();
            _logger.LogInformation("Creating {Count} enabled background agents", enabledConfigs.Count);

            foreach (var config in enabledConfigs)
            {
                try
                {
                    await CreateAndRegisterAgentAsync(config, stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to create agent {AgentId}: {Error}", config.Id, ex.Message);
                    // Continue with other agents even if one fails
                }
            }

            // Start all registered agents
            await _registry.StartAllAsync(stoppingToken);
            _logger.LogInformation("Background agent service started successfully");

            // Keep service running until cancellation
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Background agent service stopping");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Background agent service failed: {Error}", ex.Message);
            throw;
        }
    }

    /// <summary>
    /// Stops the background service.
    /// </summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Background agent service stopping");

        // Stop all agents
        await _registry.StopAllAsync(cancellationToken);

        await base.StopAsync(cancellationToken);
        _logger.LogInformation("Background agent service stopped");
    }

    private async Task CreateAndRegisterAgentAsync(BackgroundAgentConfig config, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Creating agent {AgentId} from configuration", config.Id);

        // Build AgentSpawnSpecDto from config
        var spec = _specBuilder.BuildSpec(config);

        // Create agent using creator
        var agent = _agentCreator.CreateAgent(spec);

        // Register with registry
        await _registry.RegisterAsync(agent, config, AgentRegistrationOrigin.Authored, cancellationToken);

        _logger.LogInformation("Created and registered background agent: {AgentId}", config.Id);
    }
}
