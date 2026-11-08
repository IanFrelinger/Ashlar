using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Dapper;
using JobSearch.Store;
using JobSearch.Studio;
using JobSearch.Workspace;

namespace JobSearch.Host.Studio;

/// <summary>Retention limits (STUDIO.md I14). Days; zero keeps forever.</summary>
public sealed record RetentionRules
{
    /// <summary>Archived jobs and everything about them.</summary>
    public int ArchivedJobsDays { get; init; } = 365;
    /// <summary>Run records.</summary>
    public int RunsDays { get; init; } = 180;
    /// <summary>Activity entries already exported to the user's own sheet.</summary>
    public int ExportedActivityDays { get; init; } = 400;
    /// <summary>Forge jobs and their token records.</summary>
    public int ForgeDays { get; init; } = 400;
}

/// <summary>A tombstone: a keyed hash of an erased account's id, when it was erased, and how many rows went. No personal data.</summary>
public sealed record Erasure(string SubjectHash, string DeletedAt, long Rows);

/// <summary>
/// The data-subject side of the studio: consent records, a full export of a user's data, erasure of the account, and
/// the retention sweep. These support GDPR Arts. 7, 15, 17 and 20 and the CCPA/CPRA rights to know and delete; the
/// operator's privacy notice and legal basis are theirs to supply (PRIVACY.md).
/// </summary>
public sealed class Compliance
{
    /// <summary>The privacy notice version a consent refers to; bump it when the notice changes.</summary>
    public const string NoticeVersion = "2026-11-08";

    /// <summary>Consents the studio records.</summary>
    public static readonly string[] ConsentKinds = ["privacy-notice", "terms", "age-16-or-over", "model-wording"];

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS consents (
            id INTEGER PRIMARY KEY AUTOINCREMENT, user_id TEXT NOT NULL REFERENCES users(id), kind TEXT NOT NULL, version TEXT NOT NULL,
            granted_at TEXT NOT NULL, withdrawn_at TEXT);
        CREATE INDEX IF NOT EXISTS consents_user ON consents(user_id, kind);
        CREATE TABLE IF NOT EXISTS deletions (subject_hash TEXT PRIMARY KEY, deleted_at TEXT NOT NULL, rows INTEGER NOT NULL);
        """;

    /// <summary>Tables keyed by <c>user_id</c>, children before parents, deleted in this order.</summary>
    private static readonly string[] UserTables =
    [
        "job_checks", "assessments", "applications", "documents", "conflicts", "sheet_sync", "activity", "runs", "run_locks",
        "workspaces", "connections", "preferences", "profile_fields", "resume_versions", "workflows", "forge_usage", "forge_jobs",
        "consents", "jobs",
    ];

    private readonly Db _db;
    private readonly SecretStore _secrets;
    private readonly StepLibrary _library;
    private readonly byte[] _pepper;

    /// <summary>Wires compliance over the store.</summary>
    public Compliance(Db db, SecretStore secrets, StepLibrary library, byte[] pepper)
    {
        _db = db;
        _secrets = secrets;
        _library = library;
        _pepper = pepper;
        db.Read(c => c.Execute(Schema));
    }

    /// <summary>Records a consent.</summary>
    public void Grant(string userId, string kind, string version = NoticeVersion)
    {
        if (!ConsentKinds.Contains(kind))
            throw new ArgumentException($"unknown consent {kind}");
        _db.Write((c, tx) =>
        {
            c.Execute("INSERT INTO consents (user_id, kind, version, granted_at) VALUES (@userId, @kind, @version, @at)", new { userId, kind, version, at = Clock.Iso() }, tx);
            Db.Log(c, tx, userId, userId, "consent.granted", kind, new { version });
        });
    }

    /// <summary>Withdraws a consent; as easy as giving it (GDPR Art. 7(3)).</summary>
    public void Withdraw(string userId, string kind)
    {
        _db.Write((c, tx) =>
        {
            c.Execute("UPDATE consents SET withdrawn_at=@at WHERE user_id=@userId AND kind=@kind AND withdrawn_at IS NULL", new { userId, kind, at = Clock.Iso() }, tx);
            Db.Log(c, tx, userId, userId, "consent.withdrawn", kind);
        });
    }

    /// <summary>Whether the user's current consent of <paramref name="kind"/> stands (for the current notice version).</summary>
    public bool Has(string userId, string kind, string version = NoticeVersion) =>
        _db.Read(c => c.ExecuteScalar<long>("SELECT COUNT(*) FROM consents WHERE user_id=@userId AND kind=@kind AND version=@version AND withdrawn_at IS NULL",
            new { userId, kind, version })) > 0;

    /// <summary>Whether the user may use the studio: notice, terms and age confirmed for the current version.</summary>
    public bool Onboarded(string userId) => Has(userId, "privacy-notice") && Has(userId, "terms") && Has(userId, "age-16-or-over");

    /// <summary>
    /// Everything the studio holds about the user, as JSON (access and portability). Secrets are listed by reference,
    /// never by value; documents in the user's own Drive are listed by file id.
    /// </summary>
    public JsonObject Export(string userId)
    {
        var export = new JsonObject
        {
            ["format"] = "jobsearch-studio-export/1",
            ["exportedAt"] = Clock.Iso(),
            ["note"] = "Everything Job Search Studio stores about you. Your drafts, PDFs and tracker are files in your own Google Drive; this lists their ids.",
        };
        _db.Read(c =>
        {
            export["user"] = Rows(c, "SELECT * FROM users WHERE id=@userId", userId);
            foreach (var table in UserTables)
                export[table] = Rows(c, $"SELECT * FROM {table} WHERE user_id=@userId", userId);
            export["accessGrantsGiven"] = Rows(c, "SELECT id, manager_id, scopes_json, granted_at, expires_at, revoked_at FROM access_grants WHERE user_id=@userId", userId);
            export["accessGrantsHeld"] = Rows(c, "SELECT id, user_id, scopes_json, granted_at, expires_at, revoked_at FROM access_grants WHERE manager_id=@userId", userId);
            export["privateSteps"] = Rows(c, "SELECT id, kind, name, description, behavior, created_at FROM library_steps WHERE owner_user_id=@userId AND visibility='private'", userId);
            export["sharedStepsYouAskedFor"] = Rows(c, "SELECT id, name, created_at FROM library_steps WHERE owner_user_id=@userId AND visibility='shared'", userId);
            return 0;
        });
        // A token reference names a secret; the token itself is never exported.
        if (export["connections"] is JsonArray conns)
            foreach (var conn in conns.OfType<JsonObject>())
                conn["token_ref"] = "(stored encrypted; not exported)";
        return export;
    }

    private static JsonArray Rows(Microsoft.Data.Sqlite.SqliteConnection c, string sql, string userId)
    {
        var array = new JsonArray();
        using var reader = c.ExecuteReader(sql, new { userId });
        while (reader.Read())
        {
            var row = new JsonObject();
            for (var i = 0; i < reader.FieldCount; i++)
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i) switch
                {
                    long l => JsonValue.Create(l),
                    double d => JsonValue.Create(d),
                    string text => JsonValue.Create(text),
                    byte[] bytes => JsonValue.Create(Convert.ToBase64String(bytes)),
                    var other => JsonValue.Create(Convert.ToString(other, CultureInfo.InvariantCulture)),
                };
            array.Add(row);
        }
        return array;
    }

    /// <summary>
    /// Erases the account: every row with the user's id, their grants in both directions, their secrets and private
    /// steps. Shared steps they asked for stay (generic code, no personal data) with no author. A tombstone keeps only
    /// a keyed hash of the id and the time, so a later restore from backup can be checked against it.
    /// </summary>
    public int Erase(string userId)
    {
        var tokenRefs = _db.Read(c => c.Query<string>("SELECT token_ref FROM connections WHERE user_id=@userId", new { userId }).AsList());
        var rows = _db.Write((c, tx) =>
        {
            var n = 0;
            foreach (var table in UserTables)
                n += c.Execute($"DELETE FROM {table} WHERE user_id=@userId", new { userId }, tx);
            n += c.Execute("DELETE FROM access_grants WHERE user_id=@userId OR manager_id=@userId", new { userId }, tx);
            n += c.Execute("DELETE FROM users WHERE id=@userId", new { userId }, tx);
            c.Execute("INSERT OR REPLACE INTO deletions (subject_hash, deleted_at, rows) VALUES (@hash, @at, @n)", new { hash = SubjectHash(userId), at = Clock.Iso(), n }, tx);
            return n;
        });
        foreach (var r in tokenRefs)
            _secrets.Remove(r);
        rows += _library.ForgetUser(userId);
        return rows;
    }

    /// <summary>Whether an id was erased (for restores from backup).</summary>
    public bool WasErased(string userId) =>
        _db.Read(c => c.ExecuteScalar<long>("SELECT COUNT(*) FROM deletions WHERE subject_hash=@h", new { h = SubjectHash(userId) })) > 0;

    /// <summary>
    /// After a restore from backup: erases again every account whose keyed hash is in <paramref name="ledger"/> (the
    /// erasures made after that backup was taken, from the newest ledger), and records each tombstone with its original
    /// time, so the restored copy knows every erasure the ledger does. Returns how many accounts it erased.
    /// </summary>
    public int Reapply(IEnumerable<Erasure> ledger)
    {
        var entries = ledger.ToList();
        var wanted = entries.Select(e => e.SubjectHash).ToHashSet(StringComparer.Ordinal);
        var erased = 0;
        foreach (var id in _db.Read(c => c.Query<string>("SELECT id FROM users").AsList()))
            if (wanted.Contains(SubjectHash(id)))
            {
                Erase(id);
                erased++;
            }
        _db.Write((c, tx) =>
        {
            foreach (var e in entries)
                c.Execute("INSERT OR REPLACE INTO deletions (subject_hash, deleted_at, rows) VALUES (@SubjectHash, @DeletedAt, @Rows)", e, tx);
        });
        return erased;
    }

    private string SubjectHash(string userId) => Convert.ToHexStringLower(HMACSHA256.HashData(_pepper, Encoding.UTF8.GetBytes(userId)));

    /// <summary>Deletes what has outlived its retention period. Returns rows removed per kind.</summary>
    public Dictionary<string, int> Sweep(RetentionRules rules)
    {
        var removed = new Dictionary<string, int>(StringComparer.Ordinal);
        string Cutoff(int days) => Clock.Now.AddDays(-days).ToString("o", CultureInfo.InvariantCulture);
        _db.Write((c, tx) =>
        {
            if (rules.ArchivedJobsDays > 0)
            {
                var cutoff = Cutoff(rules.ArchivedJobsDays);
                // Jobs with an application the user recorded are kept: they are the user's own history, not stale listings.
                const string Old = "SELECT j.id FROM jobs j WHERE j.archived_at IS NOT NULL AND j.archived_at < @cutoff AND NOT EXISTS (SELECT 1 FROM applications a WHERE a.job_id=j.id AND a.status<>'not_applied')";
                var n = 0;
                foreach (var child in new[] { "job_checks", "assessments", "applications", "documents", "conflicts", "sheet_sync" })
                    n += c.Execute($"DELETE FROM {child} WHERE job_id IN ({Old})", new { cutoff }, tx);
                n += c.Execute($"DELETE FROM jobs WHERE id IN ({Old})", new { cutoff }, tx);
                removed["archived jobs"] = n;
            }
            if (rules.RunsDays > 0)
                removed["runs"] = c.Execute("DELETE FROM runs WHERE started_at < @cutoff AND finished_at IS NOT NULL", new { cutoff = Cutoff(rules.RunsDays) }, tx);
            if (rules.ExportedActivityDays > 0)
                removed["activity"] = c.Execute("DELETE FROM activity WHERE exported=1 AND at < @cutoff", new { cutoff = Cutoff(rules.ExportedActivityDays) }, tx);
            if (rules.ForgeDays > 0)
            {
                var cutoff = Cutoff(rules.ForgeDays);
                removed["forge"] = c.Execute("DELETE FROM forge_usage WHERE at < @cutoff", new { cutoff }, tx)
                    + c.Execute("DELETE FROM forge_jobs WHERE created_at < @cutoff", new { cutoff }, tx);
            }
        });
        return removed;
    }
}

/// <summary>The user's workflow steps for a run: enabled steps the user may still use, with their settings.</summary>
public sealed class StudioWorkflowSource(WorkflowStore workflows, StepLibrary library) : IWorkflowSource
{
    /// <inheritdoc />
    public IReadOnlyList<CompiledStep> StepsFor(string userId)
    {
        var steps = new List<CompiledStep>();
        foreach (var s in workflows.Get(userId).Steps.Where(s => s.Enabled))
        {
            if (library.Get(s.StepId) is not { } entry || !StepLibrary.VisibleTo(entry, userId))
                continue;
            steps.Add(new CompiledStep(s.Key, entry.Id, entry.Kind, s.Label ?? entry.Name, s.Settings));
        }
        return steps;
    }
}
