namespace Ashlar.Core.Domain;

/// <summary>
/// Centralized default values for every tunable constant in the Ashlar platform.
/// <para>
/// <b>Design intent:</b> All magic numbers live here so that production code
/// never hard-codes values, and configuration documentation has a single
/// source of truth for "what happens when a setting is omitted."
/// </para>
/// <para>
/// <b>Override precedence</b> (highest → lowest):
/// environment variable → <c>appsettings.json</c> / IConfiguration binding →
/// this class.  See <c>docs/Configuration.md</c> for the full reference.
/// </para>
/// <para>
/// <b>Related files:</b>
/// <c>Ashlar.Hosting.AshlarServiceCollectionExtensions</c> — reads many of these
/// defaults during DI registration;
/// <c>RunPodBrickConfig</c> — RunPod-specific config that falls back to
/// constants here.
/// </para>
/// </summary>
public static class AshlarDefaults
{
    // ── LLM / Provider ────────────────────────────────────────────────

    /// <summary>Number of automatic retries when an LLM call fails with a transient error.
    /// Override: <c>Ashlar:Llm:RetryCount</c>.  Three strikes balances reliability against
    /// latency; exponential back-off is applied by the retry policy, not this constant.</summary>
    public const int LlmRetryCount = 3;

    /// <summary>Default sampling temperature for LLM completions.
    /// Override: <c>Ashlar:Llm:Temperature</c>.  0.2 keeps output mostly
    /// deterministic while still allowing minor variation for code-gen tasks.</summary>
    public const double LlmTemperature = 0.2;

    /// <summary>Maximum token budget per LLM completion.
    /// Override: <c>Ashlar:Llm:MaxTokens</c>.  4 096 is sufficient for most
    /// single-turn brick responses; workflows that need more should set this
    /// per-step via orchestration runtime specs.</summary>
    public const int LlmMaxTokens = 4096;

    /// <summary>Artificial delay (ms) injected by the mock LLM provider to
    /// simulate real-world latency during testing.
    /// Override: <c>Ashlar:Llm:MockDelayMs</c>.</summary>
    public const int MockDelayMs = 30;

    /// <summary>
    /// The offline, no-LLM provider name — the framework's OWN default for anything that may run
    /// without a model behind it (<c>BackgroundAgentConfig.ModelProvider</c>), the sentinel
    /// <c>BackgroundAgentRegistry</c> reads as "this role consumes no LLM", and a first-class
    /// offline route in <c>MeaiBackedModel</c>.
    /// </summary>
    /// <remarks>
    /// It is a constant, and every one of those places spells it by reference, because it was
    /// briefly a literal in four files and absent from a fifth: <c>ProviderFactory.KnownProviders</c>.
    /// A scaffold carrying the framework's own default then CERTIFIED and refused to RUN — "not a
    /// model provider this build knows" — on the same directory. A default that the allow-list does
    /// not contain is not a typo an operator can fix; it is the framework disagreeing with itself,
    /// so the two must not be able to drift apart again.
    /// </remarks>
    public const string DeterministicProviderName = "deterministic";

    // ── OpenAI ────────────────────────────────────────────────────────

    /// <summary>Default OpenAI model identifier.
    /// Override: <c>ASHLAR_OPENAI_MODEL</c> env var or <c>Ashlar:OpenAi:Model</c>.
    /// "gpt-4o-mini" chosen for cost-efficiency in default/demo scenarios.</summary>
    public const string OpenAiDefaultModel = "gpt-4o-mini";

    /// <summary>Default OpenAI chat completions endpoint.
    /// Override: <c>ASHLAR_OPENAI_BASE_URL</c> or <c>Ashlar:OpenAi:BaseUrl</c>.</summary>
    public const string OpenAiDefaultBaseUrl = "https://api.openai.com/v1/chat/completions";

    /// <summary>Default OpenAI model used for vision (image-input) tasks.
    /// Override: <c>Ashlar:OpenAi:VisionModel</c>.</summary>
    public const string OpenAiDefaultVisionModel = "gpt-4o-mini";

    // ── OpenAI-compatible (vLLM, LiteLLM, llama.cpp server, etc.) ─────

    /// <summary>Default model id when <c>provider=openai_compat</c> and <c>OPENAI_COMPAT_MODEL</c> is unset.</summary>
    public const string OpenAiCompatDefaultModel = "default";

    /// <summary>Default vision model when <c>OPENAI_COMPAT_VISION_MODEL</c> and <c>OPENAI_COMPAT_MODEL</c> are unset.</summary>
    public const string OpenAiCompatDefaultVisionModel = "default";

    // ── Azure OpenAI ──────────────────────────────────────────────────

    /// <summary>API version sent in Azure OpenAI requests.
    /// Override: <c>Ashlar:AzureOpenAi:ApiVersion</c>.  Must match a
    /// GA or preview version supported by the Azure endpoint.</summary>
    public const string AzureOpenAiDefaultApiVersion = "2024-06-01";

    // ── Ollama ────────────────────────────────────────────────────────

    /// <summary>Base URL for a local Ollama instance.
    /// Override (highest → lowest): <c>ASHLAR_OLLAMA_BASE_URL</c> →
    /// <c>Ashlar:Meai:OllamaBaseUrl</c> (MEAI path) / <c>Ashlar:NodeCapabilityRuntime:Ollama:BaseUrl</c> (NCR probe) →
    /// legacy <c>OLLAMA_BASE_URL</c> (also the only key the provider-factory path reads).
    /// Defaults to localhost because Ollama typically runs as a local sidecar;
    /// inside a container that is the container itself, so compose stacks must set one of the keys above.</summary>
    public const string OllamaDefaultBaseUrl = "http://localhost:11434";

    /// <summary>Default Ollama text model (tag form matches <c>ollama pull</c> / <c>ollama list</c>).
    /// Override (highest → lowest): <c>ASHLAR_OLLAMA_MODEL</c> → <c>Ashlar:Meai:OllamaModel</c> → legacy <c>OLLAMA_MODEL</c>.</summary>
    public const string OllamaDefaultModel = "llama3.1:latest";

    /// <summary>Default Ollama vision model for image-input tasks.
    /// Override: <c>OLLAMA_VISION_MODEL</c> (provider-factory path).</summary>
    public const string OllamaDefaultVisionModel = "richardyoung/smolvlm2-2.2b-instruct";

    /// <summary>HTTP request timeout (seconds) for Ollama calls.
    /// Override: <c>OLLAMA_TIMEOUT_SECONDS</c> (provider-factory path).  Set high (300 s)
    /// because large-model first-load can be very slow.</summary>
    public const int OllamaDefaultTimeoutSeconds = 300;

    // ── Pipeline ──────────────────────────────────────────────────────

    /// <summary>Maximum retry attempts for a failed pipeline stage before the
    /// stage is marked as permanently failed.
    /// Override: <c>Ashlar:Pipelines:Execution:MaxRetryAttempts</c>.
    /// See also <c>Ashlar.Infrastructure.Pipelines.PipelineExecutionOptions</c>.</summary>
    public const int PipelineMaxRetryAttempts = 3;

    /// <summary>Base delay (ms) between pipeline stage retries.
    /// Override: <c>Ashlar:Pipelines:Execution:RetryDelayMs</c>.
    /// Kept short (100 ms) to avoid holding pipeline threads idle.</summary>
    public const int PipelineRetryDelayMs = 100;

    // ── Configuration / Analysis ──────────────────────────────────────

    /// <summary>Cyclomatic-complexity threshold above which a method is
    /// flagged by the code-quality analysis rule.
    /// Override: <c>Ashlar:Analysis:MaxComplexityThreshold</c>.</summary>
    public const int AnalysisMaxComplexityThreshold = 20;

    /// <summary>Timeout (seconds) for the validation runner.
    /// Override: <c>Ashlar:Validation:TimeoutSeconds</c>.</summary>
    public const int ValidationTimeoutSeconds = 300;

    /// <summary>Name of the Ashlar configuration file looked up in
    /// <see cref="ConfigDirectoryName"/>.  Not user-overridable.</summary>
    public const string ConfigFileName = "config.json";

    /// <summary>Hidden directory name where Ashlar stores repo-level config.
    /// Not user-overridable; changing it would break existing repos.</summary>
    public const string ConfigDirectoryName = ".ashlar";

    // ── Audit / Buffers ───────────────────────────────────────────────

    /// <summary>Maximum entries retained in the sanitization audit ring buffer.
    /// Override: <c>Ashlar:Audit:SanitizationMaxEntries</c>.
    /// 10 000 entries ≈ a few MB; older entries are silently dropped.</summary>
    public const int SanitizationAuditMaxEntries = 10_000;

    /// <summary>Maximum entries in the data-decision audit log.
    /// Override: <c>Ashlar:Audit:DataDecisionMaxEntries</c>.
    /// Higher than sanitization because data decisions are smaller records.</summary>
    public const int DataDecisionAuditMaxEntries = 50_000;

    /// <summary>Per-agent cap on in-memory log entries.
    /// Override: <c>Ashlar:Agents:LogMaxEntriesPerAgent</c>.</summary>
    public const int AgentLogMaxEntriesPerAgent = 1_000;

    // ── Embedding ─────────────────────────────────────────────────────

    /// <summary>Dimensionality for locally-computed embeddings.
    /// Override: <c>Ashlar:Embedding:Dimension</c>.  64 dimensions is a
    /// compact trade-off for the nearest-neighbour lookups used by the
    /// semantic cache; higher dimensions improve recall at memory cost.</summary>
    public const int EmbeddingDefaultDimension = 64;

    // ── RunPod ─────────────────────────────────────────────────────────

    /// <summary>RunPod API base URL.
    /// Override: <c>Ashlar:RunPod:BaseUrl</c>.</summary>
    public const string RunPodDefaultBaseUrl = "https://api.runpod.io";

    /// <summary>Default GPU tier requested when launching RunPod instances.
    /// Override: <c>Ashlar:RunPod:PreferredGpuTier</c>.  A4000 balances
    /// cost and VRAM for medium-sized models.</summary>
    public const string RunPodDefaultGpuTier = "NVIDIA_A4000";

    /// <summary>Maximum time (minutes) to wait for a RunPod job to complete
    /// before timing out.
    /// Override: <c>Ashlar:RunPod:Timeout</c>.</summary>
    public const int RunPodDefaultTimeoutMinutes = 10;

    /// <summary>Interval (seconds) between RunPod job status polls.
    /// Override: <c>Ashlar:RunPod:PollingInterval</c>.</summary>
    public const int RunPodDefaultPollingIntervalSeconds = 2;

    /// <summary>Number of queued jobs at which the router begins considering
    /// alternative execution targets (peer network or a different GPU tier).
    /// Override: <c>Ashlar:RunPod:QueueDepthThreshold</c>.</summary>
    public const int RunPodDefaultQueueDepthThreshold = 4;

    /// <summary>NCR capability identifier used to discover peers that can
    /// accept routed generation jobs.
    /// Override: <c>Ashlar:RunPod:PeerCapabilityId</c>.</summary>
    public const string RunPodDefaultPeerCapabilityId = "generation.capability-routing";

    /// <summary>Brick identifier dispatched to a peer when routing a job
    /// through the peer network.
    /// Override: <c>Ashlar:RunPod:PeerRoutingBrickId</c>.</summary>
    public const string RunPodDefaultPeerRoutingBrickId = "generation.capability-routing";

    /// <summary>Timeout (seconds) for a single peer-to-peer generation request.
    /// Override: <c>Ashlar:RunPod:PeerRequestTimeout</c>.</summary>
    public const int RunPodDefaultPeerRequestTimeoutSeconds = 30;

    /// <summary>Interval (seconds) at which the peer discovery background
    /// task refreshes the known-peers list.
    /// Override: <c>Ashlar:RunPod:PeerDiscoveryInterval</c>.</summary>
    public const int RunPodDefaultPeerDiscoveryIntervalSeconds = 10;

    /// <summary>Default peer trust policy.  "trusted-preferred" means
    /// trusted peers are tried first, but untrusted peers are still allowed
    /// as a fallback.  See <c>RunPodBrickConfig.PeerTrustPolicy</c>.
    /// Override: <c>Ashlar:RunPod:PeerTrustPolicy</c>.</summary>
    public const string RunPodDefaultPeerTrustPolicy = "trusted-preferred";

    // ── Networking ─────────────────────────────────────────────────────

    /// <summary>Interval (seconds) between heartbeat messages on the
    /// network event bus.
    /// Override: <c>Ashlar:NetworkBus:HeartbeatIntervalSeconds</c>.</summary>
    public const int NetworkBusHeartbeatIntervalSeconds = 30;

    /// <summary>Maximum number of events retained in the in-memory event
    /// history ring buffer.
    /// Override: <c>Ashlar:NetworkBus:MaxEventHistory</c>.</summary>
    public const int NetworkBusMaxEventHistory = 10_000;

    /// <summary>Maximum hop count for multi-hop event propagation.
    /// Override: <c>Ashlar:NetworkBus:DefaultMaxHops</c>.  Kept low (3)
    /// to prevent broadcast storms in large meshes.</summary>
    public const int NetworkBusDefaultMaxHops = 3;

    // ── Brick Usage ────────────────────────────────────────────────────

    /// <summary>Maximum entries in the brick-usage tracker ring buffer.
    /// Override: <c>Ashlar:BrickUsage:MaxEntries</c>.</summary>
    public const int BrickUsageTrackerMaxEntries = 10_000;

    /// <summary>Rolling window (seconds) used to compute hourly usage rates.
    /// Override: <c>Ashlar:BrickUsage:RollingWindowSeconds</c>.  3 600 s = 1 hour.</summary>
    public const int BrickUsageTrackerRollingHourWindowSeconds = 3600;

    // ── Video ──────────────────────────────────────────────────────────

    /// <summary>Default frames-per-second for video capture/generation tasks.
    /// Override: <c>Ashlar:Video:Fps</c>.  5 fps is a deliberate compromise
    /// between file size and visual smoothness for screen-recording bricks.</summary>
    public const int VideoDefaultFps = 5;

    // ── Routing ────────────────────────────────────────────────────────

    /// <summary>Interval (seconds) between health-check probes sent to
    /// registered execution targets.
    /// Override: <c>Ashlar:Routing:HealthCheckIntervalSeconds</c>.</summary>
    public const int RoutingHealthCheckIntervalSeconds = 30;
}
