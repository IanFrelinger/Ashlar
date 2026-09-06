using Microsoft.Extensions.Logging;
using Ashlar.BackgroundAgents.DataSensitivity;
using Ashlar.Core.Application.Orchestration.Ports;
using System.Text.Json;

namespace Ashlar.BackgroundAgents.Configuration;

/// <summary>
/// Builder for creating AgentSpawnSpecDto from BackgroundAgentConfig.
/// 
/// Converts configuration into the format expected by agent creators.
/// </summary>
public class BackgroundAgentSpecBuilder
{
    private readonly IDataSensitivityRegistry _sensitivityRegistry;
    private readonly ILogger<BackgroundAgentSpecBuilder>? _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="BackgroundAgentSpecBuilder"/> class.
    /// </summary>
    /// <param name="sensitivityRegistry">The sensitivity level registry.</param>
    /// <param name="logger">Optional logger.</param>
    public BackgroundAgentSpecBuilder(
        IDataSensitivityRegistry sensitivityRegistry,
        ILogger<BackgroundAgentSpecBuilder>? logger = null)
    {
        _sensitivityRegistry = sensitivityRegistry ?? throw new ArgumentNullException(nameof(sensitivityRegistry));
        _logger = logger;
    }

    /// <summary>
    /// Build an AgentSpawnSpecDto from a BackgroundAgentConfig.
    /// </summary>
    /// <param name="config">The agent configuration.</param>
    /// <returns>An AgentSpawnSpecDto ready for agent creation.</returns>
    /// <exception cref="ArgumentException">Thrown if configuration is invalid.</exception>
    public AgentSpawnSpecDto BuildSpec(BackgroundAgentConfig config)
    {
        if (config == null)
            throw new ArgumentNullException(nameof(config));

        // Get sensitivity level to include in parameters
        var sensitivityLevel = _sensitivityRegistry.GetByName(config.MaxDataSensitivity);
        if (sensitivityLevel == null)
        {
            throw new ArgumentException($"Unknown sensitivity level: {config.MaxDataSensitivity}", nameof(config));
        }

        // Build system prompt based on role and commands
        var systemPrompt = BuildSystemPrompt(config, sensitivityLevel);

        // Determine dependencies (parent-child relationships)
        var dependencies = new List<string>();
        if (!string.IsNullOrEmpty(config.ParentId))
        {
            dependencies.Add(config.ParentId);
        }

        // Determine agent type based on role
        var agentType = DetermineAgentType(config.Role);

        // Build description with additional context
        var description = $"{config.Name}\n" +
            $"Max Sensitivity: {config.MaxDataSensitivity}\n" +
            $"RAG: {(config.RAG?.Enabled == true ? "Enabled" : "Disabled")}\n" +
            $"Web Search: {(config.WebSearch?.Enabled == true ? "Enabled" : "Disabled")}";

        return new AgentSpawnSpecDto
        {
            AgentId = config.Id,
            Name = config.Name,
            Domain = agentType,
            Goal = $"Execute {config.Role} tasks: {string.Join(", ", config.Commands)}",
            Description = description,
            Dependencies = dependencies
        };
    }

    private string BuildSystemPrompt(BackgroundAgentConfig config, IDataSensitivityLevel sensitivityLevel)
    {
        var prompt = $@"You are a {config.Role} agent named {config.Name}.

Your available commands are:
{string.Join("\n", config.Commands.Select(c => $"- {c}"))}

IMPORTANT: You can only access data with sensitivity level {config.MaxDataSensitivity} (value: {sensitivityLevel.SensitivityValue}) or lower.
Any data marked as more sensitive will be automatically blocked.
Restrictions: External LLM={sensitivityLevel.AllowsExternalLLM}, Web Search={sensitivityLevel.AllowsWebSearch}, Local Only={sensitivityLevel.RequiresLocalOnly}.";

        if (config.RAG?.Enabled == true)
        {
            prompt += $@"

You have access to a knowledge base via RAG (Retrieval Augmented Generation).
Use the 'rag.search-knowledge-base' tool to search for relevant information before making decisions.";
        }

        if (config.WebSearch?.Enabled == true)
        {
            prompt += $@"

You have access to web search capabilities.
Use the 'web.search' tool to find current information from the internet.
Note: Sensitive content will be automatically filtered from search queries.";
        }

        prompt += @"

Execute your commands based on your role and the current system state.
Report your findings and take actions as appropriate for your role.";

        return prompt;
    }

    private string DetermineAgentType(string role)
    {
        return role.ToLowerInvariant() switch
        {
            "monitor" => "Infrastructure",
            "analyzer" => "CodeGeneration",
            "optimizer" => "Infrastructure",
            "auditor" => "Security",
            "extender" => "CodeGeneration",
            _ => "Generic"
        };
    }
}
