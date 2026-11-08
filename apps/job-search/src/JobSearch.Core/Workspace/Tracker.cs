using System.Globalization;
using Dapper;
using JobSearch.Accounts;
using JobSearch.Screening;
using JobSearch.Store;

namespace JobSearch.Workspace;

/// <summary>A user's Drive workspace: folder ids and the two sheets.</summary>
public sealed record WorkspaceIds(string RootId, Dictionary<string, string> Folders, string TrackerSheetId, string ActivitySheetId);

/// <summary>What a tracker sync did.</summary>
public sealed record TrackerSyncResult(int ImportedEdits, int CellsWritten, int RowsAppended, int RowsRestored, int NewConflicts, IReadOnlyList<string> NewlyApplied);

/// <summary>
/// The Drive workspace and the tracker sheet.
/// <para><b>Who owns what.</b> System-owned columns (listing facts, fit, evidence, gaps, links, validation) are written
/// from the database. User-owned columns (application status, applied date, next steps, notes) belong to the sheet:
/// the user's edits there are imported. For every cell the last value the system wrote or accepted is stored
/// (<c>sheet_sync</c>); a sheet value that differs from it is the user's edit.</para>
/// <para><b>Never silently overwrite.</b> An edited system cell is not overwritten; it becomes a conflict, flagged in the
/// row's Sync flags until the user resolves it. A user cell changed both in the sheet and in the app since the last
/// sync is a conflict too: neither value is overwritten. A row the user deleted is re-added and flagged, not lost.</para>
/// </summary>
public sealed class TrackerService(Db db, ApplicationService applications)
{
    /// <summary>Folder roles.</summary>
    public const string ProfileFolder = "profile", ApplicationsFolder = "applications", SubmittedFolder = "submitted", ArchiveFolder = "archive";

    /// <summary>The tracker's columns in order, and whether each is user-owned.</summary>
    public static readonly (string Name, bool UserOwned)[] Columns =
    [
        ("Job ID", false), ("Employer", false), ("Title", false), ("Req ID", false), ("Link", false), ("Location", false),
        ("Arrangement", false), ("Pay", false), ("Posted", false), ("Deadline", false), ("Listing status", false),
        ("Last verified", false), ("Source", false), ("Fit", false), ("Score", false), ("Requirements", false),
        ("Evidence", false), ("Gaps / to confirm", false), ("Tailored résumé", false), ("Cover letter", false), ("PDFs", false),
        ("Materials status", false), ("Validation", false),
        ("Application status", true), ("Applied date", true), ("Next steps", true), ("Notes", true),
        ("Sync flags", false),
    ];

    /// <summary>Activity log sheet columns.</summary>
    public static readonly string[] ActivityColumns = ["When (UTC)", "Actor", "Action", "Target", "Detail", "Run"];

    /// <summary>Creates (or finds) the workspace in the user's Drive. Idempotent through app keys.</summary>
    public async Task<WorkspaceIds> ProvisionAsync(string userId, string displayName, IGoogleWorkspace g, CancellationToken ct = default)
    {
        var root = await g.EnsureFolderAsync($"Job Search — {displayName}", null, $"{userId}:root", ct).ConfigureAwait(false);
        var folders = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ProfileFolder] = await g.EnsureFolderAsync("Master résumé & profile", root, $"{userId}:folder:profile", ct).ConfigureAwait(false),
            [ApplicationsFolder] = await g.EnsureFolderAsync("Applications", root, $"{userId}:folder:applications", ct).ConfigureAwait(false),
            [SubmittedFolder] = await g.EnsureFolderAsync("Submitted (frozen copies)", root, $"{userId}:folder:submitted", ct).ConfigureAwait(false),
            [ArchiveFolder] = await g.EnsureFolderAsync("Archive", root, $"{userId}:folder:archive", ct).ConfigureAwait(false),
        };
        var tracker = await g.CreateSheetAsync("Job Tracker", root, Columns.Select(c => c.Name).ToList(), $"{userId}:sheet:tracker", ct).ConfigureAwait(false);
        var activity = await g.CreateSheetAsync("Activity Log", root, ActivityColumns, $"{userId}:sheet:activity", ct).ConfigureAwait(false);
        var ids = new WorkspaceIds(root, folders, tracker, activity);
        db.Write((c, tx) =>
        {
            var existing = c.QuerySingleOrDefault<string>("SELECT root_id FROM workspaces WHERE user_id=@userId", new { userId }, tx);
            c.Execute("""
                INSERT INTO workspaces (user_id, root_id, folders_json, tracker_sheet_id, activity_sheet_id, created_at) VALUES (@userId,@root,@f,@tracker,@activity,@at)
                ON CONFLICT(user_id) DO UPDATE SET root_id=excluded.root_id, folders_json=excluded.folders_json,
                    tracker_sheet_id=excluded.tracker_sheet_id, activity_sheet_id=excluded.activity_sheet_id
                """, new { userId, root, f = Json.Write(folders), tracker, activity, at = Clock.Iso() }, tx);
            if (existing is null)
                Db.Log(c, tx, userId, "system", "workspace.create", root);
        });
        return ids;
    }

    /// <summary>The stored workspace, or null.</summary>
    public WorkspaceIds? Get(string userId)
    {
        var r = db.Read(c => c.QuerySingleOrDefault<(string Root, string Folders, string Tracker, string Activity)?>(
            "SELECT root_id, folders_json, tracker_sheet_id, activity_sheet_id FROM workspaces WHERE user_id=@userId", new { userId }));
        return r is { } w ? new WorkspaceIds(w.Root, Json.Read<Dictionary<string, string>>(w.Folders)!, w.Tracker, w.Activity) : null;
    }

    private sealed record JobRow(
        string Id, string Employer, string Title, string? ReqId, string? Url, string? Location, string? WorkArrangement, string? PayText,
        string? PostedDate, string? Deadline, string ListingStatus, string? ListingStatusDetail, string? LastVerifiedOpenAt, string? LastCheckedAt,
        string SourceRef, string? ArchivedAt, string? Recommendation, double? Score, string? AssessmentJson,
        string Status, string? AppliedDate, string? NextSteps, string? UserNotes);

    private sealed record DocRow(string Id, string JobId, string Kind, string Format, string DriveFileId, string Status, string? ValidationJson, string? UserReviewedAt);

    private static string Link(string format, string id) =>
        format == "pdf" ? $"https://drive.google.com/file/d/{id}" : $"https://docs.google.com/document/d/{id}";

    private static Dictionary<string, string> SystemValues(JobRow j, IReadOnlyList<DocRow> docs)
    {
        var a = Json.Read<Assessment>(j.AssessmentJson);
        string Join(IEnumerable<string> items, int max = 6)
        {
            var list = items.ToList();
            return string.Join("; ", list.Take(max)) + (list.Count > max ? $"; +{list.Count - max} more" : "");
        }
        var live = docs.Where(d => d.Status is "draft" or "stale" or "submitted").ToList();
        var editable = live.Where(d => d.Format == "doc").ToDictionary(d => d.Kind, d => d);
        var pdfs = live.Where(d => d.Format == "pdf").ToList();
        var materials = live.Count == 0 ? "" :
            pdfs.Any(d => d.Status == "stale") ? "PDF stale: an editable draft changed after the PDF was made; refresh before using" :
            live.Any(d => d.Status == "submitted") ? "submitted (frozen copies in 'Submitted')" :
            live.All(d => d.UserReviewedAt is not null) ? "reviewed by user" : "draft — review before submitting";
        var validations = live.Where(d => d.Format == "doc").Select(d => Json.Read<Materials.ValidationRecord>(d.ValidationJson)).Where(v => v is not null).ToList();
        var validation = validations.Count == 0 ? "" :
            $"{(validations.All(v => v!.Passed) ? "passed" : "UNSUPPORTED CLAIMS")}; checked {validations[0]!.ValidatedAt}; " +
            $"{validations.Sum(v => v!.Claims.Count)} claims traced; {validations.Sum(v => v!.NeedsConfirmation.Count)} items to confirm; " +
            $"PDF text {string.Join("/", validations.Select(v => v!.Pdf?.Ratio.ToString("P0", CultureInfo.InvariantCulture) ?? "n/a"))}";
        var listing = j.ArchivedAt is not null ? $"{j.ListingStatus} (archived)" : j.ListingStatus;
        if (j.ListingStatusDetail is { Length: > 0 } detail) listing += $" — {detail}";
        var r = a?.Requirements;
        var reqs = r is null ? "" : Join(new[]
        {
            r.SkillsRequired.Count > 0 ? "skills: " + string.Join(", ", r.SkillsRequired) : null,
            r.YearsMin is { } y ? $"{y}+ yrs" : null, r.Degree is { } dg ? $"{dg} degree" : null,
            r.Clearance is { } cl ? $"{cl} clearance" : null, r.CitizenshipRequired ? "US citizenship" : null,
            r.VisaSponsorship == "not_offered" ? "no visa sponsorship" : null, r.TravelPct is { } t ? $"{t}% travel" : null,
        }.OfType<string>(), 8);
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Job ID"] = j.Id, ["Employer"] = j.Employer, ["Title"] = j.Title, ["Req ID"] = j.ReqId ?? "", ["Link"] = j.Url ?? "",
            ["Location"] = j.Location ?? "", ["Arrangement"] = j.WorkArrangement ?? "not stated", ["Pay"] = j.PayText ?? (r?.PayMax is { } pm ? $"up to {pm:N0}" : "not stated"),
            ["Posted"] = j.PostedDate ?? "", ["Deadline"] = j.Deadline ?? "", ["Listing status"] = listing,
            ["Last verified"] = j.LastVerifiedOpenAt ?? (j.LastCheckedAt is null ? "" : $"never open; last checked {j.LastCheckedAt}"),
            ["Source"] = j.SourceRef, ["Fit"] = j.Recommendation ?? "", ["Score"] = j.Score?.ToString("0.00", CultureInfo.InvariantCulture) ?? "",
            ["Requirements"] = reqs,
            ["Evidence"] = a is null ? "" : Join(a.Evidence.Select(e => $"{e.Requirement}: {e.CandidateEvidence}")),
            ["Gaps / to confirm"] = a is null ? "" : Join(a.Blockers.Select(b => $"BLOCKER {b.Requirement}: {b.Detail}")
                .Concat(a.Unknowns.Select(u => $"CONFIRM {u.Requirement}: {u.Detail}")).Concat(a.Gaps.Select(g => $"GAP {g.Requirement}"))),
            ["Tailored résumé"] = editable.TryGetValue("resume", out var rd) ? Link("doc", rd.DriveFileId) : "",
            ["Cover letter"] = editable.TryGetValue("cover_letter", out var cd) ? Link("doc", cd.DriveFileId) : "",
            ["PDFs"] = string.Join(" ", pdfs.Select(p => Link("pdf", p.DriveFileId))),
            ["Materials status"] = materials, ["Validation"] = validation,
            ["Application status"] = j.Status, ["Applied date"] = j.AppliedDate ?? "", ["Next steps"] = j.NextSteps ?? "", ["Notes"] = j.UserNotes ?? "",
        };
    }

    /// <summary>Reconciles the sheet with the database, user edits first. See the class remarks for the rules.</summary>
    public async Task<TrackerSyncResult> SyncAsync(string userId, IGoogleWorkspace g, WorkspaceIds ws, string? runId, CancellationToken ct = default)
    {
        var rows = await g.ReadRowsAsync(ws.TrackerSheetId, ct).ConfigureAwait(false);
        if (rows.Count == 0)
            rows.Add([]);
        var headers = rows[0];
        var missing = Columns.Select(c => c.Name).Where(n => !headers.Contains(n)).ToList();
        if (missing.Count > 0)
        {
            headers = [.. headers, .. missing]; // never reorder or drop the user's columns
            await g.SetHeadersAsync(ws.TrackerSheetId, headers, ct).ConfigureAwait(false);
        }
        string Cell(int row, string col)
        {
            var i = headers.IndexOf(col);
            return i >= 0 && i < rows[row].Count ? rows[row][i] : "";
        }

        var rowOf = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 1; i < rows.Count; i++)
        {
            var id = Cell(i, "Job ID");
            if (id.Length > 0) rowOf.TryAdd(id, i);
        }

        int imported = 0, newConflicts = 0, restored = 0;
        var newlyApplied = new List<string>();
        var writes = new List<(int Row, string Column, string Value)>();
        var appends = new List<IReadOnlyList<string>>();
        var baselineUpdates = new List<(string JobId, string Column, string Value)>();

        var jobs = db.Read(c => c.Query<JobRow>("""
            SELECT j.id, j.employer, j.title, j.req_id, j.url, j.location, j.work_arrangement, j.pay_text, j.posted_date, j.deadline,
                   j.listing_status, j.listing_status_detail, j.last_verified_open_at, j.last_checked_at, j.source_ref, j.archived_at,
                   s.recommendation, s.score, s.assessment_json, a.status, a.applied_date, a.next_steps, a.user_notes
            FROM jobs j JOIN applications a ON a.job_id=j.id LEFT JOIN assessments s ON s.job_id=j.id
            WHERE j.user_id=@userId ORDER BY j.first_seen_at, j.id
            """, new { userId }).AsList());
        var docs = db.Read(c => c.Query<DocRow>("SELECT id, job_id, kind, format, drive_file_id, status, validation_json, user_reviewed_at FROM documents WHERE user_id=@userId", new { userId }).AsList())
            .ToLookup(d => d.JobId);
        var baselines = db.Read(c => c.Query<(string JobId, string Column, string Value)>(
            "SELECT job_id, column_name, last_value FROM sheet_sync WHERE user_id=@userId", new { userId }).AsList())
            .ToDictionary(b => (b.JobId, b.Column), b => b.Value);
        var openConflicts = db.Read(c => c.Query<(string JobId, string Column, string Resolution)>(
            "SELECT job_id, column_name, COALESCE(resolution,'open') FROM conflicts WHERE user_id=@userId AND (resolved_at IS NULL OR resolution='keep_sheet')", new { userId }).AsList())
            .ToDictionary(x => (x.JobId, x.Column), x => x.Resolution);

        var restoredRecently = db.Read(c => c.Query<string>("SELECT DISTINCT target FROM activity WHERE user_id=@userId AND action='tracker.row_restored' AND at>=@since",
            new { userId, since = Clock.Iso(Clock.Now.AddDays(-7)) }).ToHashSet(StringComparer.Ordinal));

        void Conflict(string jobId, string column, string sheetValue, string systemValue)
        {
            if (openConflicts.ContainsKey((jobId, column)))
                return;
            openConflicts[(jobId, column)] = "open";
            newConflicts++;
            db.Write((c, tx) =>
            {
                c.Execute("INSERT INTO conflicts (user_id, job_id, column_name, sheet_value, system_value, detected_at) VALUES (@userId,@jobId,@column,@sheetValue,@systemValue,@at)",
                    new { userId, jobId, column, sheetValue, systemValue, at = Clock.Iso() }, tx);
                Db.Log(c, tx, userId, "system", "tracker.conflict", jobId, new { column, sheetValue, systemValue }, runId);
            });
        }

        foreach (var job in jobs)
        {
            var values = SystemValues(job, docs[job.Id].ToList());
            if (!rowOf.TryGetValue(job.Id, out var row))
            {
                var wasThere = baselines.ContainsKey((job.Id, "Job ID"));
                if (wasThere)
                {
                    restored++;
                    db.Write((c, tx) => Db.Log(c, tx, userId, "system", "tracker.row_restored", job.Id, new { reason = "row missing from the sheet" }, runId));
                    restoredRecently.Add(job.Id);
                }
                values["Sync flags"] = wasThere ? "Restored: this row was removed from the sheet. To stop tracking the job, set Application status to not_pursuing." : "";
                appends.Add(headers.Select(h => values.GetValueOrDefault(h, "")).ToList());
                foreach (var (name, _) in Columns)
                    baselineUpdates.Add((job.Id, name, values.GetValueOrDefault(name, "")));
                continue;
            }

            foreach (var (name, userOwned) in Columns.Where(c => c.Name != "Sync flags"))
            {
                var sheet = Cell(row, name);
                var system = values[name];
                var hasBase = baselines.TryGetValue((job.Id, name), out var b);
                if (userOwned)
                {
                    var sheetChanged = hasBase ? sheet != b : sheet.Length > 0 && sheet != system;
                    var appChanged = hasBase && system != b;
                    if (sheetChanged && appChanged && sheet != system)
                    {
                        Conflict(job.Id, name, sheet, system);
                    }
                    else if (sheetChanged && sheet != system)
                    {
                        if (Import(userId, job.Id, name, sheet, newlyApplied, runId))
                        {
                            imported++;
                            baselineUpdates.Add((job.Id, name, sheet));
                        }
                        else
                        {
                            Conflict(job.Id, name, sheet, system);
                        }
                    }
                    else if (appChanged && !sheetChanged)
                    {
                        writes.Add((row, name, system));
                        baselineUpdates.Add((job.Id, name, system));
                    }
                    else if (!hasBase || b != sheet)
                    {
                        baselineUpdates.Add((job.Id, name, sheet));
                    }
                }
                else
                {
                    if (openConflicts.ContainsKey((job.Id, name)))
                        continue; // the user's value stands until they resolve it
                    if (hasBase && sheet != b)
                    {
                        if (sheet != system) Conflict(job.Id, name, sheet, system);
                        else baselineUpdates.Add((job.Id, name, sheet));
                    }
                    else if (sheet != system)
                    {
                        writes.Add((row, name, system));
                        baselineUpdates.Add((job.Id, name, system));
                    }
                    else if (!hasBase)
                    {
                        baselineUpdates.Add((job.Id, name, system));
                    }
                }
            }

            var flags = openConflicts.Where(kv => kv.Key.JobId == job.Id && kv.Value == "open")
                .Select(kv => $"Conflict in '{kv.Key.Column}': your edit was kept; resolve with `jobsearch conflicts`").ToList();
            if (restoredRecently.Contains(job.Id))
                flags.Insert(0, "Restored: this row was removed from the sheet. To stop tracking the job, set Application status to not_pursuing.");
            var flagText = string.Join(" | ", flags);
            if (Cell(row, "Sync flags") != flagText)
            {
                writes.Add((row, "Sync flags", flagText));
                baselineUpdates.Add((job.Id, "Sync flags", flagText));
            }
        }

        if (writes.Count > 0)
            await g.UpdateCellsAsync(ws.TrackerSheetId, writes, ct).ConfigureAwait(false);
        if (appends.Count > 0)
            await g.AppendRowsAsync(ws.TrackerSheetId, appends, ct).ConfigureAwait(false);
        db.Write((c, tx) =>
        {
            foreach (var (jobId, column, value) in baselineUpdates)
                c.Execute("""
                    INSERT INTO sheet_sync (user_id, job_id, column_name, last_value) VALUES (@userId,@jobId,@column,@value)
                    ON CONFLICT(user_id, job_id, column_name) DO UPDATE SET last_value=excluded.last_value
                    """, new { userId, jobId, column, value }, tx);
        });
        return new TrackerSyncResult(imported, writes.Count, appends.Count, restored, newConflicts, newlyApplied);
    }

    private bool Import(string userId, string jobId, string column, string value, List<string> newlyApplied, string? runId)
    {
        const string via = "sheet";
        try
        {
            switch (column)
            {
                case "Application status":
                    var status = value.Trim().ToLowerInvariant().Replace(' ', '_');
                    if (!ApplicationService.Statuses.Contains(status))
                        return false;
                    var evidence = $"entered by the user in their tracker sheet ({Clock.Iso()})";
                    if (applications.SetStatus(userId, userId, jobId, status, evidence, via))
                        newlyApplied.Add(jobId);
                    return true;
                case "Applied date":
                    applications.SetAppliedDate(userId, userId, jobId, value, via);
                    return true;
                case "Next steps":
                    applications.SetNotes(userId, userId, jobId, null, value, via);
                    return true;
                case "Notes":
                    applications.SetNotes(userId, userId, jobId, value, null, via);
                    return true;
                default:
                    return false;
            }
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Open conflicts.</summary>
    public IReadOnlyList<(long Id, string JobId, string Column, string? SheetValue, string? SystemValue, string DetectedAt)> OpenConflicts(string userId) =>
        db.Read(c => c.Query<(long, string, string, string?, string?, string)>(
            "SELECT id, job_id, column_name, sheet_value, system_value, detected_at FROM conflicts WHERE user_id=@userId AND resolved_at IS NULL ORDER BY id", new { userId }).AsList());

    /// <summary>
    /// Resolves a conflict. <c>sheet</c>: the sheet's value stands (a system cell is then never written again; a user cell
    /// is imported). <c>system</c>: the next sync writes the app's value.
    /// </summary>
    public void Resolve(string userId, string actorId, long conflictId, string keep, AccessControl access)
    {
        access.Authorize(actorId, userId, Scope.EditNotes);
        if (keep is not ("sheet" or "system"))
            throw new ArgumentException("keep must be 'sheet' or 'system'");
        var conflict = db.Read(c => c.QuerySingleOrDefault<(string JobId, string Column, string? Sheet)?>(
            "SELECT job_id, column_name, sheet_value FROM conflicts WHERE id=@conflictId AND user_id=@userId AND resolved_at IS NULL", new { conflictId, userId }))
            ?? throw new KeyNotFoundException($"no open conflict {conflictId}");
        var userOwned = Columns.First(c => c.Name == conflict.Column).UserOwned;
        if (keep == "sheet" && userOwned)
            Import(userId, conflict.JobId, conflict.Column, conflict.Sheet ?? "", [], null);
        db.Write((c, tx) =>
        {
            c.Execute("UPDATE conflicts SET resolved_at=@at, resolution=@r WHERE id=@conflictId",
                new { conflictId, at = Clock.Iso(), r = keep == "sheet" && !userOwned ? "keep_sheet" : $"keep_{keep}" }, tx);
            // Baseline = the sheet's value: for keep_system the app's value then differs from it and is written.
            c.Execute("""
                INSERT INTO sheet_sync (user_id, job_id, column_name, last_value) VALUES (@userId,@j,@col,@v)
                ON CONFLICT(user_id, job_id, column_name) DO UPDATE SET last_value=excluded.last_value
                """, new { userId, j = conflict.JobId, col = conflict.Column, v = conflict.Sheet ?? "" }, tx);
            Db.Log(c, tx, userId, actorId, "tracker.resolve", conflict.JobId, new { conflict.Column, keep });
        });
    }

    /// <summary>Appends activity rows not yet exported to the Activity Log sheet.</summary>
    public async Task<int> ExportActivityAsync(string userId, IGoogleWorkspace g, WorkspaceIds ws, CancellationToken ct = default)
    {
        var rows = db.Read(c => c.Query<(long Id, string At, string Actor, string Action, string? Target, string? Detail, string? RunId)>(
            "SELECT id, at, actor, action, target, detail_json, run_id FROM activity WHERE user_id=@userId AND exported=0 ORDER BY id", new { userId }).AsList());
        if (rows.Count == 0)
            return 0;
        await g.AppendRowsAsync(ws.ActivitySheetId, rows.Select(r => (IReadOnlyList<string>)[r.At, r.Actor, r.Action, r.Target ?? "", r.Detail ?? "", r.RunId ?? ""]).ToList(), ct).ConfigureAwait(false);
        var maxId = rows[^1].Id;
        db.Write((c, tx) => c.Execute("UPDATE activity SET exported=1 WHERE user_id=@userId AND id<=@maxId AND exported=0", new { userId, maxId }, tx));
        return rows.Count;
    }
}
