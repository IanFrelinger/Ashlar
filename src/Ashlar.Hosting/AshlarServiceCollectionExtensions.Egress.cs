using Ashlar.Abstractions.Security.Egress;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ashlar.Hosting;

public static partial class AshlarServiceCollectionExtensions
{
    /// <summary>
    /// The composition binding of the egress mode (SPEC-007 PR 4.6, default D1): the container's
    /// <see cref="IEgressGuard"/> becomes a guard built with the strictest profile noted in the process, which
    /// includes the one this <c>AddAshlar</c> resolved, and the process's mode override, so factory clients and MEAI
    /// targets decide under the composed profile. It also registers the startup line, for the same profile.
    /// </summary>
    /// <remarks>
    /// <para><b>Only a <see cref="EgressGuard.ProcessDefault"/> registration is replaced.</b>
    /// <c>AddAshlarEgressGuard</c> registers <see cref="EgressGuard.ProcessDefault"/> with <c>TryAdd</c>, and several
    /// public members call it, so it may already have run before <c>AddAshlar</c> (RunPod routing, the node
    /// capability runtime, the model-artifact catalog, MeshLab). Every descriptor whose instance is
    /// <see cref="EgressGuard.ProcessDefault"/> is replaced in place; any other registration is the host's own guard
    /// and is kept.</para>
    /// <para><b>The strictest profile noted wins here too</b> (default D5). The caller notes its profile first, so
    /// after <c>AddAshlar(AirGapped)</c> a later <c>AddAshlar</c> that resolves Full composes an AirGapped guard and
    /// logs an AirGapped line, as <see cref="EgressGuard.ProcessDefault"/> and the remote-protocol validators already
    /// decide. The line says the profile was defaulted only when the composed profile is this call's own default.</para>
    /// <para><b>The guard never reads the environment.</b> It is built with an explicit profile and override, so a
    /// later change to <c>ASHLAR_EGRESS_MODE</c> or <c>ASHLAR_DEPLOYMENT_PROFILE</c> does not change it, and neither
    /// does a later <c>AddAshlar</c> that notes a stricter profile or raises the mode: a guard keeps what it was
    /// composed with.</para>
    /// <para><b>The startup line.</b> <c>AddAshlar</c> has no logger, so a hosted activator logs the line when a host
    /// starts: at Warning when an override is unrecognised, at Information otherwise. A host-less CLI verb never
    /// starts a host, so a mode other than plain report also goes to standard error, once per process.</para>
    /// </remarks>
    private static void BindComposedEgressGuard(
        IServiceCollection services,
        string profile,
        bool profileDefaulted,
        string? egressOverride,
        string? hostingOption)
    {
        // The strictest profile noted in the process, which the caller has just noted its own profile into.
        var composedProfile = AshlarDeploymentProfileEnvironment.ResolvedRaw ?? profile;
        var defaulted = profileDefaulted && string.Equals(composedProfile, profile, StringComparison.Ordinal);
        var composed = new EgressGuard(composedProfile, egressOverride);
        for (var i = 0; i < services.Count; i++)
        {
            var descriptor = services[i];
            if (!descriptor.IsKeyedService
                && descriptor.ServiceType == typeof(IEgressGuard)
                && ReferenceEquals(descriptor.ImplementationInstance, EgressGuard.ProcessDefault))
            {
                services[i] = ServiceDescriptor.Singleton<IEgressGuard>(composed);
            }
        }

        var (mode, modeBasis) = EgressEnforcement.ResolveMode(composedProfile, egressOverride);
        var overrideUnrecognised = EgressEnforcement.EnvironmentOverrideUnrecognised()
            || EgressEnforcement.ParseOverride(hostingOption) == EgressEnforcement.OverrideKind.Unrecognised;
        EgressEnforcement.AnnounceOnce(mode, modeBasis, composedProfile, defaulted, overrideUnrecognised);

        services.RemoveAll<EgressModeStartup>();
        services.AddSingleton(new EgressModeStartup(mode, modeBasis, composedProfile, defaulted, overrideUnrecognised));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, EgressModeStartupActivator>());
    }
}

/// <summary>The egress mode a composition resolved, for the startup line (SPEC-007 PR 4.6).</summary>
/// <param name="Mode"><c>report</c> or <c>enforce</c>.</param>
/// <param name="ModeBasis">What decided the mode.</param>
/// <param name="Profile">The canonical profile the container's guard composed: the strictest noted in the process.</param>
/// <param name="ProfileDefaulted"><see langword="true"/> when nothing set the profile and no stricter one was noted.</param>
/// <param name="OverrideUnrecognised"><see langword="true"/> when <c>ASHLAR_EGRESS_MODE</c> or
/// <c>AshlarHostingOptions.EgressMode</c> is neither <c>report</c> nor <c>enforce</c>.</param>
internal sealed record EgressModeStartup(
    string Mode,
    string ModeBasis,
    string Profile,
    bool ProfileDefaulted,
    bool OverrideUnrecognised)
{
    /// <summary>The line, as <see cref="EgressEnforcement.StartupLine"/> writes it.</summary>
    internal string Line => EgressEnforcement.StartupLine(Mode, ModeBasis, Profile, ProfileDefaulted, OverrideUnrecognised);

    /// <summary>
    /// A break-glass, an ignored override and an unrecognised value are logged at Warning; anything else at
    /// Information.
    /// </summary>
    internal LogLevel Level =>
        OverrideUnrecognised
        || string.Equals(ModeBasis, EgressEnforcement.BreakGlassBasis, StringComparison.Ordinal)
        || string.Equals(ModeBasis, EgressEnforcement.OverrideIgnoredBasis, StringComparison.Ordinal)
            ? LogLevel.Warning
            : LogLevel.Information;
}

/// <summary>
/// Logs the egress mode line once when a host starts (SPEC-007 PR 4.6): category <c>Ashlar.Egress</c>, event
/// <c>7302 EgressMode</c>. It follows the <c>EgressDecisionLoggerActivator</c> pattern and does nothing else.
/// </summary>
internal sealed class EgressModeStartupActivator : IHostedService
{
    /// <summary>The logger category, shared with the decision records.</summary>
    internal const string CategoryName = "Ashlar.Egress";

    /// <summary>The event id's number.</summary>
    internal const int EventIdValue = 7302;

    /// <summary>The event id's name.</summary>
    internal const string EventName = "EgressMode";

    private static readonly EventId StartupEventId = new(EventIdValue, EventName);

    private readonly EgressModeStartup _startup;
    private readonly ILogger _logger;

    /// <summary>Takes the resolved mode and a logger factory.</summary>
    /// <param name="startup">What the composition resolved.</param>
    /// <param name="loggerFactory">The logger factory.</param>
    public EgressModeStartupActivator(EgressModeStartup startup, ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(startup);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _startup = startup;
        _logger = loggerFactory.CreateLogger(CategoryName);
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.Log(_startup.Level, StartupEventId, "{EgressModeLine}", _startup.Line);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
