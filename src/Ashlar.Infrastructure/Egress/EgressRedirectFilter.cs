using System.Collections.Concurrent;
using Ashlar.Abstractions.Security.Egress;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;

namespace Ashlar.Infrastructure.Egress;

/// <summary>
/// The factory binding for redirects (SPEC-007 PR 4.3, design §2.6 gap 2, R-c): after every
/// <see cref="IHttpClientFactory"/> client's own configuration has run, it puts the egress redirect follower directly
/// above the client's final primary handler, and puts the guard handler back if that configuration removed it.
/// </summary>
/// <remarks>
/// <para><b>Every factory client</b> (owner decision Q5, 2026-10-05: all, the host's included). It runs after
/// <c>next(builder)</c>, so it sees the primary handler every <c>ConfigurePrimaryHttpMessageHandler</c> chose and the
/// additional handlers every <c>AddHttpMessageHandler</c> and <c>ConfigureAdditionalHttpMessageHandlers</c> left.</para>
/// <para><b>The primary.</b> An <see cref="HttpClientHandler"/> or <see cref="SocketsHttpHandler"/> has its own
/// following turned off, and the follower above it follows as it would have, deciding each new authority before it is
/// sent and following across origins (P1, default D33). Any other primary type is not changed: the follower above it
/// only decides, and the guard handler checks the response's request after the send. On AirGapped and
/// SecureWorkstation each such type is named once per client in a Warning (event 7304).</para>
/// <para><b>The guard handler.</b> A client's own <c>ConfigureAdditionalHttpMessageHandlers((h, _) =&gt; h.Clear())</c>
/// runs after the default that inserted it. If no guard handler is left, a new one is inserted at the front with the
/// client's <c>factory:</c> site, and a Warning (event 7303) names the client.</para>
/// <para><b>Report-only.</b> Nothing here refuses or changes a request; it only makes every hop decided.</para>
/// </remarks>
internal sealed class EgressRedirectFilter : IHttpMessageHandlerBuilderFilter
{
    /// <summary>The event id's number for the guard handler put back.</summary>
    internal const int GuardHandlerRestoredEventIdValue = 7303;

    /// <summary>The event id's number for a primary type whose own redirects cannot be turned off.</summary>
    internal const int UnknownPrimaryEventIdValue = 7304;

    private static readonly Action<ILogger, string, Exception?> LogGuardHandlerRestored = LoggerMessage.Define<string>(
        LogLevel.Warning,
        new EventId(GuardHandlerRestoredEventIdValue, "EgressGuardHandlerRestored"),
        "The egress guard handler was missing from HttpClient '{Client}' after the client's own configuration ran (a "
        + "ConfigureAdditionalHttpMessageHandlers that clears the list, for example). It was put back as the outermost "
        + "additional handler, so the client's sends are still decided.");

    private static readonly Action<ILogger, string, string, Exception?> LogUnknownPrimary = LoggerMessage.Define<string, string>(
        LogLevel.Warning,
        new EventId(UnknownPrimaryEventIdValue, "EgressUnknownPrimaryHandler"),
        "HttpClient '{Client}' sends through primary handler type {PrimaryType}, whose own redirect following Ashlar "
        + "cannot turn off. A redirect it follows is decided only after the send, from the response's request URI.");

    private static readonly ConcurrentDictionary<string, byte> NamedUnknownPrimaries = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next)
    {
        ArgumentNullException.ThrowIfNull(next);
        return builder =>
        {
            next(builder);
            Apply(builder);
        };
    }

    private static void Apply(HttpMessageHandlerBuilder builder)
    {
        var name = builder.Name ?? string.Empty;
        var site = EgressServiceCollectionExtensions.FactorySitePrefix + name;
        var guard = EgressServiceCollectionExtensions.ResolveGuardAndActivateLogging(builder);

        if (!builder.AdditionalHandlers.Any(handler => handler is EgressGuardHandler))
        {
            builder.AdditionalHandlers.Insert(0, EgressHttp.CreateDelegatingHandler(EgressFamilies.HttpFactory, site, guard));
            var logger = LoggerFor(builder);
            if (logger is not null)
                LogGuardHandlerRestored(logger, name, null);
        }

        var primary = builder.PrimaryHandler;
        if (primary is null)
            return;

        builder.PrimaryHandler = EgressRedirectHandler.Install(primary, followsAcrossOrigins: true, EgressFamilies.HttpFactory, site, guard);

        // The handler at the end of the chain is the one that sends (a composite primary's tail included). Of a type
        // other than the two whose following Ashlar turns off, its own redirects are decided only after the send.
        var tail = Tail(builder.PrimaryHandler);
        if (tail is not HttpClientHandler and not SocketsHttpHandler)
            WarnUnknownPrimary(builder, name, tail, guard);
    }

    private static HttpMessageHandler Tail(HttpMessageHandler handler)
    {
        var current = handler;
        for (var depth = 0; depth < 64 && current is DelegatingHandler { InnerHandler: { } inner }; depth++)
            current = inner;
        return current;
    }

    private static void WarnUnknownPrimary(HttpMessageHandlerBuilder builder, string name, HttpMessageHandler primary, IEgressGuard? guard)
    {
        var enforcing = (guard as EgressGuard ?? EgressGuard.ProcessDefault).ProfileEnforcesByDefault();
        if (!enforcing)
            return;

        var type = primary.GetType().FullName ?? primary.GetType().Name;
        if (!NamedUnknownPrimaries.TryAdd(name + "\u0000" + type, 0))
            return;

        var logger = LoggerFor(builder);
        if (logger is not null)
            LogUnknownPrimary(logger, name, type, null);
    }

    // Nothing here may stop the client being built: a logger that cannot be resolved means no warning.
    private static ILogger? LoggerFor(HttpMessageHandlerBuilder builder)
    {
        try
        {
            return builder.Services.GetService<ILoggerFactory>()?.CreateLogger(LoggerEgressDecisionSink.CategoryName);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
