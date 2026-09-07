using System.Text.Json;
using Ashlar.Core.Application.Analysis.Models;
using Ashlar.Core.Application.Validation.Models;
using Ashlar.Core.Application.Agent.Models;
using Ashlar.Core.Application.Common.Models;
using Ashlar.Orchestration.Coordination;
using Ashlar.Orchestration.Coordination.Conflicts;
using Ashlar.Orchestration.Metrics;

namespace Ashlar.CLI.Formatting;

/// <summary>
/// Console renderer implementation.
/// 
/// Provides concrete implementation of IConsoleRenderer for rendering CLI output.
/// Supports both human-readable and JSON output formats.
/// Handles formatting of complex objects (results, metrics, traces) for console display.
/// 
/// Used throughout the CLI layer for all console output operations.
/// </summary>
public class ConsoleRenderer : IConsoleRenderer
{
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>Creates a console renderer with default JSON serialization options.</summary>
    public ConsoleRenderer()
    {
        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = false,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };
    }

    /// <inheritdoc />
    public void RenderSuccess(string message)
    {
        Console.Out.WriteLine(message);
    }

    /// <inheritdoc />
    public void RenderError(string message)
    {
        Console.Error.WriteLine($"Error: {message}");
    }

    // Console.Error, not Console.Out, for all three. Callers gate these markers on --verbose and
    // never on the format flag, and they bracket the JSON write — so with --format-json --verbose
    // stdout carries "[progress] …", then the document, then "[complete] …", and the document no
    // longer parses. The caller piping it into a reader gets a syntax error under exit 0. This is the
    // failure the host logger was already moved off stdout for (Program.JsonOutputMode), and the
    // contract --format-json's own help text states: stderr still carries logs.

    /// <inheritdoc />
    public void RenderProgressStart(string message)
    {
        Console.Error.WriteLine($"[progress] {message}");
    }

    /// <inheritdoc />
    public void RenderProgressComplete(string message)
    {
        Console.Error.WriteLine($"[complete] {message}");
    }

    /// <inheritdoc />
    public void RenderProgress(ProgressReport report)
    {
        var stepInfo = report.TotalSteps.HasValue && report.CurrentStep.HasValue
            ? $" ({report.CurrentStep}/{report.TotalSteps})"
            : string.Empty;
        Console.Error.WriteLine($"[{report.Percentage}%]{stepInfo} {report.Message}");
    }

    /// <inheritdoc />
    public void RenderErrorWithCode(string message, string? errorCode, string? suggestion = null)
    {
        Console.Error.WriteLine($"Error [{errorCode}]: {message}");
        if (!string.IsNullOrWhiteSpace(suggestion))
        {
            Console.Error.WriteLine($"Suggestion: {suggestion}");
        }
    }

    /// <inheritdoc />
    public void RenderAgentList(IReadOnlyList<Ashlar.Core.Application.Agent.Models.AgentMetadata> agents, bool json)
    {
        if (json)
        {
            var envelope = new CliEnvelope<IReadOnlyList<Ashlar.Core.Application.Agent.Models.AgentMetadata>>(
                true, 
                agents, 
                null);
            Console.Out.WriteLine(JsonSerializer.Serialize(envelope, _jsonOptions));
        }
        else
        {
            if (agents.Count == 0)
            {
                Console.Out.WriteLine("No agents found.");
                return;
            }

            Console.Out.WriteLine($"Found {agents.Count} agent(s):");
            foreach (var agent in agents)
            {
                Console.Out.WriteLine($"  - {agent.Name}");
                if (!string.IsNullOrWhiteSpace(agent.Description))
                {
                    Console.Out.WriteLine($"    Description: {agent.Description}");
                }
                if (agent.Capabilities.Count > 0)
                {
                    Console.Out.WriteLine($"    Capabilities: {string.Join(", ", agent.Capabilities)}");
                }
            }
        }
    }

    /// <inheritdoc />
    public void RenderAnalysisResult(AnalysisResult result, bool json)
    {
        if (json)
        {
            var envelope = new CliEnvelope<AnalysisResult>(!result.HasViolations, result, result.HasViolations ? $"Found {result.TotalViolations} violation(s)" : null);
            Console.Out.WriteLine(JsonSerializer.Serialize(envelope, _jsonOptions));
        }
        else
        {
            if (result.HasViolations)
            {
                Console.Error.WriteLine($"Found {result.TotalViolations} violation(s):");
                foreach (var violation in result.Violations)
                {
                    Console.Error.WriteLine($"  - [{violation.Severity}] {violation.Rule}: {violation.Message} ({violation.FilePath}:{violation.LineNumber})");
                }
            }
            else
            {
                Console.Out.WriteLine("No violations found.");
            }
        }
    }

    /// <inheritdoc />
    public void RenderValidationResult(ValidationResult result, bool json)
    {
        if (json)
        {
            var envelope = new CliEnvelope<ValidationResult>(result.Passed, result, result.Passed ? null : "Validation failed");
            Console.Out.WriteLine(JsonSerializer.Serialize(envelope, _jsonOptions));
        }
        else
        {
            if (result.Passed)
            {
                Console.Out.WriteLine($"Validation passed ({result.TestsPassed}/{result.TestsRun} tests)");
            }
            else
            {
                Console.Error.WriteLine($"Validation failed ({result.TestsFailed}/{result.TestsRun} tests failed)");
                if (result.TestResults != null)
                {
                    foreach (var test in result.TestResults.Where(t => !t.Passed))
                    {
                        Console.Error.WriteLine($"  - {test.Name}: {test.Message}");
                    }
                }
            }
        }
    }

    /// <inheritdoc />
    public void RenderAgentResult(AgentExecutionResult result, bool json)
    {
        if (json)
        {
            var envelope = new CliEnvelope<AgentExecutionResult>(result.Success, result, result.Success ? null : result.Message);
            Console.Out.WriteLine(JsonSerializer.Serialize(envelope, _jsonOptions));
        }
        else
        {
            if (result.Success)
            {
                Console.Out.WriteLine($"Agent '{result.AgentName}' executed successfully");
                if (result.Duration.HasValue)
                {
                    Console.Out.WriteLine($"Duration: {result.Duration.Value.TotalMilliseconds}ms");
                }
            }
            else
            {
                Console.Error.WriteLine($"Agent '{result.AgentName}' failed: {result.Message}");
            }
        }
    }

    /// <inheritdoc />
    public void RenderOrchestrationResult(OrchestrationResult result)
    {
        // A success flag that contradicts the progress counter printed two lines below it is not a
        // success report, it is two facts left for the reader to reconcile. The empty run is
        // reported as an empty run, before anything congratulatory is printed.
        if (OrchestrationWorkReport.IsSilentSuccess(result))
        {
            foreach (var line in OrchestrationWorkReport.NoWorkReport(result))
            {
                Console.Error.WriteLine(line);
            }
            Console.Error.WriteLine($"  Agents planned: {result.Decomposition?.Agents.Count ?? 0}");
            Console.Error.WriteLine($"  Conflicts: {result.Conflicts.Count}");
            Console.Error.WriteLine($"  Escalations: {result.Escalations.Count}");
            return;
        }

        if (result.Success)
        {
            Console.Out.WriteLine($"Orchestration completed successfully");
            Console.Out.WriteLine($"  Agents: {result.Decomposition?.Agents.Count ?? 0}");
            Console.Out.WriteLine($"  Conflicts: {result.Conflicts.Count}");
            Console.Out.WriteLine($"  Escalations: {result.Escalations.Count}");
            if (result.ProgressSummary != null)
            {
                Console.Out.WriteLine($"  Progress: {result.ProgressSummary.Completed}/{result.ProgressSummary.TotalAgents} agents completed ({result.ProgressSummary.ProgressPercentage:P0})");
            }
        }
        else
        {
            Console.Error.WriteLine("Orchestration failed");
            if (result.Conflicts.Count > 0)
            {
                Console.Error.WriteLine($"  Conflicts: {result.Conflicts.Count}");
            }
            if (result.Escalations.Count > 0)
            {
                Console.Error.WriteLine($"  Escalations: {result.Escalations.Count}");
            }
        }
    }

    /// <inheritdoc />
    public void RenderJson(object data)
    {
        Console.Out.WriteLine(JsonSerializer.Serialize(data, _jsonOptions));
    }

    /// <inheritdoc />
    public void RenderTable<T>(IEnumerable<T> items)
    {
        // Simple table rendering - can be enhanced with Spectre.Console later
        foreach (var item in items)
        {
            Console.Out.WriteLine(item?.ToString() ?? string.Empty);
        }
    }

    /// <inheritdoc />
    public void RenderEscalations(IReadOnlyList<Escalation> escalations, bool verbose)
    {
        if (escalations.Count == 0)
        {
            Console.Out.WriteLine("No pending escalations.");
            return;
        }

        Console.Out.WriteLine($"Found {escalations.Count} escalation(s):");
        Console.Out.WriteLine();

        foreach (var escalation in escalations)
        {
            var severityColor = escalation.Severity switch
            {
                EscalationSeverity.Critical => "CRITICAL",
                EscalationSeverity.High => "HIGH",
                EscalationSeverity.Medium => "MEDIUM",
                EscalationSeverity.Low => "LOW",
                _ => "UNKNOWN"
            };

            Console.Out.WriteLine($"  [{severityColor}] {escalation.Id}");
            Console.Out.WriteLine($"    Status: {escalation.Status}");
            Console.Out.WriteLine($"    Escalated: {escalation.EscalatedAt:yyyy-MM-dd HH:mm:ss} UTC");

            if (escalation.Conflict != null)
            {
                Console.Out.WriteLine($"    Conflict Type: {escalation.Conflict.ConflictType}");
                Console.Out.WriteLine($"    Agents: {string.Join(", ", escalation.Conflict.AgentIds)}");
                Console.Out.WriteLine($"    Description: {escalation.Conflict.Description}");
            }
            else if (!string.IsNullOrWhiteSpace(escalation.Description))
            {
                Console.Out.WriteLine($"    Description: {escalation.Description}");
            }

            if (!string.IsNullOrWhiteSpace(escalation.IssueType))
            {
                Console.Out.WriteLine($"    Issue Type: {escalation.IssueType}");
            }

            if (!string.IsNullOrWhiteSpace(escalation.Context) && verbose)
            {
                Console.Out.WriteLine($"    Context: {escalation.Context}");
            }

            Console.Out.WriteLine();
        }
    }

    /// <inheritdoc />
    public void RenderEscalationDetails(Escalation escalation, bool verbose)
    {
        var severityColor = escalation.Severity switch
        {
            EscalationSeverity.Critical => "CRITICAL",
            EscalationSeverity.High => "HIGH",
            EscalationSeverity.Medium => "MEDIUM",
            EscalationSeverity.Low => "LOW",
            _ => "UNKNOWN"
        };

        Console.Out.WriteLine($"Escalation Details");
        Console.Out.WriteLine($"==================");
        Console.Out.WriteLine($"ID: {escalation.Id}");
        Console.Out.WriteLine($"Severity: [{severityColor}]");
        Console.Out.WriteLine($"Status: {escalation.Status}");
        Console.Out.WriteLine($"Escalated: {escalation.EscalatedAt:yyyy-MM-dd HH:mm:ss} UTC");

        if (escalation.ResolvedAt.HasValue)
        {
            Console.Out.WriteLine($"Resolved: {escalation.ResolvedAt.Value:yyyy-MM-dd HH:mm:ss} UTC");
        }

        if (!string.IsNullOrWhiteSpace(escalation.Resolution))
        {
            Console.Out.WriteLine($"Resolution: {escalation.Resolution}");
        }

        Console.Out.WriteLine();

        if (escalation.Conflict != null)
        {
            Console.Out.WriteLine("Conflict Information:");
            Console.Out.WriteLine($"  Type: {escalation.Conflict.ConflictType}");
            Console.Out.WriteLine($"  Severity: {escalation.Conflict.Severity}");
            Console.Out.WriteLine($"  Agents: {string.Join(", ", escalation.Conflict.AgentIds)}");
            Console.Out.WriteLine($"  Description: {escalation.Conflict.Description}");

            if (escalation.Conflict.Metadata != null && escalation.Conflict.Metadata.Count > 0 && verbose)
            {
                Console.Out.WriteLine("  Metadata:");
                foreach (var kvp in escalation.Conflict.Metadata)
                {
                    Console.Out.WriteLine($"    {kvp.Key}: {kvp.Value}");
                }
            }
        }
        else if (!string.IsNullOrWhiteSpace(escalation.Description))
        {
            Console.Out.WriteLine("Issue Information:");
            Console.Out.WriteLine($"  Type: {escalation.IssueType ?? "Unknown"}");
            Console.Out.WriteLine($"  Description: {escalation.Description}");
        }

        if (!string.IsNullOrWhiteSpace(escalation.Context))
        {
            Console.Out.WriteLine();
            Console.Out.WriteLine("Context:");
            Console.Out.WriteLine($"  {escalation.Context}");
        }
    }

    /// <inheritdoc />
    public void RenderConflicts(IReadOnlyList<Conflict> conflicts, bool verbose)
    {
        if (conflicts.Count == 0)
        {
            Console.Out.WriteLine("No conflicts detected.");
            return;
        }

        Console.Out.WriteLine($"Found {conflicts.Count} conflict(s):");
        Console.Out.WriteLine();

        foreach (var conflict in conflicts)
        {
            var severityColor = conflict.Severity switch
            {
                ConflictSeverity.Critical => "CRITICAL",
                ConflictSeverity.High => "HIGH",
                ConflictSeverity.Medium => "MEDIUM",
                ConflictSeverity.Low => "LOW",
                _ => "UNKNOWN"
            };

            Console.Out.WriteLine($"  [{severityColor}] {conflict.ConflictType}");
            Console.Out.WriteLine($"    Agents: {string.Join(", ", conflict.AgentIds)}");
            Console.Out.WriteLine($"    Description: {conflict.Description}");

            if (conflict.Metadata != null && conflict.Metadata.Count > 0 && verbose)
            {
                Console.Out.WriteLine("    Metadata:");
                foreach (var kvp in conflict.Metadata)
                {
                    Console.Out.WriteLine($"      {kvp.Key}: {kvp.Value}");
                }
            }

            Console.Out.WriteLine();
        }
    }

    /// <inheritdoc />
    public void RenderPerformanceReport(PerformanceReport report, bool verbose)
    {
        Console.Out.WriteLine("Performance Report");
        Console.Out.WriteLine("==================");
        Console.Out.WriteLine($"Generated: {report.GeneratedAt:yyyy-MM-dd HH:mm:ss} UTC");
        Console.Out.WriteLine();

        // Operations
        if (report.Operations.Count > 0)
        {
            Console.Out.WriteLine("Operations:");
            Console.Out.WriteLine($"  {"Operation",-30} {"Count",-10} {"Avg (ms)",-12} {"Total (ms)",-12} {"Min (ms)",-10} {"Max (ms)",-10}");
            Console.Out.WriteLine(new string('-', 90));
            foreach (var op in report.Operations)
            {
                Console.Out.WriteLine($"  {op.OperationName,-30} {op.Count,-10} {op.AverageDuration.TotalMilliseconds:F2,-12} {op.TotalDuration.TotalMilliseconds:F2,-12} {op.MinDuration.TotalMilliseconds:F2,-10} {op.MaxDuration.TotalMilliseconds:F2,-10}");
            }
            Console.Out.WriteLine();
        }

        // Agents
        if (report.Agents.Count > 0)
        {
            Console.Out.WriteLine("Agents:");
            Console.Out.WriteLine($"  {"Agent ID",-30} {"Domain",-15} {"Count",-8} {"Avg (ms)",-12} {"State",-15} {"Memory (MB)",-12}");
            Console.Out.WriteLine(new string('-', 100));
            foreach (var agent in report.Agents)
            {
                Console.Out.WriteLine($"  {agent.AgentId,-30} {agent.Domain,-15} {agent.ExecutionCount,-8} {agent.AverageDuration.TotalMilliseconds:F2,-12} {agent.LastState,-15} {agent.AverageResourceUsageMB,-12}");
            }
            Console.Out.WriteLine();
        }

        // Traces
        if (report.Traces.Count > 0 && verbose)
        {
            Console.Out.WriteLine($"Traces ({report.Traces.Count}):");
            foreach (var trace in report.Traces.OrderBy(t => t.Duration))
            {
                var status = trace.Success ? "✓" : "✗";
                Console.Out.WriteLine($"  {status} {trace.OperationName} ({trace.Duration.TotalMilliseconds:F2}ms)");
                if (trace.ParentSpanId != null)
                {
                    Console.Out.WriteLine($"    Parent: {trace.ParentSpanId}");
                }
                if (trace.Tags.Count > 0)
                {
                    Console.Out.WriteLine($"    Tags: {string.Join(", ", trace.Tags.Select(t => $"{t.Key}={t.Value}"))}");
                }
            }
            Console.Out.WriteLine();
        }
    }

    /// <inheritdoc />
    public void RenderAgentMetrics(AgentMetrics metrics, bool verbose)
    {
        Console.Out.WriteLine($"Agent Metrics: {metrics.AgentId}");
        Console.Out.WriteLine("==============");
        Console.Out.WriteLine($"Domain: {metrics.Domain}");
        Console.Out.WriteLine($"Execution Count: {metrics.ExecutionCount}");
        Console.Out.WriteLine($"Average Duration: {metrics.TotalDuration.TotalMilliseconds / metrics.ExecutionCount:F2}ms");
        Console.Out.WriteLine($"Min Duration: {metrics.MinDuration.TotalMilliseconds:F2}ms");
        Console.Out.WriteLine($"Max Duration: {metrics.MaxDuration.TotalMilliseconds:F2}ms");
        Console.Out.WriteLine($"Last State: {metrics.LastState}");
        Console.Out.WriteLine($"Total Resource Usage: {metrics.TotalResourceUsageMB}MB");
        Console.Out.WriteLine($"Total Context Tokens: {metrics.TotalContextTokensUsed}");
    }

    /// <inheritdoc />
    public void RenderTraces(IReadOnlyList<TraceSpan> traces, string? correlationId, string? operationName, bool verbose)
    {
        if (traces.Count == 0)
        {
            Console.Out.WriteLine("No traces found.");
            if (correlationId != null)
            {
                Console.Out.WriteLine($"  Correlation ID: {correlationId}");
            }
            if (operationName != null)
            {
                Console.Out.WriteLine($"  Operation: {operationName}");
            }
            return;
        }

        Console.Out.WriteLine($"Found {traces.Count} trace(s):");
        if (correlationId != null)
        {
            Console.Out.WriteLine($"  Correlation ID: {correlationId}");
        }
        if (operationName != null)
        {
            Console.Out.WriteLine($"  Operation: {operationName}");
        }
        Console.Out.WriteLine();

        foreach (var trace in traces.OrderBy(t => t.StartTime))
        {
            var status = trace.Success ? "✓" : "✗";
            var duration = trace.EndTime.HasValue
                ? $"({(trace.EndTime.Value - trace.StartTime).TotalMilliseconds:F2}ms)"
                : "(in progress)";
            
            Console.Out.WriteLine($"  {status} {trace.OperationName} {duration}");
            Console.Out.WriteLine($"    Span ID: {trace.SpanId}");
            if (trace.ParentSpanId != null)
            {
                Console.Out.WriteLine($"    Parent: {trace.ParentSpanId}");
            }
            Console.Out.WriteLine($"    Start: {trace.StartTime:yyyy-MM-dd HH:mm:ss.fff} UTC");
            if (trace.EndTime.HasValue)
            {
                Console.Out.WriteLine($"    End: {trace.EndTime.Value:yyyy-MM-dd HH:mm:ss.fff} UTC");
            }
            if (trace.Tags.Count > 0 && verbose)
            {
                Console.Out.WriteLine($"    Tags:");
                foreach (var tag in trace.Tags)
                {
                    Console.Out.WriteLine($"      {tag.Key}: {tag.Value}");
                }
            }
            Console.Out.WriteLine();
        }
    }

    private record CliEnvelope<T>(
        [property: System.Text.Json.Serialization.JsonPropertyName("ok")] bool Ok,
        [property: System.Text.Json.Serialization.JsonPropertyName("data")] T? Data,
        [property: System.Text.Json.Serialization.JsonPropertyName("error")] string? Error
    );
}
