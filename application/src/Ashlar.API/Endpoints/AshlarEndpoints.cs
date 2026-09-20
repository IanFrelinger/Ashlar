using System.Text;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Ashlar.Brick.Contracts;
using Ashlar.Brick.Contracts.Capabilities;
using Ashlar.Contracts;
using Ashlar.Core.Application.Copilot.Models;
using Ashlar.Core.Application.Copilot.Ports;
using Ashlar.Core.Application.Product.Models;
using Ashlar.Core.Application.Product.Ports;
using Ashlar.Core.Application.Knowledge.Models;
using Ashlar.Core.Application.Knowledge.Ports;
using Ashlar.Core.Application.Agent.UseCases.RunAgent;
using Ashlar.Core.Application.Bricks;
using Ashlar.Core.Domain.Exceptions;
using Ashlar.Core.Application.NodeCapabilityRuntime.Models;
using Ashlar.Core.Application.NodeCapabilityRuntime.Ports;
using Ashlar.Core.Application.Trust.Ports;
using Ashlar.Core.Application.Validation.UseCases.RunValidation;
using Ashlar.Abstractions;
using Ashlar.BackgroundAgents.Configuration;
using Ashlar.BackgroundAgents.Forge;
using Ashlar.BackgroundAgents.Objectives;
using Ashlar.BackgroundAgents.Registry;
using Ashlar.BackgroundAgents.RuntimeStudio;
using Ashlar.Infrastructure.Testing.ExecutionPlatform;
using Ashlar.Infrastructure.Execution;
using Ashlar.API.Security;
using Ashlar.Orchestration.Coordination;
using Ashlar.Orchestration.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using System.Text.Json;
using Ashlar.Core.Domain.Bricks;
using Ashlar.Core.Domain.Execution;

namespace Ashlar.API.Endpoints;
/// <summary>
/// Extension methods for mapping Ashlar API endpoints.
/// </summary>
public static class AshlarEndpoints
{
    private static readonly JsonSerializerOptions DailySerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    /// <summary>Maps all Ashlar REST API endpoints under <c>/api</c>.</summary>
    public static IEndpointRouteBuilder MapAshlarEndpoints(this IEndpointRouteBuilder app)
    {
        // Liveness: the process answers HTTP. Constant 200 by design (container HEALTHCHECK, k8s livenessProbe).
        app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTimeOffset.UtcNow }))
            .WithName("HealthCheck")
            .WithTags("Health")
            .ExcludeFromDescription();

        // Readiness: 200 only once the host has finished starting (DI built, hosted services started)
        // and until shutdown begins; 503 while starting or stopping so orchestrators stop routing
        // traffic before Kestrel closes (k8s readinessProbe, compose --wait, load balancers).
        app.MapGet("/ready", GetReadiness)
            .WithName("ReadinessCheck")
            .WithTags("Health")
            .ExcludeFromDescription();

        var group = app.MapGroup("/api").WithTags("Ashlar");
        group.MapWorkloadScalingEndpoints();
        group.MapIdeEndpoints();

        group.MapPost("/agent", RunAgentAsync)
            .WithName("RunAgent")
            .WithSummary("Invoke an agent by name")
            .Produces<AgentResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        group.MapPost("/validate", RunValidationAsync)
            .WithName("RunValidation")
            .WithSummary("Run validation tests")
            .Produces<ValidationResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        group.MapPost("/orchestrate", OrchestrateAsync)
            .WithName("Orchestrate")
            .WithSummary("Run orchestration workflow")
            .Produces<OrchestrationResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        group.MapPost("/copilot/task", RunCopilotTaskAsync)
            .WithName("RunCopilotTask")
            .WithSummary("Run copilot task and return trust-auditable context")
            .Produces<CopilotTaskResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        group.MapGet("/copilot/tasks/{taskId}", GetCopilotTaskByIdAsync)
            .WithName("GetCopilotTask")
            .WithSummary("Get one copilot task record for the resolved tenant")
            .Produces<CopilotTaskRecord>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/copilot/tasks", ListCopilotTasksAsync)
            .WithName("ListCopilotTasks")
            .WithSummary("List recent copilot tasks (newest first)")
            .Produces<IReadOnlyList<CopilotTaskRecord>>(StatusCodes.Status200OK);

        group.MapGet("/usage/summary", GetUsageSummaryAsync)
            .WithName("GetUsageSummary")
            .WithSummary("Rolling usage counters for the resolved tenant (Product Fleet Phase 0.3)")
            .Produces<Ashlar.Core.Application.Product.Models.TenantUsageSummary>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        group.MapGet("/support/diagnostics", GetSupportDiagnosticsAsync)
            .WithName("GetSupportDiagnostics")
            .WithSummary("Redacted support diagnostics export (Product Fleet Phase 0.5)")
            .Produces<SupportDiagnosticsResponse>(StatusCodes.Status200OK);

        group.MapPost("/orgs", CreateOrganizationAsync)
            .WithName("CreateOrganization")
            .WithSummary("Create a cloud organization (Product Fleet Phase 2.3)")
            .Produces<Organization>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        group.MapGet("/orgs/{orgId}", GetOrganizationAsync)
            .WithName("GetOrganization")
            .WithSummary("Get organization metadata for a member")
            .Produces<Organization>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/orgs/{orgId}/members", ListOrganizationMembersAsync)
            .WithName("ListOrganizationMembers")
            .WithSummary("List organization members")
            .Produces<IReadOnlyList<OrganizationMember>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPost("/orgs/{orgId}/members", AddOrganizationMemberAsync)
            .WithName("AddOrganizationMember")
            .WithSummary("Invite or add a member (admin only)")
            .Produces<OrganizationMember>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet("/status", GetStatusAsync)
            .WithName("GetStatus")
            .WithSummary("Get background agent status and mode")
            .Produces<StatusResponse>(StatusCodes.Status200OK);

        // Remote execution surface: hands caller-chosen images, commands and host bind mounts to the
        // local Docker daemon, so it is dark unless Ashlar:Execution:ServeRemoteExecution=true. Even
        // when opted in, the handlers refuse (403) while AuthorizationMode resolves to None.
        var executionOptions = app.ServiceProvider.GetService<IOptions<AshlarExecutionOptions>>()?.Value
            ?? new AshlarExecutionOptions();
        if (executionOptions.ServeRemoteExecution)
        {
            group.MapPost("/execution/build", BuildImageAsync)
                .WithName("BuildImage")
                .WithSummary("Build a container image from Dockerfile (for RemoteExecutionPlatform; opt-in via Ashlar:Execution:ServeRemoteExecution)")
                .Produces<ExecutionBuildResponse>(StatusCodes.Status200OK)
                .ProducesProblem(StatusCodes.Status400BadRequest)
                .ProducesProblem(StatusCodes.Status403Forbidden)
                .ProducesProblem(StatusCodes.Status500InternalServerError);

            group.MapPost("/execution/run", RunContainerAsync)
                .WithName("RunContainer")
                .WithSummary("Run a container (for RemoteExecutionPlatform; opt-in via Ashlar:Execution:ServeRemoteExecution)")
                .Produces<ExecutionRunResponse>(StatusCodes.Status200OK)
                .ProducesProblem(StatusCodes.Status400BadRequest)
                .ProducesProblem(StatusCodes.Status403Forbidden)
                .ProducesProblem(StatusCodes.Status500InternalServerError);
        }

        group.MapGet("/capabilities", GetCapabilitiesAsync)
            .WithName("GetCapabilities")
            .WithSummary("Get node capability manifest for brick routing")
            .Produces<NodeCapabilityManifestDto>(StatusCodes.Status200OK);

        group.MapGet("/bricks", ListBricksAsync)
            .WithName("ListBricks")
            .WithSummary("List bricks on this node (catalog for federated mesh)")
            .Produces<BrickCatalogResponseDto>(StatusCodes.Status200OK);

        group.MapGet("/bricks/{brickId}", GetBrickAsync)
            .WithName("GetBrick")
            .WithSummary("Get one brick catalog entry")
            .Produces<BrickCatalogEntryDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/bricks/{brickId}/execute", ExecuteBrickAsync)
            .WithName("ExecuteBrick")
            .WithSummary("Execute a brick on this node (used by RemoteBrick from peers)")
            .Produces<BrickExecuteResponseDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/security/advisory", GetSecurityAdvisory)
            .WithName("GetSecurityAdvisory")
            .WithSummary("Operator exposure profile and hints (user-configured; advisory only)")
            .Produces<SecurityAdvisoryResponse>(StatusCodes.Status200OK);

        group.MapGet("/trust/status", GetTrustStatusAsync)
            .WithName("GetTrustStatus")
            .WithSummary("Get trust boundary status and active trust policy pack")
            .Produces<TrustStatusResponse>(StatusCodes.Status200OK);

        group.MapPost("/director/run", RunDirectorWorkflowAsync)
            .WithName("RunDirectorWorkflow")
            .WithSummary("Run one directorial iteration and persist as a daily")
            .Produces<DirectorRunResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        group.MapGet("/director/dailies", ListDailiesAsync)
            .WithName("ListDirectorDailies")
            .WithSummary("List persisted directorial dailies")
            .Produces<List<DirectorDailySummary>>(StatusCodes.Status200OK);

        group.MapGet("/director/dailies/{dailyId}", GetDailyAsync)
            .WithName("GetDirectorDaily")
            .WithSummary("Get one persisted directorial daily")
            .Produces<DirectorDailyEntry>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/background-agents/summary", GetBackgroundAgentSummaryAsync)
            .WithName("GetBackgroundAgentSummary")
            .WithSummary("Get background agent health summary")
            .Produces<BackgroundAgentSummaryResponse>(StatusCodes.Status200OK);

        group.MapGet("/runtime-studio/metrics", GetRuntimeStudioMetricsAsync)
            .WithName("GetRuntimeStudioMetrics")
            .WithSummary("Runtime Studio backlog metrics (objectives, forge, observations path size)")
            .Produces<RuntimeStudioMetricsResponse>(StatusCodes.Status200OK);

        group.MapGet("/trust/dashboard", GetTrustDashboardAsync)
            .WithName("GetTrustDashboard")
            .WithSummary("Get trust boundary and recent audit events")
            .Produces<TrustDashboardResponse>(StatusCodes.Status200OK);

        group.MapPost("/trust/pause", SetTrustPauseAsync)
            .WithName("SetTrustPause")
            .WithSummary("Pause or resume trust observation boundary")
            .Produces<TrustBoundaryMutationResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        group.MapPost("/trust/rule", SetTrustRuleAsync)
            .WithName("SetTrustRule")
            .WithSummary("Update trust allow/deny rules for category/source")
            .Produces<TrustBoundaryMutationResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        group.MapGet("/knowledge/query", QueryKnowledgeAsync)
            .WithName("QueryKnowledge")
            .WithSummary("Query unified adaptation/pattern/knowledge timeline")
            .Produces<KnowledgeQueryResult>(StatusCodes.Status200OK);

        group.MapGet("/preferences", GetPreferencesAsync)
            .WithName("GetPreferences")
            .WithSummary("Get server-side user preferences")
            .Produces<PreferencesResponse>(StatusCodes.Status200OK);

        group.MapPost("/preferences", SavePreferencesAsync)
            .WithName("SavePreferences")
            .WithSummary("Save server-side user preferences")
            .Produces<PreferencesResponse>(StatusCodes.Status200OK);

        group.MapGet("/activity/feed", GetActivityFeedAsync)
            .WithName("GetActivityFeed")
            .WithSummary("Get recent activity from background agents and system events")
            .Produces<ActivityFeedResponse>(StatusCodes.Status200OK);

        group.MapPost("/changelog/generate", GenerateChangelogAsync)
            .WithName("GenerateChangelog")
            .WithSummary("Generate project changelog summary from recent changes")
            .Produces<ChangelogResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        group.MapGet("/onboarding/status", GetOnboardingStatusAsync)
            .WithName("GetOnboardingStatus")
            .WithSummary("Get setup status for first-run wizard (provider availability, config state)")
            .Produces<OnboardingStatusResponse>(StatusCodes.Status200OK);

        return app;
    }

    private static IResult GetReadiness([FromServices] IHostApplicationLifetime lifetime, HttpResponse response)
    {
        response.Headers.CacheControl = "no-store";
        var (statusCode, status) = EvaluateReadiness(lifetime.ApplicationStarted, lifetime.ApplicationStopping);
        return Results.Json(new { status, timestamp = DateTimeOffset.UtcNow }, statusCode: statusCode);
    }

    /// <summary>
    /// Readiness decision from the host lifetime tokens: <c>stopping</c> wins over everything (drain first),
    /// then <c>starting</c> until <see cref="IHostApplicationLifetime.ApplicationStarted"/> has fired, then <c>ready</c>.
    /// </summary>
    /// <param name="applicationStarted">The host's <see cref="IHostApplicationLifetime.ApplicationStarted"/> token.</param>
    /// <param name="applicationStopping">The host's <see cref="IHostApplicationLifetime.ApplicationStopping"/> token.</param>
    /// <returns>HTTP status code (200 or 503) and the <c>status</c> string reported in the body.</returns>
    internal static (int StatusCode, string Status) EvaluateReadiness(CancellationToken applicationStarted, CancellationToken applicationStopping)
    {
        if (applicationStopping.IsCancellationRequested)
        {
            return (StatusCodes.Status503ServiceUnavailable, "stopping");
        }

        if (!applicationStarted.IsCancellationRequested)
        {
            return (StatusCodes.Status503ServiceUnavailable, "starting");
        }

        return (StatusCodes.Status200OK, "ready");
    }

    private static async Task<IResult> RunAgentAsync(
        [FromBody] AgentRequest request,
        [FromServices] IMediator mediator,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request?.AgentName))
            return Results.BadRequest(new ProblemDetails { Title = "AgentName is required" });

        var logger = loggerFactory.CreateLogger("Ashlar.Api.Agent");
        try
        {
            var command = new RunAgentCommand(request.AgentName, request.InputFilePath != null ? new FileInfo(request.InputFilePath) : null);
            var result = await mediator.Send(command, cancellationToken);
            return Results.Ok(new AgentResponse(result.Success, result.Message, result.Output));
        }
        catch (AgentExecutionException ex) when (ex.ErrorCode == ErrorCodes.AgentNotFound)
        {
            // An unknown agent name is a client error, not a server fault: answer 404 naming the
            // missing agent (previously a bare 500 that logged nothing — dead-end for operators).
            logger.LogWarning(
                "Agent execution requested for unknown agent '{AgentName}': {Message}",
                ex.AgentName,
                ex.Message);
            var detail = string.IsNullOrWhiteSpace(ex.Suggestion)
                ? ex.Message
                : $"{ex.Message}. {ex.Suggestion}";
            return Results.Problem(
                title: "Agent not found",
                detail: detail,
                statusCode: StatusCodes.Status404NotFound);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Agent execution failed for '{AgentName}'.", request.AgentName);
            return Results.Problem(
                detail: IsDevelopment() ? ex.Message : "An internal error occurred. Check server logs for details.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<IResult> RunValidationAsync(
        [FromBody] ValidationRequest? request,
        [FromServices] IMediator mediator,
        CancellationToken cancellationToken)
    {
        try
        {
            var command = new RunValidationCommand(request?.Filter);
            var result = await mediator.Send(command, cancellationToken);
            return Results.Ok(new ValidationResponse(result.Passed, result.Message, result.TestsRun, result.TestsPassed, result.TestsFailed));
        }
        catch (Exception ex)
        {
            return Results.Problem(
                detail: IsDevelopment() ? ex.Message : "An internal error occurred. Check server logs for details.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<IResult> OrchestrateAsync(
        [FromBody] OrchestrationRequest request,
        [FromServices] Orchestrator orchestrator,
        [FromServices] IOrchestrationRuntimeSpecAccessor? runtimeSpecAccessor,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request?.Request))
            return Results.BadRequest(new ProblemDetails { Title = "Request is required" });

        try
        {
            OrchestrationRuntimeSpec spec;
            if (!string.IsNullOrWhiteSpace(request.RuntimeSpecJson))
            {
                try
                {
                    spec = JsonSerializer.Deserialize<OrchestrationRuntimeSpec>(
                               request.RuntimeSpecJson,
                               new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                           ?? OrchestrationRuntimeSpec.Default();
                }
                catch (JsonException ex)
                {
                    return Results.BadRequest(new ProblemDetails
                    {
                        Title = "runtimeSpecJson is invalid",
                        Detail = ex.Message
                    });
                }
            }
            else
            {
                spec = OrchestrationRuntimeSpec.Default();
            }

            if (!string.IsNullOrWhiteSpace(request.PreferModel))
                spec = spec with { Model = spec.Model with { Prefer = request.PreferModel.Trim() } };
            if (!string.IsNullOrWhiteSpace(request.Provider))
                spec = spec with { Model = spec.Model with { Provider = request.Provider.Trim() } };
            if (!string.IsNullOrWhiteSpace(request.BarrierLevel))
                spec = spec with { BarrierLevel = request.BarrierLevel.Trim() };
            if (!string.IsNullOrWhiteSpace(request.PreferredRegion))
                spec = spec with { PreferredRegion = request.PreferredRegion.Trim() };

            using var _ = runtimeSpecAccessor?.Begin(spec);
            var result = await orchestrator.OrchestrateAsync(request.Request, cancellationToken);
            return Results.Ok(new OrchestrationResponse(
                result.Success,
                BuildOrchestrationSummary(result),
                result.IntegratedOutput?.IntegratedResults,
                result.Conflicts.Count,
                result.Escalations.Count,
                ResolveOrchestrationErrorCode(result)));
        }
        catch (Exception ex)
        {
            return Results.Problem(
                detail: IsDevelopment() ? ex.Message : "An internal error occurred. Check server logs for details.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<IResult> RunCopilotTaskAsync(
        HttpContext httpContext,
        [FromBody] CopilotTaskRequest request,
        [FromServices] Orchestrator orchestrator,
        [FromServices] ICopilotTaskStore copilotTaskStore,
        [FromServices] IDataDecisionAuditLog? auditLog,
        [FromServices] IAccessBoundary? accessBoundary,
        [FromServices] IOptions<AshlarProductOptions> productOptions,
        [FromServices] ICopilotSubmissionQuota copilotSubmissionQuota,
        [FromServices] ITenantUsageStore usageStore,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request?.Task))
            return Results.BadRequest(new ProblemDetails { Title = "Task is required" });

        if (!TryResolveProductTenant(httpContext, productOptions.Value, out var tenantId, out var tenantError))
            return Results.BadRequest(new ProblemDetails { Title = "Invalid tenant", Detail = tenantError });

        if (!copilotSubmissionQuota.TryConsume(tenantId, out var quotaMsg))
            return Results.Json(new ProblemDetails { Title = "Too Many Requests", Detail = quotaMsg }, statusCode: StatusCodes.Status429TooManyRequests);

        var usageLogger = loggerFactory.CreateLogger("Ashlar.Usage");
        usageStore.RecordJobSubmitted(tenantId);

        var taskId = Guid.NewGuid().ToString("D");
        var submittedAt = DateTimeOffset.UtcNow;
        await copilotTaskStore.StoreAsync(new CopilotTaskRecord
        {
            TenantId = tenantId,
            TaskId = taskId,
            Task = request.Task.Trim(),
            SubmittedAt = submittedAt,
            CompletedAt = null,
            Success = false,
            Summary = null,
            Error = null
        }, cancellationToken);

        try
        {
            var result = await orchestrator.OrchestrateAsync(request.Task, cancellationToken);
            var summary = BuildOrchestrationSummary(result);
            await copilotTaskStore.StoreAsync(new CopilotTaskRecord
            {
                TenantId = tenantId,
                TaskId = taskId,
                Task = request.Task.Trim(),
                SubmittedAt = submittedAt,
                CompletedAt = DateTimeOffset.UtcNow,
                Success = result.Success,
                Summary = summary,
                Error = null
            }, cancellationToken);
            usageStore.RecordJobCompleted(tenantId, result.Success);
            auditLog?.LogCopilotTask(tenantId, taskId, result.Success);
            usageLogger.LogInformation(
                "AshlarUsage metric=copilot_job_completed tenant={TenantId} taskId={TaskId} success={Success}",
                tenantId,
                taskId,
                result.Success);
            var auditCount = Math.Clamp(request.AuditCount <= 0 ? 25 : request.AuditCount, 1, 200);
            var recentAudit = auditLog?.GetRecent(auditCount) ?? [];
            return Results.Ok(new CopilotTaskResponse(
                taskId,
                tenantId,
                result.Success,
                summary,
                result.IntegratedOutput?.IntegratedResults,
                accessBoundary?.IsObservationPaused ?? false,
                recentAudit));
        }
        catch (Exception ex)
        {
            await copilotTaskStore.StoreAsync(new CopilotTaskRecord
            {
                TenantId = tenantId,
                TaskId = taskId,
                Task = request.Task.Trim(),
                SubmittedAt = submittedAt,
                CompletedAt = DateTimeOffset.UtcNow,
                Success = false,
                Summary = null,
                Error = ex.Message
            }, cancellationToken);
            usageStore.RecordJobCompleted(tenantId, success: false);
            auditLog?.LogCopilotTask(tenantId, taskId, success: false);
            usageLogger.LogInformation(
                "AshlarUsage metric=copilot_job_completed tenant={TenantId} taskId={TaskId} success={Success}",
                tenantId,
                taskId,
                false);
            return Results.Problem(
                detail: IsDevelopment() ? ex.Message : "An internal error occurred. Check server logs for details.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static Task<IResult> GetSupportDiagnosticsAsync(
        [FromServices] IConfiguration configuration,
        [FromServices] IHostEnvironment environment,
        [FromServices] IOptions<AshlarEntitlementsOptions> entitlementsOptions,
        [FromServices] IOptions<AshlarProductOptions> productOptions,
        [FromServices] IOptions<AshlarSecurityOptions> securityOptions,
        [FromServices] IPrivateLicenseValidator licenseValidator)
    {
        var diagnostics = SupportDiagnosticsExporter.Build(
            configuration,
            environment,
            entitlementsOptions.Value,
            productOptions.Value,
            securityOptions.Value,
            licenseValidator.GetStatus());
        return Task.FromResult<IResult>(Results.Ok(diagnostics));
    }

    private static Task<IResult> GetUsageSummaryAsync(
        HttpContext httpContext,
        [FromServices] ITenantUsageStore usageStore,
        [FromServices] IOptions<AshlarProductOptions> productOptions,
        int hours = 24)
    {
        if (!TryResolveProductTenant(httpContext, productOptions.Value, out var tenantId, out var tenantError))
            return Task.FromResult<IResult>(Results.BadRequest(new ProblemDetails { Title = "Invalid tenant", Detail = tenantError }));

        var summary = usageStore.GetSummary(tenantId, hours);
        return Task.FromResult<IResult>(Results.Ok(summary));
    }

    private static async Task<IResult> ListCopilotTasksAsync(
        HttpContext httpContext,
        [FromServices] ICopilotTaskStore copilotTaskStore,
        [FromServices] IOptions<AshlarProductOptions> productOptions,
        [FromQuery] int maxCount = 50,
        [FromQuery] DateTimeOffset? since = null,
        CancellationToken cancellationToken = default)
    {
        if (!TryResolveProductTenant(httpContext, productOptions.Value, out var tenantId, out var tenantError))
            return Results.BadRequest(new ProblemDetails { Title = "Invalid tenant", Detail = tenantError });

        maxCount = Math.Clamp(maxCount <= 0 ? 50 : maxCount, 1, 500);
        var tasks = await copilotTaskStore.QueryAsync(maxCount, since, tenantId, cancellationToken);
        return Results.Ok(tasks);
    }

    private static async Task<IResult> GetCopilotTaskByIdAsync(
        HttpContext httpContext,
        string taskId,
        [FromServices] ICopilotTaskStore copilotTaskStore,
        [FromServices] IOptions<AshlarProductOptions> productOptions,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(taskId))
            return Results.BadRequest(new ProblemDetails { Title = "taskId is required" });

        if (!TryResolveProductTenant(httpContext, productOptions.Value, out var tenantId, out var tenantError))
            return Results.BadRequest(new ProblemDetails { Title = "Invalid tenant", Detail = tenantError });

        var task = await copilotTaskStore.GetByIdAsync(taskId.Trim(), cancellationToken);
        if (task is null || !string.Equals(task.TenantId, tenantId, StringComparison.Ordinal))
            return Results.NotFound();

        return Results.Ok(task);
    }

    private static async Task<IResult> GetStatusAsync(
        [FromServices] IServiceProvider services,
        [FromServices] IAccessBoundary? accessBoundary,
        CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        var modeStore = services.GetService<IAggressivenessModeStore>();
        var mode = modeStore?.GetMode() ?? BackgroundAgentAggressivenessMode.Active;
        var registry = services.GetService<IBackgroundAgentRegistry>();
        var activeAgents = registry?.GetAll().Count(a => a.State == BackgroundAgentState.Running) ?? 0;
        var totalAgents = registry?.GetAll().Count ?? 0;
        var activePack = accessBoundary?.GetActivePolicyPack();
        return Results.Ok(new StatusResponse(
            mode.ToString(),
            "Ashlar API is running",
            totalAgents,
            activeAgents,
            activePack?.Id,
            activePack?.Version));
    }

    private static async Task<IResult> BuildImageAsync(
        [FromBody] ExecutionBuildRequest request,
        [FromServices] IExecutionPlatform executionPlatform,
        [FromServices] IOptions<AshlarSecurityOptions> securityOptions,
        CancellationToken cancellationToken)
    {
        if (RemoteExecutionAuthRefusal(securityOptions.Value) is { } refusal)
            return refusal;

        if (string.IsNullOrWhiteSpace(request?.DockerfilePath) || string.IsNullOrWhiteSpace(request?.ImageTag) || string.IsNullOrWhiteSpace(request?.ContextPath))
            return Results.BadRequest(new ProblemDetails { Title = "DockerfilePath, ImageTag, and ContextPath are required" });

        try
        {
            var result = await executionPlatform.BuildImageAsync(
                request.DockerfilePath,
                request.ImageTag,
                request.ContextPath,
                request.BuildArgs,
                null,
                cancellationToken);
            return Results.Ok(new ExecutionBuildResponse(result.Success, result.ErrorMessage, result.Duration.TotalMilliseconds));
        }
        catch (Exception ex)
        {
            return Results.Problem(
                detail: IsDevelopment() ? ex.Message : "An internal error occurred. Check server logs for details.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<IResult> RunContainerAsync(
        [FromBody] ExecutionRunRequest request,
        [FromServices] IExecutionPlatform executionPlatform,
        [FromServices] IOptions<AshlarSecurityOptions> securityOptions,
        [FromServices] IOptions<AshlarExecutionOptions> executionOptions,
        CancellationToken cancellationToken)
    {
        if (RemoteExecutionAuthRefusal(securityOptions.Value) is { } refusal)
            return refusal;

        if (string.IsNullOrWhiteSpace(request?.ImageTag) || request?.Command == null)
            return Results.BadRequest(new ProblemDetails { Title = "ImageTag and Command are required" });

        if (!TryValidateVolumeMounts(request.VolumeMounts, executionOptions.Value.AllowedVolumeMountRoot, out var mountError))
            return Results.BadRequest(new ProblemDetails { Title = "VolumeMounts rejected", Detail = mountError });

        try
        {
            var result = await executionPlatform.RunContainerAsync(
                request.ImageTag,
                request.Command,
                request.EnvironmentVariables,
                request.VolumeMounts,
                request.WorkingDirectory,
                cancellationToken);
            return Results.Ok(new ExecutionRunResponse(result.Success, result.ExitCode, result.StandardOutput, result.StandardError, result.ContainerId, result.Duration.TotalMilliseconds));
        }
        catch (Exception ex)
        {
            return Results.Problem(
                detail: IsDevelopment() ? ex.Message : "An internal error occurred. Check server logs for details.",
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    /// <summary>
    /// The remote execution surface never runs unauthenticated: even with the operator opt-in, a
    /// request is refused (403) while the effective built-in auth mode is None.
    /// </summary>
    private static IResult? RemoteExecutionAuthRefusal(AshlarSecurityOptions security)
    {
        if (AshlarApiKeyAuthMiddleware.ResolveAuthorizationMode(security) != AshlarAuthorizationMode.None)
            return null;

        return Results.Problem(
            title: "Remote execution requires built-in auth",
            detail: "Ashlar:Execution:ServeRemoteExecution is true but Ashlar:Security:AuthorizationMode resolves to None. " +
                    "Configure ApiKey, BearerToken or Basic auth (with credentials) before serving /api/execution/*.",
            statusCode: StatusCodes.Status403Forbidden);
    }

    /// <summary>
    /// Host bind mounts on the remote surface are accepted only under the single configured root
    /// (<c>Ashlar:Execution:AllowedVolumeMountRoot</c>); with no root configured any mount is rejected.
    /// RemoteExecutionPlatform sends an empty dictionary when the caller has no mounts, so empty is fine.
    /// </summary>
    private static bool TryValidateVolumeMounts(Dictionary<string, string>? volumeMounts, string? allowedRoot, out string? error)
    {
        error = null;
        if (volumeMounts is null || volumeMounts.Count == 0)
            return true;

        if (string.IsNullOrWhiteSpace(allowedRoot))
        {
            error = "VolumeMounts are not accepted on the remote execution surface unless Ashlar:Execution:AllowedVolumeMountRoot names a single host directory.";
            return false;
        }

        var separators = new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string root;
        try
        {
            root = Path.GetFullPath(allowedRoot.Trim()).TrimEnd(separators);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = "Ashlar:Execution:AllowedVolumeMountRoot is not a valid absolute path.";
            return false;
        }

        foreach (var hostPath in volumeMounts.Keys)
        {
            var trimmed = hostPath?.Trim() ?? string.Empty;
            string? full = null;
            if (trimmed.Length > 0 && Path.IsPathRooted(trimmed))
            {
                try
                {
                    full = Path.GetFullPath(trimmed).TrimEnd(separators);
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    full = null;
                }
            }

            if (full is null)
            {
                error = $"Volume mount host path '{hostPath}' must be an absolute path under the configured root.";
                return false;
            }

            var underRoot = string.Equals(full, root, comparison)
                || full.StartsWith(root + Path.DirectorySeparatorChar, comparison)
                || full.StartsWith(root + Path.AltDirectorySeparatorChar, comparison);
            if (!underRoot)
            {
                error = $"Volume mount host path '{hostPath}' is outside Ashlar:Execution:AllowedVolumeMountRoot.";
                return false;
            }
        }

        return true;
    }

    private static async Task<IResult> GetCapabilitiesAsync(
        [FromServices] INodeCapabilityRuntime runtime,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.CompletedTask;
        var manifest = runtime.GetCapabilityManifest();
        return Results.Ok(ToDto(manifest));
    }

    private static async Task<IResult> ListBricksAsync(
        [FromServices] IBrickRegistry brickRegistry,
        [FromServices] INodeCapabilityRuntime runtime,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.CompletedTask;
        var hostCaps = ToDto(runtime.GetCapabilityManifest());
        var bricks = brickRegistry.GetAllBricks()
            .Select(b => BrickCatalogWireMapper.ToEntry(b, hostCaps))
            .ToList();
        return Results.Ok(new BrickCatalogResponseDto { Bricks = bricks });
    }

    private static async Task<IResult> GetBrickAsync(
        string brickId,
        [FromServices] IBrickRegistry brickRegistry,
        [FromServices] INodeCapabilityRuntime runtime,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.CompletedTask;
        if (string.IsNullOrWhiteSpace(brickId))
            return Results.BadRequest(new ProblemDetails { Title = "brickId is required" });

        var brick = brickRegistry.GetBrick(brickId);
        if (brick is null)
            return Results.NotFound();

        var hostCaps = ToDto(runtime.GetCapabilityManifest());
        return Results.Ok(BrickCatalogWireMapper.ToEntry(brick, hostCaps));
    }

    private static async Task<IResult> ExecuteBrickAsync(
        string brickId,
        [FromBody] BrickExecuteRequestDto? request,
        [FromServices] IBrickRegistry brickRegistry,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(brickId))
            return Results.BadRequest(new ProblemDetails { Title = "brickId is required" });
        if (request is null)
            return Results.BadRequest(new ProblemDetails { Title = "Request body is required" });
        if (!string.IsNullOrEmpty(request.BrickId) &&
            !string.Equals(request.BrickId, brickId, StringComparison.OrdinalIgnoreCase))
        {
            return Results.BadRequest(new ProblemDetails { Title = "BrickId in body must match route" });
        }

        var brick = brickRegistry.GetBrick(brickId);
        if (brick is null)
            return Results.NotFound();

        if (!Enum.TryParse<ImplementationType>(request.Implementation, true, out var implementation))
            implementation = ImplementationType.Deterministic;

        var context = BrickCatalogWireMapper.ToExecutionContext(request.ExecutionContext);
        var input = BrickValueSerializer.FromWireToBrickInput(request.Input);
        BrickInputDefaults.Apply(brick, input);

        try
        {
            var output = await brick.ExecuteAsync(input, implementation, context, cancellationToken).ConfigureAwait(false);
            return Results.Ok(new BrickExecuteResponseDto
            {
                Success = true,
                Summary = output.Summary,
                Output = BrickValueSerializer.ToWireDictionary(output)
            });
        }
        catch (Exception ex)
        {
            return Results.Ok(new BrickExecuteResponseDto
            {
                Success = false,
                Error = ex.Message
            });
        }
    }

    private static async Task<IResult> RunDirectorWorkflowAsync(
        [FromBody] DirectorRunRequest request,
        [FromServices] Orchestrator orchestrator,
        [FromServices] IMediator mediator,
        [FromServices] IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request?.Goal))
            return Results.BadRequest(new ProblemDetails { Title = "Goal is required" });

        var dailiesPath = ResolveDailiesPath(configuration);
        Directory.CreateDirectory(dailiesPath);

        DirectorDailyEntry? previousDaily = null;
        if (!string.IsNullOrWhiteSpace(request.ContinueFromDailyId))
        {
            if (!IsValidDailyId(request.ContinueFromDailyId))
                return Results.BadRequest(new ProblemDetails { Title = "ContinueFromDailyId is invalid" });

            var previousPath = Path.Combine(dailiesPath, $"{request.ContinueFromDailyId}.json");
            if (File.Exists(previousPath))
            {
                previousDaily = await TryReadDailyAsync(previousPath, cancellationToken);
            }
        }

        var prompt = BuildDirectorPrompt(request, previousDaily);
        var success = false;
        string? summary;
        string? orchestrationError = null;
        string? integratedOutputJson = null;

        try
        {
            var orchestrationResult = await orchestrator.OrchestrateAsync(prompt, cancellationToken);
            success = orchestrationResult.Success;
            summary = orchestrationResult.IntegratedOutput != null
                ? $"{orchestrationResult.IntegratedOutput.AgentOutputs.Count} agent(s) executed"
                : "No integrated output generated";
            integratedOutputJson = SerializeForDaily(orchestrationResult.IntegratedOutput?.IntegratedResults);
        }
        catch (Exception ex)
        {
            summary = "Orchestration failed before completion";
            orchestrationError = ex.Message;
        }

        ValidationResponse? validation = null;
        if (request.RunValidation)
        {
            try
            {
                var validationResult = await mediator.Send(new RunValidationCommand(request.ValidationFilter), cancellationToken);
                validation = new ValidationResponse(
                    validationResult.Passed,
                    validationResult.Message,
                    validationResult.TestsRun,
                    validationResult.TestsPassed,
                    validationResult.TestsFailed);
            }
            catch (Exception ex)
            {
                validation = new ValidationResponse(
                    false,
                    $"Validation failed: {ex.Message}",
                    0,
                    0,
                    0);
            }
        }

        var dailyId = $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}";
        var entry = new DirectorDailyEntry(
            dailyId,
            DateTimeOffset.UtcNow,
            request.Goal.Trim(),
            request.Notes?.Trim(),
            request.ContinueFromDailyId,
            success,
            summary,
            integratedOutputJson,
            validation,
            orchestrationError);

        var dailyPath = Path.Combine(dailiesPath, $"{dailyId}.json");
        await File.WriteAllTextAsync(dailyPath, JsonSerializer.Serialize(entry, DailySerializerOptions), cancellationToken);

        return Results.Ok(new DirectorRunResponse(
            entry.Success,
            entry.DailyId,
            dailyPath,
            entry.Summary,
            entry.IntegratedOutputJson,
            entry.Validation,
            entry.OrchestrationError,
            entry.ContinueFromDailyId));
    }

    private static async Task<IResult> ListDailiesAsync(
        [FromServices] IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var dailiesPath = ResolveDailiesPath(configuration);
        if (!Directory.Exists(dailiesPath))
            return Results.Ok(Array.Empty<DirectorDailySummary>());

        var summaries = new List<DirectorDailySummary>();
        foreach (var file in Directory.EnumerateFiles(dailiesPath, "*.json"))
        {
            var entry = await TryReadDailyAsync(file, cancellationToken);
            if (entry is null)
                continue;

            summaries.Add(new DirectorDailySummary(
                entry.DailyId,
                entry.CreatedAtUtc,
                entry.Goal,
                entry.Success,
                entry.Summary,
                entry.ContinueFromDailyId));
        }

        return Results.Ok(summaries.OrderByDescending(x => x.CreatedAtUtc));
    }

    private static async Task<IResult> GetDailyAsync(
        string dailyId,
        [FromServices] IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        if (!IsValidDailyId(dailyId))
            return Results.BadRequest(new ProblemDetails { Title = "dailyId is invalid" });

        var dailiesPath = ResolveDailiesPath(configuration);
        var dailyPath = Path.Combine(dailiesPath, $"{dailyId}.json");
        if (!File.Exists(dailyPath))
            return Results.NotFound(new ProblemDetails { Title = "dailyId not found" });

        var entry = await TryReadDailyAsync(dailyPath, cancellationToken);
        if (entry is null)
            return Results.NotFound(new ProblemDetails { Title = "daily entry is unreadable" });

        return Results.Ok(entry);
    }

    private static string ResolveDailiesPath(IConfiguration configuration)
    {
        var configuredPath = configuration["ASHLAR_DAILIES_PATH"];
        if (string.IsNullOrWhiteSpace(configuredPath))
            configuredPath = configuration["Ashlar:DailiesPath"];

        configuredPath ??= Path.Combine(AppContext.BaseDirectory, "dailies");
        return Path.GetFullPath(configuredPath);
    }

    private static bool IsValidDailyId(string dailyId)
    {
        if (string.IsNullOrWhiteSpace(dailyId))
            return false;

        if (dailyId.Contains("..", StringComparison.Ordinal))
            return false;

        return dailyId.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
    }

    private static async Task<DirectorDailyEntry?> TryReadDailyAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<DirectorDailyEntry>(stream, DailySerializerOptions, cancellationToken);
        }
        catch
        {
            return null;
        }
    }

    private static string BuildDirectorPrompt(DirectorRunRequest request, DirectorDailyEntry? previousDaily)
    {
        if (previousDaily is null)
            return request.Goal.Trim();

        var previousSummary = previousDaily.Summary ?? "No prior summary";
        return
            $"Continue from daily {previousDaily.DailyId}. " +
            $"Previous goal: {previousDaily.Goal}. " +
            $"Previous summary: {previousSummary}. " +
            $"Director notes for this iteration: {request.Goal.Trim()}";
    }

    private static string? SerializeForDaily(object? payload)
    {
        if (payload is null)
            return null;

        try
        {
            return JsonSerializer.Serialize(payload, DailySerializerOptions);
        }
        catch
        {
            return payload.ToString();
        }
    }

    private static NodeCapabilityManifestDto ToDto(NodeCapabilityManifest manifest)
    {
        return new NodeCapabilityManifestDto
        {
            NodeId = manifest.NodeId,
            Tier = manifest.Tier switch
            {
                NodeTier.Core => NodeTierDto.Core,
                NodeTier.Standard => NodeTierDto.Standard,
                NodeTier.Micro => NodeTierDto.Micro,
                _ => NodeTierDto.Nano
            },
            Platform = manifest.Platform switch
            {
                PlatformType.Windows => PlatformTypeDto.Windows,
                PlatformType.macOS => PlatformTypeDto.macOS,
                PlatformType.Linux => PlatformTypeDto.Linux,
                PlatformType.iOS => PlatformTypeDto.iOS,
                PlatformType.Android => PlatformTypeDto.Android,
                _ => PlatformTypeDto.Unknown
            },
            HotModelIds = manifest.HotModelIds,
            AvailableModelIds = manifest.AvailableModelIds,
            SupportedCapabilities = manifest.SupportedCapabilities.Select(cap => cap switch
            {
                TaskCapability.CodeGeneration => TaskCapabilityDto.CodeGeneration,
                TaskCapability.Embeddings => TaskCapabilityDto.Embeddings,
                TaskCapability.Vision => TaskCapabilityDto.Vision,
                TaskCapability.Reasoning => TaskCapabilityDto.Reasoning,
                TaskCapability.Classification => TaskCapabilityDto.Classification,
                _ => TaskCapabilityDto.TextGeneration
            }).ToArray(),
            AcceptingRemoteWork = manifest.AcceptingRemoteWork,
            GeneratedAt = manifest.GeneratedAt
        };
    }

    private static IResult GetSecurityAdvisory(IOptions<AshlarSecurityOptions> optionsAccessor)
    {
        var options = optionsAccessor.Value;
        if (!Enum.TryParse<AshlarExposureProfile>(options.ExposureProfile, true, out var profile))
            profile = AshlarExposureProfile.Localhost;

        var (summary, hints) = SecurityAdvisoryContent.For(profile);
        var custom = string.IsNullOrWhiteSpace(options.CustomAdvisory) ? null : options.CustomAdvisory.Trim();
        return Results.Ok(new SecurityAdvisoryResponse(
            profile.ToString(),
            summary,
            hints,
            custom,
            options.ShowAdvisoryInPortal));
    }

    private static async Task<IResult> GetBackgroundAgentSummaryAsync(
        [FromServices] IServiceProvider services,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.CompletedTask;

        var registry = services.GetService<IBackgroundAgentRegistry>();
        var modeStore = services.GetService<IAggressivenessModeStore>();
        var mode = modeStore?.GetMode() ?? BackgroundAgentAggressivenessMode.Active;
        var agents = registry?.GetAll() ?? [];
        var summaries = agents.Select(agent => new BackgroundAgentSnapshot(
            agent.Config.Id,
            agent.Config.Name,
            agent.Config.Role,
            agent.State.ToString(),
            agent.ExecutionCount,
            agent.SuccessCount,
            agent.FailureCount,
            agent.LastStartedAt,
            agent.LastCompletedAt,
            agent.LastError))
            .OrderBy(s => s.AgentId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return Results.Ok(new BackgroundAgentSummaryResponse(mode.ToString(), summaries, summaries.Length));
    }

    private static async Task<IResult> GetRuntimeStudioMetricsAsync(
        [FromServices] IObjectiveStore objectives,
        [FromServices] IChangeProposalStore proposals,
        [FromServices] IHostEnvironment host,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.CompletedTask;

        var resolved = RuntimeStudioPathResolver.Resolve(host.ContentRootPath);
        var disk = RuntimeStudioMetricsCollector.Collect(objectives, proposals, resolved.ObservationsPath);

        return Results.Ok(new RuntimeStudioMetricsResponse(
            objectives.Location,
            proposals.Location,
            resolved.ObservationsPath,
            disk.ObjectivesByStatus,
            new RuntimeStudioObjectiveSlaHints(
                disk.ObjectiveSla.OldestPendingAgeHours,
                disk.ObjectiveSla.OldestInProgressAgeHours,
                disk.ObjectiveSla.PendingCount,
                disk.ObjectiveSla.InProgressCount,
                disk.ObjectiveSla.BlockedCount),
            disk.ProposalsByStatus,
            disk.ObservationsFileBytes,
            disk.ObservationsTailLineCount,
            disk.ObservationsLastTimestamp));
    }

    private static async Task<IResult> GetTrustDashboardAsync(
        [FromServices] IDataDecisionAuditLog? auditLog,
        [FromServices] IAccessBoundary? accessBoundary,
        [FromQuery] int count = 25,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.CompletedTask;
        count = Math.Clamp(count, 1, 200);

        var paused = accessBoundary?.IsObservationPaused ?? false;
        var audit = auditLog?.GetRecent(count) ?? [];
        var byType = audit
            .GroupBy(entry => entry.EventType, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

        return Results.Ok(new TrustDashboardResponse(
            AccessBoundaryRegistered: accessBoundary != null,
            AuditLogRegistered: auditLog != null,
            IsPaused: paused,
            RecentAudit: audit,
            AuditByType: byType));
    }

    private static async Task<IResult> SetTrustPauseAsync(
        [FromBody] TrustPauseRequest request,
        [FromServices] IAccessBoundary? accessBoundary,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.CompletedTask;
        if (accessBoundary == null)
            return Results.BadRequest(new ProblemDetails { Title = "Access boundary is not registered." });

        accessBoundary.SetPause(request.Paused);
        return Results.Ok(new TrustBoundaryMutationResponse(
            true,
            "pause",
            request.Paused ? "paused" : "resumed"));
    }

    private static async Task<IResult> SetTrustRuleAsync(
        [FromBody] TrustRuleRequest request,
        [FromServices] IAccessBoundary? accessBoundary,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.CompletedTask;
        if (accessBoundary == null)
            return Results.BadRequest(new ProblemDetails { Title = "Access boundary is not registered." });

        if (string.IsNullOrWhiteSpace(request.Category) && string.IsNullOrWhiteSpace(request.Source))
            return Results.BadRequest(new ProblemDetails { Title = "Category or Source is required." });

        if (!string.IsNullOrWhiteSpace(request.Category))
        {
            accessBoundary.SetCategoryAllowed(request.Category.Trim(), request.Allowed);
            return Results.Ok(new TrustBoundaryMutationResponse(true, "category", request.Allowed ? "allowed" : "denied"));
        }

        accessBoundary.SetSourceAllowed(request.Source!.Trim(), request.Allowed);
        return Results.Ok(new TrustBoundaryMutationResponse(true, "source", request.Allowed ? "allowed" : "denied"));
    }

    private static async Task<IResult> QueryKnowledgeAsync(
        [FromServices] IKnowledgeQueryService queryService,
        [FromQuery] string? sources = null,
        [FromQuery] string? dataType = null,
        [FromQuery] string? eventType = null,
        [FromQuery] int maxCount = 100,
        [FromQuery] int offset = 0,
        [FromQuery] DateTimeOffset? since = null,
        [FromQuery] DateTimeOffset? until = null,
        CancellationToken cancellationToken = default)
    {
        var selectedSources = ParseKnowledgeSources(sources);
        var request = new KnowledgeQueryRequest
        {
            Since = since,
            Until = until,
            DataType = dataType,
            EventType = eventType,
            MaxCount = maxCount,
            Offset = offset,
            Sources = selectedSources
        };
        var result = await queryService.QueryAsync(request, cancellationToken).ConfigureAwait(false);
        return Results.Ok(result);
    }

    private static IReadOnlyList<KnowledgeSource> ParseKnowledgeSources(string? sources)
    {
        if (string.IsNullOrWhiteSpace(sources))
            return Array.Empty<KnowledgeSource>();

        return sources
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(token => Enum.TryParse<KnowledgeSource>(token, true, out var parsed) ? parsed : (KnowledgeSource?)null)
            .Where(parsed => parsed.HasValue)
            .Select(parsed => parsed!.Value)
            .Distinct()
            .ToArray();
    }

    private static async Task<IResult> GetTrustStatusAsync(
        [FromServices] IAccessBoundary accessBoundary,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.CompletedTask;

        var activePack = accessBoundary.GetActivePolicyPack();
        return Results.Ok(new TrustStatusResponse(
            accessBoundary.IsObservationPaused,
            accessBoundary.IsObservationPaused ? "Observation paused" : "Observing",
            activePack?.Id,
            activePack?.Version));
    }

    private static Task<IResult> CreateOrganizationAsync(
        HttpContext httpContext,
        [FromBody] CreateOrganizationRequest request,
        [FromServices] IOrganizationStore orgStore,
        [FromServices] IOptions<AshlarProductOptions> productOptions)
    {
        if (string.IsNullOrWhiteSpace(request?.Name))
            return Task.FromResult<IResult>(Results.BadRequest(new ProblemDetails { Title = "Name is required" }));

        Organization organization;
        try
        {
            organization = orgStore.CreateOrganization(request.Name, request.TenantId);
        }
        catch (ArgumentException ex)
        {
            return Task.FromResult<IResult>(Results.BadRequest(new ProblemDetails { Title = "Invalid organization", Detail = ex.Message }));
        }

        if (AshlarHttpOrg.TryResolveUser(httpContext.Request, productOptions.Value, out var creatorId, out _))
            orgStore.AddMember(organization.OrgId, creatorId, OrganizationRole.Admin);

        return Task.FromResult<IResult>(Results.Ok(organization));
    }

    private static Task<IResult> GetOrganizationAsync(
        HttpContext httpContext,
        string orgId,
        [FromServices] IOrganizationStore orgStore,
        [FromServices] IOptions<AshlarProductOptions> productOptions)
    {
        if (!AshlarHttpOrg.TryResolveOrgContext(
                httpContext.Request,
                productOptions.Value,
                orgStore,
                out var organization,
                out _,
                out _,
                out var error) ||
            !string.Equals(organization.OrgId, orgId.Trim(), StringComparison.Ordinal))
        {
            return Task.FromResult<IResult>(Results.Json(
                new ProblemDetails { Title = "Forbidden", Detail = error ?? "Organization access denied." },
                statusCode: StatusCodes.Status403Forbidden));
        }

        return Task.FromResult<IResult>(Results.Ok(organization));
    }

    private static Task<IResult> ListOrganizationMembersAsync(
        HttpContext httpContext,
        string orgId,
        [FromServices] IOrganizationStore orgStore,
        [FromServices] IOptions<AshlarProductOptions> productOptions)
    {
        if (!AshlarHttpOrg.TryResolveOrgContext(
                httpContext.Request,
                productOptions.Value,
                orgStore,
                out var organization,
                out _,
                out _,
                out var error) ||
            !string.Equals(organization.OrgId, orgId.Trim(), StringComparison.Ordinal))
        {
            return Task.FromResult<IResult>(Results.Json(
                new ProblemDetails { Title = "Forbidden", Detail = error ?? "Organization access denied." },
                statusCode: StatusCodes.Status403Forbidden));
        }

        return Task.FromResult<IResult>(Results.Ok(orgStore.GetMembers(orgId.Trim())));
    }

    private static Task<IResult> AddOrganizationMemberAsync(
        HttpContext httpContext,
        string orgId,
        [FromBody] AddOrganizationMemberRequest request,
        [FromServices] IOrganizationStore orgStore,
        [FromServices] IOptions<AshlarProductOptions> productOptions)
    {
        if (string.IsNullOrWhiteSpace(request?.UserId))
            return Task.FromResult<IResult>(Results.BadRequest(new ProblemDetails { Title = "UserId is required" }));

        if (!AshlarHttpOrg.TryResolveOrgContext(
                httpContext.Request,
                productOptions.Value,
                orgStore,
                out var organization,
                out _,
                out var actorRole,
                out var error) ||
            !string.Equals(organization.OrgId, orgId.Trim(), StringComparison.Ordinal))
        {
            return Task.FromResult<IResult>(Results.Json(
                new ProblemDetails { Title = "Forbidden", Detail = error ?? "Organization access denied." },
                statusCode: StatusCodes.Status403Forbidden));
        }

        if (actorRole != OrganizationRole.Admin)
        {
            return Task.FromResult<IResult>(Results.Json(
                new ProblemDetails { Title = "Forbidden", Detail = "Only organization admins can add members." },
                statusCode: StatusCodes.Status403Forbidden));
        }

        if (!Enum.TryParse<OrganizationRole>(request.Role, ignoreCase: true, out var role))
            role = OrganizationRole.Member;

        try
        {
            var member = orgStore.AddMember(orgId.Trim(), request.UserId, role);
            return Task.FromResult<IResult>(Results.Ok(member));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return Task.FromResult<IResult>(Results.BadRequest(new ProblemDetails { Title = "Invalid member", Detail = ex.Message }));
        }
    }

    private static bool TryResolveProductTenant(
        HttpContext httpContext,
        AshlarProductOptions options,
        out string tenantId,
        out string? errorDetail)
    {
        if (!AshlarHttpTenant.TryResolve(httpContext.Request, options, out tenantId, out errorDetail))
            return false;

        if (!options.RequireOrgMembership)
            return true;

        var orgStore = httpContext.RequestServices.GetService<IOrganizationStore>();
        if (orgStore is null)
        {
            errorDetail = "Organization store is not configured.";
            return false;
        }

        if (!AshlarHttpOrg.TryResolveOrgContext(
                httpContext.Request,
                options,
                orgStore,
                out var organization,
                out _,
                out _,
                out errorDetail))
            return false;

        if (!AshlarHttpOrg.TenantMatchesOrg(tenantId, organization))
        {
            errorDetail = $"Tenant '{tenantId}' does not match organization scope '{organization.TenantId}'.";
            return false;
        }

        tenantId = organization.TenantId;
        return true;
    }

    // "N agent(s) executed" is meaningless on its own when N is 0. On failure, append the
    // most recent escalation's description so the caller sees WHY (e.g. barrier context
    // missing) instead of a bare count with escalations:N and no reason. Most recent, not
    // first: EscalationManager is a host singleton, so the list spans earlier requests.
    //
    // Redaction: the generic "AgentExecution" escalation embeds the agent's ex.Message,
    // and this endpoint's own catch deliberately hides exception text outside Development.
    // Keep that posture — forward the description only for escalations whose text is
    // ours and static (the barrier code), or when running in Development.
    private static string? BuildOrchestrationSummary(OrchestrationResult result)
    {
        if (result.IntegratedOutput == null)
            return null;

        var summary = $"{result.IntegratedOutput.AgentOutputs.Count} agent(s) executed";
        if (result.Success)
            return summary;

        var last = result.Escalations.LastOrDefault(e => !string.IsNullOrWhiteSpace(e.Description));
        if (last is null)
            return summary;

        var safeToForward = IsDevelopment()
            || string.Equals(last.IssueType, BarrierContextMissingIssueType, StringComparison.Ordinal);
        return safeToForward
            ? $"{summary}: {last.Description}"
            : $"{summary}: see escalations ({last.IssueType})";
    }

    /// <summary>
    /// Mirrors <c>BarrierContextMissingException.ErrorCode</c>, which is an instance property
    /// and so cannot be used as a constant here.
    /// </summary>
    private const string BarrierContextMissingIssueType = "BARRIER_CONTEXT_MISSING";

    // The escalation issue type is the closest thing to a canonical code the orchestrator
    // emits (BarrierContextMissingException escalates under its own ErrorCode).
    private static string? ResolveOrchestrationErrorCode(OrchestrationResult result) =>
        result.Success
            ? null
            : result.Escalations
                .Select(e => e.IssueType)
                .LastOrDefault(t => !string.IsNullOrWhiteSpace(t));

    // Preferences file lives next to the config file so it survives Docker
    // container restarts when the config directory is on a volume mount.
    private static bool IsDevelopment() =>
        string.Equals(Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"), "Development", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT"), "Development", StringComparison.OrdinalIgnoreCase);

    private static readonly string PreferencesPath = ResolvePreferencesPath();

    private static string ResolvePreferencesPath()
    {
        var configPath = Environment.GetEnvironmentVariable("ASHLAR_CONFIG_PATH");
        if (!string.IsNullOrWhiteSpace(configPath))
        {
            var dir = Path.GetDirectoryName(configPath);
            if (!string.IsNullOrEmpty(dir))
                return Path.Combine(dir, "preferences.json");
        }
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ashlar", "preferences.json");
    }

    private static async Task<IResult> GetPreferencesAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(PreferencesPath))
            return Results.Ok(new PreferencesResponse(null, null, null, null, null));

        var json = await File.ReadAllTextAsync(PreferencesPath, cancellationToken);
        var prefs = System.Text.Json.JsonSerializer.Deserialize<PreferencesResponse>(json, DailySerializerOptions);
        return Results.Ok(prefs ?? new PreferencesResponse(null, null, null, null, null));
    }

    private static async Task<IResult> SavePreferencesAsync(
        [FromBody] PreferencesRequest request,
        CancellationToken cancellationToken)
    {
        var dir = Path.GetDirectoryName(PreferencesPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var prefs = new PreferencesResponse(
            request.DisplayName, request.Theme, request.DefaultFocus, request.PreferredProvider, DateTimeOffset.UtcNow);
        var json = System.Text.Json.JsonSerializer.Serialize(prefs, DailySerializerOptions);
        await File.WriteAllTextAsync(PreferencesPath, json, cancellationToken);
        return Results.Ok(prefs);
    }

    private static async Task<IResult> GetActivityFeedAsync(
        [FromServices] Ashlar.BackgroundAgents.Logging.IBackgroundAgentLogStore? logStore,
        [FromServices] Ashlar.Core.Application.Trust.Ports.IDataDecisionAuditLog? auditLog,
        [FromServices] IBackgroundAgentRegistry? agentRegistry,
        [FromQuery] int maxCount = 30,
        CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        var entries = new List<ActivityEntry>();

        if (auditLog != null)
        {
            var auditEntries = auditLog.GetRecent(maxCount, since: DateTimeOffset.UtcNow.AddHours(-24));
            foreach (var e in auditEntries)
            {
                entries.Add(new ActivityEntry(
                    e.SourceId ?? "system",
                    e.EventType ?? "audit",
                    e.Reason ?? e.FieldOrType ?? e.DataType ?? "event recorded",
                    e.Timestamp));
            }
        }

        var sorted = entries.OrderByDescending(e => e.Timestamp).Take(maxCount).ToList();
        return Results.Ok(new ActivityFeedResponse(sorted));
    }

    private static async Task<IResult> GenerateChangelogAsync(
        [FromBody] ChangelogRequest? request,
        [FromServices] IKnowledgeQueryService queryService,
        [FromServices] Ashlar.Core.Application.Trust.Ports.IDataDecisionAuditLog? auditLog,
        CancellationToken cancellationToken)
    {
        var sinceDate = DateTimeOffset.TryParse(request?.Since, out var parsed)
            ? parsed
            : DateTimeOffset.UtcNow.AddDays(-7);
        var maxEntries = request?.MaxEntries ?? 50;

        var knowledgeRequest = new KnowledgeQueryRequest
        {
            Since = sinceDate,
            MaxCount = maxEntries,
            Sources = new[] { KnowledgeSource.Adaptation, KnowledgeSource.Pattern }
        };
        var knowledge = await queryService.QueryAsync(knowledgeRequest, cancellationToken);

        var changeEntries = new List<ChangelogEntry>();
        foreach (var item in knowledge.Entries)
        {
            var prov = item.Provenance.Count > 0 ? string.Join(", ", item.Provenance.Values) : null;
            changeEntries.Add(new ChangelogEntry(
                item.Source.ToString(),
                item.Summary ?? item.DataType ?? "change recorded",
                item.Timestamp,
                prov));
        }

        if (auditLog != null)
        {
            var auditEntries = auditLog.GetRecent(maxEntries, since: sinceDate);
            foreach (var e in auditEntries.Where(e => e.EventType is "BoundaryChange" or "AmbientAction"))
            {
                changeEntries.Add(new ChangelogEntry(
                    e.EventType ?? "audit",
                    e.Reason ?? e.ChangeType ?? "system event",
                    e.Timestamp,
                    e.SourceId));
            }
        }

        var sorted = changeEntries.OrderByDescending(e => e.Timestamp).Take(maxEntries).ToList();
        var summary = sorted.Count == 0
            ? $"No changes recorded since {sinceDate:yyyy-MM-dd}."
            : $"{sorted.Count} changes since {sinceDate:yyyy-MM-dd}: {sorted.GroupBy(e => e.Category).Select(g => $"{g.Count()} {g.Key}").Aggregate((a, b) => a + ", " + b)}.";

        return Results.Ok(new ChangelogResponse(summary, sorted, DateTimeOffset.UtcNow));
    }

    private static bool IsBuiltInAuthActive(AshlarSecurityOptions sec)
    {
        if (Enum.TryParse<AshlarAuthorizationMode>(sec.AuthorizationMode, true, out var mode)
            && mode != AshlarAuthorizationMode.None)
            return true;
        return sec.RequireApiKeyForMutatingEndpoints;
    }

    private static bool AreBuiltInAuthCredentialsConfigured(AshlarSecurityOptions sec)
    {
        if (!Enum.TryParse<AshlarAuthorizationMode>(sec.AuthorizationMode, true, out var mode))
            mode = AshlarAuthorizationMode.None;

        if (mode == AshlarAuthorizationMode.None && !sec.RequireApiKeyForMutatingEndpoints)
            return true;

        if (mode == AshlarAuthorizationMode.None && sec.RequireApiKeyForMutatingEndpoints)
            mode = AshlarAuthorizationMode.ApiKey;

        return mode switch
        {
            AshlarAuthorizationMode.ApiKey =>
                !string.IsNullOrWhiteSpace(sec.ApiKey) || !string.IsNullOrWhiteSpace(sec.CopilotScopedApiKey),
            AshlarAuthorizationMode.BearerToken => !string.IsNullOrWhiteSpace(sec.BearerToken),
            AshlarAuthorizationMode.Basic =>
                !string.IsNullOrWhiteSpace(sec.BasicAuthUsername) && !string.IsNullOrWhiteSpace(sec.BasicAuthPassword),
            AshlarAuthorizationMode.ApiKeyOrBearerToken =>
                (!string.IsNullOrWhiteSpace(sec.ApiKey) || !string.IsNullOrWhiteSpace(sec.CopilotScopedApiKey))
                || !string.IsNullOrWhiteSpace(sec.BearerToken),
            AshlarAuthorizationMode.ApiKeyOrBasic =>
                (!string.IsNullOrWhiteSpace(sec.ApiKey) || !string.IsNullOrWhiteSpace(sec.CopilotScopedApiKey))
                || (!string.IsNullOrWhiteSpace(sec.BasicAuthUsername) && !string.IsNullOrWhiteSpace(sec.BasicAuthPassword)),
            AshlarAuthorizationMode.BearerTokenOrBasic =>
                !string.IsNullOrWhiteSpace(sec.BearerToken)
                || (!string.IsNullOrWhiteSpace(sec.BasicAuthUsername) && !string.IsNullOrWhiteSpace(sec.BasicAuthPassword)),
            AshlarAuthorizationMode.Any =>
                !string.IsNullOrWhiteSpace(sec.ApiKey)
                || !string.IsNullOrWhiteSpace(sec.CopilotScopedApiKey)
                || !string.IsNullOrWhiteSpace(sec.BearerToken)
                || (!string.IsNullOrWhiteSpace(sec.BasicAuthUsername) && !string.IsNullOrWhiteSpace(sec.BasicAuthPassword)),
            _ => !string.IsNullOrWhiteSpace(sec.ApiKey) || !string.IsNullOrWhiteSpace(sec.CopilotScopedApiKey)
        };
    }

    private static async Task<IResult> GetOnboardingStatusAsync(
        HttpContext httpContext,
        [FromServices] Ashlar.Infrastructure.Execution.IProviderFactory providerFactory,
        [FromServices] ICopilotTaskStore copilotTaskStore,
        [FromServices] IAccessBoundary accessBoundary,
        [FromServices] IConfiguration configuration,
        [FromServices] IOptions<AshlarSecurityOptions> securityOptions,
        [FromServices] IOptions<AshlarProductOptions> productOptions,
        CancellationToken cancellationToken)
    {
        var providers = new List<ProviderStatus>();
        foreach (var name in new[] { "ollama", "openai", "azure", "mock", "local" })
        {
            var available = providerFactory.IsProviderAvailable(name);
            var reason = name switch
            {
                "openai" when !available => "Set OPENAI_API_KEY",
                "azure" when !available => "Set AZURE_OPENAI_ENDPOINT, AZURE_OPENAI_API_KEY, AZURE_OPENAI_DEPLOYMENT",
                "mock" when !available => "Set ASHLAR_ALLOW_MOCK=1",
                "local" when !available => "Set ASHLAR_LOCAL_MODEL_PATH",
                _ => null
            };
            providers.Add(new ProviderStatus(name, available, reason));
        }

        if (!TryResolveProductTenant(httpContext, productOptions.Value, out var tenantId, out var tenantError))
            return Results.BadRequest(new ProblemDetails { Title = "Invalid tenant", Detail = tenantError });

        var tasks = await copilotTaskStore.QueryAsync(1, null, tenantId, cancellationToken);
        var hasTasks = tasks.Count > 0;

        var dailiesPath = ResolveDailiesPath(configuration);
        var hasDailies = Directory.Exists(dailiesPath) && Directory.EnumerateFiles(dailiesPath, "*.json").Any();

        var activePack = accessBoundary.GetActivePolicyPack();
        var configPath = Environment.GetEnvironmentVariable("ASHLAR_CONFIG_PATH")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ashlar", "config.json");

        var sec = securityOptions.Value;
        var authActive = IsBuiltInAuthActive(sec);

        // The default (MEAI) model path resolves its Ollama endpoint independently of the provider
        // factory that produced the "ollama" ProviderStatus above; surface it so operators (and the
        // prod-dry-run gate) can see when the two disagree. IOptions<T> always resolves (open generic),
        // so registration of the keyed local Ollama client is the signal that the pipeline is enabled.
        var meaiRegistered = httpContext.RequestServices.GetService<IServiceProviderIsKeyedService>()
            ?.IsKeyedService(typeof(Microsoft.Extensions.AI.IChatClient), Ashlar.AI.Pipeline.MeaiTargetKeys.LocalOllama) == true;
        var meaiOptions = meaiRegistered
            ? httpContext.RequestServices.GetService<IOptions<Ashlar.AI.Pipeline.MeaiPipelineOptions>>()?.Value
            : null;
        var meaiOllamaBaseUrl = meaiRegistered
            ? Ashlar.AI.Pipeline.Clients.OllamaEndpointResolver.ResolveBaseUrl(meaiOptions)
            : null;
        var meaiOllamaModel = meaiRegistered
            ? Ashlar.AI.Pipeline.Clients.OllamaEndpointResolver.ResolveModel(meaiOptions)
            : null;

        return Results.Ok(new OnboardingStatusResponse(
            IsFirstRun: !hasTasks && !hasDailies,
            ApiReachable: true,
            Providers: providers,
            HasCopilotTasks: hasTasks,
            HasDailies: hasDailies,
            ConfigPath: configPath,
            ActiveTrustPack: activePack?.Id,
            BuiltInAuthActive: authActive,
            BuiltInCredentialsConfigured: AreBuiltInAuthCredentialsConfigured(sec),
            RequireAuthForCopilotReads: sec.RequireAuthForCopilotReadApis,
            CopilotScopedKeyConfigured: !string.IsNullOrWhiteSpace(sec.CopilotScopedApiKey),
            ResolvedTenantId: tenantId,
            MeaiOllamaBaseUrl: meaiOllamaBaseUrl,
            MeaiOllamaModel: meaiOllamaModel));
    }
}
