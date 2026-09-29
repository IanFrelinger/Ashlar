using Microsoft.Extensions.Logging;
using Ashlar.Core.Application.Certification.Models;
using Ashlar.Core.Application.Certification.Ports;
using Ashlar.Infrastructure.Execution;

namespace Ashlar.Infrastructure.Certification.Composition;

/// <summary>
/// Real model implementation behind the composition generator seam. Marked isolation-enforced;
/// not used in hermetic cert-gate (record once locally; replay via test doubles in Ashlar.Tests.Infrastructure).
/// </summary>
internal sealed class ProviderCompositionGeneratorModel : ICompositionGeneratorModel
{
    private readonly IProviderFactory _providerFactory;
    private readonly ILogger<ProviderCompositionGeneratorModel>? _logger;
    private readonly string _provider;

    /// <summary>Initializes a new provider composition generator model.</summary>
    public ProviderCompositionGeneratorModel(
        IProviderFactory providerFactory,
        string provider = "ollama",
        ILogger<ProviderCompositionGeneratorModel>? logger = null)
    {
        _providerFactory = providerFactory ?? throw new ArgumentNullException(nameof(providerFactory));
        _provider = provider;
        _logger = logger;
    }

    /// <summary>Propose asynchronously.</summary>
    public async Task<ProposedComposition> ProposeAsync(
        CompositionProposerInput input,
        CancellationToken cancellationToken = default)
    {
        var userPrompt = CompositionProposerPromptBuilder.BuildUserPrompt(input);

        _logger?.LogInformation(
            "ProviderCompositionGeneratorModel proposing for {CompositionId} via {Provider}",
            input.Target.CompositionId,
            _provider);

        var response = await _providerFactory.ExecuteLLMAsync(
            _provider,
            CompositionProposerPromptBuilder.SystemPrompt,
            userPrompt,
            new { temperature = 0 },
            cancellationToken).ConfigureAwait(false);

        var spec = CompositionSpecJsonParser.Parse(response);
        return new ProposedComposition(spec, $"model:{_provider}:isolation-enforced");
    }
}
