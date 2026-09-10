using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using Ashlar.BackgroundAgents.DataSensitivity;

namespace Ashlar.BackgroundAgents.RAG;

/// <summary>
/// SQLite-backed vector store. Persists embeddings to a SQLite database; search loads and scores in memory.
/// </summary>
public sealed class SqliteVectorStore : IVectorStore, IAsyncDisposable
{
    private readonly string _connectionString;
    private readonly IDataSensitivityRegistry? _sensitivityRegistry;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;

    /// <summary>
    /// Initializes a new instance of the <see cref="SqliteVectorStore"/> class.
    /// </summary>
    /// <param name="connectionStringOrPath">SQLite connection string or file path (e.g. "Data Source=rag.db").</param>
    /// <param name="sensitivityRegistry">Optional registry for sensitivity-level filtering in search.</param>
    public SqliteVectorStore(string connectionStringOrPath, IDataSensitivityRegistry? sensitivityRegistry = null)
    {
        if (string.IsNullOrWhiteSpace(connectionStringOrPath))
            throw new ArgumentNullException(nameof(connectionStringOrPath));
        _connectionString = connectionStringOrPath.Trim().StartsWith("Data Source=", StringComparison.OrdinalIgnoreCase)
            ? connectionStringOrPath
            : $"Data Source={connectionStringOrPath}";
        _sensitivityRegistry = sensitivityRegistry;
    }

    /// <inheritdoc />
    public async Task IndexAsync(
        string id,
        string text,
        float[] embedding,
        string? sensitivityLevelName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(id))
            throw new ArgumentNullException(nameof(id));
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        var blob = FloatArrayToBlob(embedding);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO rag_vectors (id, text, embedding, sensitivity_level_name)
            VALUES ($id, $text, $embedding, $sensitivity_level_name)
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$text", text ?? string.Empty);
        cmd.Parameters.AddWithValue("$embedding", blob);
        cmd.Parameters.AddWithValue("$sensitivity_level_name", (object?)sensitivityLevelName ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<VectorSearchResult>> SearchAsync(
        float[] embedding,
        int maxResults,
        double minScore,
        string? maxSensitivityLevelName,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        IDataSensitivityLevel? maxLevel = null;
        if (!string.IsNullOrWhiteSpace(maxSensitivityLevelName) && _sensitivityRegistry != null)
            maxLevel = _sensitivityRegistry.GetByName(maxSensitivityLevelName);

        var results = new List<VectorSearchResult>();

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, text, embedding, sensitivity_level_name FROM rag_vectors";
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var docId = reader.GetString(0);
            var text = reader.GetString(1);
            var blob = (byte[]?)reader.GetValue(2);
            var sensitivityLevelName = reader.IsDBNull(3) ? null : reader.GetString(3);

            if (maxLevel != null && !string.IsNullOrEmpty(sensitivityLevelName))
            {
                var docLevel = _sensitivityRegistry!.GetByName(sensitivityLevelName);
                if (docLevel == null || !_sensitivityRegistry.CanAccess(maxLevel, docLevel))
                    continue;
            }

            if (blob == null || blob.Length == 0)
                continue;
            // Skip rows of a different dimension rather than scoring them. CosineSimilarity returns
            // 0 for a length mismatch, and the filter below admits anything at or above minScore —
            // so with the common minScore of 0.0 a stale row of the wrong dimension would come back
            // as a score-0.0 hit instead of being ignored.
            if (blob.Length != embedding.Length * sizeof(float))
                continue;
            var docEmbedding = BlobToFloatArray(blob);
            var score = VectorMath.CosineSimilarity(embedding.AsSpan(), docEmbedding.AsSpan());
            if (score >= minScore)
                results.Add(new VectorSearchResult(docId, text, score, sensitivityLevelName));
        }

        return results.OrderByDescending(r => r.Score).Take(maxResults).ToList();
    }

    /// <inheritdoc />
    public async Task RemoveAsync(string id, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM rag_vectors WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM rag_vectors";
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> GetDocumentCountAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM rag_vectors";
        var count = Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        return count;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _initLock.WaitAsync().ConfigureAwait(false);
        try
        {
            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
            return;
        await _initLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
                return;
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS rag_vectors (
                    id TEXT PRIMARY KEY,
                    text TEXT NOT NULL,
                    embedding BLOB NOT NULL,
                    sensitivity_level_name TEXT
                )
                """;
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    private static byte[] FloatArrayToBlob(float[] a)
    {
        var bytes = new byte[a.Length * sizeof(float)];
        MemoryMarshal.Cast<float, byte>(a.AsSpan()).CopyTo(bytes);
        return bytes;
    }

    private static float[] BlobToFloatArray(byte[] blob)
    {
        var count = blob.Length / sizeof(float);
        var a = new float[count];
        MemoryMarshal.Cast<byte, float>(blob.AsSpan()).CopyTo(a);
        return a;
    }
}
