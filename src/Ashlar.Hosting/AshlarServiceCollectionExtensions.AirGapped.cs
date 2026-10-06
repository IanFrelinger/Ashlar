using Ashlar.AI.Pipeline;
using Ashlar.Core.Application.Execution.Routing;
using Ashlar.Infrastructure.Deployment;
using Ashlar.Infrastructure.Execution;
using Ashlar.Infrastructure.MeshLab;
using Ashlar.Infrastructure.ModelArtifacts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Ashlar.Hosting;

public static partial class AshlarServiceCollectionExtensions
{
    /// <summary>
    /// AirGapped and SecureWorkstation hygiene (SPEC-007 PR 4.10, default D35): registers the
    /// <see cref="ResolvedDeploymentProfile"/> Infrastructure and the application hosts read, the AirGapped boot
    /// validators, and the AirGapped default of the ollama.com catalog.
    /// </summary>
    /// <remarks>
    /// <para><b>The strictest profile noted in the process</b> (default D5). The caller notes its own profile first, so
    /// the registered profile is AirGapped once any <c>AddAshlar</c> in the process resolved AirGapped, whatever this
    /// call resolved, as for the composed egress guard. A later <c>AddAshlar</c> on the same collection replaces the
    /// registration with the profile noted then, which is never weaker. A later <c>AddAshlar</c> still selects its own
    /// module set; that is not changed here.</para>
    /// <para><b>The boot validators</b> bind to the options types, on the <c>ValidateOnStart</c> pattern. Each refuses
    /// one opt-in that leaves the node under AirGapped: remote brick catalogs (<see cref="BrickHostOptions"/>), RunPod
    /// peer-network routing (<see cref="RunPodBrickConfig"/>), the MeshLab worker executor
    /// (<see cref="MeshLabWorkerExecutorOptions"/>) and the Bedrock tier (<see cref="MeaiPipelineOptions"/>). The first
    /// three are <c>IValidateOptions</c>, so they also refuse at the first resolution of the options, in a process
    /// that never starts a host. <see cref="MeaiPipelineOptions"/> is registered as a ready-made instance that the
    /// options factory never builds, so its check is a start-time validator that reads that instance.</para>
    /// <para><b>The ollama.com catalog defaults to off on AirGapped.</b> The default is applied before any binding,
    /// so an explicit <c>Enabled=true</c> still turns it on; the egress guard is the backstop for it.</para>
    /// </remarks>
    private static ResolvedDeploymentProfile RegisterDeploymentProfileHygiene(IServiceCollection services, string profile)
    {
        var resolved = new ResolvedDeploymentProfile(AshlarDeploymentProfileEnvironment.ResolvedRaw ?? profile);
        services.RemoveAll<ResolvedDeploymentProfile>();
        services.AddSingleton(resolved);

        services.AddOptions<BrickHostOptions>().ValidateOnStart();
        services.AddOptions<RunPodBrickConfig>().ValidateOnStart();
        services.AddOptions<MeshLabWorkerExecutorOptions>().ValidateOnStart();
        services.AddOptions<AirGappedBedrockCheck>().ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<BrickHostOptions>, AirGappedOptInValidator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<RunPodBrickConfig>, AirGappedOptInValidator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<MeshLabWorkerExecutorOptions>, AirGappedOptInValidator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<AirGappedBedrockCheck>, AirGappedOptInValidator>());

        if (resolved.IsAirGapped)
        {
            // First in the collection, so it runs before every binding and is only a default.
            services.Insert(0, ServiceDescriptor.Singleton<IConfigureOptions<OllamaRemoteLibraryCatalogOptions>>(
                new ConfigureOptions<OllamaRemoteLibraryCatalogOptions>(static o => o.Enabled = false)));
        }

        return resolved;
    }

    /// <summary>
    /// Refuses, when <c>AddAshlar</c> composes, a Bedrock tier already registered under AirGapped (SPEC-007 PR 4.10).
    /// </summary>
    /// <remarks>
    /// The Bedrock check cannot bind to the options pipeline: <c>AddAshlarMeaiPipeline</c> registers
    /// <see cref="MeaiPipelineOptions"/> as a ready-made <c>IOptions</c> instance that the options factory never builds,
    /// and a process that never starts a host, such as the <c>ashlar</c> CLI, runs no start-time validator at all. So
    /// <c>AddAshlar</c> also reads the collection it is about to return: an <c>IOptions&lt;MeaiPipelineOptions&gt;</c>
    /// instance with <c>Bedrock.Enabled</c>, the kernel's own registration from <c>Ashlar:Meai:Bedrock:Enabled</c>
    /// included, fails the composition with the same message the validator gives. A tier a host registers after
    /// <c>AddAshlar</c> returns is still refused at start by <see cref="AirGappedOptInValidator"/>.
    /// </remarks>
    private static void RefuseBedrockAtComposition(IServiceCollection services, ResolvedDeploymentProfile resolved)
    {
        if (!resolved.IsAirGapped)
        {
            return;
        }

        var enabled = services.Any(d =>
            d.ServiceType == typeof(IOptions<MeaiPipelineOptions>)
            && !d.IsKeyedService
            && d.ImplementationInstance is IOptions<MeaiPipelineOptions> { Value.Bedrock.Enabled: true });
        if (enabled)
        {
            throw new InvalidOperationException(AirGappedOptInValidator.BedrockRefusal);
        }
    }
}

/// <summary>The options type the Bedrock boot check validates through (SPEC-007 PR 4.10). It carries nothing.</summary>
internal sealed class AirGappedBedrockCheck
{
}

/// <summary>
/// The AirGapped boot validators (SPEC-007 PR 4.10, default D35). Each opt-in below sends work or data off the node,
/// so under AirGapped it fails boot with the setting, the reason and the remedy.
/// </summary>
internal sealed class AirGappedOptInValidator :
    IValidateOptions<BrickHostOptions>,
    IValidateOptions<RunPodBrickConfig>,
    IValidateOptions<MeshLabWorkerExecutorOptions>,
    IValidateOptions<AirGappedBedrockCheck>
{
    private readonly ResolvedDeploymentProfile _profile;
    private readonly IServiceProvider _services;

    /// <summary>Takes the profile <c>AddAshlar</c> registered.</summary>
    /// <param name="profile">The resolved profile.</param>
    /// <param name="services">For the Bedrock check, which reads the registered MEAI options instance.</param>
    public AirGappedOptInValidator(ResolvedDeploymentProfile profile, IServiceProvider services)
    {
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _services = services ?? throw new ArgumentNullException(nameof(services));
    }

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, BrickHostOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var remote = (options.RemoteCatalogBaseUrls ?? Array.Empty<string>()).Any(url => !string.IsNullOrWhiteSpace(url));
        return Refuse(remote, $"{BrickHostOptions.SectionName}:{nameof(BrickHostOptions.RemoteCatalogBaseUrls)}",
            "is non-empty", "remote brick catalogs are fetched from, and delegate execution to, other hosts");
    }

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, RunPodBrickConfig options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Refuse(options.EnablePeerNetworkRouting,
            $"{RunPodBrickConfig.SectionName}:{nameof(RunPodBrickConfig.EnablePeerNetworkRouting)}",
            "is true", "peer-network routing sends jobs to other nodes");
    }

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, MeshLabWorkerExecutorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Refuse(options.Enabled,
            $"{MeshLabWorkerExecutorOptions.SectionPath}:{nameof(MeshLabWorkerExecutorOptions.Enabled)}",
            "is true", "the worker executor polls a remote director and calls its peers");
    }

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, AirGappedBedrockCheck options)
    {
        // Every registered instance: the last registration is the one resolved, but an earlier one has already
        // registered the Bedrock tier it enabled.
        var bedrock = _services.GetServices<IOptions<MeaiPipelineOptions>>()
            .Any(o => o?.Value?.Bedrock?.Enabled == true);
        return bedrock && _profile.IsAirGapped ? ValidateOptionsResult.Fail(BedrockRefusal) : ValidateOptionsResult.Success;
    }

    private ValidateOptionsResult Refuse(bool optedIn, string setting, string state, string why) =>
        optedIn && _profile.IsAirGapped
            ? ValidateOptionsResult.Fail(RefusalMessage(setting, state, why))
            : ValidateOptionsResult.Success;

    /// <summary>The message of the Bedrock refusal, at composition and at start alike.</summary>
    internal static string BedrockRefusal { get; } =
        RefusalMessage($"{MeaiPipelineOptions.SectionName}:Bedrock:Enabled", "is true", "the Bedrock tier sends prompts to AWS");

    private static string RefusalMessage(string setting, string state, string why) =>
        $"{setting} {state}, which is not permitted under the AirGapped deployment profile: {why}. " +
        $"Remove the setting, or run under a profile other than AirGapped (SPEC-007 PR 4.10).";
}
