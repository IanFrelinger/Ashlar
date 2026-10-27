using System.Diagnostics.CodeAnalysis;

namespace Ashlar.Abstractions.Security;

/// <summary>
/// What a subject has read in one session, as privacy labels: the union of the floor and every
/// <see cref="PrivacyLabelSet"/> observed (SPEC-010 §3.4). <see cref="Egress.PrivacyRule"/> decides a send against it.
/// </summary>
/// <remarks>
/// <para>The set only grows. <see cref="Observe"/> adds labels and nothing here removes one, so there is no
/// downgrade.</para>
/// <para><see cref="Observe"/> may be called from several threads: it unites atomically, so no observation is lost
/// and the set never lacks a label that was read. A decision is still a snapshot: a send is decided against
/// <see cref="Current"/> as it stands when read, so a session must observe every read a send depends on before it
/// asks whether it may send.</para>
/// </remarks>
[Experimental(SecurityExperimental.DiagnosticId, UrlFormat = SecurityExperimental.UrlFormat)]
public sealed class ReadSet
{
    private PrivacyLabelSet _current;

    /// <summary>Starts an empty read set: <see cref="PrivacyLabelSet.None"/>.</summary>
    public ReadSet()
        : this(PrivacyLabelSet.None)
    {
    }

    /// <summary>Starts a read set at <paramref name="floor"/>.</summary>
    /// <param name="floor">The labels the session starts with, for example <see cref="PrivacyLabelSet.LocalOnly"/>
    /// for inputs nobody has labelled.</param>
    public ReadSet(PrivacyLabelSet floor)
    {
        SecurityGuard.ThrowIfNull(floor, nameof(floor));
        _current = floor;
    }

    /// <summary>The union of the floor and every set observed so far.</summary>
    public PrivacyLabelSet Current => Volatile.Read(ref _current);

    /// <summary>Records that the subject has read data carrying <paramref name="labels"/>.</summary>
    /// <param name="labels">The privacy labels of the data read.</param>
    public void Observe(PrivacyLabelSet labels)
    {
        SecurityGuard.ThrowIfNull(labels, nameof(labels));

        // Sets are immutable, so a compare-and-swap loop makes the union atomic: a concurrent Observe can delay this
        // one but never overwrite it.
        var seen = Volatile.Read(ref _current);
        while (true)
        {
            var union = seen.Union(labels);
            if (union.Equals(seen))
                return;

            var witnessed = Interlocked.CompareExchange(ref _current, union, seen);
            if (ReferenceEquals(witnessed, seen))
                return;

            seen = witnessed;
        }
    }
}
