using Microsoft.Extensions.Logging;
using Ashlar.Core.Application.Analysis.Models;
using Ashlar.Core.Domain.Values;
using Ashlar.Abstractions;
using System.Text.Json;

namespace Ashlar.Infrastructure.Analysis.Rules;

/// <summary>
/// Analysis rule for code quality metrics (cyclomatic complexity, maintainability).
/// 
/// Responsibilities:
/// - Analyzes code quality metrics using an ITool implementation
/// - Detects high cyclomatic complexity
/// - Reports maintainability issues as violations
/// 
/// Implements IAnalysisRule for use with AnalysisRuleEngine.
/// Used by AnalysisServiceAdapter to assess code quality in assemblies.
/// </summary>
public class CodeQualityRule : IAnalysisRule
{
    private readonly ILogger<CodeQualityRule> _logger;
    private readonly ITool _analyzeTool;

    /// <summary>Initializes a new code quality rule.</summary>
    public CodeQualityRule(ILogger<CodeQualityRule> logger, ITool analyzeTool)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _analyzeTool = analyzeTool ?? throw new ArgumentNullException(nameof(analyzeTool));
    }

    /// <summary>Name.</summary>
    public string Name => "CodeQuality";
    /// <summary>Description.</summary>
    public string Description => "Analyzes code quality metrics";

    /// <summary>Analyze asynchronously.</summary>
    public async Task<IReadOnlyList<Violation>> AnalyzeAsync(
        FileInfo assemblyFile,
        CancellationToken cancellationToken = default)
    {
        var violations = new List<Violation>();

        try
        {
            var snapshot = new WorldSnapshot(0, new Dictionary<string, object?>());

            // Use JsonSerializer to safely encode the path. Raw string interpolation breaks
            // on Windows paths because the backslashes (\U, \b, \n …) become invalid JSON escapes.
            var argsElement = JsonSerializer.SerializeToElement(new { path = assemblyFile.FullName });
            var analyzeCall = new ToolCall(_analyzeTool.Id, argsElement);

            var result = await _analyzeTool.InvokeAsync(analyzeCall, snapshot, cancellationToken);

            // Parse analysis results for quality metrics
            if (TryGetPayloadElement(result.Payload, out var jsonElement))
            {
                // Check for high complexity or low maintainability
                // This is a placeholder - actual implementation would parse detailed metrics
                if (jsonElement.TryGetProperty("Complexity", out var complexityElement))
                {
                    var complexity = complexityElement.GetInt32();
                    if (complexity > 20) // Threshold for high complexity
                    {
                        violations.Add(new Violation
                        {
                            Rule = Name,
                            Message = $"High cyclomatic complexity detected: {complexity}",
                            FilePath = assemblyFile.FullName,
                            Severity = RiskLevel.Medium
                        });
                    }
                }
            }
        }
        catch (BadImageFormatException)
        {
            // Native or non-managed file — not a .NET assembly. Skip silently; this is expected
            // when scanning bin/obj output that contains native interop DLLs (e.g. libllama, e_sqlite3).
            _logger.LogDebug(
                "Skipping non-managed assembly: {Path}",
                assemblyFile.FullName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Code quality analysis failed for assembly: {Path}",
                assemblyFile.FullName);
        }

        return violations;
    }

    private static bool TryGetPayloadElement(object? payload, out JsonElement jsonElement)
    {
        if (payload is JsonElement element)
        {
            jsonElement = element;
            return true;
        }

        if (payload is null)
        {
            jsonElement = default;
            return false;
        }

        jsonElement = JsonSerializer.SerializeToElement(payload);
        return true;
    }
}

