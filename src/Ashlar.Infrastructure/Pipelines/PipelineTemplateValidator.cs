using Ashlar.Core.Application.Pipelines.Models;
using Ashlar.Core.Application.Pipelines.Ports;

namespace Ashlar.Infrastructure.Pipelines;

/// <summary>
/// Structural validation for pipeline templates.
/// </summary>
public sealed class PipelineTemplateValidator : IPipelineTemplateValidator
{
    /// <summary>Validate.</summary>
    public PipelineTemplateValidationResult Validate(PipelineTemplate template)
    {
        if (template == null)
            return new PipelineTemplateValidationResult
            {
                IsValid = false,
                Errors = new[] { "Template is required." }
            };

        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(template.TemplateId))
            errors.Add("TemplateId is required.");

        if (template.Stages.Count == 0)
            errors.Add("At least one stage is required.");

        var stagesById = new Dictionary<string, PipelineStageDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var stage in template.Stages)
        {
            if (string.IsNullOrWhiteSpace(stage.Id))
            {
                errors.Add("Stage id is required.");
                continue;
            }

            if (!stagesById.TryAdd(stage.Id, stage))
                errors.Add($"Duplicate stage id '{stage.Id}'.");

            if (!Enum.IsDefined(stage.Effects))
                errors.Add($"Stage '{stage.Id}' has an invalid Effects declaration.");
            if (!Enum.IsDefined(stage.Mode) || !Enum.IsDefined(stage.StageType) ||
                (stage.JoinStrategy.HasValue && !Enum.IsDefined(stage.JoinStrategy.Value)) ||
                stage.FallbackChain.Any(worker => !Enum.IsDefined(worker)))
                errors.Add($"Stage '{stage.Id}' has an invalid execution or worker enum value.");

            if (stage.Mode == PipelineExecutionMode.Hybrid && stage.FallbackChain.Count == 0)
                errors.Add($"Hybrid stage '{stage.Id}' must define fallback chain.");

            if (stage.StageType == PipelineStageType.FanIn && stage.JoinStrategy is null)
                errors.Add($"Fan-in stage '{stage.Id}' must define join strategy.");

            if (stage.JoinStrategy == PipelineJoinStrategy.Quorum && (!stage.QuorumCount.HasValue || stage.QuorumCount.Value <= 0))
                errors.Add($"Quorum fan-in stage '{stage.Id}' must define QuorumCount > 0.");

            if (stage.Constraints.MaxParallelism.HasValue && stage.Constraints.MaxParallelism.Value <= 0)
                errors.Add($"Stage '{stage.Id}' MaxParallelism must be greater than 0.");

            if (stage.Constraints.Critical && !stage.Constraints.RequiresPostValidation)
                errors.Add($"Critical stage '{stage.Id}' must require post validation.");
        }

        var outgoing = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var incoming = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var stageId in stagesById.Keys)
        {
            outgoing[stageId] = new List<string>();
            incoming[stageId] = 0;
        }

        foreach (var edge in template.Edges)
        {
            if (!stagesById.ContainsKey(edge.FromStageId))
                errors.Add($"Edge references unknown source stage '{edge.FromStageId}'.");
            if (!stagesById.ContainsKey(edge.ToStageId))
                errors.Add($"Edge references unknown target stage '{edge.ToStageId}'.");

            if (stagesById.ContainsKey(edge.FromStageId) && stagesById.ContainsKey(edge.ToStageId))
            {
                outgoing[edge.FromStageId].Add(edge.ToStageId);
                incoming[edge.ToStageId] = incoming[edge.ToStageId] + 1;
            }
        }

        if (stagesById.Count > 0)
        {
            var roots = incoming.Where(kv => kv.Value == 0).Select(kv => kv.Key).ToList();
            if (roots.Count == 0)
                errors.Add("Pipeline must contain at least one root stage.");

            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var q = new Queue<string>(roots);
            while (q.Count > 0)
            {
                var node = q.Dequeue();
                if (!visited.Add(node))
                    continue;

                foreach (var next in outgoing[node])
                    q.Enqueue(next);
            }

            foreach (var stageId in stagesById.Keys)
            {
                if (!visited.Contains(stageId))
                    errors.Add($"Orphan or unreachable stage '{stageId}'.");
            }

            if (HasCycle(stagesById.Keys, outgoing))
                errors.Add("Pipeline graph contains at least one cycle.");
        }

        return new PipelineTemplateValidationResult
        {
            IsValid = errors.Count == 0,
            Errors = errors
        };
    }

    private static bool HasCycle(IEnumerable<string> nodes, IReadOnlyDictionary<string, List<string>> outgoing)
    {
        var color = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in nodes)
            color[node] = 0;

        foreach (var node in nodes)
        {
            if (color[node] == 0 && Visit(node, outgoing, color))
                return true;
        }

        return false;
    }

    private static bool Visit(string node, IReadOnlyDictionary<string, List<string>> outgoing, IDictionary<string, int> color)
    {
        color[node] = 1;
        foreach (var next in outgoing[node])
        {
            if (color[next] == 1)
                return true;
            if (color[next] == 0 && Visit(next, outgoing, color))
                return true;
        }

        color[node] = 2;
        return false;
    }
}
