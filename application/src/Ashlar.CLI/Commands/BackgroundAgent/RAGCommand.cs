using System.Text.Json;
using Microsoft.Extensions.Logging;
using Ashlar.BackgroundAgents.DataSensitivity;
using Ashlar.BackgroundAgents.RAG;

namespace Ashlar.CLI.Commands.BackgroundAgent;

/// <summary>
/// CLI command handler for RAG operations (index, search, stats, clear).
/// </summary>
/// <remarks>
/// <para><b>Both commands take the sensitivity decision out of the defaults.</b></para>
/// <para><c>rag index</c> REFUSES to run without <c>--sensitivity</c>. The alternative was to
/// default an omitted label to the top level, and that was rejected: a knowledge base indexed that
/// way is invisible to every search that does not pass the top clearance, so the omission would
/// surface later as "search finds nothing" with no pointer back to its cause. It used to default to
/// Public, which published every file. A refusal at index time names the missing decision at the
/// moment it is being made. (The library layer underneath still treats an omitted label as the
/// most restrictive level, for programmatic callers.)</para>
/// <para><c>rag search</c> without <c>--max-sensitivity</c> searches at the FLOOR (Public) and says
/// so; it used to search at TopSecret, returning the whole corpus. A <c>--max-sensitivity</c> that
/// names no known level is refused rather than floored, so a misspelt clearance cannot read as
/// "nothing matched".</para>
/// </remarks>
public class RAGCommand
{
    private readonly IRAGService _ragService;
    private readonly IKnowledgeBaseIndexer _indexer;
    private readonly ILogger<RAGCommand> _logger;
    private readonly IDataSensitivityRegistry _sensitivityRegistry;

    /// <summary>Creates a new RAGCommand instance.</summary>
    /// <param name="ragService">RAG service.</param>
    /// <param name="indexer">Knowledge base indexer.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="sensitivityRegistry">Registry used to recognise level names; the five primitive
    /// levels when null.</param>
    public RAGCommand(
        IRAGService ragService,
        IKnowledgeBaseIndexer indexer,
        ILogger<RAGCommand> logger,
        IDataSensitivityRegistry? sensitivityRegistry = null)
    {
        _ragService = ragService ?? throw new ArgumentNullException(nameof(ragService));
        _indexer = indexer ?? throw new ArgumentNullException(nameof(indexer));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _sensitivityRegistry = sensitivityRegistry ?? new DataSensitivityRegistry();
    }

    /// <summary>
    /// Index files or directories into the RAG store. <paramref name="sensitivity"/> is REQUIRED:
    /// without it nothing is indexed and the exit code is 1.
    /// </summary>
    public async Task<int> IndexAsync(IEnumerable<string> paths, string? sensitivity, bool formatJson, CancellationToken ct = default)
    {
        try
        {
            var pathList = paths?.Where(p => !string.IsNullOrWhiteSpace(p)).ToList() ?? new List<string>();
            if (pathList.Count == 0)
            {
                if (formatJson)
                    Console.Out.WriteLine(JsonSerializer.Serialize(new { ok = false, error = "At least one path required" }));
                else
                    Console.Error.WriteLine("At least one path required");
                return 1;
            }

            if (string.IsNullOrWhiteSpace(sensitivity))
            {
                var error = "--sensitivity is required: choose the level these documents are cleared at ("
                    + KnownLevels() + "). Nothing was indexed. Unlabelled documents are no longer "
                    + "stored as Public; the label has to be a deliberate choice.";
                if (formatJson)
                    Console.Out.WriteLine(JsonSerializer.Serialize(new { ok = false, error }));
                else
                    Console.Error.WriteLine(error);
                return 1;
            }

            if (_sensitivityRegistry.GetByName(sensitivity) is null)
            {
                return UnknownLevel("--sensitivity", sensitivity, formatJson);
            }
            var count = await _indexer.IndexDocumentsAsync(pathList, sensitivity, ct).ConfigureAwait(false);
            if (formatJson)
                Console.Out.WriteLine(JsonSerializer.Serialize(new { ok = true, paths = pathList, indexedCount = count }));
            else
                Console.Out.WriteLine($"Indexed {count} document(s) from {pathList.Count} path(s).");
            return 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RAG index failed");
            if (formatJson)
                Console.Out.WriteLine(JsonSerializer.Serialize(new { ok = false, error = ex.Message }));
            else
                Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    /// <summary>
    /// Search the RAG store. An omitted <paramref name="maxSensitivity"/> searches at the floor
    /// (Public); one that names no known level is refused with exit code 1.
    /// </summary>
    public async Task<int> SearchAsync(string query, int maxResults, double minScore, string? maxSensitivity, bool formatJson, CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                if (formatJson)
                    Console.Out.WriteLine(JsonSerializer.Serialize(new { ok = false, error = "Query required" }));
                else
                    Console.Error.WriteLine("Query required");
                return 1;
            }
            string clearance;
            if (string.IsNullOrWhiteSpace(maxSensitivity))
            {
                clearance = _sensitivityRegistry.Floor().Value;
            }
            else if (_sensitivityRegistry.GetByName(maxSensitivity) is { } level)
            {
                clearance = level.Value;
            }
            else
            {
                return UnknownLevel("--max-sensitivity", maxSensitivity, formatJson);
            }

            var results = await _ragService.SearchAsync(query, maxResults, minScore, maxSensitivity, ct).ConfigureAwait(false);
            if (formatJson)
            {
                var items = results.Select(r => new { r.Id, r.Text, r.Score, r.SensitivityLevelName }).ToList();
                var options = new JsonSerializerOptions { WriteIndented = true };
                Console.Out.WriteLine(JsonSerializer.Serialize(new { ok = true, query, clearance, results = items }, options));
            }
            else
            {
                Console.Out.WriteLine($"RAG Search: \"{query}\"");
                Console.Out.WriteLine(string.IsNullOrWhiteSpace(maxSensitivity)
                    ? $"  Clearance: {clearance} (the floor: --max-sensitivity not given)"
                    : $"  Clearance: {clearance}");
                foreach (var r in results)
                {
                    Console.Out.WriteLine($"  Score: {r.Score:F3} | {r.SensitivityLevelName ?? "-"} | {r.Id}");
                    Console.Out.WriteLine($"    {Truncate(r.Text, 120)}");
                }
            }
            return 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RAG search failed");
            if (formatJson)
                Console.Out.WriteLine(JsonSerializer.Serialize(new { ok = false, error = ex.Message }));
            else
                Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    /// <summary>
    /// Show RAG store statistics (document count).
    /// </summary>
    public async Task<int> StatsAsync(bool formatJson, CancellationToken ct = default)
    {
        try
        {
            var count = await _ragService.GetDocumentCountAsync(ct).ConfigureAwait(false);
            if (formatJson)
                Console.Out.WriteLine(JsonSerializer.Serialize(new { ok = true, documentCount = count }));
            else
                Console.Out.WriteLine($"RAG store document count: {count}");
            return 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RAG stats failed");
            if (formatJson)
                Console.Out.WriteLine(JsonSerializer.Serialize(new { ok = false, error = ex.Message }));
            else
                Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    /// <summary>
    /// Clear all documents from the RAG store.
    /// </summary>
    public async Task<int> ClearAsync(bool formatJson, CancellationToken ct = default)
    {
        try
        {
            await _ragService.ClearAsync(ct).ConfigureAwait(false);
            if (formatJson)
                Console.Out.WriteLine(JsonSerializer.Serialize(new { ok = true, action = "cleared" }));
            else
                Console.Out.WriteLine("RAG store cleared.");
            return 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RAG clear failed");
            if (formatJson)
                Console.Out.WriteLine(JsonSerializer.Serialize(new { ok = false, error = ex.Message }));
            else
                Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private string KnownLevels() =>
        string.Join(", ", _sensitivityRegistry.GetAll().Select(l => l.Value));

    private int UnknownLevel(string option, string value, bool formatJson)
    {
        var error = $"{option} '{value}' is not a known sensitivity level (known: {KnownLevels()}).";
        if (formatJson)
            Console.Out.WriteLine(JsonSerializer.Serialize(new { ok = false, error }));
        else
            Console.Error.WriteLine(error);
        return 1;
    }

    private static string Truncate(string text, int maxLen)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        text = text.Replace("\r", " ").Replace("\n", " ");
        return text.Length <= maxLen ? text : text[..maxLen] + "...";
    }
}
