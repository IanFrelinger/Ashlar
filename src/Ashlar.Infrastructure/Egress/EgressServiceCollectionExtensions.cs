using Ashlar.Abstractions.Security.Egress;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ashlar.Infrastructure.Egress;

/// <summary>
/// Registers the egress guard (SPEC-007) in a service collection: the guard, the logger subscription for
/// its decision records, and the guard handler on every <see cref="IHttpClientFactory"/> client.
/// </summary>
/// <remarks>
/// <para><b>Every factory client.</b> The guard handler goes on every client the container's factory builds: the
/// unnamed default client, every named and typed client, and the clients the host application registers itself,
/// whether they were registered before or after this call. The binding is one <c>ConfigureHttpClientDefaults</c>
/// call, so each client is bound once however many times this method runs.</para>
/// <para><b>Outermost.</b> The handler is inserted at the front of the client's additional handlers. Client defaults
/// run before any client's own configuration, so every handler added with <c>AddHttpMessageHandler</c> (retry and
/// resilience handlers among them), whether in an earlier default or in the client's own registration, runs inside
/// the guard, and one send is one decision until a redirect or a rewritten URI is evaluated again. Only
/// configuration that itself inserts at the front after this, or a handler-builder filter, can sit outside it; the
/// factory's own logging scope handler does. A filter registered here, after the logging filter, sees the primary
/// after those actions and, when the primary is an <c>HttpClientHandler</c> or <c>SocketsHttpHandler</c>, turns
/// <c>AllowAutoRedirect</c> off and puts the redirect handler directly above it.</para>
/// <para><b>Per-client site.</b> A factory client's decision site is <c>factory:</c> plus the client's name; the
/// unnamed default client's name is empty, so its site is <c>factory:</c>. The defaults builder has no client name,
/// and the <c>IHttpClientBuilder</c> methods that see the handler builder are obsolete, so the binding adds an
/// action to <see cref="HttpClientFactoryOptions.HttpMessageHandlerBuilderActions"/> for every client name through
/// the defaults builder's own service collection, and reads the name from the handler builder as each client's
/// handlers are built.</para>
/// <para><b>Enforcement.</b> Refused decisions stop a send when the resolved mode enforces. The guard does not change
/// the request. The redirect handler, when it follows, does: it updates the URI and may change the method, drop the
/// content and clear <c>Authorization</c>. Building a handler resolves
/// <see cref="IEgressGuard"/> and activates <see cref="EgressDecisionLoggerSubscription"/>; a failed guard lookup
/// falls back to <see cref="EgressGuard.ProcessDefault"/>, and decisions still reach the
/// <c>Ashlar-Egress</c> event source.</para>
/// <para><b>Logging.</b> Report and allowed decisions use Debug in <c>Ashlar.Egress</c>; enforced refusals use
/// windowed Warning records and counted summaries. The subscription is activated by a hosted service when a host
/// starts, and by the first factory client's handler construction otherwise.</para>
/// <para><b>Host exceptions.</b> Configure <see cref="EgressGuardOptions.ReportOnlyClients"/> before or after
/// this call to report for a named host-owned factory client and its redirect hops. AirGapped ignores the list;
/// Ashlar-owned names fail host startup. Each configured name logs a startup Warning. Faults remain fail-closed.</para>
/// </remarks>
public static class EgressServiceCollectionExtensions
{
    /// <summary>The start of a factory client's decision site; the client's name follows it.</summary>
    internal const string FactorySitePrefix = "factory:";

    /// <summary>
    /// Adds the egress guard: <see cref="EgressGuard.ProcessDefault"/> as <see cref="IEgressGuard"/> unless one is
    /// already registered, a logger subscription for the decision records, and the guard handler on every
    /// <see cref="IHttpClientFactory"/> client. Calling it again does nothing.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns><paramref name="services"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    public static IServiceCollection AddAshlarEgressGuard(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (services.Any(descriptor => descriptor.ServiceType == typeof(EgressGuardRegistration)))
            return services;

        services.AddSingleton(new EgressGuardRegistration());
        services.AddLogging();
        services.TryAddSingleton<IEgressGuard>(EgressGuard.ProcessDefault);
        services.TryAddSingleton<EgressDecisionLoggerSubscription>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, EgressDecisionLoggerActivator>());
        services.AddOptions<EgressGuardOptions>().ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<EgressGuardOptions>, ValidateEgressGuardOptions>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, EgressHostClientPolicyActivator>());

        services.ConfigureHttpClientDefaults(defaults => defaults.Services.ConfigureAll<HttpClientFactoryOptions>(
            options => options.HttpMessageHandlerBuilderActions.Add(handlers => handlers.AdditionalHandlers.Insert(
                0,
                EgressHttp.CreateDelegatingHandler(
                    EgressFamilies.HttpFactory,
                    FactorySitePrefix + handlers.Name,
                    ResolveGuardAndActivateLogging(handlers))))));

        // AddHttpClient's logging filter was registered by ConfigureHttpClientDefaults. Filters run in registration
        // order around next: our post-next step runs before logging adds its outer scope handler. A normal client
        // therefore needs no relocation and emits no false removed/moved-handler warning.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHttpMessageHandlerBuilderFilter, EgressRedirectBindingFilter>());

        return services;
    }

    // Runs each time a factory client's handlers are built. Resolution failures retain process enforcement.
    internal static IEgressGuard? ResolveGuardAndActivateLogging(HttpMessageHandlerBuilder handlers)
    {
        // Options errors are startup/configuration failures, not a failed guard lookup that may fall back.
        var reportOnly = false;
        try
        {
            reportOnly = handlers.Services.GetService<IOptions<EgressGuardOptions>>()?.Value
                .ReportOnlyClients.Contains(handlers.Name ?? string.Empty) == true;
        }
        catch (OptionsValidationException)
        {
            throw;
        }
        catch (Exception)
        {
            // No exception is granted if options cannot be read. Guard lookup below retains its diagnosed fallback.
        }
        IServiceProvider? services = null;
        var guard = EgressGuard.ResolveForRoute(() =>
        {
            services = handlers.Services;
            try
            {
                _ = services.GetService<EgressDecisionLoggerSubscription>();
            }
            catch (Exception)
            {
                // The logger subscription could not be built; the decisions still go to the event source.
            }
            return services.GetService<IEgressGuard>();
        }, (faulted, fault) =>
            services?.GetService<ILoggerFactory>()?.CreateLogger("Ashlar.Egress").Log(
                faulted ? LogLevel.Warning : LogLevel.Debug,
                new EventId(7306, "EgressGuardFallback"),
                "Egress guard resolution {Resolution}; using ProcessDefault; fault={Fault}",
                faulted ? "failed" : "unregistered", fault ?? "none"));
        return reportOnly ? EgressGuard.ForReportOnlyFactoryClient(guard, handlers.Name ?? string.Empty) : guard;
    }

    /// <summary>Marks a collection <see cref="AddAshlarEgressGuard"/> has already run on.</summary>
    private sealed class EgressGuardRegistration
    {
    }
}
