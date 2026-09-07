using System.Text.Json;
using Microsoft.Extensions.Logging;
using Ashlar.BackgroundAgents.Configuration;
using Ashlar.BackgroundAgents.DataSensitivity;
using Ashlar.BackgroundAgents.Registry;
using Ashlar.Core.Application.Orchestration.Ports;

namespace Ashlar.CLI.Commands.BackgroundAgent;

/// <summary>
/// CLI command handler for background agent operations (list, show, start, stop, restart).
/// Uses config loader for list/show; uses registry for start/stop/restart after ensuring agents are registered.
/// </summary>
public class BackgroundAgentCommand
{
    private readonly BackgroundAgentConfigLoader _configLoader;
    private readonly IBackgroundAgentRegistry _registry;
    private readonly BackgroundAgentSpecBuilder _specBuilder;
    private readonly IAgentCreator _agentCreator;
    private readonly ILogger<BackgroundAgentCommand> _logger;

    /// <summary>Creates a new BackgroundAgentCommand instance.</summary>
    public BackgroundAgentCommand(
        BackgroundAgentConfigLoader configLoader,
        IBackgroundAgentRegistry registry,
        BackgroundAgentSpecBuilder specBuilder,
        IAgentCreator agentCreator,
        ILogger<BackgroundAgentCommand> logger)
    {
        _configLoader = configLoader ?? throw new ArgumentNullException(nameof(configLoader));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _specBuilder = specBuilder ?? throw new ArgumentNullException(nameof(specBuilder));
        _agentCreator = agentCreator ?? throw new ArgumentNullException(nameof(agentCreator));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// List configured agents (from config); optionally merge state from registry.
    /// </summary>
    public async Task<int> ListAsync(bool formatJson, string? status, string? role, string? sensitivity, CancellationToken ct = default)
    {
        try
        {
            var configs = await _configLoader.LoadAsync(ct);
            var instances = _registry.GetAll();
            var byId = instances.ToDictionary(i => i.Config.Id, StringComparer.OrdinalIgnoreCase);

            var items = configs
                .Where(c => FilterByStatus(c, byId, status))
                .Where(c => FilterByRole(c, role))
                .Where(c => FilterBySensitivity(c, sensitivity))
                .Select(c => new
                {
                    c.Id,
                    c.Name,
                    c.Role,
                    State = byId.TryGetValue(c.Id, out var inst) ? inst.State.ToString() : "NotRegistered",
                    MaxSensitivity = c.MaxDataSensitivity
                })
                .ToList();

            if (formatJson)
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                Console.Out.WriteLine(JsonSerializer.Serialize(items, options));
            }
            else
            {
                Console.Out.WriteLine("Background Agents:");
                foreach (var item in items)
                    Console.Out.WriteLine($"  {item.Id} ({item.Role}) - {item.State} - Max Sensitivity: {item.MaxSensitivity}");
            }

            return 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "List background agents failed");
            if (formatJson)
                Console.Out.WriteLine(JsonSerializer.Serialize(new { ok = false, error = ex.Message }));
            else
                Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    /// <summary>
    /// Show details for one agent by id.
    /// </summary>
    public async Task<int> ShowAsync(string id, bool formatJson, CancellationToken ct = default)
    {
        try
        {
            var configs = await _configLoader.LoadAsync(ct);
            var config = configs.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));
            if (config == null)
            {
                if (formatJson)
                    Console.Out.WriteLine(JsonSerializer.Serialize(new { ok = false, error = $"Agent '{id}' not found" }));
                else
                    Console.Error.WriteLine($"Agent '{id}' not found");
                return 1;
            }

            var instance = _registry.GetAgent(config.Id);

            if (formatJson)
            {
                var payload = new
                {
                    config.Id,
                    config.Name,
                    config.Role,
                    State = instance?.State.ToString() ?? "NotRegistered",
                    config.ModelProvider,
                    config.ModelName,
                    config.MaxDataSensitivity,
                    Schedule = new { config.Schedule.Type, config.Schedule.Interval, config.Schedule.CronExpression },
                    config.Commands,
                    RAG = config.RAG?.Enabled ?? false,
                    WebSearch = config.WebSearch?.Enabled ?? false,
                    ExfiltrationPolicy = new
                    {
                        config.ExfiltrationPolicy.BlockExternalLLMs,
                        config.ExfiltrationPolicy.BlockWebSearch,
                        config.ExfiltrationPolicy.BlockNetworkExports,
                        config.ExfiltrationPolicy.MaxAllowedLevel
                    },
                    instance?.LastStartedAt,
                    instance?.LastCompletedAt,
                    instance?.ExecutionCount,
                    instance?.SuccessCount,
                    instance?.FailureCount,
                    instance?.LastError
                };
                var options = new JsonSerializerOptions { WriteIndented = true };
                Console.Out.WriteLine(JsonSerializer.Serialize(payload, options));
            }
            else
            {
                Console.Out.WriteLine($"Agent: {config.Id}");
                Console.Out.WriteLine($"  Name: {config.Name}");
                Console.Out.WriteLine($"  Role: {config.Role}");
                Console.Out.WriteLine($"  Status: {instance?.State.ToString() ?? "NotRegistered"}");
                Console.Out.WriteLine($"  Model Provider: {config.ModelProvider}");
                Console.Out.WriteLine($"  Max Data Sensitivity: {config.MaxDataSensitivity}");
                Console.Out.WriteLine($"  Schedule: {config.Schedule.Type} {config.Schedule.Interval} {config.Schedule.CronExpression}");
                Console.Out.WriteLine($"  Commands: {string.Join(", ", config.Commands)}");
                Console.Out.WriteLine($"  RAG: {(config.RAG?.Enabled == true ? "Enabled" : "Disabled")}");
                Console.Out.WriteLine($"  Web Search: {(config.WebSearch?.Enabled == true ? "Enabled" : "Disabled")}");
                Console.Out.WriteLine("  Exfiltration Policy:");
                Console.Out.WriteLine($"    - Block External LLMs: {config.ExfiltrationPolicy.BlockExternalLLMs}");
                Console.Out.WriteLine($"    - Block Web Search: {config.ExfiltrationPolicy.BlockWebSearch}");
                Console.Out.WriteLine($"    - Block Network Exports: {config.ExfiltrationPolicy.BlockNetworkExports}");
                Console.Out.WriteLine($"    - Max Allowed Level: {config.ExfiltrationPolicy.MaxAllowedLevel}");
            }

            return 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Show background agent failed");
            if (formatJson)
                Console.Out.WriteLine(JsonSerializer.Serialize(new { ok = false, error = ex.Message }));
            else
                Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    /// <summary>
    /// Ensure agent is registered (load config, create and register if missing), then start.
    /// </summary>
    public async Task<int> StartAsync(string id, bool formatJson, CancellationToken ct = default)
    {
        try
        {
            await EnsureAgentRegisteredAsync(id, ct);
            await _registry.StartAsync(id, ct);
            if (formatJson)
                Console.Out.WriteLine(JsonSerializer.Serialize(new { ok = true, id, action = "started" }));
            else
                Console.Out.WriteLine($"Started background agent: {id}");
            return 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Start background agent failed");
            if (formatJson)
                Console.Out.WriteLine(JsonSerializer.Serialize(new { ok = false, error = ex.Message }));
            else
                Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    /// <summary>
    /// Stop an agent by id.
    /// </summary>
    public async Task<int> StopAsync(string id, bool formatJson, CancellationToken ct = default)
    {
        try
        {
            await _registry.StopAsync(id, ct);
            if (formatJson)
                Console.Out.WriteLine(JsonSerializer.Serialize(new { ok = true, id, action = "stopped" }));
            else
                Console.Out.WriteLine($"Stopped background agent: {id}");
            return 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Stop background agent failed");
            if (formatJson)
                Console.Out.WriteLine(JsonSerializer.Serialize(new { ok = false, error = ex.Message }));
            else
                Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    /// <summary>
    /// Stop then start an agent by id.
    /// </summary>
    public async Task<int> RestartAsync(string id, bool formatJson, CancellationToken ct = default)
    {
        try
        {
            await _registry.StopAsync(id, ct);
            await EnsureAgentRegisteredAsync(id, ct);
            await _registry.StartAsync(id, ct);
            if (formatJson)
                Console.Out.WriteLine(JsonSerializer.Serialize(new { ok = true, id, action = "restarted" }));
            else
                Console.Out.WriteLine($"Restarted background agent: {id}");
            return 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Restart background agent failed");
            if (formatJson)
                Console.Out.WriteLine(JsonSerializer.Serialize(new { ok = false, error = ex.Message }));
            else
                Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    /// <summary>
    /// Apply one autoscale decision for a role: start additional auto agents when demand is high,
    /// and stop idle auto agents when demand is low.
    /// </summary>
    public async Task<int> AutoScaleAsync(
        string role,
        int demand,
        int minAgents,
        int maxAgents,
        int unitsPerAgent,
        int idleSeconds,
        bool formatJson,
        CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(role))
                throw new ArgumentException("Role is required.", nameof(role));
            if (minAgents < 0)
                throw new ArgumentException("minAgents must be >= 0.", nameof(minAgents));
            if (maxAgents < minAgents)
                throw new ArgumentException("maxAgents must be >= minAgents.", nameof(maxAgents));
            if (unitsPerAgent <= 0)
                throw new ArgumentException("unitsPerAgent must be > 0.", nameof(unitsPerAgent));
            if (idleSeconds < 0)
                throw new ArgumentException("idleSeconds must be >= 0.", nameof(idleSeconds));

            var normalizedRole = role.Trim();
            var configs = await _configLoader.LoadAsync(ct);
            var roleConfigs = configs
                .Where(c => c.Enabled && string.Equals(c.Role, normalizedRole, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (roleConfigs.Count == 0)
                throw new InvalidOperationException($"No enabled background-agent configs found for role '{normalizedRole}'.");

            var desiredTotal = CalculateDesiredAgentCount(demand, minAgents, maxAgents, unitsPerAgent);
            var now = DateTimeOffset.UtcNow;
            var instances = _registry.GetAll()
                .Where(i => string.Equals(i.Config.Role, normalizedRole, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var baselineRunning = instances.Count(i => !IsAutoscaledInstance(i, normalizedRole) && i.State == BackgroundAgentState.Running);
            var desiredAutoRunning = Math.Max(0, desiredTotal - baselineRunning);
            var autoInstances = instances.Where(i => IsAutoscaledInstance(i, normalizedRole)).ToList();
            var runningAuto = autoInstances.Where(i => i.State == BackgroundAgentState.Running).ToList();
            var stoppedAuto = autoInstances.Where(i => i.State != BackgroundAgentState.Running).ToList();

            var started = new List<string>();
            var created = new List<string>();
            var stopped = new List<string>();

            // Scale up: restart existing stopped auto agents first.
            var autoDeficit = desiredAutoRunning - runningAuto.Count;
            if (autoDeficit > 0)
            {
                foreach (var candidate in stoppedAuto.OrderBy(i => i.Config.Id).Take(autoDeficit))
                {
                    await _registry.StartAsync(candidate.Config.Id, ct);
                    started.Add(candidate.Config.Id);
                    autoDeficit--;
                    if (autoDeficit == 0)
                        break;
                }
            }

            // If still short, create new auto agents from role template.
            if (autoDeficit > 0)
            {
                var template = roleConfigs[0];
                var knownIds = _registry.GetAll().Select(i => i.Config.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
                while (autoDeficit > 0)
                {
                    var autoId = GetNextAutoscaledId(normalizedRole, knownIds);
                    knownIds.Add(autoId);
                    var autoConfig = CloneForAutoscale(template, autoId);
                    var spec = _specBuilder.BuildSpec(autoConfig);
                    var agent = _agentCreator.CreateAgent(spec);
                    await _registry.RegisterAsync(agent, autoConfig, cancellationToken: ct);
                    await _registry.StartAsync(autoId, ct);
                    created.Add(autoId);
                    started.Add(autoId);
                    autoDeficit--;
                }
            }

            // Scale down: only stop auto agents, never baseline templates.
            runningAuto = _registry.GetAll()
                .Where(i => string.Equals(i.Config.Role, normalizedRole, StringComparison.OrdinalIgnoreCase))
                .Where(i => IsAutoscaledInstance(i, normalizedRole) && i.State == BackgroundAgentState.Running)
                .OrderBy(i => i.LastCompletedAt ?? i.LastStartedAt ?? DateTimeOffset.MinValue)
                .ToList();
            var autoSurplus = runningAuto.Count - desiredAutoRunning;
            if (autoSurplus > 0)
            {
                foreach (var candidate in runningAuto)
                {
                    if (autoSurplus == 0)
                        break;
                    var lastActivity = candidate.LastCompletedAt ?? candidate.LastStartedAt ?? now;
                    var idleAge = now - lastActivity;
                    if (idleAge.TotalSeconds < idleSeconds)
                        continue;

                    await _registry.StopAsync(candidate.Config.Id, ct);
                    stopped.Add(candidate.Config.Id);
                    autoSurplus--;
                }
            }

            var payload = new
            {
                ok = true,
                role = normalizedRole,
                demand,
                desiredTotalAgents = desiredTotal,
                baselineRunningAgents = baselineRunning,
                desiredAutoRunningAgents = desiredAutoRunning,
                started,
                created,
                stopped,
                idleSeconds,
            };

            if (formatJson)
            {
                Console.Out.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
            }
            else
            {
                Console.Out.WriteLine(
                    $"Autoscale role={normalizedRole} demand={demand} desiredTotal={desiredTotal} baseline={baselineRunning} desiredAuto={desiredAutoRunning}");
                if (created.Count > 0)
                    Console.Out.WriteLine($"  created: {string.Join(", ", created)}");
                if (started.Count > 0)
                    Console.Out.WriteLine($"  started: {string.Join(", ", started)}");
                if (stopped.Count > 0)
                    Console.Out.WriteLine($"  stopped: {string.Join(", ", stopped)}");
            }

            return 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Autoscale background agents failed");
            if (formatJson)
                Console.Out.WriteLine(JsonSerializer.Serialize(new { ok = false, error = ex.Message }));
            else
                Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private async Task EnsureAgentRegisteredAsync(string id, CancellationToken ct)
    {
        if (_registry.GetAgent(id) != null)
            return;
        var configs = await _configLoader.LoadAsync(ct);
        var config = configs.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Agent '{id}' not found in configuration");
        var spec = _specBuilder.BuildSpec(config);
        var agent = _agentCreator.CreateAgent(spec);
        await _registry.RegisterAsync(agent, config, AgentRegistrationOrigin.Authored, ct);
    }

    /// <summary>Creates a new CalculateDesiredAgentCount instance.</summary>
    public static int CalculateDesiredAgentCount(int demand, int minAgents, int maxAgents, int unitsPerAgent)
    {
        var normalizedDemand = Math.Max(0, demand);
        var desired = normalizedDemand == 0
            ? minAgents
            : (int)Math.Ceiling(normalizedDemand / (double)unitsPerAgent);
        if (desired < minAgents) desired = minAgents;
        if (desired > maxAgents) desired = maxAgents;
        return desired;
    }

    private static bool IsAutoscaledInstance(BackgroundAgentInstance instance, string role)
    {
        return instance.Config.Id.StartsWith($"autoscale-{role}-", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetNextAutoscaledId(string role, HashSet<string> knownIds)
    {
        var i = 1;
        while (true)
        {
            var id = $"autoscale-{role}-{i}";
            if (!knownIds.Contains(id))
                return id;
            i++;
        }
    }

    private static BackgroundAgentConfig CloneForAutoscale(BackgroundAgentConfig template, string newId)
    {
        return new BackgroundAgentConfig
        {
            Id = newId,
            Name = $"{template.Name} (autoscaled)",
            Role = template.Role,
            ParentId = template.Id,
            ModelProvider = template.ModelProvider,
            ModelName = template.ModelName,
            Commands = template.Commands.ToList(),
            Parameters = template.Parameters != null
                ? new Dictionary<string, object>(template.Parameters)
                : null,
            Schedule = new BackgroundAgentSchedule
            {
                Type = template.Schedule.Type,
                Interval = template.Schedule.Interval,
                CronExpression = template.Schedule.CronExpression,
                InitialDelay = template.Schedule.InitialDelay
            },
            Enabled = true,
            MaxDataSensitivity = template.MaxDataSensitivity,
            AllowedDataSensitivityLevels = template.AllowedDataSensitivityLevels?.ToList(),
            CustomSensitivityLevels = template.CustomSensitivityLevels != null
                ? new Dictionary<string, CustomSensitivityLevel>(template.CustomSensitivityLevels)
                : null,
            RAG = template.RAG == null
                ? null
                : new RAGConfig
                {
                    Enabled = template.RAG.Enabled,
                    VectorStoreProvider = template.RAG.VectorStoreProvider,
                    VectorStorePath = template.RAG.VectorStorePath,
                    MaxRetrievalResults = template.RAG.MaxRetrievalResults,
                    SimilarityThreshold = template.RAG.SimilarityThreshold,
                    KnowledgeSources = template.RAG.KnowledgeSources?.ToList(),
                    MaxSourceSensitivity = template.RAG.MaxSourceSensitivity
                },
            WebSearch = template.WebSearch == null
                ? null
                : new WebSearchConfig
                {
                    Enabled = template.WebSearch.Enabled,
                    SearchProvider = template.WebSearch.SearchProvider,
                    ApiKey = template.WebSearch.ApiKey,
                    MaxResults = template.WebSearch.MaxResults,
                    FilterSensitiveContent = template.WebSearch.FilterSensitiveContent,
                    AllowedDomains = template.WebSearch.AllowedDomains?.ToList(),
                    BlockedDomains = template.WebSearch.BlockedDomains?.ToList()
                },
            ExfiltrationPolicy = new ExfiltrationPolicy
            {
                BlockExternalLLMs = template.ExfiltrationPolicy.BlockExternalLLMs,
                BlockWebSearch = template.ExfiltrationPolicy.BlockWebSearch,
                BlockNetworkExports = template.ExfiltrationPolicy.BlockNetworkExports,
                RequireLocalOnly = template.ExfiltrationPolicy.RequireLocalOnly,
                AllowedDestinations = template.ExfiltrationPolicy.AllowedDestinations?.ToList(),
                MaxAllowedLevel = template.ExfiltrationPolicy.MaxAllowedLevel
            }
        };
    }

    private static bool FilterByStatus(BackgroundAgentConfig c, Dictionary<string, BackgroundAgentInstance> byId, string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
            return true;
        var state = byId.TryGetValue(c.Id, out var inst) ? inst.State.ToString() : "NotRegistered";
        return string.Equals(state, status, StringComparison.OrdinalIgnoreCase);
    }

    private static bool FilterByRole(BackgroundAgentConfig c, string? role)
    {
        if (string.IsNullOrWhiteSpace(role))
            return true;
        return string.Equals(c.Role, role, StringComparison.OrdinalIgnoreCase);
    }

    private static bool FilterBySensitivity(BackgroundAgentConfig c, string? sensitivity)
    {
        if (string.IsNullOrWhiteSpace(sensitivity))
            return true;
        return string.Equals(c.MaxDataSensitivity, sensitivity, StringComparison.OrdinalIgnoreCase);
    }
}
