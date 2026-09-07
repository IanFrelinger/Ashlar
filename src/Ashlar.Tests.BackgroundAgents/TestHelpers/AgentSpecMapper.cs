using Ashlar.Core.Application.Orchestration.Ports;
using Ashlar.Orchestration.Architect.Models;

namespace Ashlar.Tests.BackgroundAgents.TestHelpers;

/// <summary>
/// Helper to map between Application layer DTOs and Orchestration layer models in tests.
/// Duplicates AgentFactory's internal mapping to allow direct agent construction in tests.
/// </summary>
internal static class AgentSpecMapper
{
    /// <summary>
    /// Converts AgentSpawnSpecDto (Application layer) to AgentSpawnSpec (Orchestration layer).
    /// </summary>
    public static AgentSpawnSpec ToOrchestrationSpec(this AgentSpawnSpecDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);

        return new AgentSpawnSpec
        {
            AgentId = dto.AgentId,
            Name = dto.Name,
            Domain = dto.Domain,
            Goal = dto.Goal,
            Description = dto.Description,
            Dependencies = dto.Dependencies,
            OllamaModel = dto.OllamaModel
        };
    }
}
