using System.Diagnostics;
using System.Text;
using Dapper;
using JobSearch.Accounts;
using JobSearch.Store;
using JobSearch.Workspace;

namespace JobSearch.Runs;

/// <summary>A difference between a snapshot and the live record, for a user-owned field.</summary>
public sealed record RestoreItem(string JobId, string Field, string Current, string InSnapshot);

/// <summary>
/// Optional version snapshots in git, one repository per user.
/// <para>Isolation: folders in one repository do not give separate access control, so each user gets their own
/// repository (a local directory, optionally pushed to a private remote the user authorizes). The remote must be
/// confirmed private before a push; credentials come from git's own credential helper and are never written here.</para>
/// <para>What a snapshot holds: the tracker (system and user columns), the profile with contact details redacted by
/// default, a manifest of documents (Drive ids, kinds, statuses, content hashes, validation outcome; not their text),
/// run summaries. Not held: OAuth tokens or any secret, résumé text (unless <c>includeResumeText</c>), document bodies
/// (Drive's revision history and the Submitted copies keep those).</para>
/// <para>Restore: <see cref="RestorePlan"/> lists user-owned fields (status, dates, notes) that differ from a chosen
/// commit; <see cref="ApplyRestore"/> applies them as the user, logged, after the user reviews the plan. Nothing
/// is restored silently, and system-owned fields are re-derived by the next run rather than restored.</para>
/// </summary>
public sealed class Snapshots(Db db, ApplicationService applications)
{
    private static string Csv(string v) => v.Contains(',', StringComparison.Ordinal) || v.Contains('"', StringComparison.Ordinal) || v.Contains('\n', StringComparison.Ordinal)
        ? "\"" + v.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : v;

    private static (int Code, string Out) Git(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, output);
    }

    /// <summary>Writes the user's snapshot into <paramref name="repoDir"/> and commits when something changed. Returns the commit or null.</summary>
    public string? Export(string userId, string repoDir, bool redactContact = true, bool includeResumeText = false)
    {
        Directory.CreateDirectory(repoDir);
        if (!Directory.Exists(Path.Combine(repoDir, ".git")))
        {
            Git(repoDir, "init", "-q");
            Git(repoDir, "config", "user.name", "jobsearch snapshot");
            Git(repoDir, "config", "user.email", "jobsearch@localhost");
            File.WriteAllText(Path.Combine(repoDir, ".gitignore"), "*.token\n*.secret\nsecrets.json\n");
            File.WriteAllText(Path.Combine(repoDir, "README.md"),
                "# Job search snapshot\n\nOne user's tracker, profile (contact redacted by default), document manifest and run history.\n" +
                "No credentials. Document bodies live in Google Drive (version history and the Submitted folder).\n");
        }

        var tracker = db.Read(c => c.Query("""
            SELECT j.id, j.employer, j.title, j.req_id, j.url, j.listing_status, j.last_verified_open_at, j.archived_at,
                   s.recommendation, a.status, a.applied_date, a.next_steps, a.user_notes
            FROM jobs j JOIN applications a ON a.job_id=j.id LEFT JOIN assessments s ON s.job_id=j.id WHERE j.user_id=@userId ORDER BY j.id
            """, new { userId }).Select(r => (IDictionary<string, object?>)r).ToList());
        string[] cols = ["id", "employer", "title", "req_id", "url", "listing_status", "last_verified_open_at", "archived_at", "recommendation", "status", "applied_date", "next_steps", "user_notes"];
        var sb = new StringBuilder(string.Join(",", cols) + "\n");
        foreach (var r in tracker)
            sb.Append(string.Join(",", cols.Select(c => Csv(r[c]?.ToString() ?? "")))).Append('\n');
        File.WriteAllText(Path.Combine(repoDir, "tracker.csv"), sb.ToString());

        string[] contact = ["name", "email", "phone", "location"];
        var profile = db.Read(c => c.Query<(string Field, string? Value, string Source, string Status)>(
            "SELECT field, value_json, source, status FROM profile_fields WHERE user_id=@userId ORDER BY field", new { userId }).AsList())
            .ToDictionary(f => f.Field, f => new { value = redactContact && contact.Contains(f.Field) ? "[redacted]" : f.Value, f.Source, f.Status }, StringComparer.Ordinal);
        File.WriteAllText(Path.Combine(repoDir, "profile.json"), Json.Write(profile));

        var docs = db.Read(c => c.Query("SELECT id, job_id, kind, format, drive_file_id, content_sha, resume_sha, status, user_reviewed_at FROM documents WHERE user_id=@userId ORDER BY id",
            new { userId }).AsList());
        File.WriteAllText(Path.Combine(repoDir, "documents.json"), Json.Write(docs));
        var runs = db.Read(c => c.Query("SELECT id, idempotency_key, status, attempt, started_at, finished_at, error FROM runs WHERE user_id=@userId ORDER BY started_at",
            new { userId }).AsList());
        File.WriteAllText(Path.Combine(repoDir, "runs.json"), Json.Write(runs));
        if (includeResumeText)
            File.WriteAllText(Path.Combine(repoDir, "resume.txt"),
                db.Read(c => c.QuerySingleOrDefault<string>("SELECT text FROM resume_versions WHERE user_id=@userId AND is_current=1", new { userId })) ?? "");

        Git(repoDir, "add", "-A");
        if (Git(repoDir, "diff", "--cached", "--quiet").Code == 0)
            return null; // nothing meaningful changed
        var changed = tracker.Count(r => r["status"]?.ToString() is not "not_applied");
        Git(repoDir, "commit", "-q", "-m", $"snapshot {Clock.Iso()}: {tracker.Count} jobs, {changed} with application activity, {docs.Count} documents");
        var head = Git(repoDir, "rev-parse", "HEAD").Out.Trim();
        db.Write((c, tx) => Db.Log(c, tx, userId, "system", "snapshot.commit", head, new { jobs = tracker.Count, documents = docs.Count, redactContact, includeResumeText }));
        return head;
    }

    /// <summary>
    /// Pushes to the user's remote, only when <paramref name="confirmedPrivate"/> says the remote is private (callers check
    /// it with the GitHub API under the user's own authorization) and the user asked for it.
    /// </summary>
    public (bool Pushed, string Detail) Push(string userId, string repoDir, string remoteUrl, bool confirmedPrivate)
    {
        if (!confirmedPrivate)
            return (false, "refused: the remote is not confirmed private");
        if (remoteUrl.Contains('@', StringComparison.Ordinal) && remoteUrl.StartsWith("https://", StringComparison.Ordinal))
            return (false, "refused: the remote URL carries a credential; use git's credential helper instead");
        Git(repoDir, "remote", "remove", "origin");
        Git(repoDir, "remote", "add", "origin", remoteUrl);
        var (code, output) = Git(repoDir, "push", "-q", "origin", "HEAD");
        db.Write((c, tx) => Db.Log(c, tx, userId, userId, "snapshot.push", remoteUrl, new { ok = code == 0 }));
        return (code == 0, output.Trim());
    }

    /// <summary>User-owned fields that differ between the live record and <paramref name="commit"/>.</summary>
    public IReadOnlyList<RestoreItem> RestorePlan(string userId, string repoDir, string commit)
    {
        var (code, csv) = Git(repoDir, "show", $"{commit}:tracker.csv");
        if (code != 0)
            throw new InvalidOperationException($"cannot read tracker.csv at {commit}: {csv.Trim()}");
        var lines = ParseCsv(csv);
        var header = lines[0];
        var live = db.Read(c => c.Query<(string Id, string Status, string? Applied, string? Next, string? Notes)>(
            "SELECT job_id, status, applied_date, next_steps, user_notes FROM applications WHERE user_id=@userId", new { userId }).AsList()).ToDictionary(x => x.Id);
        var plan = new List<RestoreItem>();
        foreach (var row in lines.Skip(1).Where(r => r.Count == header.Count))
        {
            string Col(string n) => row[header.IndexOf(n)];
            if (!live.TryGetValue(Col("id"), out var cur))
                continue;
            foreach (var (field, current) in new[] { ("status", cur.Status), ("applied_date", cur.Applied ?? ""), ("next_steps", cur.Next ?? ""), ("user_notes", cur.Notes ?? "") })
                if (Col(field) != current)
                    plan.Add(new RestoreItem(Col("id"), field, current, Col(field)));
        }
        return plan;
    }

    /// <summary>Applies a reviewed plan as the user.</summary>
    public int ApplyRestore(string userId, string actorId, IReadOnlyList<RestoreItem> plan, string commit)
    {
        foreach (var item in plan)
        {
            switch (item.Field)
            {
                case "status":
                    applications.SetStatus(userId, actorId, item.JobId, item.InSnapshot, $"restored by the user from snapshot {commit}", "restore");
                    break;
                case "applied_date":
                    applications.SetAppliedDate(userId, actorId, item.JobId, item.InSnapshot, "restore");
                    break;
                case "next_steps":
                    applications.SetNotes(userId, actorId, item.JobId, null, item.InSnapshot, "restore");
                    break;
                case "user_notes":
                    applications.SetNotes(userId, actorId, item.JobId, item.InSnapshot, null, "restore");
                    break;
            }
        }
        return plan.Count;
    }

    private static List<List<string>> ParseCsv(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else if (ch == '"') quoted = false;
                else cell.Append(ch);
            }
            else if (ch == '"') quoted = true;
            else if (ch == ',') { row.Add(cell.ToString()); cell.Clear(); }
            else if (ch == '\n') { row.Add(cell.ToString()); cell.Clear(); rows.Add(row); row = []; }
            else cell.Append(ch);
        }
        if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString()); rows.Add(row); }
        return rows;
    }
}
