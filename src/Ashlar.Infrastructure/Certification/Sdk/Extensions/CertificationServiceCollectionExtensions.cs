using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Ashlar.Core.Application.Autonomy;
using Ashlar.Core.Application.Certification.Ports;
using Ashlar.Infrastructure.Certification.Composition;
using Ashlar.Infrastructure.Certification.HotSwap;

namespace Ashlar.Infrastructure.Certification.Sdk.Extensions;

/// <summary>Registers certification infrastructure services (gate, stores, composition pipeline).</summary>
public static class CertificationServiceCollectionExtensions
{
    /// <summary>Adds certification infrastructure.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="recordStorePath">
    /// Directory for DURABLE certification records. When supplied, admissions survive the
    /// process; when null the store is in-memory, as before.
    /// </param>
    /// <remarks>
    /// The in-memory default means a certification granted in one process is invisible to
    /// the next, so nothing can ever be admitted from prior state. That is fine for a
    /// single-process host that certifies as it goes, and fatal for the CLI, where each
    /// invocation is a fresh process: <c>ashlar adapt --store-path ...</c> could never find
    /// an admitted brick, whatever had been certified earlier.
    ///
    /// Durability does not weaken admission. Records are written signed and re-verified on
    /// load by <see cref="FileCertificationRecordStore"/>, and the signature covers the
    /// record's ContentHash, so a mutated record reads as uncertified rather than as
    /// admitted.
    /// </remarks>
    public static IServiceCollection AddCertificationInfrastructure(
        this IServiceCollection services,
        string? recordStorePath = null)
    {
        // TryAdd for the same reason as the store below: a host that supplied its own signer
        // (the only way to hold a real HMAC key, per SPEC-006 S-4) must not have it replaced
        // by a parameterless one, which would silently drop it back to the committed dev key.
        services.TryAddSingleton<CertificationRecordSigner>();
        // A2: real executed-evidence compile check for autonomous admissions (Roslyn, in-process).
        services.TryAddSingleton<IExtensionCompileCheck, RoslynExtensionCompileCheck>();
        // A4: post-apply canary — recompiles admitted writes as they landed, so a bad auto-admission
        // is rolled back instead of left on an unattended node (Roslyn, in-process, no .NET SDK).
        services.TryAddSingleton<IPostApplyVerification, RoslynPostApplyVerification>();

        if (string.IsNullOrWhiteSpace(recordStorePath))
        {
            // TryAdd, not Add: this is the fallback, and a fallback must never displace a
            // durable store an earlier call already chose. Registering it unconditionally is
            // what let AddCertificationGate() silently revert a host to in-memory.
            services.TryAddSingleton<ICertificationRecordStore, InMemoryCertificationRecordStore>();
        }
        else
        {
            var directory = recordStorePath;

            // A path is an explicit decision, so it wins over any default already present —
            // otherwise TryAdd would just move the silent failure to the other composition
            // order, where asking for durability would be quietly ignored instead. Explicit
            // beats default in both directions; only two explicit paths race, and there the
            // last one stated wins.
            services.RemoveAll<ICertificationRecordStore>();
            services.AddSingleton<ICertificationRecordStore>(sp =>
                new FileCertificationRecordStore(
                    directory,
                    sp.GetRequiredService<CertificationRecordSigner>()));
        }

        services.AddSingleton<ICertificationGate, CertificationGate>();
        // TryAdd for the same reason as the brick signer at the top of this method — a host that
        // supplied its own composition signer (the only way to key the composition lane
        // independently of the brick lane) must not have it replaced by a container-activated one.
        // The factory is explicit rather than left to constructor injection so the brick signer's
        // newly load-bearing role is visible AT the registration site, and so a future fourth
        // constructor parameter cannot silently change which constructor the container selects.
        // This registration is what closes limitation 9 in a shipped host: the operator's key
        // reaches composition records because the brick signer arrives here.
        services.TryAddSingleton(sp => new CompositionCertificationRecordSigner(
            sp.GetRequiredService<CertificationRecordSigner>(),
            sp.GetService<ILogger<CompositionCertificationRecordSigner>>()));
        services.AddSingleton<ICompositionCertificationRecordStore, InMemoryCompositionCertificationRecordStore>();
        services.AddSingleton<ICompositionCertificationGate, CompositionCertificationGate>();
        return services;
    }

    /// <summary>Adds certification gate.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="recordStorePath">
    /// Directory for DURABLE certification records, forwarded to
    /// <see cref="AddCertificationInfrastructure"/>. Null keeps the in-memory default.
    /// </param>
    /// <remarks>
    /// Until this parameter existed the gate could only ever be composed with the in-memory
    /// store, and because that store was registered unconditionally, a host that had already
    /// asked for durability got it taken away again with no error:
    /// <code>
    /// services.AddCertificationInfrastructure(recordStorePath: path);
    /// services.AddCertificationGate();   // reverted the store to in-memory
    /// </code>
    /// Admissions then vanished at process exit, which for the CLI — a fresh process per
    /// invocation — means nothing certified could ever be admitted afterwards.
    /// </remarks>
    public static IServiceCollection AddCertificationGate(
        this IServiceCollection services,
        string? recordStorePath = null)
    {
        services.AddCertificationInfrastructure(recordStorePath);
        services.AddSingleton<CertifiedBrickRegistry>();
        services.AddSingleton<ICertifiedBrickAdmission, CertifiedBrickAdmission>();
        services.AddSingleton<CertifiedCompositionRegistry>();
        services.AddSingleton<ICertifiedCompositionAdmission, CertifiedCompositionAdmission>();
        services.AddSingleton<Ashlar.Core.Domain.Execution.IBrickRegistry>(sp =>
            sp.GetRequiredService<CertifiedBrickRegistry>());
        return services;
    }

    /// <summary>
    /// Adds the certified brick hot-swap host: verify-at-load, one collectible load
    /// context per generation, fail-closed swaps, provenance events per swap outcome.
    /// TryAdd throughout so hosts can substitute their own provenance sink.
    /// </summary>
    [Experimental(AutonomyExperimental.DiagnosticId, UrlFormat = AutonomyExperimental.UrlFormat)]
    public static IServiceCollection AddCertifiedBrickHotSwapHost(this IServiceCollection services)
    {
        services.TryAddSingleton<ICertifiedBrickSwapProvenanceSink, LoggingBrickSwapProvenanceSink>();
        services.TryAddSingleton(sp => new CertifiedBrickHotSwapHost(
            sp.GetRequiredService<ICertifiedBrickSwapProvenanceSink>(),
            sp.GetService<ILogger<CertifiedBrickHotSwapHost>>()));
        return services;
    }
}
