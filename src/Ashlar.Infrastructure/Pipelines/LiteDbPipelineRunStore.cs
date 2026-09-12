using LiteDB;
using Ashlar.Core.Application.Pipelines.Models;
using Ashlar.Core.Application.Pipelines.Ports;
using Ashlar.Core.Application.Persistence;
using Ashlar.Infrastructure.Persistence;

namespace Ashlar.Infrastructure.Pipelines;

/// <summary>
/// Durable LiteDB-backed pipeline run store.
/// </summary>
public sealed class LiteDbPipelineRunStore : IPipelineRunStore
{
    private const string CollectionName = "pipeline_runs";
    private readonly string _connectionString;
    private readonly object _gate = new();

    /// <summary>Initializes a new lite db pipeline run store.</summary>
    public LiteDbPipelineRunStore(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
            throw new ArgumentException("Database path is required.", nameof(databasePath));
        // This store handed the bare path straight to LiteDB, so it never carried a Connection=
        // option at all and took the Direct default. lock (_gate) keeps its own writes serial;
        // the named mutex is what covers a second instance and a second process.
        _connectionString = LiteDbConnectionString.ForSharedAccess(databasePath, nameof(databasePath));
        LiteDbDocumentMapper.EnsureMapped<PipelineRunDocument>();
        LiteDbDocumentMapper.EnsureMapped<PipelineStageRunDocument>();
    }

    /// <summary>Merge asynchronously.</summary>
    /// <param name="runId">The run to write; the document is addressed by this id.</param>
    /// <param name="merge">Applied to the document read inside the write transaction.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The run this store holds after the write.</returns>
    /// <remarks>
    /// Shared mode serializes individual engine operations, so the read and upsert must be
    /// inside the same transaction across store instances and processes. Creation and advance
    /// keep the existing document shape; previously persisted runs remain valid resume sources.
    /// </remarks>
    public Task<PipelineRun> MergeAsync(
        string runId,
        Func<PipelineRun?, PipelineRun> merge,
        CancellationToken cancellationToken = default)
    {
        LiteDbDocumentMapper.EnsureMapped<PipelineRunDocument>();
        LiteDbDocumentMapper.EnsureMapped<PipelineStageRunDocument>();
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(runId))
            throw new ArgumentException("Run id is required.", nameof(runId));
        if (merge == null) throw new ArgumentNullException(nameof(merge));

        lock (_gate)
        {
            using var db = new LiteDatabase(_connectionString);
            var col = db.GetCollection<PipelineRunDocument>(CollectionName);
            // No EnsureIndex here, and deliberately not the by-name form the other stores moved to.
            // RunId carries [BsonId], so LiteDB resolved `x => x.RunId` to $._id and the call landed
            // on the unique _id index the collection already maintains -- it created nothing, while
            // still driving the BsonMapper expression visitor that is not thread-safe. Declaring
            // `nameof(PipelineRunDocument.RunId)` instead would NOT be the same call: it would build a
            // second index over $.RunId, a path no stored document has, with unique:false -- silently
            // dropping the uniqueness this line was asking for. _id enforces it, as it always did.
            var merged = LiteDbAtomic.Mutate(db, () =>
            {
                var existing = col.FindById(runId);
                var next = merge(existing == null ? null : FromDocument(existing));
                if (next == null)
                    throw new InvalidOperationException(NullMergeResult(runId));

                // The merge may not move the document: the id it was read under is the id it is
                // written back under, and a changed RunId would leave two documents where the
                // caller expected one.
                if (!string.Equals(next.RunId, runId, StringComparison.Ordinal))
                    throw new InvalidOperationException(MovedRun(runId, next.RunId));

                col.Upsert(ToDocument(next));
                // LiteDB normalizes timestamps and strings when serializing. Return the actual
                // persisted identity so the creator's next advance compares the same values.
                return FromDocument(col.FindById(runId)
                    ?? throw new InvalidOperationException($"Pipeline run '{runId}' was not persisted."));
            });

            return Task.FromResult(merged);
        }
    }

    /// <summary>Why a merge that returns null is refused rather than treated as "leave it alone".</summary>
    /// <param name="runId">The run being written.</param>
    /// <returns>The message.</returns>
    /// <remarks>
    /// This port has no decline outcome on purpose — the merge is the decision, and a caller that
    /// wants the stored document left as it is returns the stored document. Reading null as a
    /// silent no-op would make a mistyped merge look like a successful write.
    /// </remarks>
    private static string NullMergeResult(string runId)
        => $"a pipeline run merge returned null for run '{runId}'. This port has no decline "
           + "outcome: return the document the merge was handed to leave the stored run unchanged.";

    /// <summary>Why a merge may not change the run id.</summary>
    /// <param name="runId">The id the read used.</param>
    /// <param name="returned">The id the merge returned.</param>
    /// <returns>The message.</returns>
    private static string MovedRun(string runId, string returned)
        => $"a pipeline run merge may not change RunId ('{runId}' -> '{returned}'). The write is "
           + "addressed by the id the read used; changing it would leave two documents where the "
           + "caller expected one.";

    /// <summary>Get asynchronously.</summary>
    public Task<PipelineRun?> GetAsync(string runId, CancellationToken cancellationToken = default)
    {
        LiteDbDocumentMapper.EnsureMapped<PipelineRunDocument>();
        LiteDbDocumentMapper.EnsureMapped<PipelineStageRunDocument>();
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(runId))
            throw new ArgumentException("Run id is required.", nameof(runId));

        PipelineRun? run = null;
        lock (_gate)
        {
            using var db = new LiteDatabase(_connectionString);
            var col = db.GetCollection<PipelineRunDocument>(CollectionName);
            var doc = col.FindById(runId);
            if (doc != null)
                run = FromDocument(doc);
        }

        return Task.FromResult(run);
    }

    private static PipelineRunDocument ToDocument(PipelineRun run)
    {
        return new PipelineRunDocument
        {
            RunId = run.RunId,
            TemplateId = run.TemplateId,
            State = run.State.ToString(),
            StartedAt = run.StartedAt,
            CompletedAt = run.CompletedAt,
            StageRuns = run.StageRuns.Select(stage => new PipelineStageRunDocument
            {
                StageId = stage.StageId,
                State = stage.State.ToString(),
                Attempt = stage.Attempt,
                WorkerId = stage.WorkerId,
                WorkerType = stage.WorkerType?.ToString(),
                Output = stage.Output,
                Error = stage.Error
            }).ToList()
        };
    }

    private static PipelineRun FromDocument(PipelineRunDocument doc)
    {
        return new PipelineRun
        {
            RunId = doc.RunId,
            TemplateId = doc.TemplateId,
            State = ParseEnum<PipelineRunState>(doc.State, PipelineRunState.Pending),
            StartedAt = doc.StartedAt,
            CompletedAt = doc.CompletedAt,
            StageRuns = doc.StageRuns.Select(stage => new PipelineStageRun
            {
                StageId = stage.StageId,
                State = ParseEnum<PipelineStageRunState>(stage.State, PipelineStageRunState.Pending),
                Attempt = stage.Attempt,
                WorkerId = stage.WorkerId,
                WorkerType = string.IsNullOrWhiteSpace(stage.WorkerType)
                    ? null
                    : ParseEnum<PipelineWorkerType>(stage.WorkerType, PipelineWorkerType.Deterministic),
                Output = stage.Output,
                Error = stage.Error
            }).ToArray()
        };
    }

    private static TEnum ParseEnum<TEnum>(string? value, TEnum fallback) where TEnum : struct
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;

        return Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed)
            ? parsed
            : fallback;
    }

    private sealed class PipelineRunDocument
    {
        /// <summary>Run id.</summary>
        [BsonId]
        public string RunId { get; set; } = string.Empty;
        /// <summary>Template id.</summary>
        public string TemplateId { get; set; } = string.Empty;
        /// <summary>State.</summary>
        public string State { get; set; } = string.Empty;
        /// <summary>Started at.</summary>
        public DateTimeOffset StartedAt { get; set; }
        /// <summary>Completed at.</summary>
        public DateTimeOffset? CompletedAt { get; set; }
        /// <summary>Stage runs.</summary>
        public List<PipelineStageRunDocument> StageRuns { get; set; } = new();
    }

    private sealed class PipelineStageRunDocument
    {
        /// <summary>Stage id.</summary>
        public string StageId { get; set; } = string.Empty;
        /// <summary>State.</summary>
        public string State { get; set; } = string.Empty;
        /// <summary>Attempt.</summary>
        public int Attempt { get; set; }
        /// <summary>Worker id.</summary>
        public string? WorkerId { get; set; }
        /// <summary>Worker type.</summary>
        public string? WorkerType { get; set; }
        /// <summary>Output.</summary>
        public string? Output { get; set; }
        /// <summary>Error.</summary>
        public string? Error { get; set; }
    }
}
