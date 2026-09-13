using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NCrontab;
using Ashlar.BackgroundAgents.DataSensitivity;

namespace Ashlar.BackgroundAgents.Configuration;

/// <summary>
/// Loads and validates background agent configurations.
/// 
/// Supports loading from:
/// - appsettings.json
/// - Dedicated configuration files
/// - Environment variables
/// </summary>
public class BackgroundAgentConfigLoader
{
    private readonly IConfiguration _configuration;
    private readonly IDataSensitivityRegistry _sensitivityRegistry;
    private readonly ILogger<BackgroundAgentConfigLoader>? _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="BackgroundAgentConfigLoader"/> class.
    /// </summary>
    /// <param name="configuration">The configuration source.</param>
    /// <param name="sensitivityRegistry">The sensitivity level registry.</param>
    /// <param name="logger">Optional logger.</param>
    public BackgroundAgentConfigLoader(
        IConfiguration configuration,
        IDataSensitivityRegistry sensitivityRegistry,
        ILogger<BackgroundAgentConfigLoader>? logger = null)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _sensitivityRegistry = sensitivityRegistry ?? throw new ArgumentNullException(nameof(sensitivityRegistry));
        _logger = logger;
    }

    /// <summary>
    /// Load background agent configurations.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of validated agent configurations.</returns>
    public Task<List<BackgroundAgentConfig>> LoadAsync(CancellationToken cancellationToken = default)
    {
        // Load from appsettings.json or dedicated config file
        var section = _configuration.GetSection("BackgroundAgents:Agents");
        var configs = new List<BackgroundAgentConfig>();

        section.Bind(configs);

        // Validate and process configurations
        foreach (var config in configs)
        {
            ValidateConfig(config);
            ProcessConfig(config);
        }

        return Task.FromResult(configs);
    }

    private void ValidateConfig(BackgroundAgentConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.Id))
            throw new InvalidOperationException("Agent ID is required");

        if (string.IsNullOrWhiteSpace(config.Name))
            throw new InvalidOperationException($"Agent {config.Id} must have a name");

        if (string.IsNullOrWhiteSpace(config.Role))
            throw new InvalidOperationException($"Agent {config.Id} must have a role");

        if (config.Commands == null || config.Commands.Count == 0)
            throw new InvalidOperationException($"Agent {config.Id} must have at least one command");

        // Validate schedule
        ValidateSchedule(config.Schedule);

        // Validate RAG config if present
        config.RAG?.ValidateProvider(config.Id);

        // Validate web search config if present
        if (config.WebSearch?.Enabled == true)
        {
            if (string.IsNullOrWhiteSpace(config.WebSearch.SearchProvider))
                throw new InvalidOperationException($"Agent {config.Id} web search enabled but no provider specified");
        }

        // Validate sensitivity level exists
        var sensitivityLevel = _sensitivityRegistry.GetByName(config.MaxDataSensitivity);
        if (sensitivityLevel == null)
        {
            throw new InvalidOperationException($"Agent {config.Id} has unknown sensitivity level: {config.MaxDataSensitivity}");
        }
    }

    private void ValidateSchedule(BackgroundAgentSchedule schedule)
    {
        switch (schedule.Type)
        {
            case ScheduleType.Interval:
                if (!schedule.Interval.HasValue)
                    throw new InvalidOperationException("Interval schedule type requires Interval to be set");
                break;

            case ScheduleType.Cron:
                if (string.IsNullOrWhiteSpace(schedule.CronExpression))
                    throw new InvalidOperationException("Cron schedule type requires CronExpression to be set");
                try
                {
                    _ = CrontabSchedule.Parse(schedule.CronExpression);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        $"Invalid cron expression: {schedule.CronExpression}. {ex.Message}", ex);
                }
                break;

            case ScheduleType.Continuous:
                // No additional validation needed
                break;

            default:
                throw new InvalidOperationException($"Unknown schedule type: {schedule.Type}");
        }
    }

    private void ProcessConfig(BackgroundAgentConfig config)
    {
        // Get sensitivity level to determine defaults
        var sensitivityLevel = _sensitivityRegistry.GetByName(config.MaxDataSensitivity);
        if (sensitivityLevel == null)
        {
            throw new InvalidOperationException($"Unknown sensitivity level: {config.MaxDataSensitivity}");
        }

        // Set default exfiltration policy if not provided
        if (config.ExfiltrationPolicy == null || 
            (config.ExfiltrationPolicy.BlockExternalLLMs == false && 
             config.ExfiltrationPolicy.BlockWebSearch == false &&
             config.ExfiltrationPolicy.BlockNetworkExports == false &&
             config.ExfiltrationPolicy.RequireLocalOnly == false &&
             string.IsNullOrWhiteSpace(config.ExfiltrationPolicy.MaxAllowedLevel)))
        {
            config.ExfiltrationPolicy = new ExfiltrationPolicy
            {
                MaxAllowedLevel = config.MaxDataSensitivity,
                BlockExternalLLMs = !sensitivityLevel.AllowsExternalLLM,
                BlockWebSearch = !sensitivityLevel.AllowsWebSearch,
                RequireLocalOnly = sensitivityLevel.RequiresLocalOnly,
                BlockNetworkExports = !sensitivityLevel.AllowsNetworkExports
            };
        }

        // Register custom sensitivity levels if provided
        if (config.CustomSensitivityLevels != null && config.CustomSensitivityLevels.Any())
        {
            var factory = new CustomSensitivityLevelFactory(_sensitivityRegistry);
            factory.RegisterFromConfig(config.CustomSensitivityLevels);
        }
    }
}
