using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Ashlar.Abstractions;
using Ashlar.AI.Pipeline.Routing;

namespace Ashlar.AI.Pipeline.Models;

/// <summary>
/// <see cref="IModel"/> adapter over the governed MEAI <see cref="IChatClient"/> (router when registered).
/// Deterministic/offline providers are not handled here — callers (e.g. <c>HotSwappableModel</c>) fall back.
/// </summary>
public sealed class MeaiBackedModel : IModel
{
    /// <summary>
    /// Provider names that never reach an MEAI chat client. Spelled as literals because this
    /// project deliberately depends on nothing but <c>Ashlar.Abstractions</c>;
    /// <c>"deterministic"</c> is the same name as
    /// <c>Ashlar.Core.Domain.AshlarDefaults.DeterministicProviderName</c>, which is the single
    /// source for the provider ALLOW-LIST and the framework default. Change one, change this.
    /// </summary>
    private static readonly HashSet<string> DeterministicProviders = new(StringComparer.OrdinalIgnoreCase)
    {
        "offline", "mock", "echo", "mock-json", "deterministic",
    };

    private readonly IChatClient _chatClient;
    private readonly ILogger<MeaiBackedModel> _logger;

    /// <summary>Creates an MEAI-backed <see cref="IModel"/>.</summary>
    public MeaiBackedModel(IChatClient chatClient, ILogger<MeaiBackedModel> logger)
    {
        _chatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<ModelOutput> CompleteAsync(ModelInput input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);

        var (provider, model, messages, capability) = Parse(input);
        if (!string.IsNullOrWhiteSpace(provider) && DeterministicProviders.Contains(provider))
        {
            throw new InvalidOperationException(
                $"Provider '{provider}' is a deterministic/offline path; MEAI chat client is not used.");
        }

        var options = new ChatOptions
        {
            ModelId = string.IsNullOrWhiteSpace(model) ? null : model,
        };

        if (!string.IsNullOrWhiteSpace(capability))
        {
            options.AdditionalProperties = new AdditionalPropertiesDictionary
            {
                [RouteRequestHints.CapabilityTierKey] = capability,
            };
        }

        _logger.LogDebug(
            "MEAI IModel complete: provider={Provider} model={Model} capability={Capability} messages={Count}",
            provider ?? "(default)",
            model ?? "(default)",
            capability ?? "(default)",
            messages.Count);

        var response = await _chatClient.GetResponseAsync(messages, options, ct).ConfigureAwait(false);
        return new ModelOutput(response.Text ?? string.Empty);
    }

    private static (string? provider, string? model, IList<ChatMessage> messages, string? capability) Parse(
        ModelInput input)
    {
        string? provider = null;
        string? model = null;
        string? capability = null;
        var messages = new List<ChatMessage>();

        foreach (var (role, content) in input.Messages)
        {
            if (string.Equals(role, "system", StringComparison.OrdinalIgnoreCase))
            {
                var systemParts = new List<string>();
                foreach (var line in (content ?? string.Empty).Split('\n'))
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

                    if (trimmed.StartsWith("ashlar.model.capability=", StringComparison.OrdinalIgnoreCase))
                    {
                        capability = trimmed["ashlar.model.capability=".Length..].Trim();
                        continue;
                    }

                    if (trimmed.StartsWith("ashlar.model.prefer=", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    systemParts.Add(line);
                }

                var systemText = string.Join('\n', systemParts).Trim();
                if (!string.IsNullOrEmpty(systemText))
                {
                    messages.Add(new ChatMessage(ChatRole.System, systemText));
                }
            }
            else if (string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase))
            {
                messages.Add(new ChatMessage(ChatRole.Assistant, content ?? string.Empty));
            }
            else
            {
                messages.Add(new ChatMessage(ChatRole.User, content ?? string.Empty));
            }
        }

        if (string.IsNullOrWhiteSpace(capability) && !string.IsNullOrWhiteSpace(provider))
        {
            capability = MapProviderToCapability(provider);
        }

        if (messages.Count == 0)
        {
            messages.Add(new ChatMessage(ChatRole.User, string.Empty));
        }

        return (provider, model, messages, capability);
    }

    private static string? MapProviderToCapability(string provider)
    {
        if (provider.Contains("bedrock", StringComparison.OrdinalIgnoreCase)
            || provider.Contains("cloud", StringComparison.OrdinalIgnoreCase))
        {
            if (provider.Contains("fast", StringComparison.OrdinalIgnoreCase))
            {
                return "fast";
            }

            if (provider.Contains("heavy", StringComparison.OrdinalIgnoreCase))
            {
                return "heavy";
            }

            return "balanced";
        }

        return null;
    }
}
