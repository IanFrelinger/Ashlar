using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Ashlar.Certification.Contracts;
using Ashlar.Core.Application.Adaptation.Ports;
using Ashlar.Core.Application.Certification.Ports;
using Ashlar.Core.Application.Observation.Ports;
using Ashlar.Core.Application.Paths;
using Ashlar.Core.Application.Rollback.Ports;
using Ashlar.Core.Domain.Bricks;
using Ashlar.Core.Domain.Execution;
using Ashlar.Infrastructure.Adaptation;
using Ashlar.Infrastructure.Adaptation.Generation;
using Ashlar.Infrastructure.Certification;
using Ashlar.Infrastructure.Certification.Sdk.Extensions;
using Ashlar.Infrastructure.Execution;
using Ashlar.Infrastructure.Observation;
using Ashlar.Infrastructure.Observation.Sdk.Extensions;
using Ashlar.Infrastructure.Rollback.Sdk.Extensions;
using Ashlar.Infrastructure.Rollback;

namespace Ashlar.Infrastructure.Adaptation.Sdk.Extensions;
/// <summary>
/// DI extension methods for the adaptation engine (Block 3 + Block 4).
/// </summary>
public static class AdaptationServiceCollectionExtensions
{
    /// <summary>
    /// Registers adaptation infrastructure: decomposer, fix generator, recompiler, rewirer, generators, source fixers,
    /// adaptation log, promoter, rollback helper, conflict resolver, version manager.
    /// When <paramref name="patternStorePath"/> is provided, also registers observation core (IPatternStore, IContextAssembler)
    /// required for BrickRecompiler and ObservationContextBrick.
    /// </summary>
    public static IServiceCollection AddAdaptationInfrastructure(this IServiceCollection services, string? patternStorePath = null)
        => services.AddAdaptationInfrastructure(patternStorePath, registerObservationCore: true);

    /// <summary>
    /// As <see cref="AddAdaptationInfrastructure(IServiceCollection, string?)"/>, but lets the
    /// caller state who owns the observation-core registrations
    /// (<see cref="IPatternStore"/>, <see cref="IPatternProcessedStore"/>,
    /// <see cref="IContextAssembler"/>).
    /// </summary>
    /// <remarks>
    /// Adaptation needs those services but does not have to be the one that registers them.
    /// A host that also adds the observation pipeline registers them there, from a different
    /// path calculation, and — because both used AddSingleton — silently won on last-wins.
    /// Passing <paramref name="registerObservationCore"/> as false makes that ownership
    /// explicit instead of leaving a registration that is overwritten in some configurations
    /// and load-bearing in others.
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <param name="patternStorePath">Pattern store path, or null to skip path-derived registrations.</param>
    /// <param name="registerObservationCore">
    /// False when the caller registers observation core itself; the services must still end up
    /// registered, because adaptation resolves them.
    /// </param>
    public static IServiceCollection AddAdaptationInfrastructure(
        this IServiceCollection services,
        string? patternStorePath,
        bool registerObservationCore)
    {
        if (registerObservationCore && !string.IsNullOrEmpty(patternStorePath))
            services.AddObservationCore(patternStorePath);

        services.AddOptions<AdaptationBrickOptions>();

        // Certification records live beside the pattern store, in the same way the
        // adaptation log, audit log and snapshots do below. Supplying a store path is
        // what makes admissions durable: without it the records are in-memory and no
        // brick certified in an earlier process can ever be found again.
        //
        // The no-pattern-store branch used to pass null, which AddCertificationInfrastructure
        // documents as "keeps the in-memory default" — so the comment above described exactly
        // the failure the code then chose. It matters most for the CLI, which is a fresh process
        // per invocation: certify in one `ashlar` command, and the next command cannot see it.
        // Resolve to the state directory instead, mirroring stateBasePath ~70 lines below.
        var certificationBasePath = !string.IsNullOrEmpty(patternStorePath)
            ? Path.GetDirectoryName(patternStorePath) ?? "."
            : RepoPathResolver.ResolveStateDirectory();
        var certificationRecordPath = Path.Combine(certificationBasePath, "ashlar-certifications");
        services.AddCertificationInfrastructure(certificationRecordPath);

        services.AddSingleton<CertifiedBrickRegistry>(sp =>
        {
            var registry = new CertifiedBrickRegistry(
                sp.GetRequiredService<ICertificationRecordStore>(),
                sp.GetRequiredService<CertificationRecordSigner>(),
                sp.GetService<Microsoft.Extensions.Logging.ILogger<CertifiedBrickRegistry>>());
            BootstrapCertifiedCatalog(sp, registry, patternStorePath);
            return registry;
        });
        services.AddSingleton<ICertifiedBrickAdmission, CertifiedBrickAdmission>();

        services.AddSingleton<BrickRegistry>(sp =>
        {
            var bricks = new List<DomainBrick>();
            if (patternStorePath != null)
            {
                var store = sp.GetRequiredService<ICertificationRecordStore>();
                var signer = sp.GetRequiredService<CertificationRecordSigner>();
                var record = store.Get("observation.context");
                // Strict plus whatever signer set the operator pinned; with nothing configured this
                // is the Strict preset unchanged.
                //
                // UNVERIFIED BEHAVIOURALLY, deliberately, and this comment is the disclosure.
                // Because `Ambient.Strict` IS the `Strict` preset instance when nothing is
                // configured, replacing this argument with `CertificationVerifyOptions.Strict`
                // changes nothing any test can observe — a green gate is not evidence that an
                // operator's pinning set reaches this factory. `PinnedWiringSiteConventionTests`
                // asserts on shipped IL that this type still calls
                // `CertificationTrustPolicy.get_Ambient`, so DELETING the wiring reddens; nothing
                // proves the pinned set is then enforced here. The reason it cannot be reached is
                // this factory's own shape: it resolves the process-wide `Ambient` directly, and
                // `Ambient` is a `Lazy` resolved once per process, so a test cannot vary it without
                // pinning the whole run. Resolving an optional `CertificationTrustPolicy` from `sp`
                // instead would create the seam — a DI contract change, flagged rather than taken.
                if (record is { Admitted: true, Signed: true }
                    && signer.Verify(record, CertificationTrustPolicy.Ambient.Strict))
                {
                    var contextAssembler = sp.GetRequiredService<IContextAssembler>();
                    bricks.Add(new ObservationContextBrick(contextAssembler));
                }
            }

            var options = sp.GetService<IOptions<AdaptationBrickOptions>>();
            if (options?.Value?.AdditionalBrickTypes is { Count: > 0 } types)
            {
                foreach (var type in types)
                {
                    try
                    {
                        var brick = (DomainBrick?)ActivatorUtilities.CreateInstance(sp, type);
                        if (brick != null)
                            bricks.Add(brick);
                    }
                    catch (Exception)
                    {
                        // Skip bricks that fail to instantiate (missing DI, etc.)
                    }
                }
            }

            return new BrickRegistry(bricks);
        });
        services.AddSingleton<IBrickRegistry>(sp => sp.GetRequiredService<BrickRegistry>());

        services.AddSingleton<IBrickDecomposer, BrickDecomposer>();
        services.AddSingleton<IFixGenerator, FixGenerator>();
        services.AddSingleton<IBrickRecompiler, BrickRecompiler>();
        services.AddSingleton<IBehaviorRewirer, BehaviorRewirer>();
        services.AddSingleton<IGeneratorModel>(sp =>
        {
            var factory = sp.GetService<Execution.IProviderFactory>();
            return factory is not null
                ? new ProviderGeneratorModel(factory)
                : new FixtureGeneratorModel();
        });
        services.AddSingleton<INewBrickGenerator>(sp =>
            new NewBrickGenerator(
                sp.GetRequiredService<IGeneratorModel>(),
                sp.GetService<IAdaptationLog>()));
        services.AddSingleton<IGenerateAndCertifyService, GenerateAndCertifyService>();
        services.AddSingleton<INewBehaviorAssembler, NewBehaviorAssembler>();
        services.AddSingleton<ISourceCodeFixer, EmptyCatchCodeFixer>();
        services.AddSingleton<IImmutableCoreRegistry, ImmutableCoreRegistry>();

        // Block 4: inheritance system
        // State files are co-located with the pattern store; without one they go to the state
        // directory (ASHLAR_STATE_DIR, else <repo root>/.ashlar/state), never the CWD / repo root.
        var stateBasePath = !string.IsNullOrEmpty(patternStorePath)
            ? Path.GetDirectoryName(patternStorePath) ?? "."
            : RepoPathResolver.ResolveStateDirectory();
        var adaptationDbPath = Path.Combine(stateBasePath, "ashlar-adaptation.db");
        services.AddSingleton<IAdaptationLog>(sp => new LiteDbAdaptationLog(adaptationDbPath));
        services.AddSingleton<IAdaptationPromoter, AdaptationPromoter>();
        services.AddSingleton<IInstanceResultAggregator, InstanceResultAggregator>();
        services.AddSingleton<IConflictResolver, ConflictResolver>();
        services.AddSingleton<ICoreVersionManager>(sp => new CoreVersionManager(RepoPathResolver.FindRepoRoot()));
        services.AddTransient<AdaptationRollbackHelper>();

        // Block 5: autonomy controls
        var auditDbPath = Path.Combine(stateBasePath, "ashlar-adaptation-audit.db");
        services.AddSingleton<IAdaptationAuditLog>(sp => new LiteDbAdaptationAuditLog(auditDbPath));
        services.AddSingleton<IUserFeedbackCapture, CliUserFeedbackCapture>();

        // Rollback infrastructure (P0.3)
        var snapshotPath = Path.Combine(stateBasePath, "ashlar-snapshots");
        services.AddRollbackInfrastructure(snapshotPath);

        return services;
    }

    private static void BootstrapCertifiedCatalog(
        IServiceProvider sp,
        CertifiedBrickRegistry registry,
        string? patternStorePath)
    {
        var store = sp.GetRequiredService<ICertificationRecordStore>();

        if (patternStorePath != null)
        {
            var record = store.Get("observation.context");
            if (record is { Admitted: true, Signed: true })
            {
                var contextAssembler = sp.GetRequiredService<IContextAssembler>();
                var brick = new ObservationContextBrick(contextAssembler);
                registry.TryAdmit(brick, record);
            }
        }

        var options = sp.GetService<IOptions<AdaptationBrickOptions>>();
        if (options?.Value?.AdditionalBrickTypes is not { Count: > 0 } types)
            return;

        foreach (var type in types)
        {
            try
            {
                var brick = (DomainBrick?)ActivatorUtilities.CreateInstance(sp, type);
                if (brick is null)
                    continue;
                var rec = store.Get(brick.Id);
                if (rec is { Admitted: true, Signed: true })
                    registry.TryAdmit(brick, rec);
            }
            catch (Exception)
            {
                // Skip bricks that fail to instantiate (missing DI, etc.)
            }
        }
    }

    /// <summary>
    /// Registers additional brick types for the adaptation pipeline.
    /// Bricks are resolved via DI (e.g. <see cref="IProviderFactory"/> for OWASPScannerBrick).
    /// Call after <see cref="AddAdaptationInfrastructure(IServiceCollection, string?)"/> and ensure required services (e.g. IProviderFactory) are registered.
    /// </summary>
    public static IServiceCollection AddAdaptationBricks(this IServiceCollection services, params Type[] brickTypes)
    {
        if (brickTypes.Length == 0) return services;
        services.Configure<AdaptationBrickOptions>(o => o.AdditionalBrickTypes.AddRange(brickTypes));
        return services;
    }
}
