using System.Text.Json;
using Ashlar.Abstractions;
using Ashlar.Abstractions.Security;
using Ashlar.Abstractions.Security.Egress;
using Ashlar.BackgroundAgents.DataSensitivity;

namespace Ashlar.BackgroundAgents.RAG;

/// <summary>
/// ITool that exposes RAG search to agents. Id: "rag_search".
/// </summary>
/// <remarks>
/// <b>The model cannot raise its own clearance.</b> The clearance searched at is the AGENT's
/// (<c>maxDataSensitivity</c> in the snapshot). The model's <c>maxSensitivityLevelName</c> argument
/// may only NARROW it; a request above the agent's clearance is ignored, and with no agent
/// clearance in the snapshot the search runs at the floor whatever the model asks for. It used to
/// be the other way round -- the model's value won and the agent's was only a fallback -- so a
/// prompt injection that talked the model into asking for TopSecret got TopSecret.
/// </remarks>
public sealed class RAGTool : ITool, IEgressLabelledTool
{
    /// <summary>
    /// Default tool id.
    /// </summary>
    public const string DefaultId = "rag_search";

    private static readonly ToolSchema SchemaInstance = new(
        DefaultId,
        "Search the RAG knowledge base for documents similar to the query. Returns matching text chunks with scores.",
        """{"type":"object","properties":{"query":{"type":"string","description":"Search query text"},"maxResults":{"type":"integer","description":"Max results (default 5)"},"minScore":{"type":"number","description":"Min similarity 0-1 (default 0.7)"},"maxSensitivityLevelName":{"type":"string","description":"Optionally narrow the search below your clearance; it cannot raise it"}},"required":["query"]}""");

    private readonly IRAGService _ragService;
    private readonly int _defaultMaxResults;
    private readonly double _defaultMinScore;
    private readonly IDataSensitivityRegistry _sensitivityRegistry;

    /// <inheritdoc />
    public string Id => DefaultId;

    /// <inheritdoc />
    public ToolSchema Schema => SchemaInstance;

    /// <summary>
    /// Initializes a new instance of the <see cref="RAGTool"/> class.
    /// </summary>
    /// <param name="ragService">RAG service.</param>
    /// <param name="defaultMaxResults">Default max results when not specified (default 5).</param>
    /// <param name="defaultMinScore">Default min score when not specified (default 0.7).</param>
    /// <param name="sensitivityRegistry">Registry used to compare the agent's clearance with the one
    /// the model asks for; the five primitive levels when null.</param>
    public RAGTool(
        IRAGService ragService,
        int defaultMaxResults = 5,
        double defaultMinScore = 0.7,
        IDataSensitivityRegistry? sensitivityRegistry = null)
    {
        _ragService = ragService ?? throw new ArgumentNullException(nameof(ragService));
        _defaultMaxResults = defaultMaxResults;
        _defaultMinScore = defaultMinScore;
        _sensitivityRegistry = sensitivityRegistry ?? new DataSensitivityRegistry();
    }

    /// <inheritdoc />
    public async Task<ToolResult> InvokeAsync(ToolCall toolCall, WorldSnapshot s, CancellationToken ct)
    {
        var args = ParseArgs(toolCall);
        var query = args.Query ?? string.Empty;
        var maxResults = args.MaxResults ?? _defaultMaxResults;
        var minScore = args.MinScore ?? _defaultMinScore;
        var maxSensitivity = EffectiveClearance(s, args.MaxSensitivityLevelName);

        var tick = s.Tick;

        IReadOnlyList<VectorSearchResult> results;
        try
        {
            results = await _ragService.SearchAsync(query, maxResults, minScore, maxSensitivity, ct).ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            // The stores now REFUSE a query whose embedding has zero magnitude instead of
            // scoring it as 0.0 against everything -- see VectorMath.UnrankableQuery. This tool
            // is the caller that had to change with them: the query text arrives in the model's
            // own JSON arguments and is never validated (a punctuation-only query reaches here
            // intact), minScore likewise (a model-supplied 0 overrides the safe default of 0.7),
            // and InvokeAsync had no catch at all, so the refusal would have surfaced as an
            // unhandled exception in the agent loop.
            //
            // Turning it into a failed ToolResult keeps the loop running and, more importantly,
            // keeps the audit line honest. The old behaviour logged
            // `RAG search: query='', results=5` -- five arbitrary documents entering the agent's
            // context, reported in exactly the same shape as a successful retrieval. An empty
            // result list with the reason attached is the one outcome a reader can tell apart
            // from both a real hit and a real miss.
            // The payload carries the refusal rather than an empty list for the same reason the
            // store throws rather than returning one: an empty list reads to the model as "the
            // knowledge base has nothing on this", which is a different and unearned claim.
            var refusal = new[] { $"RAG search REFUSED: query='{query}' cannot be ranked. {ex.Message}" };
            return new ToolResult(
                new ActionDelta(tick, tick + 1, refusal),
                new RagRefusal(Refused: true, Reason: ex.Message)
                {
                    ReadNothing = VectorMath.IsUnrankableQuery(ex),
                });
        }

        var log = new[] { $"RAG search: query='{query}', results={results.Count}" };
        var delta = new ActionDelta(tick, tick + 1, log);
        var payload = results.Select(r => new RagHit(r.Id, r.Text, r.Score, r.SensitivityLevelName)).ToList();
        return new ToolResult(delta, payload);
    }

    /// <summary>
    /// Reports the label of every hit, or <see cref="SecurityLabel.Public"/> when the search read nothing
    /// (no hit, or the unrankable-query refusal). Canonical names are the five primitive levels plus
    /// <c>top-secret</c>, any case, trimmed. Anything else is <see cref="SecurityLabel.SystemHigh"/>.
    /// </summary>
    public void ReportRead(ReadReporter read, ToolResult result)
    {
        ArgumentNullException.ThrowIfNull(read);
        switch (result?.Payload)
        {
            case RagRefusal { ReadNothing: true }:
                read.Report(SecurityLabel.Public); // read nothing: refused
                return;
            case RagRefusal:
                // A store may have read data before throwing, and its exception message is in the result.
                // Only the stores' typed, pre-read unrankable-query refusal proves that nothing was read.
                return;
            case IReadOnlyList<RagHit> hits when hits.Count == 0:
                read.Report(SecurityLabel.Public); // read nothing: no hit
                return;
            case IReadOnlyList<RagHit> hits:
                foreach (var hit in hits)
                    read.Report(MapHitLabel(hit.SensitivityLevelName, _sensitivityRegistry));
                return;
            default:
                read.Report(SecurityLabel.SystemHigh);
                return;
        }
    }

    /// <summary>
    /// The data label of one hit name: trim, <see cref="IDataSensitivityRegistry.GetByName"/>, and
    /// <see cref="DataSensitivityLabelBridge.ToDataLabel"/> only when the level is one of
    /// <see cref="DataSensitivityLevels.All"/>. Anything else, a custom level included, is
    /// <see cref="SecurityLabel.SystemHigh"/>.
    /// </summary>
    internal static SecurityLabel MapHitLabel(string? name, IDataSensitivityRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        if (string.IsNullOrWhiteSpace(name))
            return SecurityLabel.SystemHigh;

        var trimmed = name.Trim();
        if (!CanonicalSpellings.Any(canonical => string.Equals(canonical, trimmed, StringComparison.OrdinalIgnoreCase)))
            return SecurityLabel.SystemHigh;

        var level = registry.GetByName(trimmed);
        if (level is null)
            return SecurityLabel.SystemHigh;

        foreach (var primitive in DataSensitivityLevels.All)
        {
            if (ReferenceEquals(level, primitive))
                return level.ToDataLabel();
        }

        return SecurityLabel.SystemHigh;
    }

    // Match TrustTierOrder's spelling comparison before consulting a registry, whose aliases or
    // Unicode folding must not label data below the pipeline's treatment of the same stored tier.
    private static readonly string[] CanonicalSpellings =
        ["Public", "Internal", "Confidential", "Secret", "TopSecret", "top-secret"];

    /// <summary>
    /// The agent's clearance, narrowed (never widened) by what the model asked for. Null -- the
    /// floor, downstream -- when the snapshot carries no agent clearance.
    /// </summary>
    private string? EffectiveClearance(WorldSnapshot s, string? requested)
    {
        if (!s.Data.TryGetValue("maxDataSensitivity", out var levelObj)
            || levelObj is not string agentClearance
            || string.IsNullOrWhiteSpace(agentClearance))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(requested) || _sensitivityRegistry.GetByName(requested) is not { } requestedLevel)
            return agentClearance;

        return requestedLevel.SensitivityValue < _sensitivityRegistry.ResolveClearance(agentClearance).SensitivityValue
            ? requestedLevel.Value
            : agentClearance;
    }

    private static RAGSearchArgs ParseArgs(ToolCall call)
    {
        try
        {
            var json = call.Arguments.GetRawText();
            return JsonSerializer.Deserialize<RAGSearchArgs>(json) ?? new RAGSearchArgs();
        }
        catch
        {
            return new RAGSearchArgs();
        }
    }

    private sealed record RagHit(string Id, string Text, double Score, string? SensitivityLevelName);

    private sealed record RagRefusal(bool Refused, string Reason)
    {
        [System.Text.Json.Serialization.JsonIgnore]
        internal bool ReadNothing { get; init; }
    }

    private sealed class RAGSearchArgs
    {
        [System.Text.Json.Serialization.JsonPropertyName("query")]
        public string? Query { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("maxResults")]
        public int? MaxResults { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("minScore")]
        public double? MinScore { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("maxSensitivityLevelName")]
        public string? MaxSensitivityLevelName { get; set; }
    }
}
