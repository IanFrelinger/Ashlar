using System.Diagnostics.CodeAnalysis;
using Ashlar.Abstractions.Security.Egress;

namespace Ashlar.Abstractions.Security;

/// <summary>
/// Tracks the data read while producing one response and checks whether its authenticated recipient may read it.
/// The host supplies the recipient's clearance and an operator-controlled response classification floor.
/// </summary>
/// <remarks>
/// <para>Missing data classification must be supplied as <see cref="SecurityLabel.SystemHigh"/>; missing
/// recipient clearance as <see cref="SecurityLabel.Public"/>. A response cannot lower its own classification.
/// Observations through <see cref="EgressSubject.Observe"/> raise the response mark, and enclosing marks and
/// unfinished read scopes also constrain the decision.</para>
/// <para>Enter and dispose on the flow producing the response, in a <c>using</c> block. Check before serialization
/// and again after it, before releasing any representation. Serialization can execute getters that read more data.
/// Producers must finish and report all reads before releasing a response; this is not a sandbox for untrusted code.</para>
/// </remarks>
[Experimental(SecurityExperimental.DiagnosticId, UrlFormat = SecurityExperimental.UrlFormat)]
public sealed class ResponseReadScope : IDisposable
{
    private readonly SecurityLabel _clearance;
    private readonly HighWaterMark _mark;
    private readonly IDisposable _subject;
    private bool _disposed;

    /// <summary>Starts a response scope using trusted host policy, never caller-supplied tool arguments.</summary>
    /// <param name="subjectId">Host-assigned identity for the response operation.</param>
    /// <param name="clearance">Clearance of the recipient, resolved by trusted host authentication and policy.</param>
    /// <param name="responseFloor">Operator classification covering all data this response may contain.</param>
    public ResponseReadScope(string subjectId, SecurityLabel clearance, SecurityLabel responseFloor)
    {
        SecurityGuard.ThrowIfNull(clearance, nameof(clearance));
        _clearance = clearance;
        _subject = EgressSubject.Enter(subjectId, _mark = new HighWaterMark(responseFloor));
    }

    /// <summary>Checks the complete response label against the recipient's clearance at this point in the flow.</summary>
    public AccessDecision CanRead()
    {
#if NET8_0_OR_GREATER
        ObjectDisposedException.ThrowIf(_disposed, this);
#else
        if (_disposed)
            throw new ObjectDisposedException(nameof(ResponseReadScope));
#endif

        // Resolve includes enclosing marks and unfinished reads. The private mark also prevents a call from
        // another flow from losing observations made by this response's producer.
        return ReferenceMonitor.CanRead(_clearance, _mark.Current.Join(EgressSubject.Resolve().Current));
    }

    /// <summary>Ends the response scope and propagates its observations to any enclosing subject.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _subject.Dispose();
    }
}
