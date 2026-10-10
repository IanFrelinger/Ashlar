// Namespace is deliberately Ashlar.Hosting and NOT Ashlar.Hosting.Sdk.Options, even
// though this file sits under Sdk/Options/. This type is the public surface of
// AddAshlar(); its namespace is the contract consumers bind against, and the
// folder layout is organisation only. #96 moved the file here and kept the
// namespace on purpose. Do not let a folder-matches-namespace sweep "fix" it.
namespace Ashlar.Hosting;

/// <summary>
/// Options for configuring the Ashlar kernel when using AddAshlar().
/// </summary>
public sealed class AshlarHostingOptions
{
    /// <summary>
    /// Optional dependency profile that controls which non-core modules are registered.
    /// Defaults to <see cref="AshlarDeploymentProfile.Full"/> unless overridden by
    /// ASHLAR_DEPLOYMENT_PROFILE.
    /// </summary>
    public AshlarDeploymentProfile? DeploymentProfile { get; set; }

    /// <summary>
    /// Raises the egress guard's mode to <c>enforce</c> for the rest of the process (SPEC-007 PR 4). It can only
    /// raise the mode: <c>enforce</c> (trimmed, any case) raises it, and so does any other non-blank value, which
    /// fails closed; <c>report</c> and <see langword="null"/> change nothing. The raise reaches
    /// <c>EgressGuard.ProcessDefault</c>, every guard built without a profile (whatever override it was built with),
    /// and the guard this <c>AddAshlar</c> and every later one bind, which on the same service collection replaces
    /// the guard an earlier <c>AddAshlar</c> bound there. A container built before the raise and a guard bound into
    /// another collection keep the mode they were built with, like any guard built with a profile. A host's own
    /// <c>IEgressGuard</c> is kept, not replaced, so the raise reaches it only if it is a guard built without a
    /// profile. Ashlar never binds this from <c>IConfiguration</c>, so an appsettings file or a command-line argument
    /// cannot set it.
    /// <para>AirGapped and SecureWorkstation enforce by default. Only the environment can request
    /// SecureWorkstation's latched report break-glass; AirGapped ignores lowering overrides. Routes retain
    /// the AirGapped process floor even when a host guard reports.</para>
    /// </summary>
    public string? EgressMode { get; set; }

    /// <summary>
    /// Path to the configuration file (default: ~/.ashlar/config.json).
    /// </summary>
    public string? ConfigPath { get; set; }

    /// <summary>
    /// Path to the pattern store for observation/adaptation (optional).
    /// When set, enables observation context and pattern-based adaptation.
    /// </summary>
    public string? PatternStorePath { get; set; }

    /// <summary>
    /// When true, enables Trust &amp; Information Architecture (sanitization, audit).
    /// Default: from ASHLAR_TRUST_ENABLED env var, or false.
    /// </summary>
    public bool? TrustEnabled { get; set; }

    /// <summary>
    /// When true, registers the Microsoft.Extensions.AI governed chat pipeline
    /// (keyed <c>IChatClient</c> targets) and wires <c>IModel</c> through <c>MeaiBackedModel</c>.
    /// Default: ON (Phase 6). Opt out via <c>Ashlar:UseMeaiPipeline=false</c>
    /// / <c>ASHLAR_USE_MEAI_PIPELINE=0</c> to restore the legacy <c>IProviderFactory</c> <c>IModel</c> leaf.
    /// </summary>
    public bool? UseMeaiPipeline { get; set; }

    /// <summary>
    /// When true, registers background agents as IHostedService (for long-running hosts).
    /// Default: false (CLI mode; agents run on-demand).
    /// </summary>
    public bool RegisterBackgroundAgentHostedService { get; set; }

    /// <summary>
    /// When true, disables the observation pipeline (pattern store, event sources, ObservationPipelineService).
    /// Default: false — observation pipeline is registered by default. Set true for lightweight/CLI-only hosts.
    /// </summary>
    public bool DisableObservationPipeline { get; set; }

    /// <summary>
    /// When true, observation pipeline store errors are fail-open (logged and skipped) instead of stopping the host.
    /// Default: null (resolved from ASHLAR_OBSERVATION_FAIL_OPEN env var, then false).
    /// </summary>
    public bool? ObservationFailOpen { get; set; }

    /// <summary>
    /// Base URL for the Ashlar API when used as a remote client (e.g. for mobile thin client).
    /// </summary>
    public string? ApiBaseUrl { get; set; }

    /// <summary>
    /// Optional API key for authentication when connecting to Ashlar API.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// When true, enables adaptive load balancing (edge/server routing via ASHLAR_LOAD_PREFERENCE).
    /// Default: true when ASHLAR_LOAD_PREFERENCE is set (edge|server|auto).
    /// </summary>
    public bool? UseAdaptiveLoadBalancing { get; set; }

    /// <summary>
    /// When set, IExecutionPlatform uses RemoteExecutionPlatform delegating to this Ashlar API URL.
    /// Default: from ASHLAR_EXECUTION_REMOTE_URL. Use when Docker is not available (e.g. mobile, CI).
    /// </summary>
    public string? ExecutionRemoteUrl { get; set; }

    /// <summary>
    /// Strict mode configuration. When enabled, the system fails fast with verbose
    /// diagnostics during development. Set <c>ASHLAR_STRICT_MODE=1</c> or configure
    /// individual sub-flags. Flip to permissive (disabled) for production.
    /// Default: resolved from <c>ASHLAR_STRICT_MODE</c> env var, or disabled.
    /// </summary>
    public StrictModeOptions StrictMode { get; set; } = new();
}
