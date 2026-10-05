using FluentValidation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http;
using Ashlar.Abstractions.Routing;
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
    /// </summary>
    public static IServiceCollection AddAshlar(
        this IServiceCollection services,
        Action<AshlarHostingOptions>? configure = null)
    {
        var options = new AshlarHostingOptions();
        configure?.Invoke(options);
        ResolveStrictMode(options);
        var deploymentProfile = ResolveDeploymentProfile(options);
        AshlarDeploymentProfileEnvironment.NoteResolved(deploymentProfile switch
        {
            AshlarDeploymentProfile.AirGapped => "air-gapped",
            AshlarDeploymentProfile.SecureWorkstation => "secure-workstation",
            AshlarDeploymentProfile.System => "system",
            AshlarDeploymentProfile.Edge => "edge",
            AshlarDeploymentProfile.Server => "server",
            _ => "full"
        });
        var modules = GetModuleSelection(deploymentProfile);

        services.AddSingleton(options.StrictMode);

        services.AddHttpClient();
        // SPEC-007: the report-only egress guard handler on every IHttpClientFactory client in this container,
        // the host's own included (owner decision Q1 = A). Idempotent, so the kernel members below that call it add nothing.
        services.AddAshlarEgressGuard();
        var configuration = new ConfigurationBuilder()
            .AddEnvironmentVariables()
            .Build();

        AshlarKernelRegistrar.Register(services, options, modules, configuration);

        return services;
    }
}

