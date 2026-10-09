namespace Ashlar.Abstractions.Security.Egress;

/// <summary>An egress stopped by policy, rather than a transient transport failure.</summary>
/// <remarks>The message is safe for the local subject. The full decision is for operators only.</remarks>
#pragma warning disable CA1032 // A refusal must carry its decision; arbitrary messages and inner exceptions can expose classified data.
public sealed class EgressRefusedException : Exception
#pragma warning restore CA1032
{
    /// <summary>Creates a refusal carrying the operator's decision and a redacted local message.</summary>
    public EgressRefusedException(EgressDecision decision) : base(MessageFor(decision)) => Decision = decision;

    /// <summary>The full operator record. Do not serialize it to the refused subject or a remote party.</summary>
    public EgressDecision Decision { get; }

    /// <summary>The refusal category.</summary>
    public AccessDenialReason Reason => Decision.Access.Reason;

    /// <summary>The inventoried site.</summary>
    public string Site => Decision.Site;

    /// <summary>The operator-only sequence number.</summary>
    public long Sequence => Decision.Sequence;

    /// <summary>The random reference that a subject or remote party may receive.</summary>
    public string Ref => Decision.Ref;

    /// <summary>The stable error code, distinct from transport failures.</summary>
    public string ErrorCode => "EGRESS_REFUSED";

    private static string MessageFor(EgressDecision decision)
    {
        SecurityGuard.ThrowIfNull(decision, nameof(decision));
        return $"Egress refused by policy: {decision.Access.Reason}; site={decision.Site}; "
            + $"family={decision.Family}; class={decision.DestinationClass}; ref={decision.Ref}"
            + (decision.CurrentBasis == "no-subject" ? "; no-subject" : string.Empty);
    }
}
