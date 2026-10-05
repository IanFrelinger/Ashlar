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
    /// Raises the egress guard's mode to <c>enforce</c> for the whole process (SPEC-007 PR 4). It can only raise
    /// the mode: <c>enforce</c> (trimmed, any case) raises it, and so does any other non-blank value, which fails
    /// closed; <c>report</c> and <see langword="null"/> change nothing, so a lower mode comes only from the
    /// <c>ASHLAR_EGRESS_MODE</c> environment variable. Ashlar never binds this from <c>IConfiguration</c>, so an
    /// appsettings file or a command-line argument cannot set it.
    /// <para>Not yet a supported setting: until SPEC-007 PR 4.7 no route acts on the mode, so an <c>enforce</c>
    /// decision record says what would have been refused, not that a send stopped.</para>
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
