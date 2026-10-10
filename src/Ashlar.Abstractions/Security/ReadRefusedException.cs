namespace Ashlar.Abstractions.Security;

/// <summary>A redacted read refusal suitable for an agent or remote caller. Contains no label, data or inner fault.</summary>
#pragma warning disable CA1032 // Arbitrary messages or inner exceptions would expose classified response data.
public sealed class ReadRefusedException : Exception
#pragma warning restore CA1032
{
    /// <summary>Creates a refusal with a reason category and an independent, random reference.</summary>
    public ReadRefusedException(AccessDenialReason reason)
        : this(reason, Guid.NewGuid().ToString("N")) { }

    private ReadRefusedException(AccessDenialReason reason, string reference)
        : base("Response read refused; reference=" + reference)
    {
        Reason = reason;
        Ref = reference;
    }

    /// <summary>The refusal category; never the data label or operator-only decision detail.</summary>
    public AccessDenialReason Reason { get; }

    /// <summary>Random correlation reference safe to return to the refused caller.</summary>
    public string Ref { get; }

    /// <summary>Stable machine-readable category.</summary>
    public string ErrorCode { get; } = "read_refused";
}
