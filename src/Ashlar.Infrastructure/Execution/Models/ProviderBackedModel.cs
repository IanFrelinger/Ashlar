using Microsoft.Extensions.Logging;
using Ashlar.Abstractions;

namespace Ashlar.Infrastructure.Execution.Models;

/// <summary>
/// IModel adapter that routes ModelInput messages through IProviderFactory (chat/completions),
/// using the configured provider.
/// </summary>
public sealed class ProviderBackedModel : IModel
{
    private readonly IProviderFactory _providerFactory;
    private readonly ILogger<ProviderBackedModel> _logger;

    /// <summary>Initializes a new provider backed model.</summary>
    public ProviderBackedModel(IProviderFactory providerFactory, ILogger<ProviderBackedModel> logger)
    {
        _providerFactory = providerFactory;
        _logger = logger;
    }

    /// <summary>Complete asynchronously.</summary>
    public async Task<ModelOutput> CompleteAsync(ModelInput input, CancellationToken ct)
    {
        var (provider, model, systemPrompt, userPrompt) = Parse(input);
        if (string.IsNullOrWhiteSpace(provider)) provider = "offline";

        // The factory records a refused availability probe on this scope. That refusal is the answer, never
        // "not available": HotSwappableModel rethrows it instead of falling back to echo (SPEC-007 D23, row 24).
        using (var refusals = new ProviderRefusalScope())
        {
            if (!_providerFactory.IsProviderAvailable(provider))
            {
                refusals.ThrowIfRefused();
                throw new InvalidOperationException($"Provider '{provider}' not available");
            }
        }

        var text = await _providerFactory.ExecuteLLMAsync(
            provider,
            systemPrompt,
            userPrompt,
            config: string.IsNullOrWhiteSpace(model) ? new { } : new { model },
            cancellationToken: ct);

        return new ModelOutput(text);
    }

    private static (string? provider, string? model, string systemPrompt, string userPrompt) Parse(ModelInput input)
    {
        var provider = (string?)null;
        var model = (string?)null;
        var systemParts = new List<string>();
        var userParts = new List<string>();

        foreach (var (role, content) in input.Messages)
        {
            if (string.Equals(role, "system", StringComparison.OrdinalIgnoreCase))
            {
                // Optional inline runtime config:
                // ashlar.model.provider=<provider>
                foreach (var line in (content ?? "").Split('\n'))
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith("ashlar.model.provider=", StringComparison.OrdinalIgnoreCase))
                    {
                        provider = trimmed["ashlar.model.provider=".Length..].Trim();
                        continue;
                    }
                    if (trimmed.StartsWith("ashlar.model.name=", StringComparison.OrdinalIgnoreCase))
                    {
                        model = trimmed["ashlar.model.name=".Length..].Trim();
                        continue;
                    }
                    systemParts.Add(line);
                }
            }
            else
            {
                userParts.Add($"[{role}] {content}");
            }
        }

        return (provider, model, string.Join('\n', systemParts).Trim(), string.Join('\n', userParts).Trim());
    }
}

