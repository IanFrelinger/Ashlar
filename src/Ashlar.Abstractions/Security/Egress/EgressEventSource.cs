using System.Diagnostics.Tracing;
using System.Globalization;

namespace Ashlar.Abstractions.Security.Egress;

/// <summary>
/// The <c>Ashlar-Egress</c> event source: every egress decision record as event 1, <c>Decision</c>.
/// </summary>
/// <remarks>
/// <para>It costs nothing while nobody listens, and it reaches decisions made where no logger exists (CLI
/// one-shots). Turn it on with <c>dotnet-trace collect --providers Ashlar-Egress</c> or an
/// <see cref="EventListener"/>.</para>
/// <para>Every field is a string or a bool. The provider name, the event id and name, and the field names and
/// order are a public contract that operators script against: append fields, never reorder or rename them.
/// SPEC-007 PR 4.6 appended <c>modeBasis</c>, <c>refused</c> and <c>ref</c> after <c>fault</c>.</para>
/// </remarks>
[EventSource(Name = SourceName)]
internal sealed class EgressEventSource : EventSource
{
    internal const string SourceName = "Ashlar-Egress";
    internal const int DecisionEventId = 1;

    internal static readonly EgressEventSource Log = new();

    private EgressEventSource()
    {
    }

    /// <summary>Writes <paramref name="decision"/> when a listener is enabled; otherwise does nothing.</summary>
    [NonEvent]
    internal static void Write(EgressDecision decision)
    {
        if (!Log.IsEnabled())
            return;

        var access = decision.Access;
        Log.Decision(
            decision.Sequence.ToString(CultureInfo.InvariantCulture),
            decision.At.ToString("O", CultureInfo.InvariantCulture),
            decision.Mode,
            decision.Family,
            decision.Site,
            decision.Destination,
            EgressDestinations.ClassName(decision.DestinationClass),
            decision.DestinationLabel.ToString(),
            decision.DestinationBasis,
            decision.Current.ToString(),
            decision.CurrentBasis,
            access.Allowed,
            ReasonName(access.Reason),
            access.Detail,
            decision.Profile,
            decision.ProfileEnforcesByDefault,
            decision.Fault ?? string.Empty,
            decision.ModeBasis,
            decision.Refused,
            decision.Ref);
    }

    [Event(DecisionEventId, Level = EventLevel.Informational)]
    public void Decision(
        string sequence,
        string at,
        string mode,
        string family,
        string site,
        string destination,
        string destinationClass,
        string destinationLabel,
        string destinationBasis,
        string current,
        string currentBasis,
        bool allowed,
        string reason,
        string detail,
        string profile,
        bool profileEnforcesByDefault,
        string fault,
        string modeBasis,
        bool refused,
        string @ref)
    {
        WriteEvent(
            DecisionEventId,
            sequence,
            at,
            mode,
            family,
            site,
            destination,
            destinationClass,
            destinationLabel,
            destinationBasis,
            current,
            currentBasis,
            allowed,
            reason,
            detail,
            profile,
            profileEnforcesByDefault,
            fault,
            modeBasis,
            refused,
            @ref);
    }

    private static string ReasonName(AccessDenialReason reason) => reason switch
    {
        AccessDenialReason.None => "None",
        AccessDenialReason.LevelTooLow => "LevelTooLow",
        AccessDenialReason.MissingCompartment => "MissingCompartment",
        AccessDenialReason.MissingCaveat => "MissingCaveat",
        AccessDenialReason.SystemHighData => "SystemHighData",
        AccessDenialReason.NoDecision => "NoDecision",
        _ => ((int)reason).ToString(CultureInfo.InvariantCulture),
    };
}
