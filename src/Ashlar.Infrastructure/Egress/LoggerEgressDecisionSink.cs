using Ashlar.Abstractions.Security.Egress;
using Microsoft.Extensions.Logging;

namespace Ashlar.Infrastructure.Egress;

/// <summary>
/// Writes each egress decision record to an <see cref="ILogger"/>: category <c>Ashlar.Egress</c>, event
/// <c>7300 EgressDecision</c>, level Debug.
/// </summary>
/// <remarks>
/// <para><b>Why Debug.</b> Many CLI verbs build their own service collections with a console logger at
/// Information, writing to stdout, so a decision at Information would change their output. Operators turn the
/// records on with <c>Logging:LogLevel:Ashlar.Egress=Debug</c>, or attach to the <c>Ashlar-Egress</c> event source.</para>
/// <para><b>What an entry holds.</b> Only the record's own fields: outcome, site, family, destination
/// (<c>scheme://host[:port]</c> or a name), class, labels and their bases, the reason and detail, the profile, the
/// fault's type name and the sequence number. Never the payload, a header, or a URL's userinfo, path or query; the
/// record does not carry them.</para>
/// <para>The detail is for operators. It goes to this log only, never back to the subject.</para>
/// </remarks>
public sealed class LoggerEgressDecisionSink : IEgressDecisionSink
{
    /// <summary>The logger category.</summary>
    internal const string CategoryName = "Ashlar.Egress";

    /// <summary>The event id's number.</summary>
    internal const int DecisionEventIdValue = 7300;

    /// <summary>The event id's name.</summary>
    internal const string DecisionEventName = "EgressDecision";

    /// <summary>The message template. Its placeholders are the entry's structured properties.</summary>
    internal const string MessageTemplate =
        "Egress {Outcome} site={Site} family={Family} dest={Destination} class={DestinationClass} "
        + "destLabel={DestinationLabel} current={Current} ({CurrentBasis}) reason={Reason} detail={Detail} "
        + "profile={Profile} enforcesByDefault={ProfileEnforcesByDefault} fault={Fault} seq={Sequence}";

    /// <summary>The outcome when the star property would allow the egress.</summary>
    internal const string WouldAllow = "would-allow";

    /// <summary>The outcome when it would refuse it, or no decision was made.</summary>
    internal const string WouldRefuse = "would-refuse";

    /// <summary>The fault text when the evaluation did not fault.</summary>
    internal const string NoFault = "none";

    private static readonly EventId DecisionEventId = new(DecisionEventIdValue, DecisionEventName);

    private readonly ILogger _logger;

    /// <summary>Creates a sink that writes to the <c>Ashlar.Egress</c> logger of <paramref name="loggerFactory"/>.</summary>
    /// <param name="loggerFactory">The logger factory.</param>
    /// <exception cref="ArgumentNullException"><paramref name="loggerFactory"/> is <see langword="null"/>.</exception>
    public LoggerEgressDecisionSink(ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _logger = loggerFactory.CreateLogger(CategoryName);
    }

    /// <inheritdoc />
    public void Record(EgressDecision decision)
    {
        if (decision is null || !_logger.IsEnabled(LogLevel.Debug))
            return;

        var access = decision.Access;
        _logger.Log(
            LogLevel.Debug,
            DecisionEventId,
            MessageTemplate,
            access.Allowed ? WouldAllow : WouldRefuse,
            decision.Site,
            decision.Family,
            decision.Destination,
            decision.DestinationClass.ToString(),
            decision.DestinationLabel.ToString(),
            decision.Current.ToString(),
            decision.CurrentBasis,
            access.Reason.ToString(),
            access.Detail,
            decision.Profile,
            decision.ProfileEnforcesByDefault,
            decision.Fault ?? NoFault,
            decision.Sequence);
    }
}
