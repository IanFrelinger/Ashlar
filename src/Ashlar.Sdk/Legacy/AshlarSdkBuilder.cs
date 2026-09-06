#pragma warning disable CS0618 // intentional obsolete forwarding surface for NuGet compatibility

using Microsoft.Extensions.DependencyInjection;
using Ashlar.Sdk.Client;

// Namespace is deliberately Ashlar.Sdk, NOT Ashlar.Sdk.Legacy: this type exists so
// that existing `using Ashlar.Sdk;` code keeps compiling. Moving it into a .Legacy
// namespace breaks exactly the consumers the [Obsolete] shim was added to
// protect, which defeats its entire purpose. The file lives under Legacy/ for
// organisation only — the namespace is the contract.
namespace Ashlar.Sdk;

/// <summary>
/// Back-compat names for the HTTP client SDK. Prefer <see cref="AshlarClientSdkBuilder"/> and
/// <see cref="AshlarClientSdkServiceCollectionExtensions.AddAshlarClientSdk"/>.
/// </summary>
// TODO: [Obsolete] removed temporarily for TreatWarningsAsErrors compatibility.
// Thin app PR must restore [Obsolete("Renamed to AshlarClientSdkBuilder (namespace Ashlar.Sdk.Client).", error: false)]
// and delete this class when master application CLI migrates off this shim.
public sealed class AshlarSdkBuilder : AshlarClientSdkBuilder
{
    internal AshlarSdkBuilder(IServiceCollection services)
        : base(services)
    {
    }
}
