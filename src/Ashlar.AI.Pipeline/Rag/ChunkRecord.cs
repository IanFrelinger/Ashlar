using Microsoft.Extensions.VectorData;

namespace Ashlar.AI.Pipeline.Rag;

/// <summary>
/// VectorData record for indexed knowledge chunks.
/// </summary>
public sealed class ChunkRecord
{
    /// <summary>Unique chunk key.</summary>
    [VectorStoreKey]
    public string Key { get; set; } = string.Empty;

    /// <summary>Source document URI or path.</summary>
    [VectorStoreData(IsIndexed = true)]
    public string SourceUri { get; set; } = string.Empty;

    /// <summary>Chunk text.</summary>
    [VectorStoreData]
    public string Text { get; set; } = string.Empty;

    /// <summary>
    /// Trust / sensitivity tier tag (e.g. Public, Internal, Confidential, Secret, TopSecret).
    /// Must be ≤ caller tier to be returned. Defaults to the empty string -- UNLABELLED -- which
    /// <see cref="TrustTierOrder.RecordRank"/> ranks at the top, so a record built without a tier
    /// is served only to the top clearance. It used to default to "Public", which published it.
    /// </summary>
    [VectorStoreData(IsIndexed = true)]
    public string TrustTier { get; set; } = string.Empty;

    /// <summary>When the chunk was indexed (UTC).</summary>
    [VectorStoreData]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>When the chunk was last updated (UTC).</summary>
    [VectorStoreData]
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Dense embedding vector.</summary>
    [VectorStoreVector(EmbeddingDimensions)]
    public ReadOnlyMemory<float> Embedding { get; set; }

    /// <summary>Default embedding dimensionality (matches AshlarDefaults.EmbeddingDefaultDimension).</summary>
    public const int EmbeddingDimensions = 64;
}
