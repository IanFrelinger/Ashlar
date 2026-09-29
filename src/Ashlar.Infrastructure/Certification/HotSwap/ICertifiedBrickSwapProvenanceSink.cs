using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Ashlar.Core.Application.Autonomy;

namespace Ashlar.Infrastructure.Certification.HotSwap;

/// <summary>
/// Receives every hot-swap provenance event. The trust-loop plan projects these into the
/// provenance graph; until that projection lands, the default sink writes structured logs.
/// Implementations must not throw — a provenance sink failure must never fail a swap.
/// </summary>
[Experimental(AutonomyExperimental.DiagnosticId, UrlFormat = AutonomyExperimental.UrlFormat)]
public interface ICertifiedBrickSwapProvenanceSink
{
    /// <summary>Records one provenance event.</summary>
    void Record(BrickSwapProvenanceEvent provenanceEvent);
}

/// <summary>Default sink: structured log lines, one per event.</summary>
[Experimental(AutonomyExperimental.DiagnosticId, UrlFormat = AutonomyExperimental.UrlFormat)]
internal sealed class LoggingBrickSwapProvenanceSink : ICertifiedBrickSwapProvenanceSink
{
    private readonly ILogger<LoggingBrickSwapProvenanceSink> _logger;

    /// <summary>Initializes the logging sink.</summary>
    public LoggingBrickSwapProvenanceSink(ILogger<LoggingBrickSwapProvenanceSink> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public void Record(BrickSwapProvenanceEvent provenanceEvent)
    {
        _logger.LogInformation(
            "hot-swap {Outcome} gen={Generation} brick={BrickId} contentHash={ContentHash} context={ContextName} code={FailureCode} {Reason}",
            provenanceEvent.Outcome,
            provenanceEvent.Generation,
            provenanceEvent.BrickId,
            provenanceEvent.ContentHash,
            provenanceEvent.ContextName,
            provenanceEvent.FailureCode,
            provenanceEvent.Reason);
    }
}
