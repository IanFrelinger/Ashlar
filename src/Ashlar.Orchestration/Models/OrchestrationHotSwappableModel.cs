using Microsoft.Extensions.Logging;
using Ashlar.Abstractions;
using Ashlar.Core.Application.Execution.Ports;

namespace Ashlar.Orchestration.Models;

/// <summary>
/// Orchestration-layer model wrapper that standardizes "hot swap" behavior:
/// - honors "ashlar.model.prefer=deterministic" directive in system messages
/// - attempts the primary model first (if present), and falls back to deterministic if it throws
/// - if no primary model is registered, behaves deterministically
/// </summary>
public sealed class OrchestrationHotSwappableModel : IModel
{
    private readonly IModel? _primary;
    private readonly IModel _fallback;
    private readonly ILogger<OrchestrationHotSwappableModel> _logger;

    public OrchestrationHotSwappableModel(
        IModel? primary,
        ILogger<OrchestrationHotSwappableModel> logger)
    {
        _primary = primary;
        _fallback = new EchoDeterministicModel();
        _logger = logger;
    }

    /// <summary>Completes a request using the primary model with deterministic fallback on preference or failure.</summary>
    public async Task<ModelOutput> CompleteAsync(ModelInput input, CancellationToken ct)
    {
        var prefer = ParsePrefer(input);
        var provider = ParseProvider(input);

        // Deterministic preference:
        // - If a provider is explicitly selected (e.g. offline/mock-json), allow primary to run.
        // - Otherwise, use the deterministic echo fallback.
        if (_primary == null)
        {
            if (!AllowMock())
            {
                throw new ModelUnavailableException(
                    "No primary model is registered and the deterministic echo fallback is disabled "
                    + "(ASHLAR_ALLOW_MOCK != 1). This is a model-wiring gap, not a run to report as success.");
            }
            return await _fallback.CompleteAsync(input, ct);
        }
        if (string.Equals(prefer, "deterministic", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(provider))
        {
            return await _fallback.CompleteAsync(input, ct);
        }

        try
        {
            return await _primary.CompleteAsync(input, ct);
        }
        catch (Exception ex)
        {
            if (!AllowMock())
            {
                throw new ModelUnavailableException(
                    "The primary model failed and the deterministic echo fallback is disabled "
                    + "(ASHLAR_ALLOW_MOCK != 1). Point the node at a reachable provider, or set "
                    + "ASHLAR_ALLOW_MOCK=1 to permit the echo model.", ex);
            }
            _logger.LogWarning(ex, "Primary IModel failed; falling back to deterministic model (ASHLAR_ALLOW_MOCK=1)");
            return await _fallback.CompleteAsync(input, ct);
        }
    }

    private static bool AllowMock() =>
        string.Equals(Environment.GetEnvironmentVariable("ASHLAR_ALLOW_MOCK"), "1", StringComparison.Ordinal);

    private static string? ParsePrefer(ModelInput input)
    {
        foreach (var (role, content) in input.Messages)
        {
            if (!string.Equals(role, "system", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var line in (content ?? "").Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("ashlar.model.prefer=", StringComparison.OrdinalIgnoreCase))
                {
                    return trimmed["ashlar.model.prefer=".Length..].Trim();
                }
            }
        }
        return null;
    }

    private static string? ParseProvider(ModelInput input)
    {
        foreach (var (role, content) in input.Messages)
        {
            if (!string.Equals(role, "system", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var line in (content ?? "").Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("ashlar.model.provider=", StringComparison.OrdinalIgnoreCase))
                {
                    return trimmed["ashlar.model.provider=".Length..].Trim();
                }
            }
        }
        return null;
    }

    private sealed class EchoDeterministicModel : IModel
    {
        public Task<ModelOutput> CompleteAsync(ModelInput input, CancellationToken ct)
        {
            var text = input.Messages.LastOrDefault().content ?? "";
            return Task.FromResult(new ModelOutput(text));
        }
    }
}

