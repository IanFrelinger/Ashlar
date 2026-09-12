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
    /// 0 while live, 1 once disposed. An <see cref="Interlocked"/> flag rather than a bool under
    /// <see cref="_initLock"/>, because the second <c>DisposeAsync</c> must decide it has nothing
    /// to do WITHOUT touching the semaphore the first one disposed.
    /// </summary>
    private int _disposed;

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
        ObjectDisposedException.ThrowIf(IsDisposed, this);

        // Refuse an unrankable query before any I/O -- before the schema check, before the
        // connection, before the read. A zero-magnitude query has no angle to any row, so it
        // scores 0.0 against all of them and, at the common minScore of 0.0, returns the first
        // maxResults rows in the table as "hits". VectorMath.UnrankableQuery says why this
        // refuses rather than returning an empty list; VectorMath.TryCosineSimilarity says why
        // the test is on the accumulated norm and not on the components.
        if (!VectorMath.IsRankable(embedding))
            throw VectorMath.UnrankableQuery(nameof(embedding));

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

            var docEmbedding = BlobToFloatArray(blob);

            // Skip a row that cannot be ranked against this query rather than scoring it -- a
            // different dimension, or a zero-magnitude embedding. Both used to arrive as the
            // value 0.0 out of CosineSimilarity, and the filter below admits anything at or above
            // minScore, so at the common minScore of 0.0 they came back as score-0.0 hits instead
            // of being ignored. Skip, not refuse, in this direction: one unrankable row must not
            // take a good query down with it. The QUERY above is refused instead, and
            // VectorMath.UnrankableQuery says why the two directions differ.
            //
            // #582's dimension check used to sit above this as a blob-length compare, which also
            // skipped the BlobToFloatArray allocation. It was removed when TryCosineSimilarity
            // became the single authority on comparability -- deliberately, because with both in
            // place SearchAsync_RowOfDifferentDimension_IsSkippedNotReturnedAtScoreZero passed
            // with either one reverted and so had no teeth against either. Measured: with the
            // blob compare present, reverting TryCosineSimilarity's length clause left that test
            // green. An allocation for a stale row is the price of a guard that can be tested.
            if (!VectorMath.TryCosineSimilarity(embedding.AsSpan(), docEmbedding.AsSpan(), out var score))
                continue;

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

    /// <summary>
    /// Releases what this store owns: the pooled SQLite connection for its connection string, and
    /// the initialization lock.
    /// </summary>
    /// <remarks>
    /// <para><b>What this used to do, and what changes for callers.</b> It took the lock, set
    /// <c>_initialized = true</c>, and released the lock. It disposed nothing, and two things
    /// followed.</para>
    /// <para>First, disposal was unobservable: a store kept answering <see cref="SearchAsync"/>
    /// after <c>await using</c> had ended. It now throws <see cref="ObjectDisposedException"/>,
    /// which is a behaviour change for any caller that was (knowingly or not) using a disposed
    /// store.</para>
    /// <para>Second, <c>= true</c> was the one value that could break the instance.
    /// <c>_initialized</c> is the memo for schema creation, so setting it on the way out claimed
    /// <c>CREATE TABLE</c> had run when it had not. Measured: <c>new SqliteVectorStore(p)</c>,
    /// <c>DisposeAsync()</c>, then <c>IndexAsync(...)</c> died with
    /// <c>SqliteException: SQLite Error 1: 'no such table: rag_vectors'</c>, while the same call on
    /// a store that had not been disposed succeeded. That path now raises
    /// <see cref="ObjectDisposedException"/> — still an error, but one that names the actual
    /// mistake instead of blaming the schema.</para>
    /// <para><b>The resource is a pool entry, not a connection field.</b> This class holds no
    /// connection; every method opens and disposes its own. What outlived the store was the
    /// Microsoft.Data.Sqlite POOL entry for its connection string (pooling is on by default since
    /// 6.0) and, through it, an OS handle on the .db file. Measured by walking
    /// <c>/proc/self/fd</c> after <c>await using</c> had exited: 3 open descriptors on the database
    /// before <c>ClearPool</c>, 0 after. On Windows that retained handle also blocks deleting or
    /// moving the file. <c>ClearPool</c> is keyed by connection string, so this releases only this
    /// store's pool; <c>ClearAllPools</c> would reach into every other store in the process and
    /// does not belong in library code.</para>
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        // Claim the disposal before anything else. A second call returns here, without waiting
        // on a semaphore the first call has already disposed.
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        await _initLock.WaitAsync().ConfigureAwait(false);
        try
        {
            // Not "true". The schema memo is false because, after this call, nothing in this
            // instance has created a table -- and a disposed store must not be able to convince
            // itself otherwise.
            _initialized = false;

            using var pooled = new SqliteConnection(_connectionString);
            SqliteConnection.ClearPool(pooled);
        }
        finally
        {
            _initLock.Release();
        }

        // Outside the lock, and last: a caller racing a disposal can still be inside WaitAsync
        // here and will see ObjectDisposedException from the semaphore rather than a corrupt
        // read. The public-method guard narrows that window to an actual concurrent misuse.
        _initLock.Dispose();
    }

    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
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
