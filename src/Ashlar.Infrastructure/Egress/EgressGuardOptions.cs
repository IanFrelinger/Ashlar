using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ashlar.Infrastructure.Egress;

/// <summary>Host policy for factory clients that the host application owns.</summary>
public sealed class EgressGuardOptions
{
    /// <summary>
    /// Named host clients whose egress decisions should report instead of enforce. Configure with
    /// <c>services.Configure&lt;EgressGuardOptions&gt;</c>, including after <c>AddAshlar</c>.
    /// AirGapped ignores this opt-out. Ashlar-owned names are invalid and fail host startup.
    /// </summary>
    public ISet<string> ReportOnlyClients { get; } = new HashSet<string>(StringComparer.Ordinal);
}

/// <summary>
/// Factory names reserved by Ashlar registrations across the core, application, and commercial projects.
/// Typed-client names are pinned by tests that resolve their actual registrations.
/// </summary>
internal static class AshlarFactoryClientNames
{
    internal static readonly IReadOnlyList<string> All = Array.AsReadOnly(new[]
    {
        string.Empty,
        "AshlarExecution",
        "IRunPodClient",
        "IAshlarClient",
        "ashlar-sns-signing",
        "mesh-lab-worker-executor",
        "OllamaModelServingBackend",
        "Ashlar.ModelArtifactCatalog.OllamaTags",
        "Ashlar.ModelArtifactCatalog.DockerOllamaProbe",
        "Ashlar.ModelArtifactCatalog.OllamaRemoteLibrary",
    });
}

/// <summary>Rejects protected names even when the deployment profile would ignore the opt-out.</summary>
internal sealed class ValidateEgressGuardOptions : IValidateOptions<EgressGuardOptions>
{
    public ValidateOptionsResult Validate(string? name, EgressGuardOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        foreach (var client in options.ReportOnlyClients)
        {
            if (client is null || AshlarFactoryClientNames.All.Contains(client, StringComparer.Ordinal))
                return ValidateOptionsResult.Fail("Egress report-only opt-outs cannot name an Ashlar-owned client or a null client.");
        }
        return ValidateOptionsResult.Success;
    }
}

/// <summary>Announces each configured exception when the host starts, including exceptions ignored on AirGapped.</summary>
internal sealed class EgressHostClientPolicyActivator : IHostedService
{
    private readonly IOptions<EgressGuardOptions> _options;
    private readonly ILogger _logger;

    public EgressHostClientPolicyActivator(IOptions<EgressGuardOptions> options, ILoggerFactory loggerFactory)
    {
        _options = options;
        _logger = loggerFactory.CreateLogger("Ashlar.Egress");
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var client in _options.Value.ReportOnlyClients.OrderBy(name => name, StringComparer.Ordinal))
            _logger.LogWarning(new EventId(7307, "EgressHostClientOptOut"),
                "Factory client {Client} is configured for report-only egress; AirGapped ignores this exception.", client);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
