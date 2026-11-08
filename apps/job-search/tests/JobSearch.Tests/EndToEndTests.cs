using Ashlar.Abstractions.Security.Egress;
using Dapper;
using FluentAssertions;
using JobSearch.Accounts;
using JobSearch.Materials;
using JobSearch.Store;
using JobSearch.Workspace;
using Xunit;

namespace JobSearch.Tests;

/// <summary>Two users, real Ashlar workflows, simulated Google and boards. Every scenario the brief asks to be tested.</summary>
public sealed class EndToEndTests
{
    private static long Count(Harness h, string sql, object? args = null) => h.App.Db.Read(c => c.ExecuteScalar<long>(sql, args));

    private static List<T> Query<T>(Harness h, string sql, object? args = null) => h.App.Db.Read(c => c.Query<T>(sql, args).AsList());

    private static string JobId(Harness h, string userId, string title) =>
        h.App.Db.Read(c => c.QuerySingle<string>("SELECT id FROM jobs WHERE user_id=@userId AND title=@title", new { userId, title }));

    private static int RowOf(List<List<string>> rows, string jobId) => rows.FindIndex(r => r.Count > 0 && r[0] == jobId);

    private static string Cell(List<List<string>> rows, int row, string column) => rows[row][rows[0].IndexOf(column)];

    // ── isolation and concurrency ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Two_users_run_concurrently_and_nothing_crosses_between_them()
    {
        using var h = new Harness();
        var (jordan, jordanToken) = h.Jordan();
        var (sam, _) = h.Sam();

        // RunAsync can complete synchronously against the fakes, so run each on the thread pool for real overlap.
        var runs = await Task.WhenAll(Task.Run(() => h.App.Engine.RunAsync(jordan, jordan, "k1")), Task.Run(() => h.App.Engine.RunAsync(sam, sam, "k1")));
        // And once more for Jordan after Sam's jobs certainly exist, so a missing user filter cannot hide behind timing.
        (await h.App.Engine.RunAsync(jordan, jordan, "k2")).Status.Should().Be("succeeded");

        runs.Select(r => r.Status).Should().AllBe("succeeded", string.Join(" | ", runs.Select(r => r.Reason).Concat(runs.SelectMany(r => r.CouldNotComplete))));
        // Both watch the Northwind board; each gets their own rows, filtered by their own targets.
        var jordanTitles = Query<string>(h, "SELECT title FROM jobs WHERE user_id=@jordan", new { jordan });
        var samTitles = Query<string>(h, "SELECT title FROM jobs WHERE user_id=@sam", new { sam });
        jordanTitles.Should().BeEquivalentTo(["Senior Data Engineer", "Data Engineer, Platform", "Analytics Engineer", "Data Engineer (Cleared)"]);
        samTitles.Should().BeEquivalentTo(["Clinical Informatics Nurse", "Nurse Case Manager"]);
        Count(h, "SELECT COUNT(*) FROM jobs j JOIN applications a ON a.job_id=j.id WHERE j.user_id<>a.user_id").Should().Be(0);
        Count(h, "SELECT COUNT(*) FROM documents d JOIN jobs j ON j.id=d.job_id WHERE j.user_id<>d.user_id").Should().Be(0);

        // Drive: each user's files are in their own account only, and the other account cannot see them.
        var jordanFiles = h.Google.FilesOf(Harness.JordanEmail);
        var samFiles = h.Google.FilesOf(Harness.SamEmail);
        jordanFiles.Keys.Should().NotIntersectWith(samFiles.Keys);
        jordanFiles.Values.Where(f => f.Kind == FakeGoogle.Doc).Select(f => f.Content).Should().NotContain(t => t.Contains("Sam Okafor"));
        samFiles.Values.Where(f => f.Kind == FakeGoogle.Doc).Select(f => f.Content).Should().NotContain(t => t.Contains("Jordan Rivera"));
        var samTracker = h.App.Tracker.Get(sam)!.TrackerSheetId;
        var steal = () => h.Google.Client(jordanToken).ReadRowsAsync(samTracker);
        await steal.Should().ThrowAsync<NotFoundException>();
        var jordanRows = h.Google.SheetRows(Harness.JordanEmail, h.App.Tracker.Get(jordan)!.TrackerSheetId);
        jordanRows.Skip(1).Select(r => r[0]).Should().BeEquivalentTo(Query<string>(h, "SELECT id FROM jobs WHERE user_id=@jordan", new { jordan }));
    }

    [Fact]
    public async Task A_second_run_for_the_same_user_is_skipped_and_a_rerun_creates_no_duplicates()
    {
        using var h = new Harness();
        var (jordan, _) = h.Jordan();
        h.App.Accounts.SetPreferences(jordan, jordan, h.App.Accounts.GetPreferences(jordan) with { Materials = new MaterialRules { MaxNewDraftsPerRun = 10 } });

        var both = await Task.WhenAll(Task.Run(() => h.App.Engine.RunAsync(jordan, jordan, "day1")), Task.Run(() => h.App.Engine.RunAsync(jordan, jordan, "day1")));
        both.Count(r => r.Status == "succeeded").Should().Be(1);
        both.Count(r => r.Status == "skipped").Should().Be(1);

        (await h.App.Engine.RunAsync(jordan, jordan, "day1")).Reason.Should().Contain("already succeeded");

        var jobs = Count(h, "SELECT COUNT(*) FROM jobs");
        var docs = Count(h, "SELECT COUNT(*) FROM documents");
        var files = h.Google.FilesOf(Harness.JordanEmail).Count;
        var rows = h.Google.SheetRows(Harness.JordanEmail, h.App.Tracker.Get(jordan)!.TrackerSheetId).Count;
        using (Clock.Pin(() => DateTimeOffset.UtcNow.AddDays(1)))
            (await h.App.Engine.RunAsync(jordan, jordan, "day2")).Status.Should().Be("succeeded");

        Count(h, "SELECT COUNT(*) FROM jobs").Should().Be(jobs);
        Count(h, "SELECT COUNT(*) FROM documents").Should().Be(docs);
        h.Google.FilesOf(Harness.JordanEmail).Count.Should().Be(files, "no draft, folder, sheet or profile doc is duplicated");
        h.Google.SheetRows(Harness.JordanEmail, h.App.Tracker.Get(jordan)!.TrackerSheetId).Count.Should().Be(rows);
    }

    [Fact]
    public async Task A_held_lease_skips_the_run_and_an_expired_lease_is_taken_over()
    {
        using var h = new Harness();
        var (jordan, _) = h.Jordan();
        h.App.Db.Write((c, tx) =>
        {
            c.Execute("INSERT INTO runs (id, user_id, kind, idempotency_key, status, attempt, started_at) VALUES ('run_dead', @jordan, 'search', 'k', 'running', 1, @at)",
                new { jordan, at = Clock.Iso() }, tx);
            c.Execute("INSERT INTO run_locks (user_id, run_id, lease_until) VALUES (@jordan, 'run_dead', @until)", new { jordan, until = Clock.Iso(Clock.Now.AddMinutes(10)) }, tx);
        });

        var blocked = await h.App.Engine.RunAsync(jordan, jordan, "k");
        blocked.Status.Should().Be("skipped");
        blocked.Reason.Should().Contain("holds this user's lease");
        Count(h, "SELECT COUNT(*) FROM jobs").Should().Be(0);

        using (Clock.Pin(() => DateTimeOffset.UtcNow.AddMinutes(20)))
        {
            var resumed = await h.App.Engine.RunAsync(jordan, jordan, "k");
            resumed.Status.Should().Be("succeeded");
            resumed.Attempt.Should().Be(2);
        }
        Query<string>(h, "SELECT status FROM runs WHERE id='run_dead'").Single().Should().Be("abandoned");
        Count(h, "SELECT COUNT(*) FROM run_locks").Should().Be(0);
    }

    [Fact]
    public async Task An_interrupted_run_is_retried_without_duplicating_what_it_had_created()
    {
        using var h = new Harness();
        var (jordan, _) = h.Jordan();
        // Fail after the first editable draft is uploaded but before its database row exists.
        h.App.Accounts.SetPreferences(jordan, jordan, h.App.Accounts.GetPreferences(jordan) with { Boards = ["greenhouse:acme-analytics|Acme Analytics"] });
        // The first readText happens right after the first draft's editable doc is uploaded, before its database row.
        h.Google.InjectFailure(new ProviderException("google", "connection reset", retryable: false), Harness.JordanEmail, "readText");
        var first = await h.App.Engine.RunAsync(jordan, jordan, "k");
        first.Status.Should().Be("partial");
        first.CouldNotComplete.Should().Contain(c => c.Contains("connection reset"));
        var orphanKeys = h.Google.FilesOf(Harness.JordanEmail).Values.Select(f => f.AppKey).Where(k => k!.Contains(":doc:")).ToList();
        orphanKeys.Should().NotBeEmpty("the interrupted step left a Drive file with no database row");

        var second = await h.App.Engine.RunAsync(jordan, jordan, "k");
        second.Attempt.Should().Be(2);
        second.Status.Should().Be("succeeded", string.Join(" | ", second.CouldNotComplete));
        var files = h.Google.FilesOf(Harness.JordanEmail).Values;
        files.GroupBy(f => f.AppKey).Should().OnlyContain(g => g.Count() == 1, "every app key exists once in Drive");
        Query<string>(h, "SELECT idempotency_key FROM documents").Should().Contain(orphanKeys!, "the retry adopted the orphaned file");
        Count(h, "SELECT COUNT(*) FROM documents").Should().Be(Count(h, "SELECT COUNT(DISTINCT idempotency_key) FROM documents"));
    }

    // ── user edits are never overwritten ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task User_edits_in_the_sheet_are_imported_and_never_overwritten()
    {
        using var h = new Harness();
        var (jordan, _) = h.Jordan();
        await h.App.Engine.RunAsync(jordan, jordan, "d1");
        var sheet = h.App.Tracker.Get(jordan)!.TrackerSheetId;
        var job = JobId(h, jordan, "Senior Data Engineer");
        var rows = h.Google.SheetRows(Harness.JordanEmail, sheet);
        var row = RowOf(rows, job);

        h.Google.UserEditCell(Harness.JordanEmail, sheet, row, "Notes", "Recruiter Dana said the team is hiring two.");
        h.Google.UserEditCell(Harness.JordanEmail, sheet, row, "Application status", "applied");
        h.Google.UserEditCell(Harness.JordanEmail, sheet, row, "Title", "Sr Data Engineer (my label)");

        var run = await h.App.Engine.RunAsync(jordan, jordan, "d2");

        run.ImportedEdits.Should().Be(2);
        run.Frozen.Should().Contain(job);
        var app = h.App.Db.Read(c => c.QuerySingle<(string Status, string? Notes, string? Evidence, string? Snap)>(
            "SELECT status, user_notes, status_evidence, submitted_snapshot_json FROM applications WHERE job_id=@job", new { job }));
        app.Status.Should().Be("applied");
        app.Notes.Should().Be("Recruiter Dana said the team is hiring two.");
        app.Evidence.Should().Contain("entered by the user in their tracker sheet");
        app.Snap.Should().Contain("\"items\"");
        h.Google.FilesOf(Harness.JordanEmail).Values.Should().Contain(f => f.Name.Contains("(as submitted)") && f.Revisions.Any(r => r.KeepForever));

        rows = h.Google.SheetRows(Harness.JordanEmail, sheet);
        Cell(rows, row, "Title").Should().Be("Sr Data Engineer (my label)", "an edited system cell is kept, not overwritten");
        Cell(rows, row, "Sync flags").Should().Contain("Conflict in 'Title'");
        Cell(rows, row, "Notes").Should().Be("Recruiter Dana said the team is hiring two.");
        h.App.Tracker.OpenConflicts(jordan).Should().ContainSingle(c => c.Column == "Title");
    }

    [Fact]
    public async Task A_note_changed_in_both_places_is_a_conflict_and_neither_value_is_lost()
    {
        using var h = new Harness();
        var (jordan, _) = h.Jordan();
        await h.App.Engine.RunAsync(jordan, jordan, "d1");
        var sheet = h.App.Tracker.Get(jordan)!.TrackerSheetId;
        var job = JobId(h, jordan, "Analytics Engineer");
        var row = RowOf(h.Google.SheetRows(Harness.JordanEmail, sheet), job);

        h.App.Applications.SetNotes(jordan, jordan, job, "from the app", null, "cli");
        h.Google.UserEditCell(Harness.JordanEmail, sheet, row, "Notes", "from the sheet");
        await h.App.Engine.RunAsync(jordan, jordan, "d2");

        Cell(h.Google.SheetRows(Harness.JordanEmail, sheet), row, "Notes").Should().Be("from the sheet");
        h.App.Db.Read(c => c.QuerySingle<string>("SELECT user_notes FROM applications WHERE job_id=@job", new { job })).Should().Be("from the app");
        var conflict = h.App.Tracker.OpenConflicts(jordan).Single();
        (conflict.SheetValue, conflict.SystemValue).Should().Be(("from the sheet", "from the app"));

        h.App.Tracker.Resolve(jordan, jordan, conflict.Id, "sheet", h.App.Access);
        h.App.Db.Read(c => c.QuerySingle<string>("SELECT user_notes FROM applications WHERE job_id=@job", new { job })).Should().Be("from the sheet");
    }

    [Fact]
    public async Task A_deleted_tracker_row_is_restored_and_flagged_not_lost()
    {
        using var h = new Harness();
        var (jordan, _) = h.Jordan();
        await h.App.Engine.RunAsync(jordan, jordan, "d1");
        var sheet = h.App.Tracker.Get(jordan)!.TrackerSheetId;
        var job = JobId(h, jordan, "Analytics Engineer");
        h.Google.UserDeleteRow(Harness.JordanEmail, sheet, RowOf(h.Google.SheetRows(Harness.JordanEmail, sheet), job));

        await h.App.Engine.RunAsync(jordan, jordan, "d2");

        var rows = h.Google.SheetRows(Harness.JordanEmail, sheet);
        Cell(rows, RowOf(rows, job), "Sync flags").Should().StartWith("Restored");
    }

    // ── evidence for application status; delegated access ─────────────────────────────────────────────────

    [Fact]
    public async Task Nothing_is_marked_applied_without_the_users_evidence_and_a_manager_cannot_assert_it()
    {
        using var h = new Harness();
        var (jordan, _) = h.Jordan();
        var manager = h.App.Accounts.CreateUser("coach@example.com", "Career Coach");
        await h.App.Engine.RunAsync(jordan, jordan, "d1");
        var job = JobId(h, jordan, "Senior Data Engineer");

        Count(h, "SELECT COUNT(*) FROM applications WHERE status<>'not_applied'").Should().Be(0, "a run never asserts an application");
        var noEvidence = () => h.App.Applications.SetStatus(jordan, jordan, job, "applied", null, "cli");
        noEvidence.Should().Throw<ApplicationService.EvidenceRequiredException>();

        var notGranted = () => h.App.Applications.SetNotes(jordan, manager, job, "hi", null, "cli");
        notGranted.Should().Throw<AccessDeniedException>();
        var cannotGrantItself = () => h.App.Access.Grant(jordan, manager, manager, [Scope.EditNotes]);
        cannotGrantItself.Should().Throw<AccessDeniedException>();

        h.App.Access.Grant(jordan, jordan, manager, [Scope.EditNotes, Scope.RunSearch]);
        h.App.Applications.SetNotes(jordan, manager, job, "Coach: tailor the summary", null, "cli");
        var assert = () => h.App.Applications.SetStatus(jordan, manager, job, "applied", "coach says so", "cli");
        assert.Should().Throw<AccessDeniedException>("asserting an application happened is never delegated");
        var undelegable = () => h.App.Access.Grant(jordan, jordan, manager, [Scope.ConnectProvider]);
        undelegable.Should().Throw<AccessDeniedException>();
        Query<string>(h, "SELECT actor FROM activity WHERE user_id=@jordan AND action='application.notes'", new { jordan }).Should().Contain(manager);
    }

    // ── screening, materials and validation through the workflow ─────────────────────────────────────────

    [Fact]
    public async Task Screening_explains_matches_and_gaps_and_never_assumes_eligibility()
    {
        using var h = new Harness();
        var (jordan, _) = h.Jordan();
        var (sam, _) = h.Sam();
        h.App.Profiles.SetField(sam, sam, "clearance_status", "none");
        await h.App.Engine.RunAsync(jordan, jordan, "d1");
        await h.App.Engine.RunAsync(sam, sam, "d1");

        Screening.Assessment A(string user, string title) => JobSearch.Store.Json.Read<Screening.Assessment>(h.App.Db.Read(c => c.QuerySingle<string>(
            "SELECT s.assessment_json FROM assessments s JOIN jobs j ON j.id=s.job_id WHERE j.user_id=@user AND j.title=@title", new { user, title })))!;

        var senior = A(jordan, "Senior Data Engineer");
        senior.Recommendation.Should().Be("strong_fit");
        senior.Evidence.Should().Contain(e => e.Requirement == "airflow" && e.CandidateEvidence!.StartsWith("résumé line"));
        senior.Evidence.Should().Contain(e => e.Requirement == "5+ years");

        var platform = A(jordan, "Data Engineer, Platform");
        platform.Gaps.Select(g => g.Requirement).Should().Contain(["kubernetes", "terraform", "golang"]);
        platform.Notes.Should().Contain(n => n.Contains("must reside in colorado", StringComparison.OrdinalIgnoreCase));

        var cleared = A(jordan, "Data Engineer (Cleared)");
        cleared.Recommendation.Should().Be("conditional_fit");
        cleared.Unknowns.Should().Contain(u => u.Requirement.Contains("secret clearance") && u.Detail.Contains("does not say the employer sponsors"));
        cleared.Evidence.Should().Contain(e => e.Requirement == "U.S. citizenship");

        var analytics = A(jordan, "Analytics Engineer");
        analytics.Notes.Should().Contain(n => n.Contains("does not say which states"));

        var nurse = A(sam, "Clinical Informatics Nurse");
        nurse.Unknowns.Should().Contain(u => u.Requirement == "10% travel", "Sam has not stated a travel limit");
        // "none" does not rule out a job that asks only for the ability to obtain a clearance; it stays a question.
        var caseManager = A(sam, "Nurse Case Manager");
        caseManager.Blockers.Should().BeEmpty();
        caseManager.Unknowns.Should().Contain(u => u.Requirement.StartsWith("public trust clearance") && u.Detail.Contains("ability to obtain"));

        // An ACTIVE requirement does: Jordan states no clearance, so the cleared role is not currently eligible.
        h.App.Profiles.SetField(jordan, jordan, "clearance_status", "none");
        var posting = new Ashlar.WebOperations.Jobs.JobPosting("Civic Ledger", "Data Engineer (Cleared)", "Arlington, VA", null, null, "onsite", null, null, null, null,
            "Requirements\n- Active Secret clearance required\n- Python and SQL", null, new("ashby", "getJob", new Dictionary<string, string>()), true);
        var blocked = Screening.Screener.Assess(posting, h.App.Profiles.Load(jordan)!, h.App.Accounts.GetPreferences(jordan), DateOnly.FromDateTime(DateTime.UtcNow));
        blocked.Recommendation.Should().Be("not_currently_eligible");
        blocked.Blockers.Should().ContainSingle(b => b.Detail.Contains("none"));
    }

    [Fact]
    public async Task Drafts_use_only_verified_facts_validate_and_their_PDFs_extract()
    {
        using var h = new Harness();
        var (jordan, _) = h.Jordan();
        var run = await h.App.Engine.RunAsync(jordan, jordan, "d1");

        run.Drafted.Should().NotBeEmpty();
        run.Drafted.Count.Should().BeLessThanOrEqualTo(3, "the per-run draft limit");
        run.WorkflowLog.Should().Contain(l => l.Contains("jobsearch.validate-materials"));
        var docs = Query<(string JobId, string Kind, string Format, string Drive, string? V)>(h,
            "SELECT job_id, kind, format, drive_file_id, validation_json FROM documents WHERE user_id=@jordan", new { jordan });
        foreach (var d in docs.Where(d => d.Format == "doc"))
        {
            var v = JobSearch.Store.Json.Read<ValidationRecord>(d.V)!;
            v.Passed.Should().BeTrue(string.Join("; ", v.Unsupported.Select(u => u.Claim)));
            v.Pdf!.Passed.Should().BeTrue();
            v.Pdf.Ratio.Should().BeGreaterThanOrEqualTo(0.95);
            v.AtsNote.Should().Contain("No applicant tracking system outcome");
            var text = h.Google.FilesOf(Harness.JordanEmail)[d.Drive].Content;
            text.Should().NotContainAny("Kubernetes", "kubernetes", "Terraform", "Golang");
            if (d.Kind == "cover_letter")
                v.NeedsConfirmation.Should().Contain(n => n.StartsWith("Placeholder to complete"));
        }
    }

    [Fact]
    public async Task Editing_a_draft_flags_its_PDF_stale_and_a_refresh_revalidates_the_users_text()
    {
        using var h = new Harness();
        var (jordan, _) = h.Jordan();
        await h.App.Engine.RunAsync(jordan, jordan, "d1");
        var (jobId, drive) = h.App.Db.Read(c => c.QuerySingle<(string, string)>(
            "SELECT job_id, drive_file_id FROM documents WHERE user_id=@jordan AND kind='resume' AND format='doc' LIMIT 1", new { jordan }));
        var text = h.Google.FilesOf(Harness.JordanEmail)[drive].Content;
        h.Google.UserEditDoc(Harness.JordanEmail, drive, text + "\n- Ran Kubernetes clusters for 200 services");

        var run = await h.App.Engine.RunAsync(jordan, jordan, "d2");
        run.StalePdfs.Should().Contain($"{jobId}:resume");
        var approve = () => h.App.Engine.Approve(jordan, jordan, jobId);
        approve.Should().Throw<InvalidOperationException>().WithMessage("*stale*");

        (await h.App.Engine.RefreshPdfsAsync(jordan, jordan, jobId)).Should().Be(1);
        var v = JobSearch.Store.Json.Read<ValidationRecord>(h.App.Db.Read(c => c.QuerySingle<string>("SELECT validation_json FROM documents WHERE drive_file_id=@drive", new { drive })))!;
        v.Passed.Should().BeFalse();
        v.Unsupported.Should().Contain(u => u.Claim.Contains("Kubernetes"));
        Count(h, "SELECT COUNT(*) FROM documents WHERE job_id=@jobId AND status='stale'", new { jobId }).Should().Be(0);
    }

    // ── listing status, cleanup ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_closed_posting_is_confirmed_archived_and_its_drafts_moved_not_deleted()
    {
        using var h = new Harness();
        var (jordan, _) = h.Jordan();
        await h.App.Engine.RunAsync(jordan, jordan, "d1");
        var job = JobId(h, jordan, "Senior Data Engineer");
        Count(h, "SELECT COUNT(*) FROM documents WHERE job_id=@job AND status='draft'", new { job }).Should().BeGreaterThan(0);
        var filesBefore = h.Google.FilesOf(Harness.JordanEmail).Count;

        h.Boards.Close("greenhouse", "acme-analytics", "4001001");
        using (Clock.Pin(() => DateTimeOffset.UtcNow.AddDays(1)))
            await h.App.Engine.RunAsync(jordan, jordan, "d2");

        var j = h.App.Db.Read(c => c.QuerySingle<(string Status, string Detail, string? Archived)>(
            "SELECT listing_status, listing_status_detail, archived_at FROM jobs WHERE id=@job", new { job }));
        j.Status.Should().Be("closed");
        j.Detail.Should().Contain("absent from its board listing");
        j.Archived.Should().NotBeNull();
        Count(h, "SELECT COUNT(*) FROM documents WHERE job_id=@job AND status='archived'", new { job }).Should().Be(4);
        var archive = h.App.Tracker.Get(jordan)!.Folders[TrackerService.ArchiveFolder];
        h.Google.FilesOf(Harness.JordanEmail).Values.Count(f => f.Parent == archive).Should().Be(4);
        h.Google.FilesOf(Harness.JordanEmail).Count.Should().BeGreaterThanOrEqualTo(filesBefore, "nothing is deleted");
    }

    [Fact]
    public async Task One_missing_read_is_unverified_and_an_unchecked_listing_goes_stale()
    {
        using var h = new Harness();
        var (jordan, _) = h.Jordan();
        // A pasted career-page link with no board behind it: read through the page's schema.org JobPosting.
        h.App.Accounts.SetPreferences(jordan, jordan, h.App.Accounts.GetPreferences(jordan) with { Boards = [], JobLinks = ["https://careers.nobody.test/jobs/1"] });
        static bool Page(HttpRequestMessage r) => r.RequestUri!.Host == "careers.nobody.test";
        h.Boards.Force(Page, System.Net.HttpStatusCode.OK, """
            <html><head><script type="application/ld+json">{"@context":"https://schema.org","@type":"JobPosting","title":"Data Engineer",
            "hiringOrganization":{"@type":"Organization","name":"Nobody Inc"},"description":"<p>Python and SQL.</p>",
            "jobLocation":{"@type":"Place","address":{"addressLocality":"Denver","addressRegion":"CO","addressCountry":"US"}}}</script></head></html>
            """, "text/html");
        await h.App.Engine.RunAsync(jordan, jordan, "d1");
        var job = JobId(h, jordan, "Data Engineer");
        Query<string>(h, "SELECT listing_status FROM jobs WHERE id=@job", new { job }).Single().Should().Be("open");

        // The source stops returning it once: not enough to call it closed (no board listing to corroborate).
        using (Clock.Pin(() => DateTimeOffset.UtcNow.AddDays(1)))
            await h.App.Engine.RunAsync(jordan, jordan, "d2");
        Query<string>(h, "SELECT listing_status FROM jobs WHERE id=@job", new { job }).Single().Should().Be("unverified");

        // Reads failing for 8 days: never re-verified, so it goes stale (not closed).
        for (var day = 2; day <= 9; day++)
        {
            h.Boards.Force(Page, System.Net.HttpStatusCode.InternalServerError, "upstream down", "text/plain");
            using (Clock.Pin(() => DateTimeOffset.UtcNow.AddDays(day)))
                await h.App.Engine.RunAsync(jordan, jordan, $"d{day + 1}");
        }
        Query<string>(h, "SELECT listing_status FROM jobs WHERE id=@job", new { job }).Single().Should().Be("stale");
    }

    // ── failures ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Expired_access_blocks_the_run_marks_the_connection_and_changes_nothing()
    {
        using var h = new Harness();
        var (jordan, token) = h.Jordan();
        h.Google.ExpireToken(token);

        var run = await h.App.Engine.RunAsync(jordan, jordan, "d1");

        run.Status.Should().Be("blocked_auth");
        run.Reason.Should().Contain("reconnect");
        h.App.Accounts.GetConnection(jordan, "google")!.Status.Should().Be("needs_reauth");
        Count(h, "SELECT COUNT(*) FROM jobs").Should().Be(0);
        h.App.Dispatcher.Due(DateTimeOffset.UtcNow.AddDays(1)).Should().NotContain(d => d.User.Id == jordan, "a user who must reconnect is not scheduled");
        Count(h, "SELECT COUNT(*) FROM run_locks").Should().Be(0, "the lease is released");
    }

    [Fact]
    public async Task Google_rate_limits_are_retried_with_backoff_and_a_sites_rate_answer_stops_its_reads()
    {
        using var h = new Harness();
        var (jordan, _) = h.Jordan();
        h.Google.InjectFailure(new RateLimitedException("google", TimeSpan.FromSeconds(3)), Harness.JordanEmail);
        h.Boards.Force(r => r.RequestUri!.AbsolutePath.StartsWith("/v0/postings/northwind/", StringComparison.Ordinal), System.Net.HttpStatusCode.TooManyRequests, "{}");

        var run = await h.App.Engine.RunAsync(jordan, jordan, "d1");

        h.Waits.Should().Contain(TimeSpan.FromSeconds(3));
        run.CouldNotComplete.Should().Contain(c => c.StartsWith("lever: rate limited"));
        run.Status.Should().Be("partial");
    }

    [Fact]
    public async Task A_refused_egress_and_a_broken_board_are_reported_not_hidden()
    {
        using var h = new Harness(apiGuard: new EgressGuard("air-gapped", "enforce"));
        var (jordan, _) = h.Jordan();

        var run = await h.App.Engine.RunAsync(jordan, jordan, "d1");

        run.Boards.Values.Should().OnlyContain(v => v.StartsWith("refused"));
        run.CouldNotComplete.Should().Contain(c => c.Contains("egress guard"));
        h.Boards.Requests.Should().BeEmpty("nothing left the process");
        Count(h, "SELECT COUNT(*) FROM jobs").Should().Be(0);
    }

    // ── schedule, snapshots ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Each_user_is_due_at_eight_in_their_own_time_zone()
    {
        using var h = new Harness();
        var (jordan, _) = h.Jordan();  // America/Denver: 08:00 MST = 15:00Z in November
        var (sam, _) = h.Sam();        // America/New_York: 08:00 EST = 13:00Z
        var day = new DateTimeOffset(2026, 11, 9, 0, 0, 0, TimeSpan.Zero);

        h.App.Dispatcher.Due(day.AddHours(12.9)).Should().BeEmpty();
        h.App.Dispatcher.Due(day.AddHours(13.1)).Select(d => d.User.Id).Should().BeEquivalentTo([sam]);
        h.App.Dispatcher.Due(day.AddHours(15.1)).Select(d => d.User.Id).Should().BeEquivalentTo([sam, jordan]);
        Runs.Dispatcher.NextRun(h.App.Accounts.GetUser(jordan), new Preferences(), day).Should().Be(day.AddHours(15));
    }

    [Fact]
    public async Task Snapshots_commit_only_changes_hold_no_secrets_and_restore_by_reviewed_plan()
    {
        using var h = new Harness();
        var (jordan, token) = h.Jordan();
        await h.App.Engine.RunAsync(jordan, jordan, "d1");
        var repo = Path.Combine(h.Dir, "snap-" + jordan);
        var job = JobId(h, jordan, "Analytics Engineer");

        var first = h.App.Snapshots.Export(jordan, repo);
        first.Should().NotBeNull();
        h.App.Snapshots.Export(jordan, repo).Should().BeNull("nothing changed");
        var all = string.Join("\n", Directory.GetFiles(repo).Select(File.ReadAllText));
        all.Should().NotContain(token).And.NotContain("jordan.rivera@example.com").And.NotContain("(555)");

        h.App.Applications.SetNotes(jordan, jordan, job, "oops, overwrote my note", null, "cli");
        var plan = h.App.Snapshots.RestorePlan(jordan, repo, first!);
        plan.Should().ContainSingle(p => p.JobId == job && p.Field == "user_notes" && p.InSnapshot == "");
        h.App.Snapshots.Push(jordan, repo, "https://github.com/someone/snap.git", confirmedPrivate: false).Pushed.Should().BeFalse();
    }
}
