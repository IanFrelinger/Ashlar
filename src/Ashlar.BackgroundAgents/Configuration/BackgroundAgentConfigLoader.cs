using System.Diagnostics.CodeAnalysis;
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

        // Bound one agent at a time, not with section.Bind(list), because whether an agent's
        // ExfiltrationPolicy SECTION exists is only visible on its own configuration section: once
        // bound, an absent section and a written one are the same object, since
        // BackgroundAgentConfig.ExfiltrationPolicy is initialised to a default instance.
        foreach (var agentSection in section.GetChildren())
        {
            if (!TryBindAgent(agentSection, out var config))
                continue;

            ValidateConfig(config);
            ProcessConfig(config, agentSection.GetSection(nameof(BackgroundAgentConfig.ExfiltrationPolicy)).Exists());
            configs.Add(config);
        }

        return Task.FromResult(configs);
    }

    /// <summary>
    /// Binds one element of <c>BackgroundAgents:Agents</c> with the outcome
    /// <c>section.Bind(list)</c> gave that element, so binding per agent changes nothing but what
    /// can be seen about the ExfiltrationPolicy section.
    /// </summary>
    /// <remarks>
    /// Measured against the list bind on the same configurations, with the configuration binder
    /// this repository pins (10.0.x), and pinned in BackgroundAgentConfigLoaderGapCoverageTests:
    /// <list type="bullet">
    /// <item>A value the binder cannot convert -- <c>"Enabled": "notabool"</c>, an unknown
    /// <c>Schedule:Type</c>, <c>"BlockExternalLLMs": "yes"</c>: the list bind swallowed the
    /// binder's exception and LEFT THAT AGENT OUT; the others loaded. <c>Get&lt;T&gt;</c> throws the
    /// exception instead, so it is caught here. Leaving the agent out is also the closed outcome --
    /// an agent whose configuration cannot be read does not run -- and it is now logged with the
    /// element's path, where it used to be silent.</item>
    /// <item>A scalar where an agent object belongs (<c>"Agents": ["x", ...]</c>): left out, as
    /// before, and now logged.</item>
    /// <item>An EMPTY element (<c>{}</c> or <c>null</c> in JSON, an empty value elsewhere): the list
    /// bind bound it to a default instance, which <see cref="ValidateConfig"/> refuses with "Agent ID
    /// is required", failing the whole load. <c>Get&lt;T&gt;</c> returns null for it; skipping it
    /// would have quietly turned that refusal into a pass, so the default instance is handed back
    /// and refused as before.</item>
    /// </list>
    /// </remarks>
    private bool TryBindAgent(IConfigurationSection agentSection, [NotNullWhen(true)] out BackgroundAgentConfig? config)
    {
        try
        {
            config = agentSection.Get<BackgroundAgentConfig>();
        }
        catch (InvalidOperationException ex)
        {
            LogAgentLeftOut(agentSection, ex.Message, ex);
            config = null;
            return false;
        }

        if (config != null)
            return true;

        if (string.IsNullOrEmpty(agentSection.Value))
        {
            config = new BackgroundAgentConfig();
            return true;
        }

        LogAgentLeftOut(agentSection, $"'{agentSection.Value}' is a value where an agent object belongs", null);
        return false;
    }

    private void LogAgentLeftOut(IConfigurationSection agentSection, string reason, Exception? exception) =>
        _logger?.LogWarning(
            exception,
            "Background agent at {ConfigPath} was left out: its configuration could not be bound ({Reason}). The other agents are loaded; this one will not run until it is corrected.",
            agentSection.Path,
            reason);

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

    /// <summary>
    /// Applies defaults that depend on the agent's sensitivity level.
    /// </summary>
    /// <param name="config">The bound agent configuration.</param>
    /// <param name="exfiltrationPolicySectionPresent">Whether the agent's configuration contains an
    /// <c>ExfiltrationPolicy</c> section at all.</param>
    /// <remarks>
    /// <para><b>An agent with no ExfiltrationPolicy section gets the restrictions its
    /// MaxDataSensitivity implies.</b> The derivation below existed before, but it could not fire:
    /// it waited for a policy whose MaxAllowedLevel was blank, and an absent section binds to
    /// <see cref="ExfiltrationPolicy"/>'s defaults, whose MaxAllowedLevel is "Public" -- never
    /// blank. So an agent declaring MaxDataSensitivity TopSecret and saying nothing about
    /// exfiltration ran with every Block flag false. The only test of the derivation passed because
    /// it WROTE a section with a whitespace MaxAllowedLevel.</para>
    /// <para>A section that is present is the operator's explicit choice and is kept as written. The
    /// old trigger -- a written section whose four flags are all false and whose MaxAllowedLevel is
    /// blank -- still derives, so no configuration that used to be restricted loses it.</para>
    /// </remarks>
    private void ProcessConfig(BackgroundAgentConfig config, bool exfiltrationPolicySectionPresent)
    {
        // Get sensitivity level to determine defaults
        var sensitivityLevel = _sensitivityRegistry.GetByName(config.MaxDataSensitivity);
        if (sensitivityLevel == null)
        {
            throw new InvalidOperationException($"Unknown sensitivity level: {config.MaxDataSensitivity}");
        }

        // Set default exfiltration policy if not provided
        if (!exfiltrationPolicySectionPresent ||
            config.ExfiltrationPolicy == null ||
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
