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

    /// <summary>Save asynchronously.</summary>
    public Task SaveAsync(PipelineRun run, CancellationToken cancellationToken = default)
    {
        LiteDbDocumentMapper.EnsureMapped<PipelineRunDocument>();
        LiteDbDocumentMapper.EnsureMapped<PipelineStageRunDocument>();
        cancellationToken.ThrowIfCancellationRequested();
        if (run == null) throw new ArgumentNullException(nameof(run));

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
            col.Upsert(ToDocument(run));
        }

        return Task.CompletedTask;
    }

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
