using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ashlar.Abstractions.Barriers;
using Ashlar.Abstractions.Barriers.Identity;
using Ashlar.CLI.Formatting;
using Ashlar.CLI.Runtime;
using Ashlar.Core.Application.Ephemeral.Ports;
using Ashlar.Orchestration.Coordination;
using Ashlar.Orchestration.Models;
using Ashlar.Orchestration.Barriers;
using Ashlar.Orchestration.Routing;
using Ashlar.Runtime.Barriers;

namespace Ashlar.CLI.Commands;

/// <summary>
/// CLI command for orchestrating agent execution.
/// 
/// Provides the `ashlar orchestrate` command that:
/// - Takes a user request as input
/// - Delegates to Orchestrator for full workflow execution
/// - Displays progress and results in human-readable or JSON format
/// - Handles errors and provides appropriate exit codes
/// 
/// Part of the CLI layer, following the command pattern for user interactions.
/// </summary>
public class OrchestrateCommand
{
    /// <summary>Structured orchestration payload returned by programmatic callers.</summary>
    public sealed record StructuredOrchestrationResult(
        bool Ok,
        string CorrelationId,
        string? Error,
        string? ErrorCode,
        bool? Success = null,
        int? AgentCount = null,
        int? Conflicts = null,
        int? Escalations = null);

    private readonly Orchestrator _orchestrator;
    private readonly IConsoleRenderer _renderer;
    private readonly ILogger<OrchestrateCommand> _logger;
    private readonly IOrchestrationRuntimeSpecAccessor _runtime;
    private readonly IEphemeralModelLifecycle? _ephemeralLifecycle;
    private readonly IBarrierContextAccessor _barrierContextAccessor;
    private readonly BarrierHierarchy _barrierHierarchy;
    private readonly BarrierOptions _barrierOptions;
    private readonly IBarrierAuditLog _barrierAuditLog;
    private readonly IBarrierIdentityResolverPipeline _barrierResolverPipeline;

    /// <summary>Creates the orchestrate command with orchestrator, rendering, and barrier dependencies.</summary>
    public OrchestrateCommand(
        Orchestrator orchestrator,
        IConsoleRenderer renderer,
        ILogger<OrchestrateCommand> logger,
        IOrchestrationRuntimeSpecAccessor runtime,
        IBarrierContextAccessor barrierContextAccessor,
        BarrierHierarchy barrierHierarchy,
        IOptions<BarrierOptions> barrierOptions,
        IBarrierAuditLog barrierAuditLog,
        IBarrierIdentityResolverPipeline barrierResolverPipeline,
        IEphemeralModelLifecycle? ephemeralLifecycle = null)
    {
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _barrierContextAccessor = barrierContextAccessor ?? throw new ArgumentNullException(nameof(barrierContextAccessor));
        _barrierHierarchy = barrierHierarchy ?? throw new ArgumentNullException(nameof(barrierHierarchy));
        _barrierOptions = barrierOptions?.Value ?? throw new ArgumentNullException(nameof(barrierOptions));
        _barrierAuditLog = barrierAuditLog ?? throw new ArgumentNullException(nameof(barrierAuditLog));
        _barrierResolverPipeline = barrierResolverPipeline ?? throw new ArgumentNullException(nameof(barrierResolverPipeline));
        _ephemeralLifecycle = ephemeralLifecycle;
    }

    /// <summary>
    /// Executes the orchestrate command.
    /// </summary>
    /// <param name="request">User request to orchestrate</param>
    /// <param name="barrierLevel">Optional barrier level override.</param>
    /// <param name="preferredRegion">Optional preferred routing region override.</param>
    /// <param name="json">Whether to output JSON format</param>
    /// <param name="verbose">Whether to show verbose progress output</param>
    /// <returns>Exit code (0 for success, non-zero for errors)</returns>
    public Task<int> ExecuteAsync(string request, bool json, bool verbose)
        => ExecuteAsync(
            request,
            runtimeSpecPath: null,
            runtimeSpecJson: null,
            preferModel: null,
            provider: null,
            barrierLevel: null,
            preferredRegion: null,
            json,
            verbose);

    /// <summary>Runs orchestration and returns a structured result without writing console output.</summary>
    public async Task<StructuredOrchestrationResult> ExecuteStructuredAsync(
        string request,
        string? runtimeSpecPath,
        string? runtimeSpecJson,
        string? preferModel,
        string? provider,
        string? barrierLevel,
        string? preferredRegion,
        CancellationToken cancellationToken,
        string? jwt = null,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        var correlationId = Guid.NewGuid().ToString();
        using var scope = _logger.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = correlationId
        });

        try
        {
            var spec = OrchestrationRuntimeSpecLoader.Load(runtimeSpecPath, runtimeSpecJson);
            if (!string.IsNullOrWhiteSpace(preferModel))
            {
                spec = spec with { Model = spec.Model with { Prefer = preferModel!.Trim() } };
            }
            if (!string.IsNullOrWhiteSpace(provider))
            {
                spec = spec with { Model = spec.Model with { Provider = provider!.Trim() } };
            }
            if (!string.IsNullOrWhiteSpace(barrierLevel))
            {
                spec = spec with { BarrierLevel = barrierLevel.Trim() };
            }
            if (!string.IsNullOrWhiteSpace(preferredRegion))
            {
                spec = spec with { PreferredRegion = preferredRegion.Trim() };
            }

            await InitializeBarrierContextAsync(
                barrierLevel: spec.BarrierLevel,
                jwt: jwt,
                headers: headers,
                correlationId: correlationId,
                cancellationToken: cancellationToken);

            var modelPrefer = spec.Model?.Prefer;
            OrchestrationResult result;
            if (_ephemeralLifecycle != null)
            {
                await using var session = await _ephemeralLifecycle.StartSessionAsync(modelPrefer);
                using var _ = _runtime.Begin(spec);
                result = await _orchestrator.OrchestrateAsync(request);
            }
            else
            {
                using var _ = _runtime.Begin(spec);
                result = await _orchestrator.OrchestrateAsync(request);
            }

            // Same judgement as the console path: an orchestration where no agent completed is not
            // ok, and a programmatic caller must not read a different verdict from a person.
            var didWork = Formatting.OrchestrationWorkReport.DidWork(result);
            return new StructuredOrchestrationResult(
                Ok: result.Success && didWork,
                CorrelationId: correlationId,
                Error: didWork ? null : "orchestration completed with no agent completing — nothing was produced",
                ErrorCode: didWork ? null : "ORCHESTRATION_NO_WORK",
                Success: result.Success && didWork,
                AgentCount: result.Decomposition?.Agents.Count ?? 0,
                Conflicts: result.Conflicts.Count,
                Escalations: result.Escalations.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Orchestration failed");
            var errorCode = ex switch
            {
                BarrierContextMissingException b => b.ErrorCode,
                BarrierElevationException b => b.ErrorCode,
                BarrierCeilingExceededException b => b.ErrorCode,
                ArgumentException a when string.Equals(a.ParamName, "level", StringComparison.Ordinal) => "BARRIER_VALIDATION_FAILED",
                EndpointUnavailableException => "ENDPOINT_UNAVAILABLE",
                _ => "UNEXPECTED_ERROR"
            };
            return new StructuredOrchestrationResult(
                Ok: false,
                CorrelationId: correlationId,
                Error: ex.Message,
                ErrorCode: errorCode,
                Success: false,
                AgentCount: 0,
                Conflicts: 0,
                Escalations: 0);
        }
    }

    /// <summary>Executes orchestration with runtime spec overrides and writes console or JSON output.</summary>
    public async Task<int> ExecuteAsync(
        string request,
        string? runtimeSpecPath,
        string? runtimeSpecJson,
        string? preferModel,
        string? provider,
        string? barrierLevel,
        string? preferredRegion,
        bool json,
        bool verbose,
        string? jwt = null,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        var correlationId = Guid.NewGuid().ToString();
        using var scope = _logger.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = correlationId
        });

        if (verbose)
        {
            _renderer.RenderProgressStart($"CorrelationId={correlationId} :: orchestrating request: {request}");
        }

        try
        {
            var spec = OrchestrationRuntimeSpecLoader.Load(runtimeSpecPath, runtimeSpecJson);
            if (!string.IsNullOrWhiteSpace(preferModel))
            {
                spec = spec with { Model = spec.Model with { Prefer = preferModel!.Trim() } };
            }
            if (!string.IsNullOrWhiteSpace(provider))
            {
                spec = spec with { Model = spec.Model with { Provider = provider!.Trim() } };
            }
            if (!string.IsNullOrWhiteSpace(barrierLevel))
            {
                spec = spec with { BarrierLevel = barrierLevel.Trim() };
            }
            if (!string.IsNullOrWhiteSpace(preferredRegion))
            {
                spec = spec with { PreferredRegion = preferredRegion.Trim() };
            }

            await InitializeBarrierContextAsync(
                barrierLevel: spec.BarrierLevel,
                jwt: jwt,
                headers: headers,
                correlationId: correlationId,
                cancellationToken: CancellationToken.None);

            var modelPrefer = spec.Model?.Prefer;
            OrchestrationResult result;
            if (_ephemeralLifecycle != null)
            {
                await using var session = await _ephemeralLifecycle.StartSessionAsync(modelPrefer);
                using var _ = _runtime.Begin(spec);
                result = await _orchestrator.OrchestrateAsync(request);
            }
            else
            {
                using var _ = _runtime.Begin(spec);
                result = await _orchestrator.OrchestrateAsync(request);
            }

            if (json)
            {
                var didWork = Formatting.OrchestrationWorkReport.DidWork(result);
                var jsonData = new
                {
                    // `ok` is what a script branches on, so it must carry the same judgement the
                    // exit code does: a run where no agent completed is not ok.
                    ok = result.Success && didWork,
                    correlationId,
                    data = new
                    {
                        success = result.Success,
                        didWork,
                        agentCount = result.Decomposition?.Agents.Count ?? 0,
                        conflicts = result.Conflicts.Count,
                        escalations = result.Escalations.Count,
                        progress = result.ProgressSummary != null ? new
                        {
                            completed = result.ProgressSummary.Completed,
                            total = result.ProgressSummary.TotalAgents,
                            percentage = result.ProgressSummary.ProgressPercentage
                        } : null,
                        output = result.IntegratedOutput?.IntegratedResults
                    }
                };
                _renderer.RenderJson(jsonData);
            }
            else
            {
                _renderer.RenderOrchestrationResult(result);
            }

            if (verbose)
            {
                _renderer.RenderProgressComplete($"CorrelationId={correlationId} :: orchestration completed");
            }

            // An orchestration that completed nothing is not a success, whatever the flag says. The
            // renderer prints the "no agent completed" report for the same condition, so what a
            // person reads and what a script branches on can never disagree again.
            return result.Success && Formatting.OrchestrationWorkReport.DidWork(result)
                ? (int)ExitCode.Ok
                : (int)ExitCode.ValidationFailed;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Orchestration failed");
            var errorCode = ex switch
            {
                BarrierContextMissingException b => b.ErrorCode,
                BarrierElevationException b => b.ErrorCode,
                BarrierCeilingExceededException b => b.ErrorCode,
                ArgumentException a when string.Equals(a.ParamName, "level", StringComparison.Ordinal) => "BARRIER_VALIDATION_FAILED",
                EndpointUnavailableException => "ENDPOINT_UNAVAILABLE",
                _ => "UNEXPECTED_ERROR"
            };
            if (!json)
            {
                _renderer.RenderError($"Orchestration failed: {ex.Message}");
            }
            else
            {
                _renderer.RenderJson(new
                {
                    ok = false,
                    correlationId,
                    error = ex.Message,
                    errorCode
                });
            }
            return (int)ExitCode.UnexpectedError;
        }
    }

    private async Task InitializeBarrierContextAsync(
        string? barrierLevel,
        string? jwt,
        IReadOnlyDictionary<string, string>? headers,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var safeHeaders = headers ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var resolutionContext = new BarrierResolutionContext(
            CorrelationId: correlationId,
            ExplicitLevel: string.IsNullOrWhiteSpace(barrierLevel) ? null : barrierLevel,
            Headers: safeHeaders,
            CertSubjects: Array.Empty<string>(),
            CertSans: Array.Empty<string>(),
            RawJwt: jwt,
            JwtClaims: JwtClaimParser.ParseClaims(jwt),
            ApiKey: TryGetApiKey(safeHeaders));

        var result = await _barrierResolverPipeline.ResolveAsync(resolutionContext, cancellationToken);
        if (result is null)
        {
            if (_barrierOptions.RequireExplicitBarrier)
                throw new BarrierContextMissingException("*", correlationId);

            var defaultContext = BarrierContext.Create(
                _barrierHierarchy.Floor.Name,
                BarrierAuthoritySource.Default,
                "*",
                correlationId,
                _barrierHierarchy);

            _barrierContextAccessor.Initialize(defaultContext);
            await _barrierAuditLog.RecordAsync(new BarrierAuditEvent(
                BarrierAuditEventType.DefaultApplied,
                defaultContext.Level,
                defaultContext.AuthoritySource,
                "*",
                correlationId,
                string.Empty,
                DateTimeOffset.UtcNow,
                "No explicit barrier set at request boundary; defaulted to floor level."),
                cancellationToken);
            await _barrierAuditLog.RecordAsync(new BarrierAuditEvent(
                BarrierAuditEventType.ContextInitialized,
                defaultContext.Level,
                defaultContext.AuthoritySource,
                defaultContext.IssuedTo,
                correlationId,
                string.Empty,
                DateTimeOffset.UtcNow),
                cancellationToken);
            return;
        }

        await _barrierAuditLog.RecordAsync(new BarrierAuditEvent(
            BarrierAuditEventType.IdentityResolved,
            result.ResolvedLevel,
            result.AuthoritySource,
            "*",
            correlationId,
            string.Empty,
            DateTimeOffset.UtcNow,
            $"Resolved by: {result.ResolverName}. {result.Detail}"),
            cancellationToken);

        var context = BarrierContext.Create(
            result.ResolvedLevel,
            result.AuthoritySource,
            "*",
            correlationId,
            _barrierHierarchy,
            resolutionDetail: string.IsNullOrWhiteSpace(result.Detail)
                ? $"Resolved by: {result.ResolverName}."
                : $"Resolved by: {result.ResolverName}. {result.Detail}");

        _barrierContextAccessor.Initialize(context);
        await _barrierAuditLog.RecordAsync(new BarrierAuditEvent(
            BarrierAuditEventType.ContextInitialized,
            context.Level,
            context.AuthoritySource,
            context.IssuedTo,
            correlationId,
            string.Empty,
            DateTimeOffset.UtcNow),
            cancellationToken);
    }

    private static string? TryGetApiKey(IReadOnlyDictionary<string, string> headers)
    {
        if (headers.TryGetValue("x-ashlar-api-key", out var value))
            return value;
        foreach (var (key, headerValue) in headers)
        {
            if (string.Equals(key, "x-ashlar-api-key", StringComparison.OrdinalIgnoreCase))
                return headerValue;
        }
        return null;
    }
}

