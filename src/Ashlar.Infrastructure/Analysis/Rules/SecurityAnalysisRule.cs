using Microsoft.Extensions.Logging;
using Ashlar.Core.Application.Analysis.Models;
using Ashlar.Core.Domain.Values;
using Ashlar.Abstractions;
using System.Text.Json;

namespace Ashlar.Infrastructure.Analysis.Rules;

/// <summary>
/// Analysis rule for security scanning.
/// 
/// Responsibilities:
/// - Scans assemblies for security vulnerabilities using an ITool implementation
/// - Reports security findings as violations
/// - Assigns high severity to security issues
/// 
/// Implements IAnalysisRule for use with AnalysisRuleEngine.
/// Used by AnalysisServiceAdapter to detect security issues in assemblies.
/// </summary>
public class SecurityAnalysisRule : IAnalysisRule
{
    private readonly ILogger<SecurityAnalysisRule> _logger;
    private readonly ITool _securityScanTool;

    /// <summary>Initializes a new security analysis rule.</summary>
    public SecurityAnalysisRule(ILogger<SecurityAnalysisRule> logger, ITool securityScanTool)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _securityScanTool = securityScanTool ?? throw new ArgumentNullException(nameof(securityScanTool));
    }

    /// <summary>Name.</summary>
    public string Name => "SecurityScan";
    /// <summary>Description.</summary>
    public string Description => "Scans assemblies for security vulnerabilities";

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
            var securityCall = new ToolCall(_securityScanTool.Id, argsElement);

            var result = await _securityScanTool.InvokeAsync(securityCall, snapshot, cancellationToken);

            if (TryGetPayloadElement(result.Payload, out var jsonElement))
            {
                if (jsonElement.TryGetProperty("Count", out var countElement) &&
                    countElement.GetInt32() > 0)
                {
                    violations.Add(new Violation
                    {
                        Rule = Name,
                        Message = $"Security scan found {countElement.GetInt32()} finding(s)",
                        FilePath = assemblyFile.FullName,
                        Severity = RiskLevel.High
                    });
                }
            }
        }
        catch (BadImageFormatException)
        {
            // Native or non-managed file — Mono.Cecil cannot read it. Skip silently; this is
            // expected when scanning bin/obj output that contains native interop DLLs.
            _logger.LogDebug(
                "Skipping non-managed assembly: {Path}",
                assemblyFile.FullName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Security scan failed for assembly: {Path}",
                assemblyFile.FullName);

            violations.Add(new Violation
            {
                Rule = Name,
                Message = $"Security scan error: {ex.Message}",
                FilePath = assemblyFile.FullName,
                Severity = RiskLevel.Medium
            });
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

