using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ashlar.Core.Application.Autonomy;
using Ashlar.Core.Application.Certification.Ports;
using Ashlar.Core.Application.Execution.Ports;
using Ashlar.Infrastructure.Certification;
using Ashlar.Infrastructure.Certification.HotSwap;
using Ashlar.Infrastructure.Execution.Sandbox;
using Ashlar.Infrastructure.Scaling;

namespace Ashlar.Infrastructure.Autonomy;

/// <summary>
/// Composes the autonomous self-extension loop for a host: swap host with its quarantine
/// and watch surfaces, the iteration harness, cluster budget, sandbox session backend, and
/// the leaked-session reaper.
///
/// <para>Host-composed rather than folded into <c>AddAshlar</c> deliberately, on the same
/// reasoning as the MCP/A2A surfaces: a kernel consumer that merely references the package
/// must not acquire a loop that can modify the running system. Nothing here runs until
/// <see cref="AshlarAutonomyOptions.Enabled"/> is true, and the swap host still refuses
/// non-Tier-0 admissions on its own regardless of how it was registered.</para>
/// </summary>
[Experimental(AutonomyExperimental.DiagnosticId, UrlFormat = AutonomyExperimental.UrlFormat)]
public static class AutonomyServiceCollectionExtensions
{
    /// <summary>
    /// Registers the autonomy loop. <c>TryAdd</c> throughout so a host may substitute any
    /// piece — its own revocation list, provenance sink, or session backend — by
    /// registering it first.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">Configuration root; binds <c>Ashlar:Autonomy</c> when supplied.</param>
    public static IServiceCollection AddAshlarAutonomy(
        this IServiceCollection services,
        IConfiguration? configuration = null)
    {
        if (services is null)
            throw new ArgumentNullException(nameof(services));

        var optionsBuilder = services.AddOptions<AshlarAutonomyOptions>();
        if (configuration is not null)
            optionsBuilder.Bind(configuration.GetSection(AshlarAutonomyOptions.SectionName));
        optionsBuilder.ValidateOnStart();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<AshlarAutonomyOptions>, ValidateAshlarAutonomyOptions>());

        // Authority surfaces: shared singletons, because quarantine and demotion are
        // process-wide facts — two instances would mean two disagreeing truths.
        services.TryAddSingleton<ICertificateRevocationList, InMemoryCertificateRevocationList>();
        services.TryAddSingleton<ILineageAuthority>(sp =>
            new InMemoryLineageAuthority(
                sp.GetRequiredService<IOptions<AshlarAutonomyOptions>>().Value.LineageDemotionThreshold));
        services.TryAddSingleton<LoopPauseControl>();
        services.TryAddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<AshlarAutonomyOptions>>().Value;
            return new ClusterBudget(
                TimeSpan.FromSeconds(options.IterationCeilingSeconds),
                options.ThroughputGuardFactor);
        });

        // The swap host: constructed from options so watch thresholds, retention, and the
        // cadence floor are operator-visible rather than compiled in.
        services.TryAddSingleton<ICertifiedBrickSwapProvenanceSink, LoggingBrickSwapProvenanceSink>();
        services.TryAddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<AshlarAutonomyOptions>>().Value;
            return new CertifiedBrickHotSwapHost(
                sp.GetRequiredService<ICertifiedBrickSwapProvenanceSink>(),
                sp.GetService<ILogger<CertifiedBrickHotSwapHost>>(),
                hmacKey: null,
                drainTimeout: null,
                revocations: sp.GetRequiredService<ICertificateRevocationList>(),
                retentionWindow: options.RetentionWindow,
                watchThresholds: new WatchThresholds
                {
                    MinInvocations = options.WatchMinInvocations,
                    MaxErrorRateDelta = options.WatchMaxErrorRateDelta,
                    MaxLatencyFactor = options.WatchMaxLatencyFactor,
                    MaxInvocationDuration = options.WatchMaxInvocationSeconds > 0
                        ? TimeSpan.FromSeconds(options.WatchMaxInvocationSeconds)
                        : null,
                },
                lineageAuthority: sp.GetRequiredService<ILineageAuthority>(),
                pauseControl: sp.GetRequiredService<LoopPauseControl>(),
                cadenceFloor: options.CadenceFloorSeconds > 0
                    ? TimeSpan.FromSeconds(options.CadenceFloorSeconds)
                    : null);
        });

        // Sandbox backend + reaper. Registered even when sessions are unused: the reaper
        // is a cleanup backstop for whatever an earlier run leaked, and the harness simply
        // never opens a session when the context supplies no spec.
        services.TryAddSingleton<IProcessCommandRunner, ProcessCommandRunner>();
        // R2.1: no unlogged path from objective to runtime — session lifecycle events get a
        // sink by default, exactly as swap events do.
        services.TryAddSingleton<ISessionProvenanceSink, LoggingSessionProvenanceSink>();
        services.TryAddSingleton<ISandboxedSessionRunner>(sp => new DockerSandboxedSessionRunner(
            sp.GetRequiredService<IProcessCommandRunner>(),
            clock: null,
            logger: sp.GetService<ILogger<DockerSandboxedSessionRunner>>(),
            provenance: sp.GetRequiredService<ISessionProvenanceSink>(),
            // The operator's digest pin, when set: sessions refuse to start in any image
            // but the pinned identity. Null = capture only.
            expectedImageDigest: sp.GetRequiredService<IOptions<AshlarAutonomyOptions>>()
                .Value.SessionImageDigest));
        services.TryAddSingleton(sp => new DockerSandboxSessionReaper(
            sp.GetRequiredService<IProcessCommandRunner>(),
            clock: null,
            logger: sp.GetService<ILogger<DockerSandboxSessionReaper>>(),
            provenance: sp.GetRequiredService<ISessionProvenanceSink>()));

        // The harness. Resolves the gate through the container rather than constructing
        // one, so a host that substituted its own ICertificationGate keeps it.
        services.TryAddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<AshlarAutonomyOptions>>().Value;
            var logger = sp.GetService<ILogger<AutonomousIterationHarness>>();
            WarnIfCandidatesExecuteInProcess(options, logger);

            // The evidence archive, when the operator configured a directory for it.
            //
            // IT IS DELIBERATELY NOT REGISTERED AS ICertificationRecordStore, and that is the whole
            // reason it is constructed here by hand rather than routed through
            // AddCertificationInfrastructure(recordStorePath: ...). Doing the convenient thing —
            // making the archive directory the process's record store so `Save` is already on the
            // path — would also make FileCertificationRecordStore.IsAdmitted return true for the
            // held brick, because a persisted PASS record carries Admitted/Signed/Status=PASS.
            // Persisting evidence would then manufacture admission for a candidate the operator is
            // holding. EvidenceArchiveCompositionConventionTests.
            // TheEvidenceArchiveIsNeverTheAdmissionRecordStore is the guard that notices.
            //
            // It reuses the container's CertificationRecordSigner (registered by
            // AddCertificationInfrastructure with TryAddSingleton), so the archive re-verifies under
            // the same key that signed, and an operator-supplied signer reaches it for free. It
            // takes no CertificationVerifyOptions: strictness comes from CertificationTrustPolicy
            // inside the archive, so an operator's pinning set applies with no edit here.
            CertificationEvidenceArchive? archive = null;
            if (!string.IsNullOrWhiteSpace(options.EvidenceArchiveDirectory))
            {
                archive = new CertificationEvidenceArchive(
                    options.EvidenceArchiveDirectory!,
                    sp.GetRequiredService<CertificationRecordSigner>(),
                    trustPolicy: null,
                    sp.GetService<ILogger<CertificationEvidenceArchive>>());
            }

            return new AutonomousIterationHarness(
                sp.GetRequiredService<ICertificationGate>(),
                sp.GetRequiredService<CertifiedBrickHotSwapHost>(),
                sp.GetRequiredService<LoopPauseControl>(),
                sp.GetRequiredService<ILineageAuthority>(),
                sp.GetRequiredService<ISandboxedSessionRunner>(),
                sp.GetRequiredService<ClusterBudget>(),
                logger,
                buildCandidateInSession: options.BuildCandidateInSession,
                executeCandidateInSession: options.ExecuteCandidateInSession,
                holdAdmission: options.HoldAdmission,
                evidenceArchive: archive);
        });

        return services;
    }

    /// <summary>
    /// The containment warning. The three session flags default to false so a bare
    /// <c>AddAshlarAutonomy()</c> stays valid, but an ENABLED loop running with
    /// <see cref="AshlarAutonomyOptions.ExecuteCandidateInSession"/> off executes model-proposed
    /// candidate and mutant code inside the host process during the witness and mutation
    /// legs (the hold still blocks the swap; it does not block execution). That is a legitimate
    /// dev configuration and a wrong production one, so it is said once, loudly, when the
    /// harness is composed — which for the standing loop is host start. The recommended
    /// production trio is <c>UseSandboxSessions=true</c>, <c>BuildCandidateInSession=true</c>,
    /// <c>ExecuteCandidateInSession=true</c> with a pinned <c>SessionImage</c>.
    /// </summary>
    internal static void WarnIfCandidatesExecuteInProcess(AshlarAutonomyOptions options, ILogger? logger)
    {
        if (!options.Enabled || options.ExecuteCandidateInSession)
            return;

        logger?.LogWarning(
            "Ashlar:Autonomy is ENABLED with ExecuteCandidateInSession=false (UseSandboxSessions={UseSessions}, "
            + "BuildCandidateInSession={BuildInSession}): the witness and mutation legs will EXECUTE model-proposed "
            + "candidate and mutant code IN THIS PROCESS. HoldAdmission={Hold} blocks the swap, not the execution. "
            + "For anything beyond local development set UseSandboxSessions=true, BuildCandidateInSession=true and "
            + "ExecuteCandidateInSession=true with a pinned SessionImage (see docs/Configuration.md, Autonomy).",
            options.UseSandboxSessions, options.BuildCandidateInSession, options.HoldAdmission);
    }

    /// <summary>
    /// Adds the periodic leaked-session sweep. Separate from <see cref="AddAshlarAutonomy"/>
    /// because a CLI process that runs one iteration and exits wants the loop without a
    /// background sweeper, while a long-lived host wants both.
    /// </summary>
    public static IServiceCollection AddAshlarAutonomySessionReaper(this IServiceCollection services)
    {
        services.AddHostedService<SandboxSessionReaperService>();
        return services;
    }

    /// <summary>
    /// Adds the periodic human-digest render (spec §7): swap provenance is retained in a
    /// bounded ring and rendered to the log every <see cref="AshlarAutonomyOptions.DigestIntervalSeconds"/>.
    /// Separate opt-in for the same reason as the reaper — and it TAKES OVER the swap
    /// provenance sink binding with <see cref="RecordingBrickSwapProvenanceSink"/> (which
    /// still logs every event, so R2.1 holds). Order-independent with
    /// <see cref="AddAshlarAutonomy"/>; a host with its own sink should compose retention
    /// into that sink instead of calling this.
    /// </summary>
    public static IServiceCollection AddAshlarAutonomyDigestJob(this IServiceCollection services)
    {
        services.TryAddSingleton<RecordingBrickSwapProvenanceSink>();
        // Deliberately AddSingleton, not TryAdd: whichever order the host called
        // AddAshlarAutonomy and this in, the LAST single-service registration wins at
        // resolution, so the retaining sink serves the host either way.
        services.AddSingleton<ICertifiedBrickSwapProvenanceSink>(sp =>
            sp.GetRequiredService<RecordingBrickSwapProvenanceSink>());
        services.AddHostedService<AutonomyDigestService>();
        return services;
    }
}
