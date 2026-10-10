using Ashlar.Core.Application.Execution.Ports;
using Ashlar.Abstractions.Security.Egress;
using Microsoft.Extensions.Logging;

namespace Ashlar.Infrastructure.Execution.LoadPolicy;

/// <summary>
/// Load policy that reads ASHLAR_LOAD_PREFERENCE and checks provider availability.
/// </summary>
public sealed class PreferenceLoadPolicy : ILoadPolicy
{
    private readonly ILogger<PreferenceLoadPolicy>? _logger;

    /// <summary>Initializes a new preference load policy.</summary>
    public PreferenceLoadPolicy(ILogger<PreferenceLoadPolicy>? logger = null)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public LoadPreference GetPreference()
    {
        var env = Environment.GetEnvironmentVariable("ASHLAR_LOAD_PREFERENCE")?.Trim().ToLowerInvariant();
        return env switch
        {
            "edge" => LoadPreference.Edge,
            "server" => LoadPreference.Server,
            "auto" => LoadPreference.Auto,
            _ => LoadPreference.Auto
        };
    }

    /// <inheritdoc />
    public string? ResolveProvider(IProviderFactory providerFactory)
    {
        var preference = GetPreference();

        var edgeProviders = new[] { "ollama", "local" };
        var serverProviders = new[] { "openai", "azure" };

        if (preference == LoadPreference.Edge)
        {
            foreach (var p in edgeProviders)
            {
                if (IsAvailable(providerFactory, p))
                    return p;
            }
            foreach (var p in serverProviders)
            {
                if (IsAvailable(providerFactory, p))
                    return p;
            }
        }
        else if (preference == LoadPreference.Server)
        {
            foreach (var p in serverProviders)
            {
                if (IsAvailable(providerFactory, p))
                    return p;
            }
            foreach (var p in edgeProviders)
            {
                if (IsAvailable(providerFactory, p))
                    return p;
            }
        }
        else
        {
            foreach (var p in edgeProviders)
            {
                if (IsAvailable(providerFactory, p))
                    return p;
            }
            foreach (var p in serverProviders)
            {
                if (IsAvailable(providerFactory, p))
                    return p;
            }
        }

        _logger?.LogWarning("No LLM provider available (edge or server)");
        return null;
    }
    private static bool IsAvailable(IProviderFactory factory, string provider)
    {
        try { return factory.IsProviderAvailable(provider); }
        catch (Exception ex) when (EgressRefusal.Find(ex) is { } refusal)
        {
            ProviderRefusalScope.Record(refusal);
            return false;
        }
    }

}
