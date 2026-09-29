using Microsoft.Extensions.Logging;
using Ashlar.Core.Application.Adaptation.Models;
using Ashlar.Core.Application.Adaptation.Ports;
using Ashlar.Core.Domain.Bricks.Ports;
using Ashlar.Infrastructure.Execution;

namespace Ashlar.Infrastructure.Adaptation.Generation;

/// <summary>
/// Real model implementation behind the sealed seam. Marked isolation-enforced; not used in hermetic tests.
/// Structural rules in the prompt come from a <see cref="BrickConstraintManifest"/>
/// (spec R3.5) rather than prose, so the instructions the proposer sees are the same
/// object an ingest gate enforces.
/// </summary>
internal sealed class ProviderGeneratorModel : IGeneratorModel
{
    private readonly IProviderFactory _providerFactory;
    private readonly ILogger<ProviderGeneratorModel>? _logger;
    private readonly string _provider;
    private readonly BrickConstraintManifest _constraints;

    /// <summary>Initializes a new provider generator model.</summary>
    public ProviderGeneratorModel(
        IProviderFactory providerFactory,
        string provider = "ollama",
        ILogger<ProviderGeneratorModel>? logger = null,
        BrickConstraintManifest? constraints = null)
    {
        _providerFactory = providerFactory ?? throw new ArgumentNullException(nameof(providerFactory));
        _provider = provider;
        _logger = logger;
        _constraints = constraints ?? DamageResolverBrickConstraints.Default;
    }

    /// <summary>Generate asynchronously.</summary>
    public async Task<GeneratedBrickSource> GenerateAsync(
        IntentSpec intent,
        WitnessSignature witnessSignature,
        CancellationToken cancellationToken = default)
    {
        var systemPrompt = $"""
You generate deterministic C# brick implementations for Ashlar.
Rules:
- Target net8, override ExecuteAsync only.
{_constraints.RenderInstructions()}
- Return ONLY the C# source file contents.
""";

        var userPrompt = $"""
Intent: {intent.Description}
BrickId: {witnessSignature.BrickId}
Inputs: {string.Join(", ", witnessSignature.Inputs.Select(i => $"{i.Name}:{i.Type}"))}
Outputs: {string.Join(", ", witnessSignature.Outputs.Select(o => $"{o.Name}:{o.Type}"))}
""";

        _logger?.LogInformation(
            "ProviderGeneratorModel generating for intent {IntentId} via {Provider}",
            intent.IntentId,
            _provider);

        var response = await _providerFactory.ExecuteLLMAsync(
            _provider,
            systemPrompt,
            userPrompt,
            new { temperature = 0 },
            cancellationToken).ConfigureAwait(false);

        var source = ExtractSource(response);
        return new GeneratedBrickSource(
            source,
            Provenance: $"model:{_provider}:isolation-enforced",
            ClassName: InferClassName(source),
            Namespace: InferNamespace(source));
    }

    private static string ExtractSource(string response)
    {
        const string fence = "```";
        var start = response.IndexOf(fence, StringComparison.Ordinal);
        if (start >= 0)
        {
            var afterFence = response.IndexOf('\n', start);
            var end = response.IndexOf(fence, afterFence + 1, StringComparison.Ordinal);
            if (afterFence >= 0 && end > afterFence)
                return response[(afterFence + 1)..end].Trim();
        }

        return response.Trim();
    }

    private static string InferClassName(string source)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            source,
            @"public\s+sealed\s+class\s+(\w+)");
        return match.Success ? match.Groups[1].Value : "GeneratedBrick";
    }

    private string InferNamespace(string source)
    {
        var match = System.Text.RegularExpressions.Regex.Match(source, @"namespace\s+([\w.]+)");
        if (match.Success)
            return match.Groups[1].Value;
        // Fall back to the manifest's required namespace instead of a second
        // hard-coded copy of it; ingest gating (PR-D) rejects rather than infers.
        return _constraints.RequiredNamespace ?? "Ashlar.Certified.DamageResolver";
    }
}
