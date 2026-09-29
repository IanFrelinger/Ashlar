using Ashlar.Core.Application.Adaptation.Models;
using Ashlar.Core.Application.Adaptation.Ports;

namespace Ashlar.Infrastructure.Adaptation;

/// <summary>
/// Promotes validated fixes. Records to adaptation log with Promoted=true.
/// </summary>
internal sealed class AdaptationPromoter : IAdaptationPromoter
{
    private readonly IAdaptationLog _log;

    /// <summary>Initializes a new adaptation promoter.</summary>
    public AdaptationPromoter(IAdaptationLog log)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <inheritdoc />
    public Task PromoteAsync(AdaptationRecord record, CancellationToken cancellationToken = default)
    {
        var promoted = record with { Promoted = true };
        return _log.LogAsync(promoted, cancellationToken);
    }
}
