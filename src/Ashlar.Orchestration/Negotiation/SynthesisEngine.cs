using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Ashlar.Abstractions;
using Ashlar.Core.Application.Common.Ports;
using Ashlar.Orchestration.Coordination.Conflicts;
using Ashlar.Orchestration.Negotiation.Models;

namespace Ashlar.Orchestration.Negotiation;

/// <summary>
/// Generates creative resolutions for philosophy conflicts using LLM.
/// 
/// Responsibilities:
/// - Synthesizes creative resolutions using LLM (IModel)
/// - Caches synthesis results for similar conflicts
/// - Generates proposed resolutions that satisfy all parties
/// - Provides fallback synthesis when LLM unavailable
/// 
/// Used by NegotiationProtocol to resolve philosophy conflicts between agents.
/// </summary>
public sealed class SynthesisEngine
{
    private readonly ILogger<SynthesisEngine> _logger;
    private readonly IModel? _model;
    private readonly ICacheStrategy? _cache;

    public SynthesisEngine(
        ILogger<SynthesisEngine> logger,
        IModel? model = null,
        ICacheStrategy? cache = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _model = model;
        _cache = cache;
    }

    /// <summary>
    /// Attempts to synthesize a creative resolution that satisfies all parties.
    /// </summary>
    public async Task<ProposedResolution?> SynthesizeAsync(
        IReadOnlyList<NegotiationPosition> positions,
        Conflict conflict,
        CancellationToken cancellationToken = default)
    {
        if (_model == null)
        {
            _logger.LogWarning("No model available for synthesis - using fallback");
            return TryFallbackSynthesis(positions, conflict);
        }

        // Check cache for similar resolved conflicts
        var cacheKey = GenerateCacheKey(positions, conflict);
        if (_cache != null)
        {
            var cached = await _cache.GetAsync<ProposedResolution>(cacheKey, cancellationToken);
            if (cached != null)
            {
                _logger.LogInformation("Found cached synthesis for conflict");
                return cached;
            }
        }

        // Build synthesis prompt
        var prompt = BuildSynthesisPrompt(positions, conflict);

        var modelInput = new ModelInput(new[]
        {
            ("system", GetSystemPrompt()),
            ("user", prompt)
        });

        try
        {
            var output = await _model.CompleteAsync(modelInput, cancellationToken);
            var resolution = ParseResolution(output.Text, positions);

            if (resolution != null && _cache != null)
            {
                await _cache.SetAsync(cacheKey, resolution, TimeSpan.FromHours(24), cancellationToken);
            }

            return resolution;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Synthesis failed");
            return TryFallbackSynthesis(positions, conflict);
        }
    }

    private string GetSystemPrompt()
    {
        return """
            You are an expert mediator and system architect specializing in resolving 
            design conflicts between autonomous agents. Your goal is to find creative 
            resolutions that satisfy the underlying goals of all parties, even if their 
            stated positions seem incompatible.

            When synthesizing a resolution:

            1. Focus on UNDERLYING GOALS, not stated positions
            2. Look for creative reframings that make the conflict disappear
            3. Consider phased approaches, hybrid solutions, or architectural patterns
            4. Ensure the resolution is concrete and actionable

            Respond with a JSON object containing:

            {
                "description": "Clear description of the synthesized resolution",
                "requiredChanges": { "agentId": "what this agent must change" },
                "reasoning": "Why this satisfies everyone's underlying goals",
                "confidence": 0.0-1.0
            }
            """;
    }

    private string BuildSynthesisPrompt(
        IReadOnlyList<NegotiationPosition> positions,
        Conflict conflict)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"## Conflict: {conflict.Description}");
        sb.AppendLine();

        sb.AppendLine("## Agent Positions:");

        foreach (var pos in positions)
        {
            sb.AppendLine($"### {pos.AgentId} ({pos.Domain})");
            sb.AppendLine($"Primary Goal: {pos.PrimaryGoal}");
            if (pos.UnderlyingGoals.Any())
            {
                sb.AppendLine($"Underlying Goals: {string.Join(", ", pos.UnderlyingGoals)}");
            }
            if (pos.HardConstraints.Any())
            {
                sb.AppendLine($"Hard Constraints: {string.Join(", ", pos.HardConstraints)}");
            }
            sb.AppendLine($"Flexibility: {pos.FlexibilityScore:P0}");
            sb.AppendLine();
        }

        sb.AppendLine("## Task");
        sb.AppendLine("Find a creative resolution that satisfies all agents' underlying goals.");

        return sb.ToString();
    }

    private ProposedResolution? ParseResolution(string output, IReadOnlyList<NegotiationPosition> positions)
    {
        try
        {
            // Strip markdown code blocks if present
            var json = output;
            if (json.Contains("```"))
            {
                var start = json.IndexOf('{');
                var end = json.LastIndexOf('}');
                if (start >= 0 && end > start)
                {
                    json = json.Substring(start, end - start + 1);
                }
            }

            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var requiredChanges = new Dictionary<string, string>();
            if (root.TryGetProperty("requiredChanges", out var changes))
            {
                foreach (var prop in changes.EnumerateObject())
                {
                    requiredChanges[prop.Name] = prop.Value.GetString() ?? "";
                }
            }

            return new ProposedResolution
            {
                ProposerId = "synthesis-engine",
                Description = root.GetProperty("description").GetString() ?? "",
                RequiredChanges = requiredChanges,
                Confidence = root.TryGetProperty("confidence", out var conf)
                    ? conf.GetDouble()
                    : 0.5
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse synthesis output");
            return null;
        }
    }

    private ProposedResolution? TryFallbackSynthesis(
        IReadOnlyList<NegotiationPosition> positions,
        Conflict conflict)
    {
        // Simple fallback: suggest phased approach
        if (positions.Count == 2)
        {
            return new ProposedResolution
            {
                ProposerId = "synthesis-fallback",
                Description = $"Phased approach: implement {positions[0].AgentId}'s design first, " +
                    $"then integrate {positions[1].AgentId}'s requirements in phase 2",
                RequiredChanges = positions.ToDictionary(
                    p => p.AgentId,
                    p => "Adapt design for phased integration"),
                Confidence = 0.4
            };
        }

        return null;
    }

    /// <summary>
    /// Derives the synthesis cache key.
    /// </summary>
    /// <remarks>
    /// <para><b>This uses a per-process hash, deliberately left in place. Here is the argument,
    /// and what would overturn it.</b></para>
    ///
    /// <para><c>string.GetHashCode()</c> is randomized per process — measured over five launches
    /// of the same binary on the same two positions: -251093190, -938106677, 155402064,
    /// -1452362662, 1110133770. The key reaches only <c>ICacheStrategy</c>, whose one
    /// implementation is <c>MemoryCacheStrategy</c>: a <c>ConcurrentDictionary</c> that is never
    /// serialized, never written to disk, never compared against a value from another process,
    /// and discarded at shutdown. Within a process the hash is consistent, so the cache works as
    /// intended; across a restart it is a guaranteed total miss. That costs a warm cache, not a
    /// wrong answer, and 32-bit collisions are tolerable here because a collision returns a
    /// resolution for a similar conflict rather than corrupting state.</para>
    ///
    /// <para><b>What would make it a defect.</b> Any <c>ICacheStrategy</c> backed by Redis, disk,
    /// or SQL. That substitution is available from outside this repository — the kernel registrar
    /// uses <c>AddSingleton</c>, not <c>TryAddSingleton</c>, so a consumer registering after
    /// <c>AddAshlar</c> wins — and the failure would be silent, because a cache that never hits
    /// is indistinguishable from a cold one. Note that the 24-hour TTL on the <c>SetAsync</c>
    /// above already assumes a durability the in-memory store cannot provide, so intent and
    /// implementation are not in agreement today.</para>
    ///
    /// <para><b>Why not simply fix it now.</b> Ashlar.Orchestration references only Core.Domain,
    /// Core.Application and Abstractions, so it can reach neither
    /// <c>Ashlar.Infrastructure.Caching.CacheKeyGenerator</c> nor
    /// <c>Ashlar.Certification.Contracts.BrickContentHasher</c>. A stable key here means either a
    /// new public helper in Core.Application beside <c>ICacheStrategy</c> — with
    /// <c>CacheKeyGenerator</c> delegating to it, so a second SHA-256 implementation is not
    /// introduced — or a third private copy. That is a layering decision worth taking
    /// deliberately rather than as a side effect of a hashing change. The sibling consumer of the
    /// same interface, <c>DecompositionRetriever</c>, already keys on stable content with a
    /// 30-day TTL: the two users of <c>ICacheStrategy</c> disagree about whether its keys must be
    /// process-independent, and settling that on the interface is the fix that would make this
    /// one correct by construction.</para>
    /// </remarks>
    private string GenerateCacheKey(IReadOnlyList<NegotiationPosition> positions, Conflict conflict)
    {
        var positionKeys = string.Join("|", positions.Select(p => $"{p.AgentId}:{p.PrimaryGoal}"));
        return $"synthesis:{conflict.ConflictType}:{positionKeys.GetHashCode()}";
    }
}

