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
/// <para><b>It labels what it returns</b> (SPEC-007 PR 4.5). It declares itself labelled
/// (<see cref="ILabelledTool"/>): invoked through <see cref="InvokeLabelledAsync"/>, it reports each hit's tier through the
/// caller's <see cref="ReadReporter"/>, and "read nothing" (<see cref="SecurityLabel.Public"/>) for no hits and for its
/// unrankable-query refusal, whose payload holds only the model's own query and the store's message. A tier is the
/// label of one of the five canonical levels only when its trimmed name resolves through the registry to one of
/// <see cref="DataSensitivityLevels.All"/>, which also accepts any case and <c>top-secret</c>; anything else, a custom
/// level, a blank or no tier included, is <see cref="SecurityLabel.SystemHigh"/>. That is exactly what the RAG
/// pipeline's <c>TrustTierOrder.RecordLabel</c> gives, and a cert-gate parity test holds the two together. Labels carry
/// the level only (the owner's 2026-10-05 answer to Q8).</para>
/// </remarks>
public sealed class RAGTool : ILabelledTool
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
    public Task<ToolResult> InvokeAsync(ToolCall toolCall, WorldSnapshot s, CancellationToken ct) =>
        InvokeCoreAsync(toolCall, s, read: null, ct);

    /// <inheritdoc />
    public Task<ToolResult> InvokeLabelledAsync(ToolCall toolCall, WorldSnapshot s, ReadReporter report, CancellationToken ct) =>
        InvokeCoreAsync(toolCall, s, report ?? throw new ArgumentNullException(nameof(report)), ct);

    // The spellings the RAG pipeline's TrustTierOrder ranks (trimmed, ordinal, ignoring case): the five canonical names
    // and top-secret. Compared the same way here, so a tier the pipeline serves as unlabelled is never labelled below it.
    private static readonly string[] CanonicalSpellings = { "Public", "Internal", "Confidential", "Secret", "TopSecret", "top-secret" };

    /// <summary>
    /// The label of a hit whose stored tier is <paramref name="tier"/>: the bare label of a canonical level when the
    /// trimmed name is a spelling the RAG pipeline ranks and resolves through <paramref name="registry"/> to one of
    /// <see cref="DataSensitivityLevels.All"/>, otherwise <see cref="SecurityLabel.SystemHigh"/>.
    /// </summary>
    private static SecurityLabel HitLabel(IDataSensitivityRegistry registry, string? tier)
    {
        if (string.IsNullOrWhiteSpace(tier))
            return SecurityLabel.SystemHigh;

        // The registry does not trim (DataSensitivityLevels.FromName), and TrustTierOrder does: trim here, so " Secret "
        // is Secret on both sides.
        var trimmed = tier.Trim();

        // FromName folds with ToLowerInvariant and TrustTierOrder with OrdinalIgnoreCase, and the two differ on a few
        // letters (U+0130 lower-cases to i but does not upper-case to I), so a name only FromName accepts would be
        // labelled here while the pipeline serves it as unlabelled: accept only a spelling TrustTierOrder also ranks.
        if (!CanonicalSpellings.Any(name => string.Equals(name, trimmed, StringComparison.OrdinalIgnoreCase)))
            return SecurityLabel.SystemHigh;

        // A custom level the registry knows is not one of the five, so it is SystemHigh.
        var level = registry.GetByName(trimmed);
        return level is not null && DataSensitivityLevels.All.Any(canonical => ReferenceEquals(canonical, level))
            ? level.ToDataLabel()
            : SecurityLabel.SystemHigh;
    }

    private async Task<ToolResult> InvokeCoreAsync(ToolCall toolCall, WorldSnapshot s, ReadReporter? read, CancellationToken ct)
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

            // Read nothing, but only for the stores' own unrankable-query refusal, which they throw before any record is
            // scored. Any other ArgumentException from a store may carry what it read, and its message is in the
            // payload, so it stays unreported and counts as SystemHigh (SPEC-007 PR 4.5).
            if (VectorMath.IsUnrankableQuery(ex))
                read?.Report(SecurityLabel.Public);
            return new ToolResult(
                new ActionDelta(tick, tick + 1, refusal),
                new { Refused = true, Reason = ex.Message });
        }

        // A label for everything the result carries: each hit's tier, or "read nothing" when there is none.
        if (read is not null)
        {
            read.Report(SecurityLabel.Public);
            foreach (var hit in results)
                read.Report(HitLabel(_sensitivityRegistry, hit.SensitivityLevelName));
        }

        var log = new[] { $"RAG search: query='{query}', results={results.Count}" };
        var delta = new ActionDelta(tick, tick + 1, log);
        var payload = results.Select(r => new { r.Id, r.Text, r.Score, r.SensitivityLevelName }).ToList();
        return new ToolResult(delta, payload);
    }

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
