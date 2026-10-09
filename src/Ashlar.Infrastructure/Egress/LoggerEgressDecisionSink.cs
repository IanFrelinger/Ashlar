using Ashlar.Abstractions.Security;
using Ashlar.Abstractions.Security.Egress;
using Microsoft.Extensions.Logging;

namespace Ashlar.Infrastructure.Egress;

/// <summary>
/// Writes each egress decision record to an <see cref="ILogger"/>: category <c>Ashlar.Egress</c>, event
/// <c>7300 EgressDecision</c> at Debug, or <c>7301 EgressRefused</c> at Warning under enforcement.
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
public sealed class LoggerEgressDecisionSink : IEgressDecisionSink, IDisposable
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
        + "profile={Profile} enforcesByDefault={ProfileEnforcesByDefault} fault={Fault} seq={Sequence} "
        + "mode={Mode} modeBasis={ModeBasis} ref={Ref}";

    /// <summary>The outcome when the star property would allow the egress.</summary>
    internal const string WouldAllow = "would-allow";

    /// <summary>The outcome when it would refuse it, or no decision was made.</summary>
    internal const string WouldRefuse = "would-refuse";

    /// <summary>The fault text when the evaluation did not fault.</summary>
    internal const string NoFault = "none";

    private static readonly EventId DecisionEventId = new(DecisionEventIdValue, DecisionEventName);
    private static readonly EventId RefusedEventId = new(7301, "EgressRefused");
    private static readonly EventId SummaryEventId = new(7302, "EgressRefusalsSuppressed");
    private static readonly TimeSpan WindowLength = TimeSpan.FromMinutes(5);

    private readonly ILogger _logger;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private readonly Dictionary<(string Site, AccessDenialReason Reason), RefusalWindow> _windows = new();
    private bool _disposed;

    /// <summary>Creates a sink that writes to the <c>Ashlar.Egress</c> logger of <paramref name="loggerFactory"/>.</summary>
    /// <param name="loggerFactory">The logger factory.</param>
    /// <exception cref="ArgumentNullException"><paramref name="loggerFactory"/> is <see langword="null"/>.</exception>
    public LoggerEgressDecisionSink(ILoggerFactory loggerFactory) : this(loggerFactory, TimeProvider.System)
    {
    }

    internal LoggerEgressDecisionSink(ILoggerFactory loggerFactory, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _logger = loggerFactory.CreateLogger(CategoryName);
        _clock = clock;
    }

    /// <inheritdoc />
    public void Record(EgressDecision decision)
    {
        if (decision is null)
            return;

        var level = decision.Refused ? LogLevel.Warning : LogLevel.Debug;
        if (!_logger.IsEnabled(level))
            return;

        lock (_gate)
        {
            if (_disposed)
                return;
            if (decision.Refused)
            {
                var key = (decision.Site, decision.Access.Reason);
                if (_windows.TryGetValue(key, out var existing))
                {
                    existing.Suppressed++;
                    return;
                }

                var window = new RefusalWindow(key.Site, key.Reason, _clock.GetUtcNow());
                _windows.Add(key, window);
                window.Timer = _clock.CreateTimer(_ => Expire(key, window), null, WindowLength, Timeout.InfiniteTimeSpan);
            }
        }

        var access = decision.Access;
        _logger.Log(
            level,
            decision.Refused ? RefusedEventId : DecisionEventId,
            MessageTemplate,
            decision.Refused ? "refused" : access.Allowed ? WouldAllow : WouldRefuse,
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
            decision.Sequence,
            decision.Mode,
            decision.ModeBasis,
            decision.Ref);
    }

    private void Expire((string Site, AccessDenialReason Reason) key, RefusalWindow window)
    {
        lock (_gate)
        {
            if (!_windows.TryGetValue(key, out var current) || !ReferenceEquals(current, window))
                return;
            _windows.Remove(key);
        }
        window.Timer?.Dispose();
        WriteSummary(window);
    }

    private void WriteSummary(RefusalWindow window)
    {
        if (window.Suppressed == 0)
            return;
        try
        {
            _logger.LogWarning(SummaryEventId,
                "{SuppressedCount} egress refusals at {Site}/{Reason} suppressed since {Since}",
                window.Suppressed, window.Site, window.Reason.ToString(), window.Started);
        }
        catch (Exception)
        {
            // Timer callbacks do not run under Publish's exception fence; logging must not terminate the host.
            EgressDecisionLog.RecordSinkFault();
        }
    }

    /// <summary>Releases timers and reports any remaining suppressed counts. Safe to call repeatedly.</summary>
    public void Dispose()
    {
        RefusalWindow[] remaining;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            remaining = _windows.Values.ToArray();
            _windows.Clear();
        }
        foreach (var window in remaining)
        {
            window.Timer?.Dispose();
            WriteSummary(window);
        }
    }

    private sealed class RefusalWindow(string site, AccessDenialReason reason, DateTimeOffset started)
    {
        internal string Site { get; } = site;
        internal AccessDenialReason Reason { get; } = reason;
        internal DateTimeOffset Started { get; } = started;
        internal long Suppressed { get; set; }
        internal ITimer? Timer { get; set; }
    }
}
