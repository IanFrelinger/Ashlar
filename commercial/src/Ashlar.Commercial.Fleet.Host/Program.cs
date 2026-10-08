using System.Threading.RateLimiting;
using MediatR;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi.Models;
using Ashlar.API.Endpoints;
using Ashlar.API.Middleware.Ingress;
using Ashlar.API.Security;
using Ashlar.BackgroundAgents.Extending;
using Ashlar.BackgroundAgents.HostRunners;
using Ashlar.BackgroundAgents.Optimization;
using Ashlar.BackgroundAgents.Testing;
using Ashlar.Commercial.Fleet.Api;
using Ashlar.Contracts;
using Ashlar.Core.Application.Middleware.Ports;
using Ashlar.Hosting;
using Ashlar.Infrastructure.Egress;
using Ashlar.Ingress.AwsSns;
using Ashlar.Ingress.DynamoDb;
using Ashlar.Runtime;
using Ashlar.Transport.Grpc;

var builder = WebApplication.CreateBuilder(args);

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

var disableObservationPipeline =
    builder.Configuration.GetValue("Ashlar:DisableObservationPipeline", defaultValue: false);

builder.Services.AddLogging(b => b.AddConsole());
builder.Services.AddHttpContextAccessor();
builder.Services.Configure<GrpcTransportOptions>(
    builder.Configuration.GetSection("Ashlar:GrpcTransport"));
builder.Services.Configure<AshlarSecurityOptions>(
    builder.Configuration.GetSection(AshlarSecurityOptions.SectionPath));
builder.Services.Configure<AshlarProductOptions>(
    builder.Configuration.GetSection(AshlarProductOptions.SectionPath));
builder.Services.Configure<AshlarEntitlementsOptions>(
    builder.Configuration.GetSection(AshlarEntitlementsOptions.SectionPath));
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<ICopilotSubmissionQuota, CopilotSubmissionQuota>();
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
    options.SwaggerDoc("v1", new OpenApiInfo { Title = "Ashlar Commercial Fleet Host", Version = "v1" }));
builder.Services.AddAshlarRuntimeRouting(builder.Configuration);

builder.Services.AddSingleton<IAshlarIngressAccessor, HttpAshlarIngressAccessor>();
builder.Services.AddHttpClient("ashlar-sns-signing", c => c.Timeout = TimeSpan.FromSeconds(15))
    .ConfigurePrimaryHttpMessageHandler(static (handler, _) =>
    {
        if (handler is HttpClientHandler httpClientHandler)
            httpClientHandler.AllowAutoRedirect = false;
        else if (handler is SocketsHttpHandler socketsHttpHandler)
            socketsHttpHandler.AllowAutoRedirect = false;
    });
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
});

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
    options.DisableObservationPipeline = disableObservationPipeline;
});
// SPEC-007: AddAshlar has already installed the report-only egress guard on every IHttpClientFactory client in this
// host, including the Fleet.Infrastructure clients registered below, whose project cannot reach Ashlar.Infrastructure.
// This call is idempotent and adds nothing; it states the coverage here.
builder.Services.AddAshlarEgressGuard();

builder.Services.AddAshlarCommercialFleetDirector(
    builder.Configuration,
    includeKnowledgeReplication: !disableObservationPipeline);

builder.Services.AddMediatR(cfg =>
    cfg.RegisterServicesFromAssembly(typeof(RecordSmsYesApprovalCommand).Assembly));

var app = builder.Build();

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<IngressEnvelopeMiddleware>();
app.UseWebSockets();
app.UseAshlarMeshCorrelation();
app.UseAshlarMeshSecurity();
app.UseAshlarApiKeyAuth();
app.UseAshlarCopilotScopedAuthorization();
// No UsePrivateLicenseGate() here, and that is a recorded decision rather than a gap in the copy
// from Ashlar.API: this host registers neither IPrivateLicenseValidator nor the
// Ashlar:PrivateLicense options, so Ashlar:PrivateLicense:EnforceLicense has no effect on Fleet.Host;
// the enforcement in docs/product-fleet/private-reference-deployment.md runs in that stack's
// Ashlar.API container (.docker/Dockerfile.api). Adding it here is a licensing change with its own
// blast radius (with enforcement on, an expired license would answer 402 to fleet node
// registration), not part of gating Swagger; docs/OpenCoreBoundary.md tracks moving the gate into
// the commercial host.
app.UseRateLimiter();

// --- Swagger (OpenAPI document + UI): the same gate as application/src/Ashlar.API/Program.cs ---
// On in Development, otherwise opt-in via Ashlar:Api:EnableSwagger (Ashlar__Api__EnableSwagger=true);
// an explicit false turns it off in Development too. /swagger is outside /api, so neither
// UseAshlarApiKeyAuth nor UseAshlarMeshSecurity ever evaluates it: whenever it is served, it is served
// to anyone who can reach the port, under every AuthorizationMode and AuthorizationScope. This host
// used to call UseSwagger unconditionally, which published its route catalogue in Production.
// The key is also read from appsettings.{Environment}.json files this project does not own: the
// ProjectReference to Ashlar.API copies its appsettings.Development.json and appsettings.Testing.json
// into this host's build and publish output, and the Testing one sets EnableSwagger to true, so
// ASPNETCORE_ENVIRONMENT=Testing serves Swagger from the fleet-host image too.
// Pinned, that exception included, by FleetHostSwaggerExposureTests.
{
    var enableSwagger = app.Configuration.GetValue<bool?>("Ashlar:Api:EnableSwagger") ?? app.Environment.IsDevelopment();
    if (enableSwagger)
    {
        app.UseSwagger();
        app.UseSwaggerUI(static c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "Ashlar Commercial Fleet Host v1"));
        if (!app.Environment.IsDevelopment())
        {
            app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Ashlar.Security")
                .LogInformation("Swagger UI is enabled outside Development (Ashlar:Api:EnableSwagger=true): /swagger exposes the full route catalogue.");
        }
    }
}

app.MapAshlarEndpoints();
app.MapAshlarCommercialFleetEndpoints();
app.MapIngressEndpoints();

app.Run();

/// <summary>Entry point type for integration tests and hosting.</summary>
public partial class FleetHostProgram;
