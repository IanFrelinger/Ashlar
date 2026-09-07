using Microsoft.Extensions.Logging;
using Ashlar.Abstractions;
using Ashlar.Infrastructure.Execution;

using Ashlar.Core.Application.Execution.Ports;

namespace Ashlar.Infrastructure.Execution.Models;

/// <summary>
/// A model wrapper that can hot-swap between an agentic provider-backed model and a deterministic fallback.
/// Selection rules (in priority order):
/// - If system messages contain "ashlar.model.prefer=deterministic" -> deterministic.
/// - Else if system messages contain "ashlar.model.provider=&lt;name&gt;" -> try provider-backed with that provider.
/// - Else if env var ASHLAR_MODEL_PROVIDER is set -> try provider-backed with that provider.
/// - Else -> deterministic.
///
/// If provider-backed execution fails, it falls back to deterministic.
///
/// <para>One name is NOT eligible for that fallback: a provider this build does not know. Falling
/// back on an unreachable provider is a degradation; falling back on a misspelled one hides a
/// mistake and answers with an echo, so an unknown name is refused outright — see CompleteAsync.</para>
/// </summary>
public sealed class HotSwappableModel : IModel
{
    private readonly IModel _agentic;
    private readonly IModel _deterministic;
    private readonly ILogger<HotSwappableModel> _logger;

    /// <summary>
    /// Initializes a new hot swappable model.
    /// <paramref name="agentic"/> is typically <see cref="ProviderBackedModel"/> (legacy)
    /// or an MEAI-backed <see cref="IModel"/> when the MEAI pipeline is enabled.
    /// </summary>
    public HotSwappableModel(
        IModel agentic,
        ILogger<HotSwappableModel> logger)
    {
        _agentic = agentic ?? throw new ArgumentNullException(nameof(agentic));
        _deterministic = new EchoDeterministicModel();
        _logger = logger;
    }

    /// <summary>Complete asynchronously.</summary>
    public async Task<ModelOutput> CompleteAsync(ModelInput input, CancellationToken ct)
    {
        var (prefer, providerOverride) = ParseRuntime(input);

        // Attach provider override via system directive for ProviderBackedModel.
        var provider = providerOverride;
        if (string.IsNullOrWhiteSpace(provider))
        {
            provider = Environment.GetEnvironmentVariable("ASHLAR_MODEL_PROVIDER");
        }

        // A provider NAME that this build cannot route to is not a degraded provider — it is a
        // typo or a stale config, and no fallback rescues it. Letting it through meant the routed
        // call failed at request time, the catch below treated that as "provider unreachable", and
        // (under ASHLAR_ALLOW_MOCK=1) the echo model answered — which is how `self-extend run
        // --provider does-not-exist` reported "ok / passed QA gates" over work no model ever saw.
        //
        // Checked BEFORE the deterministic branch, because that branch routes to the provider too.
        // Unreachable may degrade; unknown may not.
        if (!string.IsNullOrWhiteSpace(provider) && !ProviderFactory.IsKnownProvider(provider))
        {
            throw new ModelUnavailableException(
                $"'{provider}' is not a model provider this build knows. Known providers: "
                + ProviderFactory.KnownProviderList() + ". "
                + "This is refused rather than degraded to the echo model on purpose: an unreachable "
                + "provider is a condition to fall back from, but a name that is not a provider is a "
                + "mistake, and falling back would report success over a model that was never called. "
                + "Fix the name (--provider, ASHLAR_MODEL_PROVIDER, or the agent's model.provider in "
                + "ashlar.yaml), or use `mock` to run offline on canned responses.");
        }

        // Deterministic preference:
        // - if a provider is specified (e.g., "offline"/"mock-json"), treat it as a deterministic-safe path
        //   and try provider-backed first (still no network for offline providers), with fallback to echo.
        // - otherwise, use deterministic echo.
        if (string.Equals(prefer, "deterministic", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(provider))
            {
                var routed = InjectProviderDirective(input, provider);
                try
                {
                    return await _agentic.CompleteAsync(routed, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Deterministic(provider) model failed; falling back to deterministic echo");
                    return await _deterministic.CompleteAsync(input, ct);
                }
            }

            return await _deterministic.CompleteAsync(input, ct);
        }

        if (!string.IsNullOrWhiteSpace(provider))
        {
            var routed = InjectProviderDirective(input, provider);
            try
            {
                return await _agentic.CompleteAsync(routed, ct);
            }
            catch (Exception ex)
            {
                if (!AllowMock())
                {
                    throw new ModelUnavailableException(
                        $"Model provider '{provider}' failed and the deterministic echo fallback is disabled "
                        + "(ASHLAR_ALLOW_MOCK != 1). Point the node at a reachable provider, or set "
                        + "ASHLAR_ALLOW_MOCK=1 to permit the echo model. Refusing to report success over an "
                        + "unreachable model.", ex);
                }
                _logger.LogWarning(ex, "Agentic model failed; falling back to deterministic (ASHLAR_ALLOW_MOCK=1)");
                return await _deterministic.CompleteAsync(input, ct);
            }
        }

        // No provider and no explicit deterministic preference: this is an UNCONFIGURED model, not a
        // choice. Echoing here is what let an unwired node report "passed QA gates" over zero work.
        if (!AllowMock())
        {
            throw new ModelUnavailableException(
                "No model provider is configured (ASHLAR_MODEL_PROVIDER unset and no ashlar.model.provider "
                + "directive) and the deterministic echo fallback is disabled (ASHLAR_ALLOW_MOCK != 1). "
                + "Configure a provider, or set ASHLAR_ALLOW_MOCK=1 to run the echo model.");
        }
        return await _deterministic.CompleteAsync(input, ct);
    }

    private static bool AllowMock() =>
        string.Equals(Environment.GetEnvironmentVariable("ASHLAR_ALLOW_MOCK"), "1", StringComparison.Ordinal);

    private static (string? prefer, string? provider) ParseRuntime(ModelInput input)
    {
        string? prefer = null;
        string? provider = null;

        foreach (var (role, content) in input.Messages)
        {
            if (!string.Equals(role, "system", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var line in (content ?? "").Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("ashlar.model.prefer=", StringComparison.OrdinalIgnoreCase))
                {
                    prefer = trimmed["ashlar.model.prefer=".Length..].Trim();
                }
                else if (trimmed.StartsWith("ashlar.model.provider=", StringComparison.OrdinalIgnoreCase))
                {
                    provider = trimmed["ashlar.model.provider=".Length..].Trim();
                }
            }
        }

        return (prefer, provider);
    }

    private static ModelInput InjectProviderDirective(ModelInput input, string provider)
    {
        var msgs = input.Messages.ToList();
        var directive = $"ashlar.model.provider={provider}";

        for (var i = 0; i < msgs.Count; i++)
        {
            var (role, content) = msgs[i];
            if (!string.Equals(role, "system", StringComparison.OrdinalIgnoreCase)) continue;
            msgs[i] = (role, directive + "\n" + (content ?? ""));
            return new ModelInput(msgs);
        }

        // No system message; prepend one.
        msgs.Insert(0, ("system", directive));
        return new ModelInput(msgs);
    }

    private sealed class EchoDeterministicModel : IModel
    {
        /// <summary>Complete asynchronously.</summary>
        public Task<ModelOutput> CompleteAsync(ModelInput input, CancellationToken ct)
        {
            var text = input.Messages.LastOrDefault().content ?? "";
            return Task.FromResult(new ModelOutput(text));
        }
    }
}

