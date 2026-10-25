using System.Security.Cryptography;
using System.Text.Json;
using Ashlar.Core.Application.Pipelines.Models;

namespace Ashlar.Infrastructure.Pipelines;

/// <summary>Binds a persisted operation key to the work it identifies.</summary>
internal static class PipelineOperationIdentity
{
    internal static string Fingerprint(
        string templateId,
        string templateVersion,
        PipelineStageDefinition stage,
        PipelineExecutionNode node,
        IReadOnlyDictionary<string, string> inputs)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Version = 1,
            TemplateId = templateId,
            TemplateVersion = templateVersion,
            Stage = stage,
            Predecessors = node.Predecessors.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            Successors = node.Successors.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            Inputs = inputs.OrderBy(x => x.Key, StringComparer.Ordinal).ToArray()
        });
        return Convert.ToHexString(SHA256.HashData(payload));
    }

    internal static bool CanReplay(PipelineStageEffects effects)
        => effects is PipelineStageEffects.None or PipelineStageEffects.Idempotent;
}
