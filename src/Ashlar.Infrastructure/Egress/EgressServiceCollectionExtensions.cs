using Ashlar.Abstractions.Security.Egress;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http;

namespace Ashlar.Infrastructure.Egress;

/// <summary>
/// Registers the report-only egress guard (SPEC-007) in a service collection: the guard, the logger subscription for
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
/// the guard, and one send is one decision. Only configuration that itself inserts at the front after this, or a
/// handler-builder filter, can sit outside it; the factory's own logging scope handler does.</para>
/// <para><b>Per-client site.</b> A factory client's decision site is <c>factory:</c> plus the client's name; the
/// unnamed default client's name is empty, so its site is <c>factory:</c>. The defaults builder has no client name,
/// and the <c>IHttpClientBuilder</c> methods that see the handler builder are obsolete, so the binding adds an
/// action to <see cref="HttpClientFactoryOptions.HttpMessageHandlerBuilderActions"/> for every client name through
/// the defaults builder's own service collection, and reads the name from the handler builder as each client's
/// handlers are built.</para>
/// <para><b>Report-only.</b> Nothing here refuses, throws on a send or changes a request. Building a handler resolves
/// <see cref="IEgressGuard"/> and activates <see cref="EgressDecisionLoggerSubscription"/>; if either fails, the client
/// is still built, with <see cref="EgressGuard.ProcessDefault"/>, and the decisions still reach the
/// <c>Ashlar-Egress</c> event source.</para>
/// <para><b>Logging.</b> Decisions are written to the <c>Ashlar.Egress</c> category at Debug, so they are off
/// unless an operator turns that category on. The subscription is activated by a hosted service when a host
/// starts, and by the first factory client's handler construction otherwise.</para>
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

        services.ConfigureHttpClientDefaults(defaults => defaults.Services.ConfigureAll<HttpClientFactoryOptions>(
            options => options.HttpMessageHandlerBuilderActions.Add(handlers => handlers.AdditionalHandlers.Insert(
                0,
                EgressHttp.CreateDelegatingHandler(
                    EgressFamilies.HttpFactory,
                    FactorySitePrefix + handlers.Name,
                    ResolveGuardAndActivateLogging(handlers))))));

        return services;
    }

    // Runs each time a factory client's handlers are built. Nothing here may stop the client being built, so a
    // failure leaves the guard at ProcessDefault (a null guard) and the records on the event source only.
    private static IEgressGuard? ResolveGuardAndActivateLogging(HttpMessageHandlerBuilder handlers)
    {
        IServiceProvider services;
        try
        {
            services = handlers.Services;
        }
        catch (Exception)
        {
            return null;
        }

        try
        {
            _ = services.GetService<EgressDecisionLoggerSubscription>();
        }
        catch (Exception)
        {
            // The logger subscription could not be built; the decisions still go to the event source.
        }

        try
        {
            return services.GetService<IEgressGuard>();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Marks a collection <see cref="AddAshlarEgressGuard"/> has already run on.</summary>
    private sealed class EgressGuardRegistration
    {
    }
}
