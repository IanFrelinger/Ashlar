using System.Collections.Concurrent;
using Ashlar.Abstractions.Security.Egress;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;

namespace Ashlar.Infrastructure.Egress;

/// <summary>
/// Turns automatic redirects off on a factory client's known primary and puts <see cref="EgressRedirectHandler"/>
/// above it, and puts the guard handler back when a client cleared the additional handlers.
/// </summary>
internal sealed class EgressRedirectBindingFilter : IHttpMessageHandlerBuilderFilter
{
    private static readonly ConcurrentDictionary<string, byte> NamedUnknownPrimaries = new(StringComparer.Ordinal);
    private readonly ILogger _logger;

    /// <summary>Logs on the <c>Ashlar.Egress</c> category.</summary>
    /// <param name="loggerFactory">The logger factory.</param>
    /// <exception cref="ArgumentNullException"><paramref name="loggerFactory"/> is <see langword="null"/>.</exception>
    public EgressRedirectBindingFilter(ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _logger = loggerFactory.CreateLogger("Ashlar.Egress");
    }

    /// <inheritdoc />
    public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next)
    {
        ArgumentNullException.ThrowIfNull(next);
        return builder =>
        {
            next(builder);
            Bind(builder);
        };
    }

    private void Bind(HttpMessageHandlerBuilder builder)
    {
        var primary = builder.PrimaryHandler;
        var guard = EgressServiceCollectionExtensions.ResolveGuardAndActivateLogging(builder);
        var site = EgressServiceCollectionExtensions.FactorySitePrefix + builder.Name;
        if (primary is not null)
        {
            builder.PrimaryHandler = EgressRedirects.InsertAbovePrimary(primary, EgressFamilies.HttpFactory, site, guard, followCrossHost: true);
            var tail = Tail(builder.PrimaryHandler);
            if (tail is not HttpClientHandler and not SocketsHttpHandler
                && (guard as EgressGuard ?? EgressGuard.ProcessDefault).ProfileEnforcesByDefault())
            {
                var type = tail.GetType().FullName ?? tail.GetType().Name;
                if (NamedUnknownPrimaries.TryAdd(builder.Name + "\u0000" + type, 0))
                    _logger.LogWarning(new EventId(7304, "EgressUnknownPrimaryHandler"),
                        "Factory client {Client} is built on primary handler {PrimaryType}; automatic redirects were not disabled.",
                        builder.Name, type);
            }
        }

        var handlers = builder.AdditionalHandlers;
        var index = -1;
        for (var i = 0; i < handlers.Count; i++)
        {
            if (handlers[i] is EgressGuardHandler)
            {
                index = i;
                break;
            }
        }

        if (index < 0)
        {
            _logger.LogWarning(new EventId(7303, "EgressGuardHandlerRestored"),
                "Factory client {Client} removed the egress guard handler; it was re-inserted at the front.",
                builder.Name);
            var guardHandler = (EgressGuardHandler)EgressHttp.CreateDelegatingHandler(EgressFamilies.HttpFactory, site, guard);
            handlers.Insert(0, guardHandler);
            guardHandler.UnmediatedRedirectWarning = Warn;
            return;
        }

        if (index != 0)
        {
            var existing = handlers[index];
            handlers.RemoveAt(index);
            handlers.Insert(0, existing);
            _logger.LogWarning(new EventId(7303, "EgressGuardHandlerRestored"),
                "Factory client {Client} moved the egress guard handler off the front; it was put back.",
                builder.Name);
        }

        if (handlers[0] is EgressGuardHandler atFront)
            atFront.UnmediatedRedirectWarning = Warn;
    }

    private static HttpMessageHandler Tail(HttpMessageHandler handler)
    {
        var current = handler;
        for (var depth = 0; depth < 64 && current is DelegatingHandler { InnerHandler: { } inner }; depth++)
            current = inner;
        return current;
    }

    private void Warn(string message) => _logger.LogWarning("{Message}", message);
}
