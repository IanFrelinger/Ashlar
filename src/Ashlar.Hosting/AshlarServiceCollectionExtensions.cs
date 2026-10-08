using FluentValidation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http;
using Ashlar.Abstractions;
using Ashlar.Abstractions.Routing;
using Ashlar.Abstractions.Security.Egress;
using Ashlar.Abstractions.Transport;
using Ashlar.BackgroundAgents;
using Ashlar.BackgroundAgents.Trust;
using Ashlar.Core.Application.Adaptation.Ports;
using Ashlar.Core.Application.Analysis.UseCases.AnalyzeCode;
using Ashlar.Core.Application.Common.Ports;
using Ashlar.Core.Application.Common.Services;
using Ashlar.Core.Application.Copilot.Ports;
using Ashlar.Core.Application.Ephemeral.Ports;
using Ashlar.Core.Application.Knowledge.Ports;
using Ashlar.Core.Application.Observation.Ports;
using Ashlar.Core.Application.Paths;
using Ashlar.Core.Application.Testing.UseCases.RunTests;
using Ashlar.Core.Application.Trust.Ports;
using Ashlar.Core.Application.Validation.UseCases.RunValidation;
using Ashlar.Infrastructure;
using Ashlar.Infrastructure.Copilot;
using Ashlar.Infrastructure.Egress;
using Ashlar.Infrastructure.Execution;
using Ashlar.Infrastructure.Execution.Ephemeral;
using Ashlar.Infrastructure.Execution.LoadPolicy;
using Ashlar.Infrastructure.Execution.Routing;
using Ashlar.Infrastructure.Knowledge;
using Ashlar.Infrastructure.Maintenance;
using Ashlar.Infrastructure.ModelArtifacts;
using Ashlar.Infrastructure.NodeCapabilityRuntime;
using Ashlar.Infrastructure.Persistence;
using Ashlar.Infrastructure.Persistence.Ephemeral;
using Ashlar.Infrastructure.Pipelines;
using Ashlar.Orchestration;
using Ashlar.Orchestration.Models;
using Ashlar.Orchestration.Transport;
using Ashlar.Runtime;
using Ashlar.Runtime.Routing;
using Ashlar.Transport.Grpc;

namespace Ashlar.Hosting;

/// <summary>
/// DI composition root for the Ashlar kernel.  This is the single place that wires every
/// subsystem together — orchestration, adaptation, persistence, trust, execution, etc.
/// <para>
/// <b>Architecture:</b> The method <see cref="AddAshlar"/> follows a strict registration
/// order because later registrations depend on services registered earlier (e.g. the
/// model decorator chain wraps <c>ProviderBackedModel → HotSwappableModel →
/// OrchestrationRuntimeModelDecorator</c>, so the provider factory must already exist).
/// </para>
/// <para>
/// <b>Deployment profiles:</b> A <see cref="AshlarDeploymentProfile"/> (resolved from
/// <c>ASHLAR_DEPLOYMENT_PROFILE</c> or <see cref="AshlarHostingOptions.DeploymentProfile"/>)
/// controls which subsystem modules are included via <see cref="ModuleSelection"/>.
/// Profiles range from <c>Full</c> (all modules) down to <c>System</c> (bare minimum
/// for CLI/headless tooling).
/// </para>
/// <para>
/// <b>Related files:</b>
/// <see cref="AshlarHostingOptions"/> — caller-facing option bag;
/// <see cref="AshlarDeploymentProfile"/> — deployment tier enum;
/// <c>Ashlar.Core.Domain.AshlarDefaults</c> — all tuneable default constants.
/// </para>
/// </summary>
public static partial class AshlarServiceCollectionExtensions
{
    /// <summary>
    /// Adds Ashlar with an explicit deployment profile.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="profile">Dependency profile to apply.</param>
    /// <param name="configure">Optional additional options overrides.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddAshlarProfile(
        this IServiceCollection services,
        AshlarDeploymentProfile profile,
        Action<AshlarHostingOptions>? configure = null)
    {
        return services.AddAshlar(options =>
        {
            options.DeploymentProfile = profile;
            configure?.Invoke(options);
        });
    }

    /// <summary>
    /// Registers every Ashlar subsystem into the DI container.  The registration order
    /// matters: downstream registrations (model decorator chain, workflow executor)
    /// resolve services registered in earlier blocks.
    /// <para>
    /// <b>Environment variables read here (see inline comments for each):</b>
    /// <c>ASHLAR_STRICT_MODE</c>, <c>ASHLAR_DEPLOYMENT_PROFILE</c>,
    /// <c>ASHLAR_LOOP_PARALLEL</c>, <c>ASHLAR_LOOP_INSTRUMENT</c>,
    /// <c>ASHLAR_OBSERVATION_FAIL_OPEN</c>, <c>ASHLAR_EPHEMERAL</c>,
    /// <c>ASHLAR_EPHEMERAL_MODELS</c>, <c>ASHLAR_EPHEMERAL_DB</c>,
    /// <c>ASHLAR_TRUST_ENABLED</c>, <c>ASHLAR_LOAD_PREFERENCE</c>,
    /// <c>ASHLAR_EXECUTION_REMOTE_URL</c>.
    /// </para>
    /// <para>
    /// It also composes the egress guard's mode (SPEC-007 PR 4), which is <c>report</c> on every profile; a mode
    /// setting exists but is not yet supported. After <c>AddAshlar</c> has resolved AirGapped (or SecureWorkstation),
    /// a later <c>AddAshlar</c> in the same process with a less strict profile does not lower the profile the
    /// process notes: the remote-protocol validators, the egress guard and the container's own guard keep it.
    /// </para>
    /// </summary>
    public static IServiceCollection AddAshlar(
        this IServiceCollection services,
        Action<AshlarHostingOptions>? configure = null)
    {
        var options = new AshlarHostingOptions();
        configure?.Invoke(options);
        ResolveStrictMode(options);
        var deploymentProfile = ResolveDeploymentProfile(options, out var profileDefaulted);
        // The hosting options object carries the profile this call resolved, including one that came from
        // ASHLAR_DEPLOYMENT_PROFILE rather than the configure callback.
        options.DeploymentProfile = deploymentProfile;
        var canonicalProfile = deploymentProfile switch
        {
            AshlarDeploymentProfile.AirGapped => "air-gapped",
            AshlarDeploymentProfile.SecureWorkstation => "secure-workstation",
            AshlarDeploymentProfile.System => "system",
            AshlarDeploymentProfile.Edge => "edge",
            AshlarDeploymentProfile.Server => "server",
            _ => "full"
        };
        // The strictest profile noted in the process wins (SPEC-007 PR 4, D5).
        AshlarDeploymentProfileEnvironment.NoteResolved(canonicalProfile);
        // Infrastructure and the MEAI pipeline read this options value. They never read the environment or
        // Effective themselves. Captured after NoteResolved, so a later less-strict AddAshlar still records
        // the strictest profile this process has noted.
        var notedProfile = AshlarDeploymentProfileEnvironment.Effective(canonicalProfile) ?? canonicalProfile;
        services.AddOptions<AshlarResolvedDeploymentProfileOptions>()
            .Configure(resolved => resolved.Profile = notedProfile)
            .PostConfigure(resolved => resolved.Profile = notedProfile);
        // SPEC-007 PR 4.6: ASHLAR_EGRESS_MODE is read once per process (here, or at the first decision of a process
        // that never runs AddAshlar), and AshlarHostingOptions.EgressMode can only raise it.
        var egressOverride = EgressEnforcement.NoteHostingOption(options.EgressMode);
        var modules = GetModuleSelection(deploymentProfile);

        services.AddSingleton(options.StrictMode);

        services.AddHttpClient();
        // SPEC-007: the egress guard handler on every IHttpClientFactory client in this container, the host's own
        // included (owner decision Q1 = A). Idempotent, so the kernel members below that call it add nothing.
        services.AddAshlarEgressGuard();
        BindComposedEgressGuard(services, canonicalProfile, profileDefaulted, egressOverride, options.EgressMode);
        var configuration = new ConfigurationBuilder()
            .AddEnvironmentVariables()
            .Build();

        AshlarKernelRegistrar.Register(services, options, modules, configuration);

        return services;
    }
}

