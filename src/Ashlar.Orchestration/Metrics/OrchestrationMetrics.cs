using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Ashlar.Core.Application.Common.Ports;
using Ashlar.Abstractions.Agents;

namespace Ashlar.Orchestration.Metrics;

/// <summary>
/// Enhanced metrics collector for orchestration-specific metrics.
///
/// Responsibilities:
/// - Records operation execution times and counts
/// - Tracks agent execution metrics (duration, state, resource usage)
/// - Manages distributed tracing spans
/// - Generates performance reports
/// - Integrates with base IMetricsCollector for standard metrics
///
/// Thread-safe implementation using concurrent collections.
/// Used throughout orchestration for comprehensive metrics collection.
/// </summary>
public sealed class OrchestrationMetrics
{
    private readonly IMetricsCollector? _baseCollector;
    private readonly ILogger<OrchestrationMetrics> _logger;
    private readonly ConcurrentDictionary<string, OperationMetrics> _operations = new();
    private readonly ConcurrentDictionary<string, AgentMetrics> _agentMetrics = new();
    private readonly Dictionary<string, TraceSpan> _spans = new(StringComparer.Ordinal);
    private readonly Queue<string> _spanOrder = new();
    private readonly object _spanGate = new();
    private readonly int _maxRetainedSpans;

    public OrchestrationMetrics(
        ILogger<OrchestrationMetrics> logger,
        IMetricsCollector? baseCollector = null) : this(logger, 1024, baseCollector)
    {
    }

    /// <summary>
    /// Creates a collector retaining at most <paramref name="maxRetainedSpans"/> traces,
    /// including unfinished traces. Oldest traces are evicted at admission; ending an evicted
    /// trace is harmless. Export traces separately when a full history is required.
    /// </summary>
    public OrchestrationMetrics(ILogger<OrchestrationMetrics> logger, int maxRetainedSpans,
        IMetricsCollector? baseCollector = null)
    {
        if (maxRetainedSpans <= 0) throw new ArgumentOutOfRangeException(nameof(maxRetainedSpans));
        _maxRetainedSpans = maxRetainedSpans;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _baseCollector = baseCollector;
    }

    /// <summary>
    /// Records execution time for an orchestration operation.
    /// </summary>
    public void RecordOperation(string operationName, TimeSpan duration, Dictionary<string, object>? metadata = null)
    {
        var metrics = _operations.AddOrUpdate(
            operationName,
            new OperationMetrics
            {
                OperationName = operationName,
                TotalDuration = duration,
                Count = 1,
                MinDuration = duration,
                MaxDuration = duration,
                Metadata = metadata ?? new Dictionary<string, object>()
            },
            (key, existing) =>
            {
                existing.TotalDuration += duration;
                existing.Count++;
                existing.MinDuration = TimeSpan.FromMilliseconds(Math.Min(existing.MinDuration.TotalMilliseconds, duration.TotalMilliseconds));
                existing.MaxDuration = TimeSpan.FromMilliseconds(Math.Max(existing.MaxDuration.TotalMilliseconds, duration.TotalMilliseconds));
                if (metadata != null)
                {
                    foreach (var kvp in metadata)
                    {
                        existing.Metadata[kvp.Key] = kvp.Value;
                    }
                }
                return existing;
            });

        _baseCollector?.RecordExecutionTime($"Orchestration.{operationName}", duration);
        _baseCollector?.IncrementCounter($"Orchestration.{operationName}.Count");

        _logger.LogDebug("Recorded operation {Operation}: {Duration}ms", operationName, duration.TotalMilliseconds);
    }

    /// <summary>
    /// Records metrics for an agent execution.
    /// </summary>
    public void RecordAgentExecution(
        string agentId,
        string domain,
        TimeSpan duration,
        AgentState finalState,
        int? resourceUsageMB = null,
        int? contextTokensUsed = null)
    {
        var metrics = _agentMetrics.AddOrUpdate(
            agentId,
            new AgentMetrics
            {
                AgentId = agentId,
                Domain = domain,
                ExecutionCount = 1,
                TotalDuration = duration,
                MinDuration = duration,
                MaxDuration = duration,
                LastState = finalState,
                TotalResourceUsageMB = resourceUsageMB ?? 0,
                TotalContextTokensUsed = contextTokensUsed ?? 0
            },
            (key, existing) =>
            {
                existing.ExecutionCount++;
                existing.TotalDuration += duration;
                existing.MinDuration = TimeSpan.FromMilliseconds(Math.Min(existing.MinDuration.TotalMilliseconds, duration.TotalMilliseconds));
                existing.MaxDuration = TimeSpan.FromMilliseconds(Math.Max(existing.MaxDuration.TotalMilliseconds, duration.TotalMilliseconds));
                existing.LastState = finalState;
                existing.TotalResourceUsageMB += resourceUsageMB ?? 0;
                existing.TotalContextTokensUsed += contextTokensUsed ?? 0;
                return existing;
            });

        _baseCollector?.RecordExecutionTime($"Agent.{domain}.{agentId}", duration);
        _baseCollector?.IncrementCounter($"Agent.{domain}.Executed");
        _baseCollector?.IncrementCounter($"Agent.State.{finalState}");

        if (resourceUsageMB.HasValue)
        {
            _baseCollector?.IncrementCounter($"Agent.Resource.MemoryMB", resourceUsageMB.Value);
        }

        _logger.LogDebug("Recorded agent metrics {AgentId}: {Duration}ms, state={State}", agentId, duration.TotalMilliseconds, finalState);
    }

    /// <summary>
    /// Starts a trace span for distributed tracing.
    /// </summary>
    public string StartSpan(string operationName, string? parentSpanId = null, Dictionary<string, string>? tags = null)
    {
        var spanId = Guid.NewGuid().ToString();
        var span = new TraceSpan
        {
            SpanId = spanId,
            OperationName = operationName,
            ParentSpanId = parentSpanId,
            StartTime = DateTimeOffset.UtcNow,
            Tags = tags is null ? new Dictionary<string, string>() : new Dictionary<string, string>(tags)
        };

        lock (_spanGate)
        {
            while (_spans.Count >= _maxRetainedSpans)
                _spans.Remove(_spanOrder.Dequeue());
            _spans[spanId] = span;
            _spanOrder.Enqueue(spanId);
        }
        _logger.LogDebug("Started span {SpanId}: {Operation}", spanId, operationName);
        return spanId;
    }

    /// <summary>
    /// Ends a trace span.
    /// </summary>
    public void EndSpan(string spanId, bool success = true, Dictionary<string, string>? additionalTags = null)
    {
        lock (_spanGate)
        {
            if (!_spans.TryGetValue(spanId, out var span))
            {
                _logger.LogWarning("Attempted to end non-existent span {SpanId}", spanId);
                return;
            }

            span.EndTime = DateTimeOffset.UtcNow;
            span.Duration = span.EndTime.Value - span.StartTime;
            span.Success = success;

            if (additionalTags != null)
            {
                foreach (var kvp in additionalTags)
                {
                    span.Tags[kvp.Key] = kvp.Value;
                }
            }

            _logger.LogDebug("Ended span {SpanId}: {Duration}ms, success={Success}", spanId, span.Duration.TotalMilliseconds, success);
        }
    }

    /// <summary>
    /// Gets a performance report for all operations.
    /// </summary>
    public PerformanceReport GetPerformanceReport()
    {
        var operations = _operations.Values
            .Select(m => new OperationReport
            {
                OperationName = m.OperationName,
                Count = m.Count,
                TotalDuration = m.TotalDuration,
                AverageDuration = TimeSpan.FromMilliseconds(m.TotalDuration.TotalMilliseconds / m.Count),
                MinDuration = m.MinDuration,
                MaxDuration = m.MaxDuration,
                Metadata = m.Metadata
            })
            .OrderByDescending(o => o.TotalDuration)
            .ToList();

        var agentReports = _agentMetrics.Values
            .Select(m => new AgentReport
            {
                AgentId = m.AgentId,
                Domain = m.Domain,
                ExecutionCount = m.ExecutionCount,
                AverageDuration = TimeSpan.FromMilliseconds(m.TotalDuration.TotalMilliseconds / m.ExecutionCount),
                MinDuration = m.MinDuration,
                MaxDuration = m.MaxDuration,
                LastState = m.LastState,
                AverageResourceUsageMB = m.ExecutionCount > 0 ? m.TotalResourceUsageMB / m.ExecutionCount : 0,
                AverageContextTokensUsed = m.ExecutionCount > 0 ? m.TotalContextTokensUsed / m.ExecutionCount : 0
            })
            .OrderByDescending(a => a.ExecutionCount)
            .ToList();

        var traceSpans = GetTraces()
            .Where(s => s.EndTime.HasValue)
            .Select(s => new SpanReport
            {
                SpanId = s.SpanId,
                OperationName = s.OperationName,
                ParentSpanId = s.ParentSpanId,
                Duration = s.Duration,
                Success = s.Success,
                Tags = s.Tags
            })
            .ToList();

        return new PerformanceReport
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            Operations = operations,
            Agents = agentReports,
            Traces = traceSpans
        };
    }

    /// <summary>
    /// Gets metrics for a specific agent.
    /// </summary>
    public AgentMetrics? GetAgentMetrics(string agentId)
    {
        return _agentMetrics.TryGetValue(agentId, out var metrics) ? metrics : null;
    }

    /// <summary>
    /// Gets all trace spans for a correlation ID or operation.
    /// </summary>
    public IReadOnlyList<TraceSpan> GetTraces(string? correlationId = null, string? operationName = null)
    {
        lock (_spanGate)
        {
            return _spans.Values
                .Where(s =>
                    (correlationId == null || s.Tags.TryGetValue("correlationId", out var cid) && cid == correlationId) &&
                    (operationName == null || s.OperationName == operationName))
                .Select(s => new TraceSpan
                {
                    SpanId = s.SpanId,
                    OperationName = s.OperationName,
                    ParentSpanId = s.ParentSpanId,
                    StartTime = s.StartTime,
                    EndTime = s.EndTime,
                    Duration = s.Duration,
                    Success = s.Success,
                    Tags = new Dictionary<string, string>(s.Tags)
                }).ToList();
        }
    }

    /// <summary>
    /// Clears all collected metrics.
    /// </summary>
    public void Clear()
    {
        _operations.Clear();
        _agentMetrics.Clear();
        lock (_spanGate)
        {
            _spans.Clear();
            _spanOrder.Clear();
        }
        _logger.LogInformation("Cleared all orchestration metrics");
    }
}
