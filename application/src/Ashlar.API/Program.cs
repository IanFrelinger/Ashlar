// ──────────────────────────────────────────────────────────────────────────────
// Ashlar.API — Self-hosted API + SPA portal host
//
// This is the HTTP entry-point for Ashlar. It serves:
//   1. A REST API (mapped via MapAshlarEndpoints) for orchestration, analysis,
//      configuration, and background-agent management.
//   2. A static SPA portal (wwwroot/index.html) via UseStaticFiles +
//      MapFallbackToFile, so the UI is co-located with the API.
//
// Startup sequence:
//   • Optional agent config merge — if ASHLAR_BACKGROUND_AGENTS_CONFIG is set,
//     the JSON file is merged into the host configuration so that background-
//     agent definitions (schedules, models, policies) can live in a separate
//     file managed independently of appsettings.json.
//   • DI registration — AddAshlar (shared with the CLI host) registers all
//     kernel services. The key difference from the CLI: the API sets
//     RegisterBackgroundAgentHostedService = true, which causes background
//     agents to run as IHostedService instances inside this process rather
//     than in a standalone daemon.
//   • Security — AshlarSecurityOptions controls an exposure profile
//     (Public / Lan / Tailnet / Localhost). It does NOT enforce network policy,
//     but any off-loopback profile with AuthorizationMode=None refuses to start
//     unless AllowUnauthenticatedNetworkExposure=true is set explicitly.
//     Optional built-in auth modes (ApiKey, Bearer, Basic, and composite
//     OR-modes) protect mutating endpoints via UseAshlarApiKeyAuth middleware.
//   • Remote execution — /api/execution/* (container build/run for
//     RemoteExecutionPlatform) is mapped only when
//     Ashlar:Execution:ServeRemoteExecution=true and refuses AuthorizationMode=None.
//   • Mesh correlation — UseAshlarMeshCorrelation assigns / echoes X-Ashlar-Correlation-Id
//     for /api/mesh and brick execute (Phase 3).
//   • MeshSecurityOptions — optional Ashlar:Security:Mesh tokens, body size cap,
//     and rate limits for /api/mesh and POST /api/bricks/*/execute (Phase 2);
//     UseAshlarMeshSecurity runs before UseAshlarApiKeyAuth.
//   • Static files + endpoints — DefaultFiles/StaticFiles serve the SPA;
//     MapAshlarEndpoints wires the API; MapFallbackToFile routes unknown paths
//     to index.html for client-side routing.
//   • Observability — console logging is human-readable by default and switches
//     to JSON lines with Ashlar:Logging:Json=true / ASHLAR_LOG_JSON=1. Metrics stay
//     in-process (MemoryMetricsCollector) unless OTEL_EXPORTER_OTLP_ENDPOINT is
//     set, in which case AddAshlarOpenTelemetry exports the "Ashlar" meter plus
//     ASP.NET Core / HttpClient traces and metrics over OTLP.
//
// Environment variables consumed here:
//   ASHLAR_BACKGROUND_AGENTS_CONFIG — path to agent-definition JSON file
//   ASHLAR_LOG_JSON                 — 1/true = JSON console logging (also Ashlar:Logging:Json)
//   OTEL_EXPORTER_OTLP_ENDPOINT   — when set, enables OTLP trace + metric export
//                                   (OTEL_SERVICE_NAME etc. are honoured by the OTel SDK)
//   (see also AshlarSecurityOptions for auth-related env vars)
// ──────────────────────────────────────────────────────────────────────────────

using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MediatR;
using Microsoft.OpenApi.Models;
using Ashlar.API.Endpoints;
using Ashlar.API.Middleware.Ingress;
using Ashlar.API.Security;
using Ashlar.Core.Application.Middleware.Ports;
using Ashlar.Core.Application.Product.Ports;
using Ashlar.Infrastructure.Product;
using Ashlar.Infrastructure.Egress;
using Ashlar.Contracts;
using Ashlar.BackgroundAgents.Extending;
using Ashlar.BackgroundAgents.HostRunners;
using Ashlar.BackgroundAgents.Optimization;
using Ashlar.BackgroundAgents.Testing;
using Ashlar.Abstractions;
using Ashlar.Abstractions.Security.Egress;
using Ashlar.API.Protocols;
using Ashlar.Hosting;
using Ashlar.Hosting.Sdk.Extensions;
using Ashlar.Ingress.AwsSns;
using Ashlar.Ingress.DynamoDb;
using Ashlar.Mcp.Client;
using Ashlar.Mcp.Server;
using Ashlar.Runtime;
using Ashlar.Tools.Dev;
using Ashlar.Transport.A2A;
using Ashlar.Transport.A2A.Server;
using Ashlar.Transport.Grpc;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

// --- Optional agent config merge ---
var agentsConfigPath = Environment.GetEnvironmentVariable("ASHLAR_BACKGROUND_AGENTS_CONFIG");
if (!string.IsNullOrWhiteSpace(agentsConfigPath))
{
    var raw = agentsConfigPath.Trim();
    var resolved = Path.IsPathRooted(raw)
        ? raw
        : Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, raw));
    if (!File.Exists(resolved))
    {
        throw new InvalidOperationException(
            $"ASHLAR_BACKGROUND_AGENTS_CONFIG file not found: {resolved}");
    }

    builder.Configuration.AddJsonFile(resolved, optional: false, reloadOnChange: true);
}

// --- Logging: console by default; JSON lines when Ashlar:Logging:Json=true or ASHLAR_LOG_JSON=1 ---
// Read through builder.Configuration (not the kernel's env-only options binding) so the key
// works from appsettings, environment variables and UseSetting alike.
builder.Services.AddLogging(b => b.AddConsole().AddAshlarJsonConsoleIfRequested(builder.Configuration));
builder.Services.AddHttpContextAccessor();
builder.Services.Configure<GrpcTransportOptions>(
    builder.Configuration.GetSection("Ashlar:GrpcTransport"));
builder.Services.Configure<AshlarSecurityOptions>(
    builder.Configuration.GetSection(AshlarSecurityOptions.SectionPath));
builder.Services.Configure<AshlarExecutionOptions>(
    builder.Configuration.GetSection(AshlarExecutionOptions.SectionPath));
builder.Services.Configure<AshlarProductOptions>(
    builder.Configuration.GetSection(AshlarProductOptions.SectionPath));
builder.Services.Configure<AshlarEntitlementsOptions>(
    builder.Configuration.GetSection(AshlarEntitlementsOptions.SectionPath));
builder.Services.Configure<AshlarPrivateLicenseOptions>(
    builder.Configuration.GetSection(AshlarPrivateLicenseOptions.SectionPath));
builder.Services.AddSingleton<IPrivateLicenseValidator, PrivateLicenseValidator>();
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<ICopilotSubmissionQuota, CopilotSubmissionQuota>();
builder.Services.AddSingleton<ITenantUsageStore, InMemoryTenantUsageStore>();
builder.Services.AddSingleton<IOrganizationStore, InMemoryOrganizationStore>();
builder.Services.Configure<MeshSecurityOptions>(
    builder.Configuration.GetSection(MeshSecurityOptions.SectionPath));
builder.Services.Configure<SmsIngressDynamoDbOptions>(
    builder.Configuration.GetSection(SmsIngressDynamoDbOptions.SectionPath));
builder.Services.AddOptions<AshlarMiddlewareIngressOptions>()
    .Bind(builder.Configuration.GetSection(AshlarMiddlewareIngressOptions.SectionPath))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<AshlarMiddlewareIngressOptions>, ValidateAshlarMiddlewareIngressOptions>();

var smsIngressPreview = builder.Configuration.GetSection(AshlarMiddlewareIngressOptions.SectionPath)
    .Get<AshlarMiddlewareIngressOptions>() ?? new AshlarMiddlewareIngressOptions();
if (string.Equals(smsIngressPreview.SmsIngressApprovalStore, SmsIngressApprovalStoreKind.DynamoDb, StringComparison.OrdinalIgnoreCase))
    builder.Services.AddDynamoDbSmsIngressApprovalStore();
else
    builder.Services.AddSingleton<ISmsIngressApprovalStore, MemorySmsIngressApprovalStore>();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(static options =>
    options.SwaggerDoc("v1", new OpenApiInfo { Title = "Ashlar.API", Version = "v1" }));
builder.Services.AddAshlarRuntimeRouting(builder.Configuration);

// --- Agent-protocol adapters (MCP + A2A) ---
// All four directions are feature-flagged off by default (Ashlar:Mcp:*, Ashlar:A2A:*); registering
// them here only makes the surfaces *available*. AddAshlarA2ATransport must run before AddAshlar so
// its scheme registration participates in the kernel's remote-transport composition.
builder.Services.AddAshlarMcpServer(builder.Configuration).WithHttpTransport();
builder.Services.AddAshlarMcpClient(builder.Configuration);
builder.Services.AddAshlarA2AServer(builder.Configuration);
builder.Services.AddAshlarA2ATransport(builder.Configuration);
builder.Services.AddSingleton<IAshlarA2AAgentCatalog, AgentRegistryA2ACatalog>();
// Mirror of the A2A card-anonymity decision for the auth middleware (see
// AshlarProtocolIngressOptions) — bound from the same section so there is a single source key.
builder.Services.Configure<AshlarProtocolIngressOptions>(
    builder.Configuration.GetSection(AshlarA2AServerOptions.SectionPath));
// Read-only repo tools available for MCP allowlisting (exposure still requires
// Ashlar:Mcp:Server:ExposedToolIds entries); mutating tools are deliberately not pre-registered.
builder.Services.AddSingleton<ITool, RepoFsReadTool>();
builder.Services.AddSingleton<ITool, RepoFsListTool>();

builder.Services.AddSingleton<IAshlarIngressAccessor, HttpAshlarIngressAccessor>();
builder.Services.AddHttpClient("ashlar-sns-signing", c => c.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddSingleton<ISnsSignatureVerifier, SnsRsaSignatureVerifier>();
builder.Services.AddRateLimiter(static o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.OnRejected = static (_, _) => ValueTask.CompletedTask;
    o.AddPolicy<string>("ashlar-sms-ingress-posts", static httpContext =>
    {
        var opts = httpContext.RequestServices.GetRequiredService<IOptionsMonitor<AshlarMiddlewareIngressOptions>>().CurrentValue;
        if (opts.IngressSmsPostRateLimitPermitLimit <= 0)
        {
            return RateLimitPartition.GetFixedWindowLimiter(
                "off",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = int.MaxValue,
                    Window = TimeSpan.FromDays(1),
                    QueueLimit = 0,
                    AutoReplenishment = true,
                });
        }

        var key = httpContext.Connection.RemoteIpAddress?.ToString() ?? "anonymous";
        var windowSeconds = opts.IngressSmsPostRateLimitWindowSeconds > 0 ? opts.IngressSmsPostRateLimitWindowSeconds : 60;
        return RateLimitPartition.GetFixedWindowLimiter(
            key,
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = opts.IngressSmsPostRateLimitPermitLimit,
                Window = TimeSpan.FromSeconds(windowSeconds),
                QueueLimit = 0,
                AutoReplenishment = true,
            });
    });
    o.AddPolicy<string>("ashlar-mcp", static httpContext =>
    {
        var opts = httpContext.RequestServices.GetRequiredService<IOptionsMonitor<AshlarMiddlewareIngressOptions>>().CurrentValue;
        return BuildPerIpFixedWindowPartition(httpContext, opts.McpRateLimitPermitLimit, opts.McpRateLimitWindowSeconds);
    });
    o.AddPolicy<string>("ashlar-a2a", static httpContext =>
    {
        var opts = httpContext.RequestServices.GetRequiredService<IOptionsMonitor<AshlarMiddlewareIngressOptions>>().CurrentValue;
        return BuildPerIpFixedWindowPartition(httpContext, opts.A2ARateLimitPermitLimit, opts.A2ARateLimitWindowSeconds);
    });
});

// Planner / optimizer / tester background agents need the same runners as `ashlar background-agent daemon`.
builder.Services.TryAddSingleton<ICodeAnalysisRunner, CodeAnalysisRunnerAdapter>();
builder.Services.TryAddSingleton<ITestRunRunner, TestRunRunnerAdapter>();
builder.Services.TryAddSingleton<SelfExtendRunnerAdapter>();
builder.Services.TryAddSingleton<ISelfExtendRunner>(sp =>
    sp.GetRequiredService<SelfExtendRunnerAdapter>());

builder.Services.AddAshlar(options =>
{
    options.PatternStorePath = builder.Configuration["Ashlar:PatternStorePath"];
    options.RegisterBackgroundAgentHostedService =
        builder.Configuration.GetValue("Ashlar:RegisterBackgroundAgentHostedService", defaultValue: true);
});
// SPEC-007: AddAshlar has already installed the report-only egress guard on every IHttpClientFactory client in this
// host, ashlar-sns-signing above included. This call is idempotent and adds nothing; it states the coverage here.
builder.Services.AddAshlarEgressGuard();

builder.Services.AddMediatR(cfg =>
    cfg.RegisterServicesFromAssembly(typeof(RecordSmsYesApprovalCommand).Assembly));

// --- Observability: OTLP export (traces + metrics), enabled only by the standard OTel endpoint var ---
// Must run after AddAshlar: AddAshlarOpenTelemetry replaces the in-process MemoryMetricsCollector
// (the default when the endpoint is unset) with the OpenTelemetry-backed collector, whose "Ashlar"
// meter carries the ncr.* / ashlar.* keys as attributes on ashlar.operation.duration / .count.
// The exporter batches in the background, so an unreachable endpoint never fails startup.
var otlpEndpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
if (!string.IsNullOrWhiteSpace(otlpEndpoint))
{
    // SPEC-007 EG-TEL-01, report-only: decided once, at registration; the SDK owns the batch export after this.
    // The name overload, never new Uri(...): a malformed endpoint must not fail startup here.
    _ = EgressGuard.ProcessDefault.Evaluate(new EgressRequest(EgressFamilies.Telemetry, "EG-TEL-01", otlpEndpoint));
    var otelServiceName = builder.Configuration["OTEL_SERVICE_NAME"];
    builder.Services.AddAshlarOpenTelemetry(metrics => metrics
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddOtlpExporter());
    builder.Services.AddOpenTelemetry()
        .ConfigureResource(resource => resource.AddService(
            serviceName: string.IsNullOrWhiteSpace(otelServiceName) ? "Ashlar.API" : otelServiceName))
        .WithTracing(tracing => tracing
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddOtlpExporter());
}

// SPEC-007 PR 4.10 (owner decision Q6): on AirGapped and SecureWorkstation, a non-loopback address Kestrel bound fails
// the start. The configuration check after Build refuses the configured ones before anything binds.
builder.Services.AddHostedService<LoopbackListenerVerifier>();

var app = builder.Build();

// --- Inbound listeners: loopback only on AirGapped and SecureWorkstation (SPEC-007 PR 4.10, owner decision Q6) ---
// Responses on inbound connections are not mediated until SPEC-007 PR 5, so on those profiles a configured address
// that is not loopback refuses the start here, before Kestrel binds anything. LoopbackListenerVerifier checks what
// Kestrel actually bound, after it starts, for a listener a host adds in code.
{
    var listenerViolation = Ashlar.Infrastructure.Deployment.LoopbackListenerPolicy.Violation(
        app.Services.GetService<Ashlar.Infrastructure.Deployment.ResolvedDeploymentProfile>(),
        Ashlar.Infrastructure.Deployment.LoopbackListenerPolicy.ConfiguredAddresses(app.Configuration, app.Urls),
        "Ashlar.API");
    if (listenerViolation is not null)
    {
        throw new InvalidOperationException(listenerViolation);
    }
}

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<IngressEnvelopeMiddleware>();
app.UseWebSockets();

// --- Security: exposure profile (fails closed off-loopback) + optional built-in auth ---
{
    var sec = app.Services.GetRequiredService<IOptions<AshlarSecurityOptions>>().Value;
    var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Ashlar.Security");
    var hasConfiguredAuthMode = Enum.TryParse<AshlarAuthorizationMode>(sec.AuthorizationMode, true, out var authMode)
        && authMode != AshlarAuthorizationMode.None;
    // The legacy flag counts as "auth configured": with no key it now fails closed (401) instead of open.
    var hasAnyBuiltInAuth = hasConfiguredAuthMode || sec.RequireApiKeyForMutatingEndpoints;

    if (Enum.TryParse<AshlarExposureProfile>(sec.ExposureProfile, true, out var prof))
    {
        var offLoopback = prof is AshlarExposureProfile.Lan or AshlarExposureProfile.Tailnet or AshlarExposureProfile.Public;
        if (offLoopback && !hasAnyBuiltInAuth)
        {
            // Off-loopback with AuthorizationMode=None means every mutating route under /api (and the
            // opt-in execution surface) would answer unauthenticated callers on the network. Refuse to
            // start unless the operator states that something in front of Ashlar.API authenticates.
            if (!sec.AllowUnauthenticatedNetworkExposure)
            {
                throw new InvalidOperationException(
                    $"Ashlar:Security:ExposureProfile is '{prof}' but no built-in auth is configured (Ashlar:Security:AuthorizationMode=None). " +
                    "Refusing to start: mutating routes under /api would be reachable from the network without credentials. " +
                    "Set Ashlar:Security:AuthorizationMode (ApiKey, BearerToken, Basic, ...) plus the matching credential, " +
                    "or set Ashlar:Security:AllowUnauthenticatedNetworkExposure=true only when an authenticating proxy or network ACL fronts this host " +
                    "(see SECURITY.md, 'Default posture and in-scope surfaces').");
            }

            log.LogWarning(
                "ExposureProfile is {Profile} with no built-in auth and AllowUnauthenticatedNetworkExposure=true: mutating routes under /api are unauthenticated. Ensure an authenticating proxy or network ACL fronts Ashlar.API.",
                prof);
        }

        if (prof == AshlarExposureProfile.Public)
        {
            log.LogWarning(
                "ExposureProfile is Public: use TLS and authentication in front of Ashlar.API; the profile does not enforce network policy.");
        }
        else if (prof is AshlarExposureProfile.Lan or AshlarExposureProfile.Tailnet)
        {
            log.LogInformation("ExposureProfile is {Profile}: review docs for firewall / ACL guidance.", prof);
        }
    }

    if (hasConfiguredAuthMode)
    {
        if (!Enum.TryParse<AshlarAuthorizationScope>(sec.AuthorizationScope, true, out var authScope))
        {
            authScope = AshlarAuthorizationScope.MutatingApi;
            log.LogWarning(
                "Ashlar built-in auth scope '{Scope}' is invalid; defaulting to {DefaultScope}.",
                sec.AuthorizationScope,
                authScope);
        }

        log.LogInformation("Ashlar built-in auth mode {Mode} is enabled with scope {Scope}.", authMode, authScope);
        if (sec.RequireApiKeyForMutatingEndpoints)
        {
            log.LogInformation(
                "Legacy RequireApiKeyForMutatingEndpoints is also set. Built-in auth mode {Mode} takes precedence.",
                authMode);
        }

        var missingConfig = authMode switch
        {
            AshlarAuthorizationMode.ApiKey => string.IsNullOrWhiteSpace(sec.ApiKey),
            AshlarAuthorizationMode.BearerToken => string.IsNullOrWhiteSpace(sec.BearerToken),
            AshlarAuthorizationMode.Basic => string.IsNullOrWhiteSpace(sec.BasicAuthUsername) || string.IsNullOrWhiteSpace(sec.BasicAuthPassword),
            AshlarAuthorizationMode.ApiKeyOrBearerToken => string.IsNullOrWhiteSpace(sec.ApiKey) && string.IsNullOrWhiteSpace(sec.BearerToken),
            AshlarAuthorizationMode.ApiKeyOrBasic => string.IsNullOrWhiteSpace(sec.ApiKey)
                                                   && (string.IsNullOrWhiteSpace(sec.BasicAuthUsername) || string.IsNullOrWhiteSpace(sec.BasicAuthPassword)),
            AshlarAuthorizationMode.BearerTokenOrBasic => string.IsNullOrWhiteSpace(sec.BearerToken)
                                                        && (string.IsNullOrWhiteSpace(sec.BasicAuthUsername) || string.IsNullOrWhiteSpace(sec.BasicAuthPassword)),
            AshlarAuthorizationMode.Any => string.IsNullOrWhiteSpace(sec.ApiKey)
                                         && string.IsNullOrWhiteSpace(sec.BearerToken)
                                         && (string.IsNullOrWhiteSpace(sec.BasicAuthUsername) || string.IsNullOrWhiteSpace(sec.BasicAuthPassword)),
            _ => false
        };

        if (missingConfig)
        {
            log.LogWarning(
                "Ashlar built-in auth mode {Mode} is enabled but required credentials are not fully configured. Protected routes will reject requests until configuration is complete.",
                authMode);
        }
    }
    else if (sec.RequireApiKeyForMutatingEndpoints && string.IsNullOrWhiteSpace(sec.ApiKey))
    {
        log.LogWarning(
            "Ashlar API key auth is required for mutating endpoints, but no API key is configured. Mutating endpoints will reject every request (401) until Ashlar:Security:ApiKey is set.");
    }
}

// --- Middleware pipeline: SPA static files → auth → API endpoints ---
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAshlarMeshCorrelation();
app.UseAshlarMeshSecurity();
app.UseAshlarApiKeyAuth();
app.UsePrivateLicenseGate();
app.UseAshlarCopilotScopedAuthorization();

app.UseRateLimiter();

// --- Swagger (OpenAPI document + UI): on in Development, otherwise opt-in via Ashlar:Api:EnableSwagger ---
// The document enumerates every mapped route and schema; keep it off the network by default and let
// operators turn it on explicitly (Ashlar__Api__EnableSwagger=true) when they front the host with auth.
{
    var enableSwagger = app.Configuration.GetValue<bool?>("Ashlar:Api:EnableSwagger") ?? app.Environment.IsDevelopment();
    if (enableSwagger)
    {
        app.UseSwagger();
        app.UseSwaggerUI(static c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "Ashlar.API v1"));
        if (!app.Environment.IsDevelopment())
        {
            app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Ashlar.Security")
                .LogInformation("Swagger UI is enabled outside Development (Ashlar:Api:EnableSwagger=true): /swagger exposes the full route catalogue.");
        }
    }
}

app.MapAshlarEndpoints();
app.MapIngressEndpoints();

// --- Agent-protocol endpoints (map nothing while disabled; see IngressCatalog rows) ---
// Both live under /api so AshlarApiKeyAuthMiddleware protects them (all verbs — see
// ShouldProtect's protocol-path handling); the root agent card is the /.well-known exception
// handled explicitly there. NO AllowAnonymous anywhere on these surfaces.
app.MapAshlarMcpEndpoint()?.RequireRateLimiting("ashlar-mcp");
var a2aEndpoints = app.MapAshlarA2AEndpoints();
if (a2aEndpoints is not null)
{
    foreach (var rpcEndpoint in a2aEndpoints.RpcEndpoints)
    {
        rpcEndpoint.RequireRateLimiting("ashlar-a2a");
    }

    foreach (var cardEndpoint in a2aEndpoints.CardEndpoints)
    {
        cardEndpoint.RequireRateLimiting("ashlar-a2a");
    }
}

// An unmatched path under /api is a missing endpoint, not a page. Without this, the SPA fallback below
// answers every unknown /api path with 200 and index.html: a client that misspells a route, or calls a
// surface that is feature-flagged off, parses an HTML page as its result. It also makes SECURITY.md's
// "(404 otherwise)" true of the remote-execution routes, which were answering 405/200 instead.
// This catch-all is less specific than every mapped API route, so real endpoints still win it.
app.Map("/api/{**rest}", () => Results.NotFound(new { error = "No such endpoint." }));

app.MapFallbackToFile("index.html");

app.Run();

static RateLimitPartition<string> BuildPerIpFixedWindowPartition(
    HttpContext httpContext, int permitLimit, int windowSeconds)
{
    if (permitLimit <= 0)
    {
        return RateLimitPartition.GetFixedWindowLimiter(
            "off",
            static _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = int.MaxValue,
                Window = TimeSpan.FromDays(1),
                QueueLimit = 0,
                AutoReplenishment = true,
            });
    }

    var key = httpContext.Connection.RemoteIpAddress?.ToString() ?? "anonymous";
    var window = windowSeconds > 0 ? windowSeconds : 60;
    return RateLimitPartition.GetFixedWindowLimiter(
        key,
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permitLimit,
            Window = TimeSpan.FromSeconds(window),
            QueueLimit = 0,
            AutoReplenishment = true,
        });
}

/// <summary>
/// Exposes the implicit Program entry point for ASP.NET Core integration tests (<c>WebApplicationFactory&lt;Program&gt;</c>).
/// </summary>
public partial class Program
{
}
