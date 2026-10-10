using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Ashlar.Abstractions.Security.Egress;
using Ashlar.BackgroundAgents.Registry;
using Ashlar.Core.Application.NodeCapabilityRuntime.Ports;
using Ashlar.Infrastructure.Execution;
using Ashlar.Infrastructure.Ide;
using Ashlar.Orchestration.Coordination;

namespace Ashlar.API.Endpoints;

/// <summary>
/// IDE-facing API for the Ashlar VS Code / Cursor extension (chat, models, agents, edit proposals).
/// </summary>
public static class IdeEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Maps <c>/api/ide/*</c> onto the Ashlar API group.</summary>
    public static RouteGroupBuilder MapIdeEndpoints(this RouteGroupBuilder group)
    {
        var ide = group.MapGroup("/ide").WithTags("Ide");

        ide.MapGet("/health", GetIdeHealthAsync)
            .WithName("IdeHealth")
            .WithSummary("IDE connectivity probe (Ashlar + local providers)")
            .Produces<IdeHealthResponse>(StatusCodes.Status200OK);

        ide.MapGet("/models", GetIdeModelsAsync)
            .WithName("IdeListModels")
            .WithSummary("List local (and optional cloud) models for the IDE picker")
            .Produces<IdeModelsResponse>(StatusCodes.Status200OK);

        ide.MapGet("/agents", GetIdeAgentsAsync)
            .WithName("IdeListAgents")
            .WithSummary("List configured / running agents for the IDE picker")
            .Produces<IdeAgentsResponse>(StatusCodes.Status200OK);

        ide.MapPost("/session", CreateIdeSessionAsync)
            .WithName("IdeCreateSession")
            .WithSummary("Bind an IDE workspace root to a short-lived session id")
            .Produces<IdeSessionResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        ide.MapPost("/chat", IdeChatAsync)
            .WithName("IdeChat")
            .WithSummary("Ask a local-first chat question with optional file context")
            .Produces<IdeChatResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        ide.MapPost("/chat/stream", IdeChatStreamAsync)
            .WithName("IdeChatStream")
            .WithSummary("SSE stream of chat tokens (event: run|delta|done|error)")
            .Produces(StatusCodes.Status200OK, contentType: "text/event-stream")
            .ProducesProblem(StatusCodes.Status400BadRequest);

        ide.MapPost("/edit", IdeEditAsync)
            .WithName("IdeEdit")
            .WithSummary("Propose file patches from a prompt + contexts (apply in the IDE)")
            .Produces<IdeEditResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        ide.MapPost("/plan", IdePlanAsync)
            .WithName("IdePlan")
            .WithSummary("Produce an implementation plan without file patches")
            .Produces<IdePlanResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        ide.MapPost("/director", IdeDirectorAsync)
            .WithName("IdeDirector")
            .WithSummary("Run a multi-agent director-style goal (tracked on the timeline)")
            .Produces<IdeDirectorResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        ide.MapGet("/runs", ListIdeRunsAsync)
            .WithName("IdeListRuns")
            .WithSummary("List recent IDE runs (timeline)")
            .Produces<IdeRunsResponse>(StatusCodes.Status200OK);

        ide.MapGet("/runs/{runId}", GetIdeRunAsync)
            .WithName("IdeGetRun")
            .WithSummary("Get one IDE run by id")
            .Produces<IdeRunRecord>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        ide.MapPost("/runs/{runId}/cancel", CancelIdeRunAsync)
            .WithName("IdeCancelRun")
            .WithSummary("Cancel a running IDE operation")
            .Produces<IdeCancelResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return group;
    }

    private static Task<IResult> ListIdeRunsAsync([FromQuery] int? take)
    {
        var runs = IdeRunTracker.Instance.List(take ?? 30);
        return Task.FromResult(Results.Ok(new IdeRunsResponse(runs)));
    }

    private static Task<IResult> GetIdeRunAsync(string runId)
    {
        var run = IdeRunTracker.Instance.Get(runId);
        return Task.FromResult(run is null
            ? Results.NotFound(new ProblemDetails { Title = $"Unknown run '{runId}'" })
            : Results.Ok(run));
    }

    private static Task<IResult> CancelIdeRunAsync(string runId)
    {
        var ok = IdeRunTracker.Instance.TryCancel(runId);
        if (!ok)
        {
            var existing = IdeRunTracker.Instance.Get(runId);
            if (existing is null)
                return Task.FromResult(Results.NotFound(new ProblemDetails { Title = $"Unknown run '{runId}'" }));
            return Task.FromResult(Results.Ok(new IdeCancelResponse(runId, false, existing.Status)));
        }

        return Task.FromResult(Results.Ok(new IdeCancelResponse(runId, true, "cancelled")));
    }

    private static Task<IResult> GetIdeHealthAsync(
        [FromServices] IServiceProvider services,
        [FromServices] IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var providerFactory = services.GetService<IProviderFactory>();
        var ollamaAvailable = providerFactory?.IsProviderAvailable("ollama") ?? false;
        var localAvailable = providerFactory?.IsProviderAvailable("local") ?? false;
        var ollamaBase = configuration["OLLAMA_BASE_URL"]
            ?? Environment.GetEnvironmentVariable("OLLAMA_BASE_URL")
            ?? "http://127.0.0.1:11434";

        return Task.FromResult(Results.Ok(new IdeHealthResponse(
            Status: "healthy",
            Timestamp: DateTimeOffset.UtcNow,
            OllamaAvailable: ollamaAvailable,
            LocalGgufAvailable: localAvailable,
            OllamaBaseUrl: ollamaBase,
            LocalOnlyRecommended: true,
            Features: new IdeFeatures(
                Chat: true,
                ChatStream: true,
                Plan: true,
                Edit: true,
                Director: true,
                Runs: true,
                Workloads: true))));
    }

    private static async Task<IResult> GetIdeModelsAsync(
        [FromServices] IServiceProvider services,
        [FromServices] IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var models = new List<IdeModelInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var ncr = services.GetService<INodeCapabilityRuntime>();
        if (ncr is not null)
        {
            var manifest = ncr.GetCapabilityManifest();
            foreach (var id in manifest.AvailableModelIds)
            {
                if (!seen.Add(id))
                    continue;
                var hot = manifest.HotModelIds.Contains(id, StringComparer.OrdinalIgnoreCase);
                models.Add(new IdeModelInfo(id, "ncr", LocalOnly: true, Hot: hot));
            }
        }

        var ollamaBase = configuration["OLLAMA_BASE_URL"]
            ?? Environment.GetEnvironmentVariable("OLLAMA_BASE_URL")
            ?? "http://127.0.0.1:11434";
        try
        {
            var http = services.GetService<IHttpClientFactory>()?.CreateClient() ?? EgressHttp.CreateClient(EgressFamilies.Http, "EG-MDL-14");
            using var response = await http.GetAsync($"{ollamaBase.TrimEnd('/')}/api/tags", cancellationToken)
                .ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                var tags = await response.Content.ReadFromJsonAsync<OllamaTagsDto>(JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
                foreach (var m in tags?.Models ?? [])
                {
                    if (string.IsNullOrWhiteSpace(m.Name) || !seen.Add(m.Name))
                        continue;
                    models.Add(new IdeModelInfo(m.Name, "ollama", LocalOnly: true, Hot: false));
                }
            }
        }
        catch (Exception ex) when (EgressRefusal.Find(ex) is not null)
        {
            var refusal = EgressRefusal.Find(ex)!;
            var logger = services.GetService<ILoggerFactory>()?.CreateLogger("Ashlar.API.Endpoints.IdeEndpoints");
            if (logger is not null)
                logger.LogWarning(new EventId(7307, "EgressRefused"), "IDE model discovery egress refused: {Refusal}", refusal.Message);
            else
                Console.Error.WriteLine($"IDE model discovery egress refused: {refusal.Message}");
        }
        catch
        {
            // Ollama optional when NCR already listed models
        }

        var defaultModel = configuration["OLLAMA_MODEL"]
            ?? Environment.GetEnvironmentVariable("OLLAMA_MODEL")
            ?? models.FirstOrDefault()?.Id;

        return Results.Ok(new IdeModelsResponse(models, defaultModel));
    }

    private static async Task<IResult> GetIdeAgentsAsync(
        [FromServices] IServiceProvider services,
        [FromServices] IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var agents = new List<IdeAgentInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Prefer the agent-set JSON for the IDE picker. Resolving IBackgroundAgentRegistry
        // can cold-start CycleEventStore/ObservationStore against a large Docker bind mount
        // and block for minutes — never gate the picker on that when the scheduler is off.
        var configPath = Environment.GetEnvironmentVariable("ASHLAR_BACKGROUND_AGENTS_CONFIG")
            ?? configuration["ASHLAR_BACKGROUND_AGENTS_CONFIG"];
        if (!string.IsNullOrWhiteSpace(configPath) && File.Exists(configPath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(configPath, cancellationToken).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("BackgroundAgents", out var root) &&
                    root.TryGetProperty("Agents", out var arr) &&
                    arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in arr.EnumerateArray())
                    {
                        var id = el.TryGetProperty("Id", out var idEl) ? idEl.GetString() : null;
                        if (string.IsNullOrWhiteSpace(id) || !seen.Add(id))
                            continue;
                        agents.Add(new IdeAgentInfo(
                            id!,
                            el.TryGetProperty("Name", out var n) ? n.GetString() ?? id! : id!,
                            el.TryGetProperty("Role", out var r) ? r.GetString() ?? "" : "",
                            el.TryGetProperty("ModelProvider", out var mp) ? mp.GetString() ?? "ollama" : "ollama",
                            el.TryGetProperty("ModelName", out var mn) ? mn.GetString() : null,
                            "Configured",
                            !el.TryGetProperty("Enabled", out var en) || en.ValueKind != JsonValueKind.False));
                    }
                }
            }
            catch
            {
                // Config parse is best-effort for IDE pickers
            }
        }

        var hostedEnabled = configuration.GetValue("Ashlar:RegisterBackgroundAgentHostedService", defaultValue: true);
        var hostedEnv = Environment.GetEnvironmentVariable("Ashlar__RegisterBackgroundAgentHostedService");
        if (!string.IsNullOrWhiteSpace(hostedEnv) &&
            bool.TryParse(hostedEnv, out var hostedParsed))
            hostedEnabled = hostedParsed;

        if (hostedEnabled)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(2));
                var registryTask = Task.Run(() =>
                {
                    var registry = services.GetService<IBackgroundAgentRegistry>();
                    return registry?.GetAll()
                        ?? (IReadOnlyList<BackgroundAgentInstance>)Array.Empty<BackgroundAgentInstance>();
                }, cts.Token);

                var winner = await Task.WhenAny(registryTask, Task.Delay(Timeout.InfiniteTimeSpan, cts.Token))
                    .ConfigureAwait(false);
                if (winner == registryTask && registryTask.Status == TaskStatus.RanToCompletion)
                {
                    foreach (var instance in registryTask.Result)
                    {
                        var c = instance.Config;
                        var idx = agents.FindIndex(a =>
                            string.Equals(a.Id, c.Id, StringComparison.OrdinalIgnoreCase));
                        var info = new IdeAgentInfo(
                            c.Id,
                            c.Name,
                            c.Role,
                            c.ModelProvider,
                            c.ModelName,
                            instance.State.ToString(),
                            c.Enabled);
                        if (idx >= 0)
                            agents[idx] = info;
                        else if (seen.Add(c.Id))
                            agents.Add(info);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Registry cold-start skipped — config list is enough for the picker
            }
            catch
            {
                // Registry optional for IDE
            }
        }

        return Results.Ok(new IdeAgentsResponse(agents));
    }

    private static Task<IResult> CreateIdeSessionAsync([FromBody] IdeSessionRequest? body)
    {
        if (body is null || string.IsNullOrWhiteSpace(body.WorkspaceRoot))
            return Task.FromResult(Results.BadRequest(new ProblemDetails { Title = "WorkspaceRoot is required" }));

        var sessionId = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        return Task.FromResult(Results.Ok(new IdeSessionResponse(
            sessionId,
            body.WorkspaceRoot.Trim(),
            DateTimeOffset.UtcNow)));
    }

    private static async Task<IResult> IdeChatAsync(
        [FromBody] IdeChatRequest? body,
        [FromServices] Orchestrator orchestrator,
        CancellationToken cancellationToken)
    {
        if (body is null || string.IsNullOrWhiteSpace(body.Prompt))
            return Results.BadRequest(new ProblemDetails { Title = "Prompt is required" });

        var mode = body.Mode ?? "ask";
        var kind = mode.Equals("plan", StringComparison.OrdinalIgnoreCase) ? "plan" : "chat";
        var (runId, token) = IdeRunTracker.Instance.Start(kind, body.Prompt, body.AgentId, body.ModelId, cancellationToken);
        var prompt = kind == "plan"
            ? BuildPlanPrompt(body.Prompt, body.Contexts)
            : BuildContextPrompt(body.Prompt, body.Contexts, mode);
        try
        {
            var result = await orchestrator.OrchestrateAsync(prompt, token).ConfigureAwait(false);
            var text = ExtractAssistantText(result);
            IdeRunTracker.Instance.Complete(runId, text);
            return Results.Ok(new IdeChatResponse(
                Success: result.Success,
                Message: text,
                ModelId: body.ModelId,
                AgentId: body.AgentId,
                RunId: runId,
                Raw: result.Success ? null : result));
        }
        catch (OperationCanceledException)
        {
            IdeRunTracker.Instance.TryCancel(runId);
            return Results.Ok(new IdeChatResponse(
                Success: false,
                Message: "Cancelled",
                ModelId: body.ModelId,
                AgentId: body.AgentId,
                RunId: runId,
                Raw: null));
        }
        catch (Exception ex)
        {
            IdeRunTracker.Instance.Fail(runId, ex.Message);
            return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task IdeChatStreamAsync(
        HttpContext http,
        [FromBody] IdeChatRequest? body,
        [FromServices] Orchestrator orchestrator,
        [FromServices] IServiceProvider services,
        CancellationToken cancellationToken)
    {
        if (body is null || string.IsNullOrWhiteSpace(body.Prompt))
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            await http.Response.WriteAsJsonAsync(
                new ProblemDetails { Title = "Prompt is required" },
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var mode = body.Mode ?? "ask";
        var kind = mode.Equals("plan", StringComparison.OrdinalIgnoreCase) ? "plan" : "chat";
        var (runId, token) = IdeRunTracker.Instance.Start(kind, body.Prompt, body.AgentId, body.ModelId, cancellationToken);
        var prompt = kind == "plan"
            ? BuildPlanPrompt(body.Prompt, body.Contexts)
            : BuildContextPrompt(body.Prompt, body.Contexts, mode);

        http.Response.Headers.CacheControl = "no-cache";
        http.Response.Headers.Append("X-Accel-Buffering", "no");
        http.Response.ContentType = "text/event-stream";

        await WriteSseAsync(http, "run", new { runId, kind }, token).ConfigureAwait(false);

        try
        {
            var chatClient = services.GetService<IChatClient>();
            var sb = new StringBuilder();

            if (chatClient is not null)
            {
                var messages = new List<ChatMessage> { new(ChatRole.User, prompt) };
                var options = new ChatOptions
                {
                    ModelId = string.IsNullOrWhiteSpace(body.ModelId) ? null : body.ModelId,
                };

                await foreach (var update in chatClient.GetStreamingResponseAsync(messages, options, token)
                    .ConfigureAwait(false))
                {
                    var text = update.Text;
                    if (string.IsNullOrEmpty(text))
                        continue;
                    sb.Append(text);
                    await WriteSseAsync(http, "delta", new { text }, token).ConfigureAwait(false);
                }
            }
            else
            {
                var result = await orchestrator.OrchestrateAsync(prompt, token).ConfigureAwait(false);
                var text = ExtractAssistantText(result);
                sb.Append(text);
                if (!string.IsNullOrEmpty(text))
                    await WriteSseAsync(http, "delta", new { text }, token).ConfigureAwait(false);
            }

            var full = sb.ToString();
            IdeRunTracker.Instance.Complete(runId, full);
            await WriteSseAsync(
                http,
                "done",
                new
                {
                    runId,
                    success = true,
                    message = full,
                    modelId = body.ModelId,
                    agentId = body.AgentId,
                },
                token).ConfigureAwait(false);
        }
        catch (Exception ex) when (EgressRefusal.Find(ex) is not null)
        {
            var refusal = EgressRefusal.Find(ex)!;
            var message = EgressRefusal.RemoteMessage(refusal.Ref);
            IdeRunTracker.Instance.Fail(runId, message);
            await WriteSseAsync(http, "error",
                new { runId, message, errorCode = EgressRefusal.Code, egressRef = refusal.Ref },
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            IdeRunTracker.Instance.TryCancel(runId);
            await WriteSseAsync(
                http,
                "done",
                new { runId, success = false, message = "Cancelled" },
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            IdeRunTracker.Instance.Fail(runId, ex.Message);
            await WriteSseAsync(
                http,
                "error",
                new { runId, message = ex.Message },
                CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static async Task WriteSseAsync(HttpContext http, string eventName, object payload, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        await http.Response.WriteAsync($"event: {eventName}\ndata: {json}\n\n", ct).ConfigureAwait(false);
        await http.Response.Body.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task<IResult> IdeEditAsync(
        [FromBody] IdeEditRequest? body,
        [FromServices] Orchestrator orchestrator,
        CancellationToken cancellationToken)
    {
        if (body is null || string.IsNullOrWhiteSpace(body.Prompt))
            return Results.BadRequest(new ProblemDetails { Title = "Prompt is required" });

        var mode = string.IsNullOrWhiteSpace(body.Mode) ? "edit" : body.Mode.Trim().ToLowerInvariant();
        if (mode == "plan")
        {
            return await IdePlanCoreAsync(body.Prompt, body.Contexts, body.ModelId, body.AgentId, orchestrator, cancellationToken)
                .ConfigureAwait(false);
        }

        var (runId, token) = IdeRunTracker.Instance.Start("edit", body.Prompt, body.AgentId, body.ModelId, cancellationToken);
        var prompt = BuildEditPrompt(body.Prompt, body.Contexts);
        try
        {
            var result = await orchestrator.OrchestrateAsync(prompt, token).ConfigureAwait(false);
            var assistant = ExtractAssistantText(result);
            var patches = SanitizePatches(TryParsePatches(assistant, body.Contexts), body.Contexts);
            if (patches.Count == 0 && body.Contexts is { Count: > 0 })
            {
                var primary = body.Contexts[0];
                if (IsSafeRelativePath(primary.Path))
                {
                    patches.Add(new IdeFilePatch(
                        NormalizePath(primary.Path),
                        primary.Content,
                        assistant,
                        Summary: "Model did not return structured JSON patches; review carefully before apply."));
                }
            }

            var proposalId = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
            IdeRunTracker.Instance.Complete(runId, $"Proposal {proposalId}: {patches.Count} patch(es)", proposalId);
            return Results.Ok(new IdeEditResponse(
                Success: result.Success,
                Message: result.Success
                    ? $"Proposal {proposalId}: {patches.Count} patch(es)"
                    : "Orchestration reported failure",
                ProposalId: proposalId,
                Patches: patches,
                RawText: assistant,
                Mode: "edit",
                RunId: runId));
        }
        catch (OperationCanceledException)
        {
            IdeRunTracker.Instance.TryCancel(runId);
            return Results.Ok(new IdeEditResponse(
                Success: false,
                Message: "Cancelled",
                ProposalId: "",
                Patches: Array.Empty<IdeFilePatch>(),
                RawText: null,
                Mode: "edit",
                RunId: runId));
        }
        catch (Exception ex)
        {
            IdeRunTracker.Instance.Fail(runId, ex.Message);
            return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static Task<IResult> IdePlanAsync(
        [FromBody] IdeChatRequest? body,
        [FromServices] Orchestrator orchestrator,
        CancellationToken cancellationToken)
    {
        if (body is null || string.IsNullOrWhiteSpace(body.Prompt))
            return Task.FromResult(Results.BadRequest(new ProblemDetails { Title = "Prompt is required" }));

        return IdePlanCoreAsync(body.Prompt, body.Contexts, body.ModelId, body.AgentId, orchestrator, cancellationToken);
    }

    private static async Task<IResult> IdeDirectorAsync(
        [FromBody] IdeDirectorRequest? body,
        [FromServices] Orchestrator orchestrator,
        CancellationToken cancellationToken)
    {
        if (body is null || string.IsNullOrWhiteSpace(body.Goal))
            return Results.BadRequest(new ProblemDetails { Title = "Goal is required" });

        var (runId, token) = IdeRunTracker.Instance.Start("director", body.Goal, body.AgentId, body.ModelId, cancellationToken);
        var prompt = BuildDirectorPrompt(body.Goal, body.Contexts);
        try
        {
            var result = await orchestrator.OrchestrateAsync(prompt, token).ConfigureAwait(false);
            var text = ExtractAssistantText(result);
            IdeRunTracker.Instance.Complete(runId, text);
            return Results.Ok(new IdeDirectorResponse(
                Success: result.Success,
                Message: text,
                RunId: runId,
                AgentId: body.AgentId,
                ModelId: body.ModelId));
        }
        catch (OperationCanceledException)
        {
            IdeRunTracker.Instance.TryCancel(runId);
            return Results.Ok(new IdeDirectorResponse(false, "Cancelled", runId, body.AgentId, body.ModelId));
        }
        catch (Exception ex)
        {
            IdeRunTracker.Instance.Fail(runId, ex.Message);
            return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<IResult> IdePlanCoreAsync(
        string promptText,
        IReadOnlyList<IdeFileContext>? contexts,
        string? modelId,
        string? agentId,
        Orchestrator orchestrator,
        CancellationToken cancellationToken)
    {
        var (runId, token) = IdeRunTracker.Instance.Start("plan", promptText, agentId, modelId, cancellationToken);
        var prompt = BuildPlanPrompt(promptText, contexts);
        try
        {
            var result = await orchestrator.OrchestrateAsync(prompt, token).ConfigureAwait(false);
            var text = ExtractAssistantText(result);
            IdeRunTracker.Instance.Complete(runId, text);
            return Results.Ok(new IdePlanResponse(
                Success: result.Success,
                Plan: text,
                ModelId: modelId,
                AgentId: agentId,
                RunId: runId,
                Steps: TryParsePlanSteps(text)));
        }
        catch (OperationCanceledException)
        {
            IdeRunTracker.Instance.TryCancel(runId);
            return Results.Ok(new IdePlanResponse(false, "Cancelled", modelId, agentId, runId, Array.Empty<IdePlanStep>()));
        }
        catch (Exception ex)
        {
            IdeRunTracker.Instance.Fail(runId, ex.Message);
            return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static string BuildDirectorPrompt(string goal, IReadOnlyList<IdeFileContext>? contexts)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are the Ashlar technical director coordinating specialist agents.");
        sb.AppendLine("Decompose the goal, reason about trade-offs, and produce a clear multi-agent outcome summary.");
        sb.AppendLine("Prefer local/offline capabilities. Do not invent credentials or external network calls.");
        sb.AppendLine();
        sb.AppendLine("Director goal:");
        sb.AppendLine(goal.Trim());
        AppendContexts(sb, contexts);
        return sb.ToString();
    }

    private static string BuildContextPrompt(string prompt, IReadOnlyList<IdeFileContext>? contexts, string mode)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"You are Ashlar IDE assistant in {mode} mode. Prefer local/offline reasoning.");
        sb.AppendLine(mode.Equals("plan", StringComparison.OrdinalIgnoreCase)
            ? "Produce a concrete implementation plan. Do not invent full file contents."
            : "Answer clearly and concisely.");
        sb.AppendLine();
        sb.AppendLine("User request:");
        sb.AppendLine(prompt.Trim());
        AppendContexts(sb, contexts);
        return sb.ToString();
    }

    private static string BuildPlanPrompt(string prompt, IReadOnlyList<IdeFileContext>? contexts)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are Ashlar IDE planner. Prefer local/offline reasoning.");
        sb.AppendLine("Return a short, actionable plan. Prefer JSON of shape:");
        sb.AppendLine("""{"steps":[{"title":"...","detail":"...","paths":["relative/file.cs"]}]}""");
        sb.AppendLine("If you cannot emit JSON, use a numbered list. Do NOT emit full file contents or patches.");
        sb.AppendLine();
        sb.AppendLine("User goal:");
        sb.AppendLine(prompt.Trim());
        AppendContexts(sb, contexts);
        return sb.ToString();
    }

    private static string BuildEditPrompt(string prompt, IReadOnlyList<IdeFileContext>? contexts)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are Ashlar IDE edit assistant. Propose file changes for a local offline coding agent.");
        sb.AppendLine("Respond with ONLY a JSON object of this shape (no markdown fences):");
        sb.AppendLine("""{"patches":[{"path":"relative/path.cs","newContent":"...full file...","summary":"what changed"}]}""");
        sb.AppendLine("Rules:");
        sb.AppendLine("- Paths must be relative (no .., no drive letters, no absolute paths).");
        sb.AppendLine("- Prefer editing files present in the provided contexts.");
        sb.AppendLine("- Keep changes minimal. Preserve existing style.");
        sb.AppendLine("- newContent must be the FULL file contents after the edit.");
        sb.AppendLine();
        sb.AppendLine("User edit request:");
        sb.AppendLine(prompt.Trim());
        AppendContexts(sb, contexts);
        return sb.ToString();
    }

    private static void AppendContexts(StringBuilder sb, IReadOnlyList<IdeFileContext>? contexts)
    {
        if (contexts is null || contexts.Count == 0)
            return;
        sb.AppendLine();
        sb.AppendLine("Workspace file contexts:");
        foreach (var ctx in contexts.Take(12))
        {
            if (!IsSafeRelativePath(ctx.Path))
                continue;
            sb.AppendLine($"--- FILE: {NormalizePath(ctx.Path)} ---");
            if (!string.IsNullOrWhiteSpace(ctx.Language))
                sb.AppendLine($"language: {ctx.Language}");
            if (ctx.SelectionStartLine is int start && ctx.SelectionEndLine is int end)
                sb.AppendLine($"selection lines: {start}-{end}");
            sb.AppendLine(ctx.Content ?? "");
            sb.AppendLine("--- END FILE ---");
        }
    }

    private static string ExtractAssistantText(OrchestrationResult result)
    {
        if (result.IntegratedOutput?.IntegratedResults is { Count: > 0 } results)
        {
            var parts = new List<string>();
            foreach (var kv in results)
            {
                try
                {
                    parts.Add(kv.Value is string s
                        ? s
                        : JsonSerializer.Serialize(kv.Value, JsonOptions));
                }
                catch
                {
                    parts.Add(kv.Value?.ToString() ?? "");
                }
            }
            return string.Join("\n\n", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        }

        if (result.IntegratedOutput?.AgentOutputs is { Count: > 0 } outputs)
        {
            return JsonSerializer.Serialize(outputs, JsonOptions);
        }

        return result.Success
            ? "Orchestration completed with no textual output."
            : "Orchestration failed.";
    }

    private static List<IdeFilePatch> TryParsePatches(string assistant, IReadOnlyList<IdeFileContext>? contexts)
    {
        var patches = new List<IdeFilePatch>();
        var json = ExtractJsonObject(assistant);
        if (json is null)
            return patches;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("patches", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return patches;

            foreach (var p in arr.EnumerateArray())
            {
                var path = p.TryGetProperty("path", out var pathEl) ? pathEl.GetString() : null;
                var newContent = p.TryGetProperty("newContent", out var nc) ? nc.GetString()
                    : p.TryGetProperty("new_content", out var nc2) ? nc2.GetString()
                    : p.TryGetProperty("content", out var c) ? c.GetString() : null;
                if (string.IsNullOrWhiteSpace(path) || newContent is null)
                    continue;
                var summary = p.TryGetProperty("summary", out var s) ? s.GetString() : null;
                var normalized = NormalizePath(path!);
                var original = contexts?.FirstOrDefault(ctx =>
                    string.Equals(NormalizePath(ctx.Path), normalized, StringComparison.OrdinalIgnoreCase))?.Content;
                patches.Add(new IdeFilePatch(normalized, original, newContent, summary));
            }
        }
        catch
        {
            // leave empty — caller may fallback
        }

        return patches;
    }

    private static List<IdeFilePatch> SanitizePatches(
        List<IdeFilePatch> patches,
        IReadOnlyList<IdeFileContext>? contexts)
    {
        var allowed = new HashSet<string>(
            (contexts ?? Array.Empty<IdeFileContext>())
                .Where(c => IsSafeRelativePath(c.Path))
                .Select(c => NormalizePath(c.Path)),
            StringComparer.OrdinalIgnoreCase);

        return patches
            .Where(p => IsSafeRelativePath(p.Path))
            .Select(p => p with { Path = NormalizePath(p.Path) })
            // Prefer in-context files; still allow new relative paths within workspace.
            .Where(p => allowed.Count == 0 || allowed.Contains(p.Path) || !p.Path.Contains("..", StringComparison.Ordinal))
            .GroupBy(p => p.Path, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Last())
            .ToList();
    }

    private static IReadOnlyList<IdePlanStep> TryParsePlanSteps(string text)
    {
        var steps = new List<IdePlanStep>();
        var json = ExtractJsonObject(text);
        if (json is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("steps", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var s in arr.EnumerateArray())
                    {
                        var title = s.TryGetProperty("title", out var t) ? t.GetString() : null;
                        if (string.IsNullOrWhiteSpace(title))
                            continue;
                        var detail = s.TryGetProperty("detail", out var d) ? d.GetString() : null;
                        var paths = new List<string>();
                        if (s.TryGetProperty("paths", out var pArr) && pArr.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var p in pArr.EnumerateArray())
                            {
                                var path = p.GetString();
                                if (!string.IsNullOrWhiteSpace(path) && IsSafeRelativePath(path))
                                    paths.Add(NormalizePath(path));
                            }
                        }
                        steps.Add(new IdePlanStep(title!, detail, paths));
                    }
                }
            }
            catch
            {
                // fall through to prose
            }
        }

        if (steps.Count > 0)
            return steps;

        // Numbered list fallback: "1. Do X"
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var m = System.Text.RegularExpressions.Regex.Match(line, @"^\d+[\.\)]\s+(.+)$");
            if (m.Success)
                steps.Add(new IdePlanStep(m.Groups[1].Value, null, Array.Empty<string>()));
        }

        return steps;
    }

    private static bool IsSafeRelativePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        var p = path.Replace('\\', '/').Trim();
        if (p.StartsWith('/') || p.Contains(':', StringComparison.Ordinal))
            return false;
        if (p.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(s => s == ".."))
            return false;
        return true;
    }

    private static string NormalizePath(string path) =>
        path.Replace('\\', '/').Trim().TrimStart('/');

    private static string? ExtractJsonObject(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var trimmed = text.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNl = trimmed.IndexOf('\n');
            var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (firstNl > 0 && lastFence > firstNl)
                trimmed = trimmed[(firstNl + 1)..lastFence].Trim();
        }
        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');
        if (start < 0 || end <= start)
            return null;
        return trimmed[start..(end + 1)];
    }

    private sealed class OllamaTagsDto
    {
        [System.Text.Json.Serialization.JsonPropertyName("models")]
        public List<OllamaTagModelDto>? Models { get; set; }
    }

    private sealed class OllamaTagModelDto
    {
        [System.Text.Json.Serialization.JsonPropertyName("name")]
        public string? Name { get; set; }
    }
}

/// <summary>IDE health probe response.</summary>
public sealed record IdeHealthResponse(
    string Status,
    DateTimeOffset Timestamp,
    bool OllamaAvailable,
    bool LocalGgufAvailable,
    string OllamaBaseUrl,
    bool LocalOnlyRecommended,
    IdeFeatures? Features = null);

/// <summary>Advertised IDE capability flags for the extension.</summary>
public sealed record IdeFeatures(
    bool Chat,
    bool ChatStream,
    bool Plan,
    bool Edit,
    bool Director,
    bool Runs,
    bool Workloads);

/// <summary>Model picker row.</summary>
public sealed record IdeModelInfo(string Id, string Source, bool LocalOnly, bool Hot);

/// <summary>Models list response.</summary>
public sealed record IdeModelsResponse(IReadOnlyList<IdeModelInfo> Models, string? DefaultModelId);

/// <summary>Agent picker row.</summary>
public sealed record IdeAgentInfo(
    string Id,
    string Name,
    string Role,
    string ModelProvider,
    string? ModelName,
    string State,
    bool Enabled);

/// <summary>Agents list response.</summary>
public sealed record IdeAgentsResponse(IReadOnlyList<IdeAgentInfo> Agents);

/// <summary>Bind workspace to session.</summary>
public sealed record IdeSessionRequest(string WorkspaceRoot, string? IdeName = null);

/// <summary>Session create response.</summary>
public sealed record IdeSessionResponse(string SessionId, string WorkspaceRoot, DateTimeOffset CreatedAtUtc);

/// <summary>File context for chat/edit.</summary>
public sealed record IdeFileContext(
    string Path,
    string? Content,
    string? Language = null,
    int? SelectionStartLine = null,
    int? SelectionEndLine = null);

/// <summary>IDE chat request.</summary>
public sealed record IdeChatRequest(
    string Prompt,
    string? ModelId = null,
    string? AgentId = null,
    string? Mode = "ask",
    string? SessionId = null,
    IReadOnlyList<IdeFileContext>? Contexts = null);

/// <summary>IDE chat response.</summary>
public sealed record IdeChatResponse(
    bool Success,
    string Message,
    string? ModelId,
    string? AgentId,
    string RunId,
    object? Raw);

/// <summary>IDE edit request.</summary>
public sealed record IdeEditRequest(
    string Prompt,
    string? ModelId = null,
    string? AgentId = null,
    string? SessionId = null,
    string? Mode = "edit",
    IReadOnlyList<IdeFileContext>? Contexts = null);

/// <summary>One proposed file patch for the IDE to apply.</summary>
public sealed record IdeFilePatch(
    string Path,
    string? OriginalContent,
    string NewContent,
    string? Summary);

/// <summary>IDE edit response.</summary>
public sealed record IdeEditResponse(
    bool Success,
    string Message,
    string ProposalId,
    IReadOnlyList<IdeFilePatch> Patches,
    string? RawText,
    string Mode = "edit",
    string? RunId = null);

/// <summary>One plan step.</summary>
public sealed record IdePlanStep(string Title, string? Detail, IReadOnlyList<string> Paths);

/// <summary>IDE plan response (no file writes).</summary>
public sealed record IdePlanResponse(
    bool Success,
    string Plan,
    string? ModelId,
    string? AgentId,
    string RunId,
    IReadOnlyList<IdePlanStep> Steps);

/// <summary>IDE director request.</summary>
public sealed record IdeDirectorRequest(
    string Goal,
    string? ModelId = null,
    string? AgentId = null,
    IReadOnlyList<IdeFileContext>? Contexts = null);

/// <summary>IDE director response.</summary>
public sealed record IdeDirectorResponse(
    bool Success,
    string Message,
    string RunId,
    string? AgentId,
    string? ModelId);

/// <summary>IDE runs list.</summary>
public sealed record IdeRunsResponse(IReadOnlyList<IdeRunRecord> Runs);

/// <summary>Cancel result.</summary>
public sealed record IdeCancelResponse(string RunId, bool Cancelled, string Status);
