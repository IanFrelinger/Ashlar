#pragma warning disable CS0618 // intentional obsolete forwarding surface for NuGet compatibility

using Microsoft.Extensions.DependencyInjection;
using Ashlar.Sdk.Client;

// Namespace is deliberately Ashlar.Sdk, NOT Ashlar.Sdk.Legacy: `services.AddAshlarSdk(...)`
// resolves only when this extension class sits in the namespace the caller already
// has a `using` for. A .Legacy namespace makes the shim invisible to the very code
// it exists to keep compiling.
namespace Ashlar.Sdk;

/// <summary>Obsolete entry points preserved for binary compatibility.</summary>
public static class AshlarSdkLegacyExtensions
{
    /// <inheritdoc cref="AshlarClientSdkServiceCollectionExtensions.AddAshlarClientSdk"/>
    // TODO: [Obsolete] removed temporarily for TreatWarningsAsErrors compatibility.
    // Thin app PR must restore [Obsolete("Use Ashlar.Sdk.Client.AshlarClientSdkServiceCollectionExtensions.AddAshlarClientSdk.", error: false)]
    // and delete this method when master application CLI migrates off this shim.
    public static IServiceCollection AddAshlarSdk(
        this IServiceCollection services,
        string baseUrl,
        Action<AshlarSdkBuilder>? configure = null)
    {
        return AshlarClientSdkServiceCollectionExtensions.AddAshlarClientSdk(
            services,
            baseUrl,
            configure == null ? null : b => configure(new AshlarSdkBuilder(b.Services)));
    }
}
