using Microsoft.Extensions.Logging;
using Ashlar.Orchestration.Agents;
using Ashlar.Orchestration.Architect;
using Ashlar.Orchestration.Architect.Models;
using System.Text.Json;

namespace Ashlar.Orchestration.Coordination.Conflicts;

/// <summary>
/// Detects conflicts between agents using four conflict types.
/// 
/// Conflict types detected:
/// 1. Schema conflicts: Incompatible data schemas between agents
/// 2. Resource conflicts: Competing for the same resources (files, APIs, etc.)
/// 3. Constraint conflicts: Conflicting constraints or requirements
/// 4. Philosophy conflicts: Fundamental design or approach disagreements
/// 
/// Performs pairwise analysis of all agents to identify potential conflicts
/// that may require negotiation or escalation.
/// </summary>
public sealed class ConflictDetector
{
    private readonly ILogger<ConflictDetector> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConflictDetector"/> class.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    public ConflictDetector(ILogger<ConflictDetector> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Detects all conflicts between agents.
    /// 
    /// Performs pairwise analysis of all agents checking for all four conflict types.
    /// Returns a list of all detected conflicts that may require resolution.
    /// </summary>
    /// <param name="agents">List of agent containers to analyze</param>
    /// <returns>List of detected conflicts</returns>
    public IReadOnlyList<Conflict> DetectConflicts(IReadOnlyList<AgentContainer> agents)
    {
        var conflicts = new List<Conflict>();

        // Check all pairs of agents for conflicts
        for (int i = 0; i < agents.Count; i++)
        {
            for (int j = i + 1; j < agents.Count; j++)
            {
                var agent1 = agents[i];
                var agent2 = agents[j];

                // Check for schema conflicts
                var schemaConflict = DetectSchemaConflict(agent1, agent2);
                if (schemaConflict != null)
                {
                    conflicts.Add(schemaConflict);
                }

                // Check for resource conflicts
                var resourceConflict = DetectResourceConflict(agent1, agent2);
                if (resourceConflict != null)
                {
                    conflicts.Add(resourceConflict);
                }

                // Check for constraint conflicts
                var constraintConflict = DetectConstraintConflict(agent1, agent2);
                if (constraintConflict != null)
                {
                    conflicts.Add(constraintConflict);
                }

                // Check for philosophy conflicts
                var philosophyConflict = DetectPhilosophyConflict(agent1, agent2);
                if (philosophyConflict != null)
                {
                    conflicts.Add(philosophyConflict);
                }
            }
        }

        _logger.LogInformation("Detected {ConflictCount} conflicts among {AgentCount} agents", 
            conflicts.Count, agents.Count);

        return conflicts;
    }

    /// <summary>
    /// Detects schema conflicts between two agents.
    /// 
    /// Checks if both agents have output schemas and if they are incompatible.
    /// </summary>
    /// <param name="agent1">The first agent container.</param>
    /// <param name="agent2">The second agent container.</param>
    /// <returns>A <see cref="Conflict"/> if a schema conflict is detected, null otherwise.</returns>
    private Conflict? DetectSchemaConflict(AgentContainer agent1, AgentContainer agent2)
    {
        var spec1 = agent1.Agent.Spec;
        var spec2 = agent2.Agent.Spec;

        // Check if both agents have output schemas
        if (!spec1.OutputSchema.HasValue || !spec2.OutputSchema.HasValue)
        {
            return null;
        }

        // Check if schemas are incompatible (simplified check - compare JSON structure)
        var schema1 = spec1.OutputSchema.Value;
        var schema2 = spec2.OutputSchema.Value;

        if (!AreSchemasCompatible(schema1, schema2))
        {
            return new Conflict
            {
                ConflictType = ConflictType.Schema,
                AgentIds = new[] { agent1.AgentId, agent2.AgentId },
                Description = $"Agents {agent1.AgentId} and {agent2.AgentId} have incompatible output schemas",
                Severity = ConflictSeverity.High
            };
        }

        return null;
    }

    /// <summary>
    /// Detects resource conflicts by summing requirements across all agents and comparing to budget.
    /// 
    /// Checks if total resource requirements (compute, memory, context) exceed the budget.
    /// </summary>
    /// <param name="agents">The list of agent containers to check.</param>
    /// <param name="budget">Optional resource budget. If not provided, uses default values.</param>
    /// <returns>A read-only list of detected resource conflicts.</returns>
    public IReadOnlyList<Conflict> DetectResourceConflicts(
        IReadOnlyList<AgentContainer> agents,
        ResourceBudget? budget = null)
    {
        var conflicts = new List<Conflict>();
        budget ??= new ResourceBudget
        {
            MaxComputeSeconds = 3600,
            MaxContextTokens = 200000,
            MaxMemoryMB = 16384
        };

        // Sum all resource requirements
        var totalCompute = agents
            .Select(a => a.Agent.Spec.ResourceRequirements?.EstimatedComputeSeconds ?? 0)
            .Sum();
        var totalMemory = agents
            .Select(a => a.Agent.Spec.ResourceRequirements?.RequiredMemoryMB ?? 0)
            .Sum();
        var totalContext = agents
            .Select(a => a.Agent.Spec.ResourceRequirements?.RequiredContextTokens ?? 0)
            .Sum();

        var issues = new List<string>();
        if (budget.MaxComputeSeconds.HasValue && totalCompute > budget.MaxComputeSeconds.Value)
        {
            issues.Add($"Total compute time ({totalCompute}s) exceeds budget ({budget.MaxComputeSeconds.Value}s)");
        }
        if (budget.MaxMemoryMB.HasValue && totalMemory > budget.MaxMemoryMB.Value)
        {
            issues.Add($"Total memory ({totalMemory}MB) exceeds budget ({budget.MaxMemoryMB.Value}MB)");
        }
        if (budget.MaxContextTokens.HasValue && totalContext > budget.MaxContextTokens.Value)
        {
            issues.Add($"Total context tokens ({totalContext}) exceeds budget ({budget.MaxContextTokens.Value})");
        }

        if (issues.Count > 0)
        {
            conflicts.Add(new Conflict
            {
                ConflictType = ConflictType.Resource,
                AgentIds = agents.Select(a => a.AgentId).ToArray(),
                Description = $"Resource contention across {agents.Count} agents: {string.Join(", ", issues)}",
                Severity = ConflictSeverity.Medium
            });
        }

        return conflicts;
    }

    /// <summary>
    /// Detects resource conflicts between two agents.
    /// 
    /// Checks if combined resource requirements exceed reasonable thresholds.
    /// </summary>
    /// <param name="agent1">The first agent container.</param>
    /// <param name="agent2">The second agent container.</param>
    /// <returns>A <see cref="Conflict"/> if a resource conflict is detected, null otherwise.</returns>
    private Conflict? DetectResourceConflict(AgentContainer agent1, AgentContainer agent2)
    {
        var reqs1 = agent1.Agent.Spec.ResourceRequirements;
        var reqs2 = agent2.Agent.Spec.ResourceRequirements;

        if (reqs1 == null || reqs2 == null)
        {
            return null;
        }

        // Check if combined resource requirements exceed reasonable limits
        var totalCompute = (reqs1.EstimatedComputeSeconds ?? 0) + (reqs2.EstimatedComputeSeconds ?? 0);
        var totalMemory = (reqs1.RequiredMemoryMB ?? 0) + (reqs2.RequiredMemoryMB ?? 0);
        var totalContext = (reqs1.RequiredContextTokens ?? 0) + (reqs2.RequiredContextTokens ?? 0);

        // Thresholds for conflict detection
        const int maxComputeSeconds = 3600; // 1 hour
        const int maxMemoryMB = 16384; // 16GB
        const int maxContextTokens = 200000; // 200k tokens

        var issues = new List<string>();
        if (totalCompute > maxComputeSeconds)
        {
            issues.Add($"Combined compute time ({totalCompute}s) exceeds {maxComputeSeconds}s");
        }
        if (totalMemory > maxMemoryMB)
        {
            issues.Add($"Combined memory ({totalMemory}MB) exceeds {maxMemoryMB}MB");
        }
        if (totalContext > maxContextTokens)
        {
            issues.Add($"Combined context tokens ({totalContext}) exceeds {maxContextTokens}");
        }

        if (issues.Count > 0)
        {
            return new Conflict
            {
                ConflictType = ConflictType.Resource,
                AgentIds = new[] { agent1.AgentId, agent2.AgentId },
                Description = $"Resource contention between {agent1.AgentId} and {agent2.AgentId}: {string.Join(", ", issues)}",
                Severity = ConflictSeverity.Medium
            };
        }

        return null;
    }

    /// <summary>
    /// Detects constraint conflicts between two agents.
    /// 
    /// Checks if constraints from different agents contradict each other
    /// (e.g., "must be fast" vs "must be slow").
    /// </summary>
    /// <param name="agent1">The first agent container.</param>
    /// <param name="agent2">The second agent container.</param>
    /// <returns>A <see cref="Conflict"/> if a constraint conflict is detected, null otherwise.</returns>
    private Conflict? DetectConstraintConflict(AgentContainer agent1, AgentContainer agent2)
    {
        var constraints1 = agent1.Agent.Spec.Constraints;
        var constraints2 = agent2.Agent.Spec.Constraints;

        // Check if constraints from different agents contradict each other
        foreach (var c1 in constraints1)
        {
            foreach (var c2 in constraints2)
            {
                if (AreConstraintsContradictory(c1, c2))
                {
                    return new Conflict
                    {
                        ConflictType = ConflictType.Constraint,
                        AgentIds = new[] { agent1.AgentId, agent2.AgentId },
                        Description = $"Contradictory constraints: {agent1.AgentId} requires '{c1.Description}' but {agent2.AgentId} requires '{c2.Description}'",
                        Severity = ConflictSeverity.High
                    };
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Detects philosophy conflicts between two agents.
    /// 
    /// Uses keyword analysis to detect opposing design philosophies
    /// (e.g., performance vs quality, simplicity vs complexity).
    /// </summary>
    /// <param name="agent1">The first agent container.</param>
    /// <param name="agent2">The second agent container.</param>
    /// <returns>A <see cref="Conflict"/> if a philosophy conflict is detected, null otherwise.</returns>
    private Conflict? DetectPhilosophyConflict(AgentContainer agent1, AgentContainer agent2)
    {
        var goal1 = agent1.Agent.Spec.Goal.ToLowerInvariant();
        var goal2 = agent2.Agent.Spec.Goal.ToLowerInvariant();

        // Simple philosophy conflict detection using keyword analysis
        // In a real implementation, this would use embeddings for semantic similarity

        var philosophyKeywords = new Dictionary<string, string[]>
        {
            ["performance"] = new[] { "fast", "quick", "efficient", "optimize", "performance" },
            ["quality"] = new[] { "thorough", "comprehensive", "detailed", "quality", "polish" },
            ["simplicity"] = new[] { "simple", "minimal", "clean", "straightforward" },
            ["complexity"] = new[] { "complex", "advanced", "sophisticated", "feature-rich" }
        };

        var goal1Philosophy = DetectPhilosophy(goal1, philosophyKeywords);
        var goal2Philosophy = DetectPhilosophy(goal2, philosophyKeywords);

        // Check for opposing philosophies
        if ((goal1Philosophy == "performance" && goal2Philosophy == "quality") ||
            (goal1Philosophy == "simplicity" && goal2Philosophy == "complexity"))
        {
            return new Conflict
            {
                ConflictType = ConflictType.Philosophy,
                AgentIds = new[] { agent1.AgentId, agent2.AgentId },
                Description = $"Philosophical disagreement: {agent1.AgentId} emphasizes {goal1Philosophy} while {agent2.AgentId} emphasizes {goal2Philosophy}",
                Severity = ConflictSeverity.Low
            };
        }

        return null;
    }

    /// <summary>
    /// Detects schema incompatibilities by comparing output schemas in detail.
    /// </summary>
    public IReadOnlyList<Conflict> DetectSchemaConflicts(IReadOnlyList<AgentContainer> agents)
    {
        var conflicts = new List<Conflict>();
        var schemaGroups = new Dictionary<string, List<AgentContainer>>();

        // Group agents by schema
        foreach (var agent in agents)
        {
            if (!agent.Agent.Spec.OutputSchema.HasValue)
            {
                continue;
            }

            var schemaKey = GetSchemaKey(agent.Agent.Spec.OutputSchema.Value);
            if (!schemaGroups.ContainsKey(schemaKey))
            {
                schemaGroups[schemaKey] = new List<AgentContainer>();
            }
            schemaGroups[schemaKey].Add(agent);
        }

        // Check for incompatibilities between schema groups
        var schemaList = schemaGroups.Values.ToList();
        for (int i = 0; i < schemaList.Count; i++)
        {
            for (int j = i + 1; j < schemaList.Count; j++)
            {
                var group1 = schemaList[i];
                var group2 = schemaList[j];

                if (!AreSchemasCompatible(
                    group1[0].Agent.Spec.OutputSchema!.Value,
                    group2[0].Agent.Spec.OutputSchema!.Value))
                {
                    var allAgents = group1.Concat(group2).Select(a => a.AgentId).ToArray();
                    conflicts.Add(new Conflict
                    {
                        ConflictType = ConflictType.Schema,
                        AgentIds = allAgents,
                        Description = $"Incompatible schemas: {group1.Count} agents use schema type A, {group2.Count} agents use schema type B",
                        Severity = ConflictSeverity.High
                    });
                }
            }
        }

        return conflicts;
    }

    /// <summary>
    /// Generates a key for a JSON schema to group similar schemas.
    /// 
    /// Uses the schema type if available, otherwise uses a hash of the schema text.
    /// </summary>
    /// <param name="schema">The JSON schema element.</param>
    /// <returns>A string key representing the schema.</returns>
    /// <remarks>
    /// <para><b>The per-process hash below is left in place, and a stable hash is not what is
    /// wrong here.</b></para>
    ///
    /// <para><c>string.GetHashCode()</c> is randomized per process (measured: five distinct
    /// values over five launches on the same schema text). It does not matter at this site. The
    /// return value is used at exactly one place — as the key of <c>schemaGroups</c>, a
    /// <c>Dictionary</c> declared as a local inside <c>DetectSchemaConflicts</c>, which then
    /// reads only <c>.Values</c> and discards the keys. The key does not outlive the method call,
    /// let alone the process: it is never persisted, serialized, returned, or logged, and the
    /// emitted <c>Conflict</c> carries agent ids and a description, never this key.</para>
    ///
    /// <para><b>What would make it a defect.</b> Putting the key into the emitted
    /// <c>Conflict</c>, or caching <c>schemaGroups</c> across calls. Both are unlikely shapes for
    /// this method, and either one is a visible diff.</para>
    ///
    /// <para><b>Two things that ARE wrong here, and matter more.</b> First, the hash path is
    /// nearly dead: the branch above returns <c>type.GetString()</c> whenever the schema has a
    /// <c>type</c> property, so every object schema collapses to the single key <c>"object"</c>
    /// regardless of its properties, and <c>AreSchemasCompatible</c> is never invoked between two
    /// object schemas. Only a schema with no <c>type</c> at all reaches the hash. Second, even
    /// when reached the key is taken over <c>GetRawText()</c>, so two byte-different spellings of
    /// the same schema — differing only in whitespace or property order — land in different
    /// groups. Swapping in SHA-256 fixes neither and would leave the site looking audited while
    /// the grouping stayed wrong. The fix, if this method is taken up, is to key on a canonical
    /// form of the schema (normalized property order and whitespace) and only then hash it.</para>
    /// </remarks>
    private static string GetSchemaKey(JsonElement schema)
    {
        try
        {
            if (schema.TryGetProperty("type", out var type))
            {
                return type.GetString() ?? "unknown";
            }
            return schema.GetRawText().GetHashCode().ToString();
        }
        catch
        {
            return schema.GetRawText().GetHashCode().ToString();
        }
    }

    /// <summary>
    /// Determines if two JSON schemas are compatible.
    /// 
    /// Checks:
    /// - Type compatibility (including integer/number compatibility)
    /// - Required properties compatibility
    /// - Property structure compatibility
    /// </summary>
    /// <param name="schema1">The first schema to compare.</param>
    /// <param name="schema2">The second schema to compare.</param>
    /// <returns>True if the schemas are compatible, false otherwise.</returns>
    private static bool AreSchemasCompatible(JsonElement schema1, JsonElement schema2)
    {
        // Enhanced compatibility check - compare structure in detail
        try
        {
            var json1 = schema1.GetRawText();
            var json2 = schema2.GetRawText();
            
            // If schemas are identical, they're compatible
            if (json1 == json2)
            {
                return true;
            }

            // Parse and compare structure
            var doc1 = JsonDocument.Parse(json1);
            var doc2 = JsonDocument.Parse(json2);

            var root1 = doc1.RootElement;
            var root2 = doc2.RootElement;

            // Check type compatibility
            if (root1.TryGetProperty("type", out var type1) &&
                root2.TryGetProperty("type", out var type2))
            {
                var type1Str = type1.GetString();
                var type2Str = type2.GetString();
                
                if (type1Str != type2Str)
                {
                    // Check if types are compatible (e.g., integer and number)
                    if (!AreTypesCompatible(type1Str, type2Str))
                    {
                        return false;
                    }
                }
            }

            // Check properties compatibility (for object types)
            if (root1.TryGetProperty("properties", out var props1) &&
                root2.TryGetProperty("properties", out var props2))
            {
                // Get all property names
                var props1Names = props1.EnumerateObject().Select(p => p.Name).ToHashSet();
                var props2Names = props2.EnumerateObject().Select(p => p.Name).ToHashSet();

                // Check for required properties conflicts
                if (root1.TryGetProperty("required", out var required1) &&
                    root2.TryGetProperty("required", out var required2))
                {
                    var req1 = required1.EnumerateArray().Select(e => e.GetString()).ToHashSet();
                    var req2 = required2.EnumerateArray().Select(e => e.GetString()).ToHashSet();

                    // If one schema requires a property that the other doesn't have, incompatible
                    if (req1.Any(r => r != null && !props2Names.Contains(r)) ||
                        req2.Any(r => r != null && !props1Names.Contains(r)))
                    {
                        return false;
                    }
                }
            }

            // Default to compatible if we can't determine incompatibility
            return true;
        }
        catch
        {
            // If we can't parse, assume incompatible
            return false;
        }
    }

    /// <summary>
    /// Determines if two JSON schema types are compatible.
    /// 
    /// Compatible pairs:
    /// - integer and number are compatible
    /// - Same types are compatible
    /// </summary>
    /// <param name="type1">The first type.</param>
    /// <param name="type2">The second type.</param>
    /// <returns>True if the types are compatible, false otherwise.</returns>
    private static bool AreTypesCompatible(string? type1, string? type2)
    {
        if (type1 == type2) return true;

        // Type compatibility matrix
        var compatibleTypes = new Dictionary<string, HashSet<string>>
        {
            ["integer"] = new HashSet<string> { "number" },
            ["number"] = new HashSet<string> { "integer" },
            ["string"] = new HashSet<string> { "string" },
            ["boolean"] = new HashSet<string> { "boolean" },
            ["array"] = new HashSet<string> { "array" },
            ["object"] = new HashSet<string> { "object" }
        };

        if (type1 != null && compatibleTypes.TryGetValue(type1, out var compat))
        {
            return compat.Contains(type2 ?? "");
        }

        return false;
    }

    /// <summary>
    /// Determines if two constraints are contradictory.
    /// 
    /// Checks for explicit opposite keywords (e.g., "fast" vs "slow", "high" vs "low").
    /// </summary>
    /// <param name="c1">The first constraint.</param>
    /// <param name="c2">The second constraint.</param>
    /// <returns>True if the constraints are contradictory, false otherwise.</returns>
    private static bool AreConstraintsContradictory(AgentConstraint c1, AgentConstraint c2)
    {
        // Check for explicit contradictions
        var opposites = new[]
        {
            ("fast", "slow"),
            ("high", "low"),
            ("maximum", "minimum"),
            ("must", "must not"),
            ("required", "forbidden"),
            ("enabled", "disabled")
        };

        var desc1 = c1.Description.ToLowerInvariant();
        var desc2 = c2.Description.ToLowerInvariant();

        foreach (var (word1, word2) in opposites)
        {
            if ((desc1.Contains(word1) && desc2.Contains(word2)) ||
                (desc1.Contains(word2) && desc2.Contains(word1)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Detects the design philosophy from an agent's goal using keyword matching.
    /// 
    /// Philosophy categories:
    /// - performance: fast, quick, efficient, optimize
    /// - quality: thorough, comprehensive, detailed, polish
    /// - simplicity: simple, minimal, clean, straightforward
    /// - complexity: complex, advanced, sophisticated, feature-rich
    /// </summary>
    /// <param name="goal">The agent's goal text to analyze.</param>
    /// <param name="philosophyKeywords">Dictionary mapping philosophy names to keyword arrays.</param>
    /// <returns>The detected philosophy name, or "neutral" if none detected.</returns>
    private static string DetectPhilosophy(string goal, Dictionary<string, string[]> philosophyKeywords)
    {
        foreach (var (philosophy, keywords) in philosophyKeywords)
        {
            if (keywords.Any(keyword => goal.Contains(keyword)))
            {
                return philosophy;
            }
        }

        return "neutral";
    }
}

