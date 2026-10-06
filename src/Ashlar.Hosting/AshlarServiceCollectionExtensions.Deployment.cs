namespace Ashlar.Hosting;

public static partial class AshlarServiceCollectionExtensions
{
    /// <summary>
    /// Resolves the deployment profile from (in priority order):
    /// 1. Explicit <see cref="AshlarHostingOptions.DeploymentProfile"/> set by the caller.
    /// 2. <c>ASHLAR_DEPLOYMENT_PROFILE</c> environment variable (case-insensitive;
    ///    accepts "full", "server", "edge", "airgapped"/"air-gapped",
    ///    "secureworkstation"/"secure-workstation"/"workstation", "system"/"core").
    /// 3. Falls back to <see cref="AshlarDeploymentProfile.Full"/>.
    /// </summary>
    /// <param name="options">The caller's options.</param>
    /// <param name="defaulted"><see langword="true"/> when nothing set the profile, so it is
    /// <see cref="AshlarDeploymentProfile.Full"/> by default.</param>
    private static AshlarDeploymentProfile ResolveDeploymentProfile(AshlarHostingOptions options, out bool defaulted)
    {
        defaulted = false;
        if (options.DeploymentProfile.HasValue)
        {
            return options.DeploymentProfile.Value;
        }

        var raw = Environment.GetEnvironmentVariable("ASHLAR_DEPLOYMENT_PROFILE");
        if (string.IsNullOrWhiteSpace(raw))
        {
            defaulted = true;
            return AshlarDeploymentProfile.Full;
        }

        if (TryParseDeploymentProfile(raw, out var parsed))
        {
            return parsed;
        }

        throw new InvalidOperationException(
            $"ASHLAR_DEPLOYMENT_PROFILE='{raw}' is not recognized. " +
            "Valid values: full, server, edge, air-gapped, air_gapped, secure-workstation, secure_workstation, workstation, system.");
    }

    private static bool TryParseDeploymentProfile(string? raw, out AshlarDeploymentProfile profile)
    {
        profile = AshlarDeploymentProfile.Full;
        if (!AshlarDeploymentProfileEnvironment.TryParseKnown(raw, out var canonical))
        {
            return false;
        }

        profile = canonical switch
        {
            "full" => AshlarDeploymentProfile.Full,
            "server" => AshlarDeploymentProfile.Server,
            "edge" => AshlarDeploymentProfile.Edge,
            "air-gapped" => AshlarDeploymentProfile.AirGapped,
            "system" => AshlarDeploymentProfile.System,
            "secure-workstation" => AshlarDeploymentProfile.SecureWorkstation,
            _ => AshlarDeploymentProfile.Full
        };
        return true;
    }

    /// <summary>
    /// Maps a deployment profile to the set of subsystem modules that should
    /// be registered.  The peeling order (Full → Server → Edge → AirGapped
    /// → System) progressively strips capabilities:
    /// <list type="bullet">
    ///   <item><c>Full</c>     — everything; used in development &amp; CI.</item>
    ///   <item><c>Server</c>   — same as Full (reserved for future server-specific gating).</item>
    ///   <item><c>Edge</c>     — persistence + pipelines only; no NCR, no agents.</item>
    ///   <item><c>AirGapped</c>— NCR + adaptation + persistence; no network transport.</item>
    ///   <item><c>System</c>   — bare minimum for CLI tooling; nothing optional.</item>
    ///   <item><c>SecureWorkstation</c> — local trust/agents/RAG/observation; no transport egress.</item>
    /// </list>
    /// </summary>
    private static ModuleSelection GetModuleSelection(AshlarDeploymentProfile profile)
    {
        return profile switch
        {
            AshlarDeploymentProfile.Full => new ModuleSelection(
                IncludeNodeCapabilityRuntime: true,
                IncludeRuntimeTransport: true,
                IncludePersistence: true,
                IncludeAdaptation: true,
                IncludePipelineComposition: true,
                IncludeBackgroundAgents: true,
                IncludeBackgroundAgentRag: true,
                IncludeObservationPipeline: true,
                IncludeTrustServices: true,
                IncludeWorkflowIntegrations: true,
                IncludeTestingAdapters: true),
            AshlarDeploymentProfile.Server => new ModuleSelection(
                IncludeNodeCapabilityRuntime: true,
                IncludeRuntimeTransport: true,
                IncludePersistence: true,
                IncludeAdaptation: true,
                IncludePipelineComposition: true,
                IncludeBackgroundAgents: true,
                IncludeBackgroundAgentRag: true,
                IncludeObservationPipeline: true,
                IncludeTrustServices: true,
                IncludeWorkflowIntegrations: true,
                IncludeTestingAdapters: true),
            AshlarDeploymentProfile.Edge => new ModuleSelection(
                IncludeNodeCapabilityRuntime: false,
                IncludeRuntimeTransport: false,
                IncludePersistence: true,
                IncludeAdaptation: false,
                IncludePipelineComposition: true,
                IncludeBackgroundAgents: false,
                IncludeBackgroundAgentRag: false,
                IncludeObservationPipeline: false,
                IncludeTrustServices: false,
                IncludeWorkflowIntegrations: false,
                IncludeTestingAdapters: false),
            AshlarDeploymentProfile.AirGapped => new ModuleSelection(
                IncludeNodeCapabilityRuntime: true,
                IncludeRuntimeTransport: false,
                IncludePersistence: true,
                IncludeAdaptation: true,
                IncludePipelineComposition: true,
                IncludeBackgroundAgents: false,
                IncludeBackgroundAgentRag: false,
                IncludeObservationPipeline: false,
                IncludeTrustServices: false,
                IncludeWorkflowIntegrations: false,
                IncludeTestingAdapters: false),
            AshlarDeploymentProfile.System => new ModuleSelection(
                IncludeNodeCapabilityRuntime: false,
                IncludeRuntimeTransport: false,
                IncludePersistence: false,
                IncludeAdaptation: false,
                IncludePipelineComposition: false,
                IncludeBackgroundAgents: false,
                IncludeBackgroundAgentRag: false,
                IncludeObservationPipeline: false,
                IncludeTrustServices: false,
                IncludeWorkflowIntegrations: false,
                IncludeTestingAdapters: false),
            AshlarDeploymentProfile.SecureWorkstation => new ModuleSelection(
                IncludeNodeCapabilityRuntime: true,
                IncludeRuntimeTransport: false,
                IncludePersistence: true,
                IncludeAdaptation: true,
                IncludePipelineComposition: true,
                IncludeBackgroundAgents: true,
                IncludeBackgroundAgentRag: true,
                IncludeObservationPipeline: true,
                IncludeTrustServices: true,
                IncludeWorkflowIntegrations: true,
                IncludeTestingAdapters: false),
            _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, "Unknown Ashlar deployment profile.")
        };
    }

    /// <summary>
    /// Applies the <c>ASHLAR_STRICT_MODE</c> ("1" / "true") environment variable
    /// when the caller has not already enabled strict mode programmatically.
    /// Strict mode turns configuration warnings into hard failures — intended
    /// for CI gates where misconfiguration should break the build.
    /// </summary>
    private static void ResolveStrictMode(AshlarHostingOptions options)
    {
        if (!options.StrictMode.Enabled)
        {
            options.StrictMode.Enabled = ParseBooleanEnvironmentVariable("ASHLAR_STRICT_MODE");
        }
    }

    internal static bool ParseBooleanEnvironmentVariable(string key)
    {
        var value = Environment.GetEnvironmentVariable(key);
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }
}
