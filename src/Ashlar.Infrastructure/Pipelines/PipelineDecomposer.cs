using Ashlar.Core.Application.Pipelines.Models;
using Ashlar.Core.Application.Pipelines.Ports;

namespace Ashlar.Infrastructure.Pipelines;

/// <summary>
/// Decomposes templates into executable DAG nodes.
/// </summary>
public sealed class PipelineDecomposer : IPipelineDecomposer
{
    private readonly IPipelineTemplateValidator _validator;

    /// <summary>Initializes a new pipeline decomposer.</summary>
    public PipelineDecomposer(IPipelineTemplateValidator validator)
    {
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
    }

    /// <summary>Decompose.</summary>
    public PipelineExecutionGraph Decompose(PipelineTemplate template)
    {
        var validation = _validator.Validate(template);
        if (!validation.IsValid)
            throw new InvalidOperationException($"Template validation failed: {string.Join("; ", validation.Errors)}");

        var predecessors = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var successors = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var stage in template.Stages)
        {
            predecessors[stage.Id] = new List<string>();
            successors[stage.Id] = new List<string>();
        }

        foreach (var edge in template.Edges)
        {
            predecessors[edge.ToStageId].Add(edge.FromStageId);
            successors[edge.FromStageId].Add(edge.ToStageId);
        }

        var nodes = template.Stages.Select(stage => new PipelineExecutionNode
        {
            StageId = stage.Id,
            Mode = stage.Mode,
            StageType = stage.StageType,
            JoinStrategy = stage.JoinStrategy,
            QuorumCount = stage.QuorumCount,
            Predecessors = predecessors[stage.Id].AsReadOnly(),
            Successors = successors[stage.Id].AsReadOnly(),
            FallbackChain = stage.Mode == PipelineExecutionMode.Hybrid
                ? stage.FallbackChain
                : Array.Empty<PipelineWorkerType>()
        }).ToList();

        return new PipelineExecutionGraph
        {
            TemplateId = template.TemplateId,
            Nodes = nodes.AsReadOnly()
        };
    }
}
