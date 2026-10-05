#if NETSTANDARD2_0
using System.Reflection;

namespace Ashlar.Abstractions.Security.Egress;

/// <summary>
/// On the netstandard2.0 asset only: the hop <see cref="EgressHttp"/> puts between the guard handler and the inner
/// handler, so that a synchronous <c>Send</c>, which this asset cannot evaluate, is refused before anything is sent.
/// </summary>
/// <remarks>
/// <para><b>Why it exists.</b> netstandard2.0 has no <c>HttpMessageHandler.Send</c>, so on this asset the guard
/// handler cannot override it. On a runtime that has one (.NET 5 and later; a .NET 5-7 app binds this asset), a
/// synchronous <c>HttpClient.Send</c> or <c>HttpMessageInvoker.Send</c> reaches the runtime's
/// <c>DelegatingHandler.Send</c>, which forwards to the inner handler's <c>Send</c> without calling <c>SendAsync</c>,
/// so it would go out unevaluated. This hop derives from <see cref="HttpMessageHandler"/>, not
/// <see cref="DelegatingHandler"/>, and overrides only <c>SendAsync</c>. A synchronous <c>Send</c> therefore reaches
/// the runtime's base <c>HttpMessageHandler.Send</c>, which throws <see cref="NotSupportedException"/> naming this
/// type before anything is sent. The type's name is the explanation the caller sees.</para>
/// <para><b>The record.</b> No code of this assembly runs on that synchronous path: nothing here can override the
/// member that is called. So the refusal is recorded once, when the hop is built on such a runtime: a
/// <c>NoDecision</c> record with <see cref="EgressDecision.Fault"/> <c>SynchronousSendUnsupported</c>, naming the
/// family and site whose synchronous sends are refused, so it reaches the operator log and not only the caller. On
/// .NET Framework, Mono and Unity, which have no synchronous <c>Send</c>, nothing is refused and nothing is recorded:
/// the hop only forwards.</para>
/// <para><b>Asynchronous sends are unchanged.</b> <c>SendAsync</c> is forwarded through an owned
/// <see cref="HttpMessageInvoker"/> with the same request, token and response instances; the guard handler above has
/// already evaluated it exactly once. Disposing the hop disposes the inner handler. Under an outer
/// <see cref="HttpMessageInvoker"/> (not an <see cref="HttpClient"/>) on .NET 5-7, the runtime's request telemetry
/// may count such a send twice.</para>
/// </remarks>
internal sealed class SynchronousSendRefusedOnNetstandard20Asset : HttpMessageHandler
{
    /// <summary>The <see cref="EgressDecision.Fault"/> of the record the hop publishes when it is built.</summary>
    internal const string Fault = "SynchronousSendUnsupported";

    /// <summary>Why <see cref="EgressHttp.CreateDelegatingHandler"/> is refused where <see cref="RuntimeHasSynchronousSend"/>.</summary>
    internal const string FactoryHandlerRefusal =
        "The netstandard2.0 build of Ashlar.Abstractions cannot evaluate a synchronous Send on this runtime, and an "
        + "IHttpClientFactory pipeline sets the guard handler's inner handler itself, so a synchronous Send through it "
        + "would go out unevaluated. Reference Ashlar.Abstractions from a net8.0 or later target, whose asset evaluates "
        + "synchronous sends.";

    private readonly HttpMessageInvoker _inner;

    private SynchronousSendRefusedOnNetstandard20Asset(HttpMessageHandler inner, string family, string site, IEgressGuard? guard)
    {
        _inner = new HttpMessageInvoker(inner, disposeHandler: true);
        if (RuntimeHasSynchronousSend)
            (guard as EgressGuard ?? EgressGuard.ProcessDefault).PublishNoDecision(family, site, Fault);
    }

    /// <summary>
    /// <see langword="true"/> when the runtime's <see cref="HttpMessageHandler"/> has a synchronous <c>Send</c>
    /// (.NET 5 and later), which this asset cannot override. Read once.
    /// </summary>
    internal static bool RuntimeHasSynchronousSend { get; } = typeof(HttpMessageHandler).GetMethod(
        "Send",
        BindingFlags.Instance | BindingFlags.NonPublic,
        binder: null,
        new[] { typeof(HttpRequestMessage), typeof(CancellationToken) },
        modifiers: null) is not null;

    /// <summary>
    /// The hop over <paramref name="inner"/>, which it then owns, for the guard handler to sit on. On a runtime with a
    /// synchronous <c>Send</c> it publishes the refusal record for <paramref name="family"/> and <paramref name="site"/>,
    /// with the profile of <paramref name="guard"/> when it is an <see cref="EgressGuard"/>, else of
    /// <see cref="EgressGuard.ProcessDefault"/>.
    /// </summary>
    internal static HttpMessageHandler Over(HttpMessageHandler inner, string family, string site, IEgressGuard? guard) =>
        new SynchronousSendRefusedOnNetstandard20Asset(inner, family, site, guard);

    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        _inner.SendAsync(request, cancellationToken);

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _inner.Dispose();

        base.Dispose(disposing);
    }
}
#endif
