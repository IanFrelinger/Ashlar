using Microsoft.Extensions.AI;
using Microsoft.Extensions.VectorData;
using Ashlar.AI.Pipeline.Embeddings;
using Ashlar.AI.Pipeline.Governance;

namespace Ashlar.AI.Pipeline.Rag;

/// <summary>
/// Indexes and searches <see cref="ChunkRecord"/> via VectorData + governed embeddings.
/// </summary>
public sealed class VectorDataRagService
{
    public const string DefaultCollectionName = "ashlar-chunks";

    private readonly VectorStoreCollection<string, ChunkRecord> _collection;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddings;
    private readonly IChatInvocationAuditor _auditor;

    /// <summary>Creates a RAG façade over a VectorData collection.</summary>
    public VectorDataRagService(
        VectorStoreCollection<string, ChunkRecord> collection,
        IEmbeddingGenerator<string, Embedding<float>> embeddings,
        IChatInvocationAuditor auditor)
    {
        _collection = collection ?? throw new ArgumentNullException(nameof(collection));
        _embeddings = embeddings ?? throw new ArgumentNullException(nameof(embeddings));
        _auditor = auditor ?? throw new ArgumentNullException(nameof(auditor));
    }

    /// <summary>Indexes a chunk (embed + upsert).</summary>
    /// <remarks>
    /// <para><b>An omitted tier is unlabelled, not Public.</b> It is stored as the empty string
    /// and <see cref="TrustTierOrder.RecordRank"/> ranks it at the top, so it is served only to
    /// the top clearance. The parameter used to default to "Public", which published every chunk
    /// whose caller forgot to classify it.</para>
    /// <para><b>Re-indexing never lowers a label.</b> Upserting an existing key at a LOWER tier than
    /// the one it is stored at is refused (and audited as <c>rag:index</c> / <c>denied</c>): the
    /// knowledge-base indexer keys chunks on the full file path, so re-running an index over the
    /// same files with a lower label used to downgrade previously Secret content silently.
    /// Lowering a label is a separate, explicit act: <see cref="RemoveAsync"/> first. The same or
    /// a higher tier is accepted. The read-then-upsert is not atomic across concurrent writers to
    /// one key; it guards the sequential re-index, which is the path that downgraded.</para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">The key is stored at a higher tier.</exception>
    public async Task IndexAsync(
        string key,
        string text,
        string? sourceUri = null,
        string? trustTier = null,
        CancellationToken cancellationToken = default)
    {
        var tier = TrustTierOrder.NormalizeRecordTier(trustTier);

        var existing = await _collection.GetAsync(key, options: null, cancellationToken).ConfigureAwait(false);
        if (existing is not null
            && TrustTierOrder.RecordRank(tier) < TrustTierOrder.RecordRank(existing.TrustTier))
        {
            var stored = Describe(existing.TrustTier);
            var requested = Describe(tier);
            _auditor.Record(new ChatInvocationAuditRecord
            {
                TargetKey = "rag:index",
                Outcome = "denied",
                PolicyDecisions = new[]
                {
                    "event=downgrade_refused",
                    $"stored_tier={stored}",
                    $"requested_tier={requested}",
                },
            });
            throw new InvalidOperationException(
                $"Refusing to re-index '{key}' at '{requested}': it is already stored at '{stored}', and "
                + "re-indexing must never lower a record's sensitivity. If the lower label is correct, "
                + "remove the record first and index it again -- lowering a label has to be a separate, "
                + "deliberate act.");
        }

        var embedding = await EmbedAsync(text, cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        await _collection.UpsertAsync(new ChunkRecord
        {
            Key = key,
            Text = text,
            SourceUri = sourceUri ?? string.Empty,
            TrustTier = tier,
            CreatedAt = now,
            UpdatedAt = now,
            Embedding = embedding,
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Searches for similar chunks, excluding records above <paramref name="callerMaxTrustTier"/>.
    /// </summary>
    /// <remarks>
    /// An omitted or unrecognised <paramref name="callerMaxTrustTier"/> is the FLOOR
    /// (<see cref="TrustTierOrder.Floor"/>), never everything. The <c>rag:search</c> audit record
    /// carries the clearance actually applied (<c>caller_tier=</c>) and why
    /// (<c>caller_tier_basis=</c> explicit | omitted | unrecognised), so a floored search can be
    /// told apart from one the caller asked for.
    /// </remarks>
    public async Task<IReadOnlyList<VectorSearchResult<ChunkRecord>>> SearchAsync(
        string query,
        string? callerMaxTrustTier,
        int top = 5,
        double? minScore = null,
        CancellationToken cancellationToken = default)
    {
        var embedding = await EmbedAsync(query, cancellationToken).ConfigureAwait(false);
        var (appliedTier, basis) = TrustTierOrder.ResolveCaller(callerMaxTrustTier);
        var maxRank = TrustTierOrder.CallerRank(appliedTier);
        var options = new VectorSearchOptions<ChunkRecord>
        {
            Filter = r => TrustTierOrder.RecordRank(r.TrustTier) <= maxRank,
            ScoreThreshold = minScore,
        };

        var results = new List<VectorSearchResult<ChunkRecord>>();
        await foreach (var hit in _collection.SearchAsync(embedding, top, options, cancellationToken)
            .ConfigureAwait(false))
        {
            results.Add(hit);
        }

        _auditor.Record(new ChatInvocationAuditRecord
        {
            TargetKey = "rag:search",
            Outcome = "success",
            PolicyDecisions = new[]
            {
                "event=retrieval",
                $"results={results.Count}",
                $"caller_tier={appliedTier}",
                $"caller_tier_basis={basis}",
            },
        });

        return results;
    }

    /// <summary>Re-indexes a batch of existing chunks into the VectorData store.</summary>
    public async Task<int> ReindexAsync(
        IEnumerable<(string Key, string Text, string? SourceUri, string TrustTier)> chunks,
        CancellationToken cancellationToken = default)
    {
        var count = 0;
        foreach (var chunk in chunks)
        {
            await IndexAsync(chunk.Key, chunk.Text, chunk.SourceUri, chunk.TrustTier, cancellationToken)
                .ConfigureAwait(false);
            count++;
        }

        _auditor.Record(new ChatInvocationAuditRecord
        {
            TargetKey = "rag:reindex",
            Outcome = "success",
            PolicyDecisions = new[] { "event=reindex", $"count={count}" },
        });
        return count;
    }

    /// <summary>Removes a chunk by key.</summary>
    public Task RemoveAsync(string key, CancellationToken cancellationToken = default) =>
        _collection.DeleteAsync(key, cancellationToken);

    /// <summary>Clears the default collection.</summary>
    public Task ClearAsync(CancellationToken cancellationToken = default) =>
        _collection.EnsureCollectionDeletedAsync(cancellationToken);

    /// <summary>Returns the number of indexed chunks when the store is in-process.</summary>
    public Task<int> GetDocumentCountAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_collection is InProcessChunkCollection inProcess)
        {
            return Task.FromResult(inProcess.Count);
        }

        return Task.FromResult(-1);
    }

    private static string Describe(string? tier) => string.IsNullOrWhiteSpace(tier) ? "(unlabelled)" : tier;

    private async Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken)
    {
        var generated = await _embeddings.GenerateAsync([text], cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return generated[0].Vector.ToArray();
    }
}
