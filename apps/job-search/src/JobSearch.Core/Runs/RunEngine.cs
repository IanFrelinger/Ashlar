using System.Globalization;
using System.Text.RegularExpressions;
using Ashlar.WebOperations.Jobs;
using Ashlar.Core.Application.Workflows;
using Ashlar.Core.Domain.Bricks;
using Ashlar.Core.Domain.Execution;
using Dapper;
using JobSearch.Accounts;
using JobSearch.Bricks;
using JobSearch.Materials;
using JobSearch.Profile;
using JobSearch.Store;
using JobSearch.Workspace;
using Microsoft.Data.Sqlite;

namespace JobSearch.Runs;

/// <summary>The Ashlar runtime one run uses: a scoped workflow executor and the brick registry it resolves bricks from.</summary>
public interface IAshlarRuntime
{
    /// <summary>A new scope. Each run gets its own, so event streams never mix between users.</summary>
    AshlarScope CreateScope();

    /// <summary>The verified certified bricks.</summary>
    Certified.ICertifiedRules Certified { get; }
}

/// <summary>An Ashlar DI scope with what a run needs.</summary>
public sealed record AshlarScope(WorkflowExecutor Executor, IBrickRegistry Bricks, IDisposable Lifetime) : IDisposable
{
    /// <inheritdoc />
    public void Dispose() => Lifetime.Dispose();
}

/// <summary>What one run checked, changed, created, and could not complete.</summary>
public sealed record RunSummary
{
    /// <summary>Run id (empty when skipped before a run row was created).</summary>
    public string RunId { get; init; } = "";
    /// <summary>The user.</summary>
    public string UserId { get; init; } = "";
    /// <summary>succeeded | partial | blocked_auth | failed | skipped.</summary>
    public string Status { get; set; } = "running";
    /// <summary>Why it was skipped or blocked.</summary>
    public string? Reason { get; set; }
    /// <summary>Attempt number for this idempotency key.</summary>
    public int Attempt { get; init; }
    /// <summary>Per board: postings listed, or the error.</summary>
    public Dictionary<string, string> Boards { get; init; } = new(StringComparer.Ordinal);
    /// <summary>Postings read at the source.</summary>
    public int PostingsRead { get; set; }
    /// <summary>New jobs tracked.</summary>
    public int NewJobs { get; set; }
    /// <summary>Listing status changes: job → from → to.</summary>
    public List<string> StatusChanges { get; init; } = [];
    /// <summary>Jobs that got new drafts.</summary>
    public List<string> Drafted { get; init; } = [];
    /// <summary>PDFs flagged stale because their editable draft changed.</summary>
    public List<string> StalePdfs { get; init; } = [];
    /// <summary>Jobs archived and drafts moved to Archive.</summary>
    public List<string> Archived { get; init; } = [];
    /// <summary>Submitted materials frozen.</summary>
    public List<string> Frozen { get; init; } = [];
    /// <summary>Tracker: imported user edits.</summary>
    public int ImportedEdits { get; set; }
    /// <summary>Tracker: new conflicts.</summary>
    public int NewConflicts { get; set; }
    /// <summary>What could not be completed, and why.</summary>
    public List<string> CouldNotComplete { get; init; } = [];
    /// <summary>Postings left for the next run by the per-run read limit (not a failure).</summary>
    public int Deferred { get; set; }
    /// <summary>One line per workflow node that ran.</summary>
    public List<string> WorkflowLog { get; init; } = [];
}

/// <summary>
/// Runs one user's search. Safeguards:
/// <list type="bullet">
/// <item>Per-user lease in the database: a second run for the same user is skipped while the first holds it; an
/// abandoned lease expires and the next run takes over.</item>
/// <item>Idempotency key per run (the daily key is the user's local date): a key that already succeeded is not run again;
/// a failed, partial or abandoned one is retried as the next attempt.</item>
/// <item>Every Drive file carries an app key and every document row a unique idempotency key, so a retried run finds
/// what an interrupted one created instead of duplicating it.</item>
/// <item>Each user's Drive is reached only through that user's own connection; every query is scoped by user id; each
/// run has its own Ashlar scope.</item>
/// <item>Expired access stops the run as blocked_auth and marks the connection; Google rate limits are retried with
/// backoff; a site's <c>rate</c> answer stops reads from that site for the run. All are recorded.</item>
/// </list>
/// </summary>
public sealed class RunEngine(
    Db db, AccountService accounts, ProfileService profiles, TrackerService tracker,
    IWorkspaceFactory workspaces, IAshlarRuntime ashlar, Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? ((d, ct) => Task.Delay(d, ct));

    /// <summary>How long a lease holds without renewal.</summary>
    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(15);

    /// <summary>The daily idempotency key: the user's local date.</summary>
    public static string DailyKey(User user, DateTimeOffset now) =>
        "daily:" + TimeZoneInfo.ConvertTime(now, TimeZoneInfo.FindSystemTimeZoneById(user.Timezone)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Runs a search for <paramref name="userId"/>.</summary>
    public async Task<RunSummary> RunAsync(string userId, string actorId, string? idempotencyKey = null, CancellationToken ct = default)
    {
        var user = accounts.GetUser(userId);
        var key = idempotencyKey ?? DailyKey(user, Clock.Now);
        var runId = Ids.New("run");

        // 1. Lease + idempotency, atomically.
        var start = db.Write<(string? Skip, int Attempt)>((c, tx) =>
        {
            var held = c.QuerySingleOrDefault<(string RunId, string Until)?>("SELECT run_id, lease_until FROM run_locks WHERE user_id=@userId", new { userId }, tx);
            if (held is { } h && Clock.Parse(h.Until) > Clock.Now)
                return ($"another run ({h.RunId}) holds this user's lease until {h.Until}", 0);
            var previous = c.Query<(string Id, string Status, int Attempt)>(
                "SELECT id, status, attempt FROM runs WHERE user_id=@userId AND idempotency_key=@key ORDER BY attempt DESC", new { userId, key }, tx).AsList();
            if (previous.Any(p => p.Status == "succeeded"))
                return ($"run '{key}' already succeeded", 0);
            foreach (var p in previous.Where(p => p.Status == "running"))
                c.Execute("UPDATE runs SET status='abandoned', finished_at=@at, error='lease expired before the run finished' WHERE id=@id", new { id = p.Id, at = Clock.Iso() }, tx);
            var attempt = previous.Count == 0 ? 1 : previous.Max(p => p.Attempt) + 1;
            c.Execute("INSERT OR REPLACE INTO run_locks (user_id, run_id, lease_until) VALUES (@userId,@runId,@until)", new { userId, runId, until = Clock.Iso(Clock.Now + Lease) }, tx);
            c.Execute("INSERT INTO runs (id, user_id, kind, idempotency_key, status, attempt, started_at) VALUES (@runId,@userId,'search',@key,'running',@attempt,@at)",
                new { runId, userId, key, attempt, at = Clock.Iso() }, tx);
            Db.Log(c, tx, userId, actorId, "run.start", key, new { attempt }, runId);
            return (null, attempt);
        });
        if (start.Skip is { } why)
            return new RunSummary { UserId = userId, Status = "skipped", Reason = why };

        var s = new RunSummary { RunId = runId, UserId = userId, Attempt = start.Attempt };
        try
        {
            await ExecuteAsync(user, runId, s, ct).ConfigureAwait(false);
            s.Status = s.CouldNotComplete.Count == 0 ? "succeeded" : "partial";
        }
        catch (AuthExpiredException ex)
        {
            accounts.MarkConnection(userId, "google", "needs_reauth", ex.Message);
            s.Status = "blocked_auth";
            s.Reason = $"{ex.Message}. The user must reconnect Google Drive; nothing else was changed after this point.";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            s.Status = "failed";
            s.Reason = "cancelled; safe to retry";
        }
#pragma warning disable CA1031 // any other failure is recorded on the run and the lease released; a retry resumes safely
        catch (Exception ex)
#pragma warning restore CA1031
        {
            s.Status = "failed";
            s.Reason = $"{ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            db.Write((c, tx) =>
            {
                c.Execute("UPDATE runs SET status=@status, finished_at=@at, summary_json=@json, error=@reason WHERE id=@runId",
                    new { runId, status = s.Status, at = Clock.Iso(), json = Json.Write(s), reason = s.Reason }, tx);
                c.Execute("DELETE FROM run_locks WHERE user_id=@userId AND run_id=@runId", new { userId, runId }, tx);
                Db.Log(c, tx, userId, "system", "run.finish", s.Status, new { s.Reason, s.NewJobs, s.PostingsRead, drafted = s.Drafted.Count, s.CouldNotComplete }, runId);
            });
        }
        return s;
    }

    private void Renew(string userId, string runId) =>
        db.Write((c, tx) => c.Execute("UPDATE run_locks SET lease_until=@until WHERE user_id=@userId AND run_id=@runId",
            new { userId, runId, until = Clock.Iso(Clock.Now + Lease) }, tx));

    private async Task<T> Retry<T>(Func<Task<T>> work, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await work().ConfigureAwait(false);
            }
            catch (RateLimitedException rl) when (attempt < 3)
            {
                await _delay(rl.RetryAfter * Math.Pow(2, attempt), ct).ConfigureAwait(false);
            }
        }
    }

    private async Task Step(RunSummary s, string name, Func<Task> work, CancellationToken ct)
    {
        try
        {
            await Retry(async () => { await work().ConfigureAwait(false); return 0; }, ct).ConfigureAwait(false);
        }
        catch (ProviderException ex) when (ex is not AuthExpiredException)
        {
            s.CouldNotComplete.Add($"{name}: {ex.Message}");
        }
    }

    private async Task ExecuteAsync(User user, string runId, RunSummary s, CancellationToken ct)
    {
        var userId = user.Id;
        var g = workspaces.ForUser(userId);
        var profile = profiles.Load(userId);
        var prefs = accounts.GetPreferences(userId);
        var ws = await Retry(() => tracker.ProvisionAsync(userId, user.DisplayName, g, ct), ct).ConfigureAwait(false);

        // 2. The user's edits come in first, so nothing below can overwrite them.
        await Step(s, "tracker import", async () =>
        {
            var r = await tracker.SyncAsync(userId, g, ws, runId, ct).ConfigureAwait(false);
            s.ImportedEdits += r.ImportedEdits;
            s.NewConflicts += r.NewConflicts;
            foreach (var jobId in r.NewlyApplied)
                s.Frozen.Add(await FreezeSubmittedAsync(userId, jobId, g, ws, ct).ConfigureAwait(false));
        }, ct).ConfigureAwait(false);

        if (profile is null)
        {
            s.CouldNotComplete.Add("search: no résumé uploaded yet; screening needs one");
        }
        else
        {
            await Step(s, "master résumé", () => PublishMasterAsync(userId, profile, g, ws, ct), ct).ConfigureAwait(false);
            using var scope = ashlar.CreateScope();
            await SearchAsync(user, runId, profile, prefs, g, ws, scope, s, ct).ConfigureAwait(false);
        }
        Renew(userId, runId);

        await Step(s, "staleness check", () => CheckStalenessAsync(userId, g, s, runId, ct), ct).ConfigureAwait(false);
        await Step(s, "cleanup", () => CleanupAsync(userId, prefs, profile?.ResumeSha, g, ws, s, runId, ct), ct).ConfigureAwait(false);
        await Step(s, "tracker write", async () =>
        {
            var r = await tracker.SyncAsync(userId, g, ws, runId, ct).ConfigureAwait(false);
            s.ImportedEdits += r.ImportedEdits;
            s.NewConflicts += r.NewConflicts;
        }, ct).ConfigureAwait(false);
        await Step(s, "activity export", () => tracker.ExportActivityAsync(userId, g, ws, ct), ct).ConfigureAwait(false);
    }

    private async Task PublishMasterAsync(string userId, CandidateProfile p, IGoogleWorkspace g, WorkspaceIds ws, CancellationToken ct)
    {
        var html = new Draft("master", p.ResumeText.Split('\n').Select(l => ("p", l.TrimEnd())).ToList()).Html();
        await g.CreateDocAsync($"Master résumé ({p.ResumeSha[..8]})", ws.Folders[TrackerService.ProfileFolder], html, $"{userId}:master:{p.ResumeSha}", ct).ConfigureAwait(false);
        var lines = new List<(string, string)> { ("h", "Candidate profile"), ("p", "Generated from the app's record; edit with `jobsearch profile`, not here. Unknown means not provided.") };
        lines.AddRange(p.Fields.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f =>
            ("li", $"{f.Key}: {(f.Value.Value is { } v ? v.ToString() : "unknown")} [{f.Value.Status}{(f.Value.Evidence is { } e ? $"; {e}" : "")}]")));
        var profileHash = ResumeParser.Sha256(Json.Write(p.Fields));
        await g.CreateDocAsync($"Candidate profile ({Clock.Now:yyyy-MM-dd})", ws.Folders[TrackerService.ProfileFolder], new Draft("profile", lines).Html(),
            $"{userId}:profile:{profileHash}", ct).ConfigureAwait(false);
    }

    // ── search ───────────────────────────────────────────────────────────────────────────────────────────

    private sealed record Candidate(string Key, string Employer, string? JobId, BoardEntry? Entry);

    private async Task SearchAsync(User user, string runId, CandidateProfile profile, Preferences prefs, IGoogleWorkspace g, WorkspaceIds ws,
        AshlarScope scope, RunSummary s, CancellationToken ct)
    {
        var userId = user.Id;
        var listedOk = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var discovered = new List<Candidate>();

        // Discovery: each board through the jobs.list-board brick.
        var boardBrick = scope.Bricks.GetBrick("jobs.list-board") ?? throw new InvalidOperationException("brick jobs.list-board is not registered in the Ashlar host");
        foreach (var spec in prefs.Boards)
        {
            var input = new BrickInput();
            input.Set("board", spec);
            var output = await boardBrick.ExecuteAsync(input, ImplementationType.Deterministic, new RunContext(userId, runId), ct).ConfigureAwait(false);
            var signal = output.Get<string>("signal");
            var entries = output.Get("entries", (IReadOnlyList<BoardEntry>?)null) ?? [];
            s.Boards[spec] = signal is "ok" or "partial" ? $"{entries.Count} postings ({signal})" : $"{signal}: {output.Get<string>("detail")}";
            if (signal == "ok" && BoardRef.TryParse(spec, out var b, out _))
                listedOk[$"{b!.Site}:{b.Slug}"] = entries.Select(e => e.Source.Key()).ToHashSet(StringComparer.Ordinal);
            if (signal is not ("ok" or "partial"))
                s.CouldNotComplete.Add($"board {spec}: {output.Get<string>("detail")}");
            discovered.AddRange(entries.Where(e => Screening.Screener.TitleRelevant(e.Title, prefs.TargetRoles, prefs.ExcludeKeywords))
                .Select(e => new Candidate(e.Source.Key(), e.Employer, null, e)));
        }
        foreach (var link in prefs.JobLinks)
        {
            if (JobPostings.FromUrl(link) is { } parsed)
                discovered.Add(new Candidate(parsed.Ref.Key(), parsed.Employer ?? "", null, null));
            else
                s.CouldNotComplete.Add($"job link is not an https URL: {link}");
        }

        // Known jobs, and which discovered ones are new.
        var known = db.Read(c => c.Query<(string Id, string SourceRef, string Employer, string Status, string? LastChecked)>(
            "SELECT id, source_ref, employer, listing_status, last_checked_at FROM jobs WHERE user_id=@userId AND archived_at IS NULL", new { userId }).AsList());
        var knownRefs = known.ToDictionary(k => k.SourceRef, k => k.Id, StringComparer.Ordinal);
        // New postings are taken round-robin across boards, so one large board cannot use up the whole read budget.
        var fresh = discovered.Where(d => !knownRefs.ContainsKey(d.Key)).GroupBy(d => d.Key).Select(gp => gp.First())
            .GroupBy(d => d.Key[..d.Key.IndexOf(':', StringComparison.Ordinal)] + (PostingRef.TryParse(d.Key)?.Args.GetValueOrDefault("company") ?? ""))
            .SelectMany(board => board.Select((c, i) => (c, i))).OrderBy(x => x.i).Select(x => x.c).ToList();
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(Clock.Now, TimeZoneInfo.FindSystemTimeZoneById(user.Timezone)).DateTime);
        // Jobs that qualify for drafts but have none for the current résumé (e.g. a run was interrupted mid-draft) are
        // re-queued whatever their last check time; everything else is re-checked about once a day.
        var pendingDrafts = db.Read(c => c.Query<string>("""
            SELECT j.id FROM jobs j JOIN assessments s ON s.job_id=j.id WHERE j.user_id=@userId AND j.archived_at IS NULL AND j.listing_status='open'
            AND s.recommendation IN @draftFor AND NOT EXISTS (SELECT 1 FROM documents d WHERE d.job_id=j.id AND d.resume_sha=@sha AND d.status<>'archived')
            """, new { userId, draftFor = prefs.Materials.DraftFor, sha = profile.ResumeSha }).ToHashSet(StringComparer.Ordinal));
        var recheck = known.Where(k => k.Status != "closed" && (pendingDrafts.Contains(k.Id) || k.LastChecked is null || Clock.Parse(k.LastChecked)!.Value < Clock.Now.AddHours(-20)))
            .OrderBy(k => pendingDrafts.Contains(k.Id) ? 0 : 1).ThenBy(k => k.LastChecked ?? "", StringComparer.Ordinal)
            .Select(k => new Candidate(k.SourceRef, k.Employer, k.Id, null)).ToList();

        // Reads are capped per run so traffic stays at human volume; the rest wait for the next run.
        var cap = Math.Max(1, prefs.MaxPostingReadsPerRun);
        var queue = recheck.Take(cap / 2).Concat(fresh).Concat(recheck.Skip(cap / 2)).Take(cap).ToList();
        var deferred = recheck.Count + fresh.Count - queue.Count;
        // Deferral is the budget working, not a failure: the run can still succeed, and the rest waits for tomorrow.
        s.Deferred = Math.Max(0, deferred);

        var draftBudget = prefs.Materials.MaxNewDraftsPerRun;
        var rateLimitedSites = new HashSet<string>(StringComparer.Ordinal);
        foreach (var cand in queue)
        {
            ct.ThrowIfCancellationRequested();
            var site = PostingRef.TryParse(cand.Key)?.Site ?? "unknown";
            if (rateLimitedSites.Contains(site))
                continue;
            var allowDraft = draftBudget > 0 && (cand.JobId is null || !HasCurrentDrafts(userId, cand.JobId, profile.ResumeSha));
            var ctx = new CandidateContext
            {
                UserId = userId, Fields = profile.Fields.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal),
                ResumeText = profile.ResumeText, ResumeSha = profile.ResumeSha, Preferences = prefs, AllowDraft = allowDraft,
                Today = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            };
            var outcome = await PostingWorkflow.RunAsync(scope.Executor, cand.Key, cand.Employer, ctx, ct).ConfigureAwait(false);
            s.PostingsRead++;
            lock (s.WorkflowLog) s.WorkflowLog.AddRange(outcome.NodeLog.Select(l => $"{cand.Key} {l}"));
            if (outcome.Signal == "rate")
            {
                rateLimitedSites.Add(site);
                s.CouldNotComplete.Add($"{site}: rate limited by the source; remaining {site} reads wait for the next run ({outcome.Detail})");
            }
            var jobId = Persist(userId, runId, cand, outcome, listedOk, s);
            if (jobId is not null && outcome is { Drafted: true, Resume: not null, CoverLetter: not null, ResumePdf: not null, CoverPdf: not null })
            {
                await Step(s, $"drafts for {jobId}", async () =>
                {
                    await PublishDraftsAsync(userId, jobId, profile.ResumeSha, outcome, g, ws, ct).ConfigureAwait(false);
                    s.Drafted.Add(jobId);
                }, ct).ConfigureAwait(false);
                draftBudget--;
            }
            Renew(userId, runId);
        }
    }

    private bool HasCurrentDrafts(string userId, string jobId, string resumeSha) =>
        db.Read(c => c.ExecuteScalar<long>("SELECT COUNT(*) FROM documents WHERE user_id=@userId AND job_id=@jobId AND resume_sha=@resumeSha AND status<>'archived'",
            new { userId, jobId, resumeSha })) > 0;

    private static readonly Regex NonWord = new("[^a-z0-9]+", RegexOptions.None, TimeSpan.FromSeconds(1));

    private static string Norm(string? s) => NonWord.Replace((s ?? "").ToLowerInvariant(), " ").Trim();

    /// <summary>Canonical posting URL for deduplication: scheme and host lowercased, no query or fragment, no trailing slash.</summary>
    public static string? NormUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) ? $"{u.Scheme}://{u.Host.ToLowerInvariant()}{u.AbsolutePath.TrimEnd('/')}" : null;

    /// <summary>Records the outcome: dedupes and upserts the job, its check, listing status and assessment. Returns the job id.</summary>
    private string? Persist(string userId, string runId, Candidate cand, PostingOutcome o, Dictionary<string, HashSet<string>> listedOk, RunSummary s)
    {
        var p = o.Posting;
        var employer = p?.Employer ?? cand.Entry?.Employer ?? cand.Employer;
        var title = p?.Title ?? cand.Entry?.Title;
        if (title is null && cand.JobId is null)
            return null; // a pasted link that could not be read: nothing to track yet
        var url = p?.Url ?? cand.Entry?.Url;
        var reqId = p?.RequisitionId;
        var now = Clock.Iso();

        return db.Write((c, tx) =>
        {
            // Deduplicate: same source posting; same employer + requisition; same URL; else same employer + title + location.
            var id = cand.JobId
                ?? c.QuerySingleOrDefault<string>("SELECT id FROM jobs WHERE user_id=@userId AND source_ref=@k", new { userId, k = cand.Key }, tx)
                ?? (reqId is null ? null : c.QuerySingleOrDefault<string>("SELECT id FROM jobs WHERE user_id=@userId AND employer_norm=@e AND req_id=@reqId",
                    new { userId, e = Norm(employer), reqId }, tx))
                ?? (NormUrl(url) is { } nu ? c.QuerySingleOrDefault<string>("SELECT id FROM jobs WHERE user_id=@userId AND url_norm=@nu", new { userId, nu }, tx) : null);
            var dedupeKey = reqId is not null ? $"req:{Norm(employer)}:{reqId}" : NormUrl(url) is { } u2 ? $"url:{u2}" : $"ttl:{Norm(employer)}:{Norm(title)}:{Norm(p?.Location ?? cand.Entry?.Location)}";
            var isNew = id is null;
            if (id is null)
            {
                id = Ids.New("job");
                var inserted = c.Execute("""
                    INSERT INTO jobs (id, user_id, dedupe_key, employer, employer_norm, title, title_norm, req_id, url, url_norm, source_ref,
                        location, work_arrangement, pay_text, posted_date, description, listing_status, first_seen_at)
                    VALUES (@id,@userId,@dedupeKey,@employer,@en,@title,@tn,@reqId,@url,@nu,@k,@loc,@arr,@pay,@posted,@desc,'unverified',@now)
                    ON CONFLICT(user_id, dedupe_key) DO NOTHING
                    """, new
                {
                    id, userId, dedupeKey, employer, en = Norm(employer), title, tn = Norm(title), reqId, url, nu = NormUrl(url), k = cand.Key,
                    loc = p?.Location ?? cand.Entry?.Location, arr = p?.WorkArrangement ?? cand.Entry?.WorkArrangement, pay = p?.PayText ?? cand.Entry?.PayText,
                    posted = p?.PostedDate ?? cand.Entry?.PostedDate, desc = p?.Description, now,
                }, tx);
                if (inserted == 0)
                {
                    id = c.QuerySingle<string>("SELECT id FROM jobs WHERE user_id=@userId AND dedupe_key=@dedupeKey", new { userId, dedupeKey }, tx);
                    isNew = false;
                }
                else
                {
                    c.Execute("INSERT INTO applications (job_id, user_id, status, status_set_by, status_set_at, updated_at) VALUES (@id,@userId,'not_applied','system',@now,@now)",
                        new { id, userId, now }, tx);
                    s.NewJobs++;
                    Db.Log(c, tx, userId, "system", "job.new", id, new { employer, title, source = cand.Key }, runId);
                }
            }
            if (p is not null && !isNew)
            {
                c.Execute("""
                    UPDATE jobs SET title=@title, title_norm=@tn, req_id=COALESCE(@reqId, req_id), url=COALESCE(@url, url), url_norm=COALESCE(@nu, url_norm),
                        location=@loc, work_arrangement=@arr, pay_text=@pay, posted_date=COALESCE(@posted, posted_date), description=@desc
                    WHERE id=@id AND user_id=@userId
                    """, new { id, userId, title = p.Title, tn = Norm(p.Title), reqId, url, nu = NormUrl(url), loc = p.Location, arr = p.WorkArrangement, pay = p.PayText, posted = p.PostedDate, desc = p.Description }, tx);
            }

            // The check, and what it means for the listing.
            var previousCheck = c.QuerySingleOrDefault<string>("SELECT result FROM job_checks WHERE job_id=@id ORDER BY id DESC LIMIT 1", new { id }, tx);
            var result = o.Signal switch { "found" => "found", "gone" => "gone", _ => "error" };
            c.Execute("INSERT INTO job_checks (user_id, job_id, run_id, checked_at, source, result, detail) VALUES (@userId,@id,@runId,@now,@src,@result,@detail)",
                new { userId, id, runId, now, src = $"web-operations:{cand.Key}", result, detail = o.Detail }, tx);

            var current = c.QuerySingle<(string Status, string? Verified)>("SELECT listing_status, last_verified_open_at FROM jobs WHERE id=@id", new { id }, tx);
            string status = current.Status, detail;
            string? verified = current.Verified;
            var read = PostingRef.TryParse(cand.Key);
            var board = read is not null && read.Args.TryGetValue("company", out var slug) ? $"{read.Site}:{slug}" : ""; // e.g. greenhouse:acme
            switch (o.Signal)
            {
                case "found" when o.ProvesOpen:
                    (status, detail, verified) = ("open", $"verified at the employer's source on {now[..10]}", now);
                    break;
                case "found":
                    (status, detail) = ("unverified", "listed at the source, but the source does not prove it is open");
                    break;
                case "gone":
                    var absentFromBoard = listedOk.TryGetValue(board, out var refs) && !refs.Contains(cand.Key);
                    if (absentFromBoard || previousCheck == "gone")
                        (status, detail) = ("closed", $"confirmed closed: the employer's source no longer has it ({(absentFromBoard ? "absent from its board listing" : "two consecutive checks")}), {now[..10]}");
                    else
                        (status, detail) = ("unverified", "the employer's source did not return it; will confirm on the next check");
                    break;
                default:
                    detail = $"not checked this run: {o.Detail}";
                    break;
            }
            if (status != current.Status)
                s.StatusChanges.Add($"{id}: {current.Status} → {status}");
            c.Execute("UPDATE jobs SET listing_status=@status, listing_status_detail=@detail, last_checked_at=@now, last_verified_open_at=@verified WHERE id=@id AND user_id=@userId",
                new { id, userId, status, detail, now, verified }, tx);
            if (status != current.Status)
                Db.Log(c, tx, userId, "system", "job.listing_status", id, new { from = current.Status, to = status, detail }, runId);

            if (o.Assessment is { } a)
            {
                c.Execute("""
                    INSERT INTO assessments (job_id, user_id, recommendation, score, assessment_json, assessed_at) VALUES (@id,@userId,@rec,@score,@json,@now)
                    ON CONFLICT(job_id) DO UPDATE SET recommendation=excluded.recommendation, score=excluded.score, assessment_json=excluded.assessment_json, assessed_at=excluded.assessed_at
                    """, new { id, userId, rec = a.Recommendation, score = a.Score, json = Json.Write(a), now }, tx);
                if (a.Requirements.Deadline is { } dl)
                    c.Execute("UPDATE jobs SET deadline=@dl WHERE id=@id AND user_id=@userId", new { id, userId, dl }, tx);
            }
            return id;
        });
    }

    // ── materials ────────────────────────────────────────────────────────────────────────────────────────

    private async Task PublishDraftsAsync(string userId, string jobId, string resumeSha, PostingOutcome o, IGoogleWorkspace g, WorkspaceIds ws, CancellationToken ct)
    {
        var p = o.Posting!;
        var folder = await g.EnsureFolderAsync($"{p.Employer} — {p.Title} ({jobId[^6..]})", ws.Folders[TrackerService.ApplicationsFolder], $"{userId}:job:{jobId}", ct).ConfigureAwait(false);
        var v = resumeSha[..12];
        foreach (var (kind, draft, pdf, validation) in new[] { ("resume", o.Resume!, o.ResumePdf!, o.ResumeValidation!), ("cover_letter", o.CoverLetter!, o.CoverPdf!, o.CoverValidation!) })
        {
            var docKey = $"{userId}:{jobId}:{kind}:doc:{v}";
            var pdfKey = $"{userId}:{jobId}:{kind}:pdf:{v}";
            var label = kind == "resume" ? "Résumé" : "Cover letter";
            var docFile = await g.CreateDocAsync($"{label} — {p.Employer} — {p.Title} (editable)", folder, draft.Html(), docKey, ct).ConfigureAwait(false);
            var docSha = ResumeParser.Sha256(await g.ReadTextAsync(docFile, ct).ConfigureAwait(false));
            var pdfFile = await g.CreateFileAsync($"{label} — {p.Employer} — {p.Title}.pdf", folder, pdf, "application/pdf", pdfKey, ct).ConfigureAwait(false);
            var pdfSha = ResumeParser.Sha256(Convert.ToBase64String(pdf));
            db.Write((c, tx) =>
            {
                var docId = c.QuerySingleOrDefault<string>("SELECT id FROM documents WHERE idempotency_key=@docKey", new { docKey }, tx) ?? Ids.New("doc");
                c.Execute("""
                    INSERT INTO documents (id, user_id, job_id, kind, format, idempotency_key, drive_file_id, content_sha, resume_sha, status, validation_json, created_at, updated_at)
                    VALUES (@docId,@userId,@jobId,@kind,'doc',@docKey,@docFile,@docSha,@resumeSha,'draft',@vj,@now,@now) ON CONFLICT(idempotency_key) DO NOTHING
                    """, new { docId, userId, jobId, kind, docKey, docFile, docSha, resumeSha, vj = Json.Write(validation), now = Clock.Iso() }, tx);
                c.Execute("""
                    INSERT INTO documents (id, user_id, job_id, kind, format, idempotency_key, drive_file_id, content_sha, source_doc_id, source_content_sha, resume_sha, status, validation_json, created_at, updated_at)
                    VALUES (@id,@userId,@jobId,@kind,'pdf',@pdfKey,@pdfFile,@pdfSha,@docId,@docSha,@resumeSha,'draft',@vj,@now,@now) ON CONFLICT(idempotency_key) DO NOTHING
                    """, new { id = Ids.New("doc"), userId, jobId, kind, pdfKey, pdfFile, pdfSha, docId, docSha, resumeSha, vj = Json.Write(validation.Pdf), now = Clock.Iso() }, tx);
                Db.Log(c, tx, userId, "system", "materials.draft", jobId, new { kind, docFile, pdfFile, passed = validation.Passed, origin = validation.Origin, certified = validation.Certified?.Passed, pdfText = validation.Pdf?.Ratio, needsConfirmation = validation.NeedsConfirmation.Count });
            });
        }
    }

    private sealed record DocRef(string Id, string JobId, string Kind, string Format, string DriveFileId, string ContentSha, string? SourceDocId, string? SourceContentSha, string Status, string ResumeSha);

    private async Task CheckStalenessAsync(string userId, IGoogleWorkspace g, RunSummary s, string runId, CancellationToken ct)
    {
        var docs = db.Read(c => c.Query<DocRef>("SELECT id, job_id, kind, format, drive_file_id, content_sha, source_doc_id, source_content_sha, status, resume_sha FROM documents WHERE user_id=@userId AND status IN ('draft','stale')", new { userId }).AsList());
        foreach (var doc in docs.Where(d => d.Format == "doc"))
        {
            string sha;
            try
            {
                sha = ResumeParser.Sha256(await g.ReadTextAsync(doc.DriveFileId, ct).ConfigureAwait(false));
            }
            catch (NotFoundException)
            {
                s.CouldNotComplete.Add($"{doc.JobId} {doc.Kind}: the editable draft is missing from Drive (moved out of the app's reach or trashed)");
                continue;
            }
            foreach (var pdf in docs.Where(d => d.Format == "pdf" && d.SourceDocId == doc.Id && d.Status == "draft" && d.SourceContentSha != sha))
            {
                db.Write((c, tx) =>
                {
                    c.Execute("UPDATE documents SET status='stale', updated_at=@at WHERE id=@id AND user_id=@userId", new { id = pdf.Id, userId, at = Clock.Iso() }, tx);
                    Db.Log(c, tx, userId, "system", "materials.pdf_stale", pdf.JobId, new { pdf.Kind, reason = "the editable draft changed after this PDF was made" }, runId);
                });
                s.StalePdfs.Add($"{pdf.JobId}:{pdf.Kind}");
            }
        }
    }

    /// <summary>
    /// Re-renders stale PDFs from the user's current editable text and re-validates it (an edit can add a claim the
    /// résumé does not support; that is flagged, not blocked). Drive keeps the previous PDF as a revision.
    /// </summary>
    public async Task<int> RefreshPdfsAsync(string userId, string actorId, string jobId, CancellationToken ct = default)
    {
        new AccessControl(db).Authorize(actorId, userId, Scope.ManageMaterials);
        var g = workspaces.ForUser(userId);
        var profile = profiles.Load(userId) ?? throw new InvalidOperationException("no résumé");
        var docs = db.Read(c => c.Query<DocRef>("SELECT id, job_id, kind, format, drive_file_id, content_sha, source_doc_id, source_content_sha, status, resume_sha FROM documents WHERE user_id=@userId AND job_id=@jobId AND status IN ('draft','stale')", new { userId, jobId }).AsList());
        var assessment = Json.Read<Screening.Assessment>(db.Read(c => c.QuerySingleOrDefault<string>("SELECT assessment_json FROM assessments WHERE job_id=@jobId AND user_id=@userId", new { jobId, userId }))) ?? new Screening.Assessment();
        var job = db.Read(c => c.QuerySingle<(string Employer, string Title, string? Description)>("SELECT employer, title, description FROM jobs WHERE id=@jobId AND user_id=@userId", new { jobId, userId }));
        var posting = new JobPosting(job.Employer, job.Title, null, null, null, null, null, null, null, null, job.Description ?? "", null, new PostingRef("user", "draft", new Dictionary<string, string>()), false);
        var refreshed = 0;
        foreach (var pdf in docs.Where(d => d.Format == "pdf" && d.Status == "stale"))
        {
            var editable = docs.First(d => d.Id == pdf.SourceDocId);
            var text = await g.ReadTextAsync(editable.DriveFileId, ct).ConfigureAwait(false);
            var draft = new Draft(pdf.Kind, text.Split('\n').Select(l => l.StartsWith("- ", StringComparison.Ordinal) ? ("li", l[2..]) : ("p", l)).ToList());
            var bytes = Pdf.Render(draft, $"{pdf.Kind} — {job.Employer} — {job.Title}");
            var validation = Validator.WithCertified(Validator.Validate(draft, profile, posting, assessment, Pdf.CheckExtractable(bytes, draft.Text)),
                await Certified.CertifiedClaims.CheckAsync(ashlar.Certified, draft, profile, posting, ct).ConfigureAwait(false));
            await g.UpdateFileAsync(pdf.DriveFileId, bytes, "application/pdf", ct).ConfigureAwait(false);
            var sha = ResumeParser.Sha256(text);
            db.Write((c, tx) =>
            {
                c.Execute("UPDATE documents SET status='draft', source_content_sha=@sha, content_sha=@pdfSha, validation_json=@vp, user_reviewed_at=NULL, updated_at=@at WHERE id=@id",
                    new { id = pdf.Id, sha, pdfSha = ResumeParser.Sha256(Convert.ToBase64String(bytes)), vp = Json.Write(validation.Pdf), at = Clock.Iso() }, tx);
                c.Execute("UPDATE documents SET content_sha=@sha, validation_json=@v, user_reviewed_at=NULL, updated_at=@at WHERE id=@id",
                    new { id = editable.Id, sha, v = Json.Write(validation), at = Clock.Iso() }, tx);
                Db.Log(c, tx, userId, actorId, "materials.pdf_refresh", jobId, new { pdf.Kind, passed = validation.Passed, unsupported = validation.Unsupported.Select(u => u.Claim) });
            });
            refreshed++;
        }
        return refreshed;
    }

    /// <summary>The user's review of a job's materials (required before the tracker calls them ready).</summary>
    public void Approve(string userId, string actorId, string jobId)
    {
        new AccessControl(db).Authorize(actorId, userId, Scope.ManageMaterials);
        db.Write((c, tx) =>
        {
            var stale = c.ExecuteScalar<long>("SELECT COUNT(*) FROM documents WHERE user_id=@userId AND job_id=@jobId AND status='stale'", new { userId, jobId }, tx);
            if (stale > 0)
                throw new InvalidOperationException("a PDF is stale; refresh it (`jobsearch refresh-pdf`) and review again");
            var n = c.Execute("UPDATE documents SET user_reviewed_at=@at WHERE user_id=@userId AND job_id=@jobId AND status='draft'", new { userId, jobId, at = Clock.Iso() }, tx);
            Db.Log(c, tx, userId, actorId, "materials.reviewed", jobId, new { documents = n });
        });
    }

    /// <summary>
    /// Preserves the exact materials in the workspace when the user records an application: frozen copies in
    /// Submitted, the PDF revision kept forever where Drive supports it, and a snapshot record with hashes and any
    /// warning (stale PDF, not reviewed, validation not passed). The system records what was there; it never submits.
    /// </summary>
    public async Task<string> FreezeSubmittedAsync(string userId, string jobId, IGoogleWorkspace g, WorkspaceIds ws, CancellationToken ct)
    {
        var docs = db.Read(c => c.Query<(string Id, string Kind, string Format, string DriveFileId, string ContentSha, string Status, string? Reviewed, string? Validation)>(
            "SELECT id, kind, format, drive_file_id, content_sha, status, user_reviewed_at, validation_json FROM documents WHERE user_id=@userId AND job_id=@jobId AND status IN ('draft','stale','submitted')",
            new { userId, jobId }).AsList());
        var job = db.Read(c => c.QuerySingle<(string Employer, string Title)>("SELECT employer, title FROM jobs WHERE id=@jobId AND user_id=@userId", new { jobId, userId }));
        var folder = await g.EnsureFolderAsync($"{job.Employer} — {job.Title} — applied {Clock.Now:yyyy-MM-dd}", ws.Folders[TrackerService.SubmittedFolder], $"{userId}:submitted:{jobId}", ct).ConfigureAwait(false);
        var items = new List<object>();
        var warnings = new List<string>();
        foreach (var d in docs)
        {
            var meta = await g.MetaAsync(d.DriveFileId, ct).ConfigureAwait(false);
            var copy = await g.CopyAsync(d.DriveFileId, $"{meta.Name} (as submitted)", folder, $"{userId}:submitted:{d.Id}", ct).ConfigureAwait(false);
            var revision = d.Format == "pdf" ? await g.KeepRevisionForeverAsync(copy, ct).ConfigureAwait(false) : null;
            if (d.Status == "stale") warnings.Add($"{d.Kind} PDF was stale (its editable draft had changed) when the application was recorded");
            if (d.Reviewed is null && d.Status != "submitted") warnings.Add($"{d.Kind} {d.Format} had not been marked reviewed");
            if (d.Format == "doc" && Json.Read<ValidationRecord>(d.Validation) is { Passed: false }) warnings.Add($"{d.Kind} had unsupported claims at its last validation");
            items.Add(new { d.Kind, d.Format, original = d.DriveFileId, copy, revision, d.ContentSha, version = meta.Version });
        }
        if (docs.Count == 0) warnings.Add("no app-generated materials existed for this job; nothing was copied");
        db.Write((c, tx) =>
        {
            c.Execute("UPDATE documents SET status='submitted', updated_at=@at WHERE user_id=@userId AND job_id=@jobId AND status IN ('draft','stale')", new { userId, jobId, at = Clock.Iso() }, tx);
            c.Execute("UPDATE applications SET submitted_snapshot_json=@snap WHERE job_id=@jobId AND user_id=@userId",
                new { jobId, userId, snap = Json.Write(new { frozenAt = Clock.Iso(), folder, items, warnings }) }, tx);
            Db.Log(c, tx, userId, "system", "materials.frozen", jobId, new { folder, files = items.Count, warnings });
        });
        return jobId;
    }

    /// <summary>Freezes materials after a status set outside a run (CLI).</summary>
    public async Task FreezeSubmittedAsync(string userId, string jobId, CancellationToken ct = default)
    {
        var g = workspaces.ForUser(userId);
        var ws = tracker.Get(userId) ?? throw new InvalidOperationException("no workspace yet; run a search first");
        await FreezeSubmittedAsync(userId, jobId, g, ws, ct).ConfigureAwait(false);
    }

    // ── cleanup ──────────────────────────────────────────────────────────────────────────────────────────

    private async Task CleanupAsync(string userId, Preferences prefs, string? resumeSha, IGoogleWorkspace g, WorkspaceIds ws, RunSummary s, string runId, CancellationToken ct)
    {
        var staleBefore = Clock.Iso(Clock.Now.AddDays(-prefs.Screening.StaleAfterDays));
        db.Write((c, tx) =>
        {
            foreach (var (id, status) in c.Query<(string, string)>("""
                SELECT id, listing_status FROM jobs WHERE user_id=@userId AND archived_at IS NULL AND listing_status IN ('open','unverified')
                AND COALESCE(last_verified_open_at, first_seen_at) < @staleBefore
                """, new { userId, staleBefore }, tx).AsList())
            {
                c.Execute("UPDATE jobs SET listing_status='stale', listing_status_detail=@d WHERE id=@id",
                    new { id, d = $"not verified open in the last {prefs.Screening.StaleAfterDays} days" }, tx);
                s.StatusChanges.Add($"{id}: {status} → stale");
                Db.Log(c, tx, userId, "system", "job.listing_status", id, new { from = status, to = "stale" }, runId);
            }
        });

        // Closed listings the user has not applied to are archived; anything with application history is kept in place.
        var toArchive = db.Read(c => c.Query<string>("""
            SELECT j.id FROM jobs j JOIN applications a ON a.job_id=j.id
            WHERE j.user_id=@userId AND j.archived_at IS NULL AND j.listing_status='closed' AND a.status IN ('not_applied','preparing','not_pursuing')
            """, new { userId }).AsList());
        var drafts = db.Read(c => c.Query<(string Id, string JobId, string DriveFileId, string ResumeSha, string? Rec)>("""
            SELECT d.id, d.job_id, d.drive_file_id, d.resume_sha, s.recommendation FROM documents d LEFT JOIN assessments s ON s.job_id=d.job_id
            JOIN applications a ON a.job_id=d.job_id WHERE d.user_id=@userId AND d.status IN ('draft','stale') AND a.status IN ('not_applied','preparing','not_pursuing')
            """, new { userId }).AsList());
        // Obsolete unsubmitted drafts: the job is being archived, the résumé they came from was replaced, or the job no longer qualifies.
        foreach (var d in drafts.Where(d => toArchive.Contains(d.JobId) || (resumeSha is not null && d.ResumeSha != resumeSha) || d.Rec == "not_currently_eligible"))
        {
            await g.MoveAsync(d.DriveFileId, ws.Folders[TrackerService.ArchiveFolder], ct).ConfigureAwait(false);
            db.Write((c, tx) =>
            {
                c.Execute("UPDATE documents SET status='archived', updated_at=@at WHERE id=@id AND user_id=@userId", new { id = d.Id, userId, at = Clock.Iso() }, tx);
                Db.Log(c, tx, userId, "system", "materials.archived", d.JobId, new { file = d.DriveFileId, reason = toArchive.Contains(d.JobId) ? "listing closed" : d.ResumeSha != resumeSha ? "résumé replaced" : "no longer eligible" }, runId);
            });
        }
        foreach (var id in toArchive)
        {
            db.Write((c, tx) =>
            {
                c.Execute("UPDATE jobs SET archived_at=@at WHERE id=@id AND user_id=@userId", new { id, userId, at = Clock.Iso() }, tx);
                Db.Log(c, tx, userId, "system", "job.archived", id, new { reason = "confirmed closed; no application recorded" }, runId);
            });
            s.Archived.Add(id);
        }
    }
}
