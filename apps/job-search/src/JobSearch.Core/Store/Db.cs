using System.Globalization;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;

namespace JobSearch.Store;

/// <summary>
/// The system of record (SQLite, WAL). Every table that holds user data carries <c>user_id</c>, and every query in
/// this application filters on it. Each operation opens its own connection, so concurrent runs for different users
/// write side by side; <see cref="WriteAsync"/> takes the write lock up front (<c>BEGIN IMMEDIATE</c>) for the few
/// read-then-write sections that must not interleave.
/// </summary>
public sealed class Db
{
    private const string Schema = """
        CREATE TABLE IF NOT EXISTS users (
            id TEXT PRIMARY KEY, email TEXT NOT NULL UNIQUE, display_name TEXT NOT NULL,
            timezone TEXT NOT NULL, created_at TEXT NOT NULL);

        -- value_json NULL = unknown. source: resume | user. status: derived | confirmed | rejected.
        CREATE TABLE IF NOT EXISTS profile_fields (
            user_id TEXT NOT NULL REFERENCES users(id), field TEXT NOT NULL, value_json TEXT,
            source TEXT NOT NULL, status TEXT NOT NULL, evidence TEXT, updated_at TEXT NOT NULL,
            PRIMARY KEY (user_id, field));

        CREATE TABLE IF NOT EXISTS preferences (
            user_id TEXT PRIMARY KEY REFERENCES users(id), json TEXT NOT NULL, updated_at TEXT NOT NULL);

        CREATE TABLE IF NOT EXISTS resume_versions (
            id TEXT PRIMARY KEY, user_id TEXT NOT NULL REFERENCES users(id), sha256 TEXT NOT NULL, text TEXT NOT NULL,
            filename TEXT, drive_file_id TEXT, is_current INTEGER NOT NULL, created_at TEXT NOT NULL,
            UNIQUE (user_id, sha256));

        -- The secret itself is in the secret store under token_ref; never here.
        CREATE TABLE IF NOT EXISTS connections (
            user_id TEXT NOT NULL REFERENCES users(id), provider TEXT NOT NULL, account_email TEXT,
            token_ref TEXT NOT NULL, status TEXT NOT NULL, status_detail TEXT, updated_at TEXT NOT NULL,
            PRIMARY KEY (user_id, provider));

        CREATE TABLE IF NOT EXISTS access_grants (
            id TEXT PRIMARY KEY, user_id TEXT NOT NULL REFERENCES users(id), manager_id TEXT NOT NULL REFERENCES users(id),
            scopes_json TEXT NOT NULL, granted_at TEXT NOT NULL, expires_at TEXT, revoked_at TEXT);

        CREATE TABLE IF NOT EXISTS workspaces (
            user_id TEXT PRIMARY KEY REFERENCES users(id), root_id TEXT NOT NULL, folders_json TEXT NOT NULL,
            tracker_sheet_id TEXT NOT NULL, activity_sheet_id TEXT NOT NULL, created_at TEXT NOT NULL);

        CREATE TABLE IF NOT EXISTS jobs (
            id TEXT PRIMARY KEY, user_id TEXT NOT NULL REFERENCES users(id), dedupe_key TEXT NOT NULL,
            employer TEXT NOT NULL, employer_norm TEXT NOT NULL, title TEXT NOT NULL, title_norm TEXT NOT NULL,
            req_id TEXT, url TEXT, url_norm TEXT, source_ref TEXT NOT NULL, location TEXT, work_arrangement TEXT,
            pay_text TEXT, posted_date TEXT, deadline TEXT, description TEXT,
            listing_status TEXT NOT NULL, listing_status_detail TEXT,
            first_seen_at TEXT NOT NULL, last_checked_at TEXT, last_verified_open_at TEXT, archived_at TEXT,
            UNIQUE (user_id, dedupe_key));
        CREATE INDEX IF NOT EXISTS jobs_user_req ON jobs(user_id, employer_norm, req_id);
        CREATE INDEX IF NOT EXISTS jobs_user_url ON jobs(user_id, url_norm);
        CREATE INDEX IF NOT EXISTS jobs_user_ref ON jobs(user_id, source_ref);

        CREATE TABLE IF NOT EXISTS job_checks (
            id INTEGER PRIMARY KEY AUTOINCREMENT, user_id TEXT NOT NULL, job_id TEXT NOT NULL REFERENCES jobs(id),
            run_id TEXT, checked_at TEXT NOT NULL, source TEXT NOT NULL, result TEXT NOT NULL, detail TEXT);

        CREATE TABLE IF NOT EXISTS assessments (
            job_id TEXT PRIMARY KEY REFERENCES jobs(id), user_id TEXT NOT NULL, recommendation TEXT NOT NULL,
            score REAL NOT NULL, assessment_json TEXT NOT NULL, assessed_at TEXT NOT NULL, excluded_by TEXT);

        -- The user's own workflow: the steps they added to the per-posting pipeline (JobSearch.Studio).
        CREATE TABLE IF NOT EXISTS workflows (user_id TEXT PRIMARY KEY REFERENCES users(id), doc_json TEXT NOT NULL, updated_at TEXT NOT NULL);

        -- User-owned. The system never moves status past not_applied on its own.
        CREATE TABLE IF NOT EXISTS applications (
            job_id TEXT PRIMARY KEY REFERENCES jobs(id), user_id TEXT NOT NULL, status TEXT NOT NULL,
            status_evidence TEXT, status_set_by TEXT, status_set_at TEXT, applied_date TEXT,
            next_steps TEXT, user_notes TEXT, submitted_snapshot_json TEXT, updated_at TEXT NOT NULL);

        CREATE TABLE IF NOT EXISTS documents (
            id TEXT PRIMARY KEY, user_id TEXT NOT NULL, job_id TEXT NOT NULL REFERENCES jobs(id),
            kind TEXT NOT NULL, format TEXT NOT NULL, idempotency_key TEXT NOT NULL UNIQUE, drive_file_id TEXT NOT NULL,
            content_sha TEXT NOT NULL, source_doc_id TEXT, source_content_sha TEXT, resume_sha TEXT NOT NULL,
            status TEXT NOT NULL, user_reviewed_at TEXT, validation_json TEXT, created_at TEXT NOT NULL, updated_at TEXT NOT NULL);

        CREATE TABLE IF NOT EXISTS runs (
            id TEXT PRIMARY KEY, user_id TEXT NOT NULL, kind TEXT NOT NULL, idempotency_key TEXT NOT NULL,
            status TEXT NOT NULL, attempt INTEGER NOT NULL, started_at TEXT NOT NULL, finished_at TEXT,
            summary_json TEXT, error TEXT);
        CREATE INDEX IF NOT EXISTS runs_user_key ON runs(user_id, idempotency_key);

        CREATE TABLE IF NOT EXISTS run_locks (user_id TEXT PRIMARY KEY, run_id TEXT NOT NULL, lease_until TEXT NOT NULL);

        CREATE TABLE IF NOT EXISTS activity (
            id INTEGER PRIMARY KEY AUTOINCREMENT, user_id TEXT NOT NULL, actor TEXT NOT NULL, action TEXT NOT NULL,
            target TEXT, detail_json TEXT, run_id TEXT, at TEXT NOT NULL, exported INTEGER NOT NULL DEFAULT 0);

        -- The last value the system wrote to (or accepted from) each tracker cell: the baseline for edit detection.
        CREATE TABLE IF NOT EXISTS sheet_sync (
            user_id TEXT NOT NULL, job_id TEXT NOT NULL, column_name TEXT NOT NULL, last_value TEXT NOT NULL,
            PRIMARY KEY (user_id, job_id, column_name));

        CREATE TABLE IF NOT EXISTS conflicts (
            id INTEGER PRIMARY KEY AUTOINCREMENT, user_id TEXT NOT NULL, job_id TEXT NOT NULL, column_name TEXT NOT NULL,
            sheet_value TEXT, system_value TEXT, detected_at TEXT NOT NULL, resolved_at TEXT, resolution TEXT);
        """;

    private readonly string _connectionString;

    /// <summary>Opens (and creates) the database at <paramref name="path"/>.</summary>
    public Db(string path)
    {
        DefaultTypeMap.MatchNamesWithUnderscores = true;
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = true, DefaultTimeout = 30 }.ToString();
        using var c = Open();
        c.Execute("PRAGMA journal_mode=WAL;");
        c.Execute(Schema);
        // Columns added after a table first shipped: CREATE TABLE IF NOT EXISTS leaves an existing table as it was.
        AddColumnIfMissing(c, "assessments", "excluded_by", "TEXT");
    }

    private static void AddColumnIfMissing(SqliteConnection c, string table, string column, string type)
    {
        var columns = c.Query<string>($"SELECT name FROM pragma_table_info('{table}')").ToHashSet(StringComparer.Ordinal);
        if (!columns.Contains(column))
            c.Execute($"ALTER TABLE {table} ADD COLUMN {column} {type}");
    }

    /// <summary>A new open connection with foreign keys on and a 30 s busy timeout.</summary>
    public SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        c.Execute("PRAGMA foreign_keys=ON; PRAGMA busy_timeout=30000;");
        return c;
    }

    /// <summary>Runs <paramref name="work"/> in one IMMEDIATE transaction: the write lock is taken before any read.</summary>
    public T Write<T>(Func<SqliteConnection, SqliteTransaction, T> work)
    {
        using var c = Open();
        using var tx = c.BeginTransaction(deferred: false);
        var result = work(c, tx);
        tx.Commit();
        return result;
    }

    /// <summary>As <see cref="Write{T}"/>, with no result.</summary>
    public void Write(Action<SqliteConnection, SqliteTransaction> work) => Write<object?>((c, t) => { work(c, t); return null; });

    /// <summary>Async form, for callers on async paths; the work itself is synchronous SQLite.</summary>
    public Task<T> WriteAsync<T>(Func<SqliteConnection, SqliteTransaction, T> work) => Task.FromResult(Write(work));

    /// <summary>Runs a read on its own connection.</summary>
    public T Read<T>(Func<SqliteConnection, T> work)
    {
        using var c = Open();
        return work(c);
    }

    /// <summary>Appends an activity row (the per-user audit trail) inside the caller's transaction.</summary>
    public static void Log(SqliteConnection c, SqliteTransaction? tx, string userId, string actor, string action, string? target = null,
        object? detail = null, string? runId = null) =>
        c.Execute("INSERT INTO activity (user_id, actor, action, target, detail_json, run_id, at) VALUES (@userId,@actor,@action,@target,@detail,@runId,@at)",
            new { userId, actor, action, target, detail = Json.Write(detail ?? new { }), runId, at = Clock.Iso() }, tx);
}

/// <summary>JSON with one set of options everywhere.</summary>
public static class Json
{
    /// <summary>The options: camelCase, no indentation.</summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>Serializes.</summary>
    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);

    /// <summary>Deserializes, or default for null text.</summary>
    public static T? Read<T>(string? text) => text is null ? default : JsonSerializer.Deserialize<T>(text, Options);
}

/// <summary>Time, replaceable in tests.</summary>
public static class Clock
{
    private static readonly AsyncLocal<Func<DateTimeOffset>?> Override = new();

    /// <summary>Now, unless a test pinned it for the current async flow.</summary>
    public static DateTimeOffset Now => Override.Value?.Invoke() ?? DateTimeOffset.UtcNow;

    /// <summary>Pins <see cref="Now"/> for the current async flow until disposed.</summary>
    public static IDisposable Pin(Func<DateTimeOffset> now)
    {
        var previous = Override.Value;
        Override.Value = now;
        return new Restore(() => Override.Value = previous);
    }

    /// <summary>ISO-8601 UTC, seconds precision.</summary>
    public static string Iso(DateTimeOffset? at = null) =>
        (at ?? Now).ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>Parses <see cref="Iso"/> output.</summary>
    public static DateTimeOffset? Parse(string? iso) =>
        iso is null ? null : DateTimeOffset.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }
}

/// <summary>Ids.</summary>
public static class Ids
{
    /// <summary>prefix_ + 16 hex chars.</summary>
    public static string New(string prefix) => $"{prefix}_{Guid.NewGuid():N}"[..(prefix.Length + 17)];
}
