using System.Text;
using System.Text.Json;
using Ashlar.Abstractions.Security.Egress;
using Ashlar.WebOperations;
using JobSearch.Store;
using JobSearch.Workspace;

#pragma warning disable ASHLAREXP002 // EgressGuard is the experimental egress surface; the demo constructs a report-mode guard so the real egress check runs.

namespace JobSearch.Host;

/// <summary>
/// End to end with no network and no credentials: two synthetic users, simulated Google accounts, synthetic boards
/// answered at their public API endpoints through the real web-operations client, specs and egress check, real Ashlar workflows, real PDFs.
/// </summary>
public static class Demo
{
    /// <summary>Runs the demo and writes what each user would see in Drive to <paramref name="outDir"/>.</summary>
    public static async Task<int> RunAsync(string outDir)
    {
        var fixtures = Path.Combine(AppContext.BaseDirectory, "fixtures");
        var data = Path.Combine(Path.GetTempPath(), "jobsearch-demo-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(outDir);
        var google = new FakeGoogle();
        var boards = new StubBoards(Path.Combine(fixtures, "boards.json"));
        var client = new WebOperationClient(new SpecStore(Path.Combine(data, "specs")), new EgressGuard("full", "report"), boards,
            new WebOperationsOptions { PerHostInterval = TimeSpan.Zero }, now: () => Clock.Now);
        using var app = new App(new AppOptions { DataDir = data, GoogleMode = "fake", Fake = google, Web = client });

        Console.WriteLine("== 1. Two accounts, each with their own résumé, goals and Google connection");
        var jordan = app.Accounts.CreateUser("jordan.rivera@example.com", "Jordan Rivera", "America/Denver");
        app.Profiles.StoreResume(jordan, jordan, "jordan-rivera.txt", await File.ReadAllTextAsync(Path.Combine(fixtures, "resumes", "jordan-rivera.txt")).ConfigureAwait(false));
        app.Profiles.SetField(jordan, jordan, "work_authorization", "US citizen");
        app.Profiles.SetField(jordan, jordan, "requires_sponsorship", "no");
        app.Accounts.SetPreferences(jordan, jordan, app.Accounts.GetPreferences(jordan) with
        {
            TargetRoles = ["data engineer", "analytics engineer"], Locations = ["Denver, CO"], WorkArrangements = ["remote", "hybrid"], SalaryMin = 130000,
            Boards = ["greenhouse:acme-analytics|Acme Analytics", "lever:northwind|Northwind Health", "ashby:civic-ledger|Civic Ledger"],
        });
        app.ConnectFakeGoogle(jordan, "jordan.rivera@example.com");

        var sam = app.Accounts.CreateUser("sam.okafor@example.com", "Sam Okafor", "America/New_York");
        app.Profiles.StoreResume(sam, sam, "sam-okafor.txt", await File.ReadAllTextAsync(Path.Combine(fixtures, "resumes", "sam-okafor.txt")).ConfigureAwait(false));
        app.Accounts.SetPreferences(sam, sam, app.Accounts.GetPreferences(sam) with
        {
            TargetRoles = ["informatics nurse", "nurse case manager"], Locations = ["Columbus, OH"],
            Boards = ["lever:northwind|Northwind Health", "ashby:civic-ledger|Civic Ledger"],
        });
        app.ConnectFakeGoogle(sam, "sam.okafor@example.com");
        Console.WriteLine($"   Jordan {jordan} (America/Denver), Sam {sam} (America/New_York). Sam's work authorization and clearance are left unknown.");

        Console.WriteLine("== 2. Both searches run at the same time");
        var day1 = await Task.WhenAll(app.Engine.RunAsync(jordan, jordan, "demo-day-1"), app.Engine.RunAsync(sam, sam, "demo-day-1")).ConfigureAwait(false);
        foreach (var r in day1) Summarize(app, r);

        Console.WriteLine("== 3. Day 2: Jordan edits the tracker sheet; an employer closes a posting");
        var ws = app.Tracker.Get(jordan)!;
        var rows = google.SheetRows("jordan.rivera@example.com", ws.TrackerSheetId);
        var senior = rows.FindIndex(r => r.Count > 2 && r[2] == "Senior Data Engineer");
        google.UserEditCell("jordan.rivera@example.com", ws.TrackerSheetId, senior, "Application status", "applied");
        google.UserEditCell("jordan.rivera@example.com", ws.TrackerSheetId, senior, "Notes", "Applied on the Acme site; recruiter is Dana.");
        Console.WriteLine("   Jordan typed: status=applied and a note, in their own sheet.");
        boards.Close("lever", "northwind", "8f14e45f-ceea-467a-9e3a-7c2c1f0a0002");
        Console.WriteLine("   Northwind closed the Analytics Engineer posting.");
        RunSummaryList day2;
        using (Clock.Pin(() => DateTimeOffset.UtcNow.AddDays(1)))
            day2 = new RunSummaryList(await Task.WhenAll(app.Engine.RunAsync(jordan, "scheduler", "demo-day-2"), app.Engine.RunAsync(sam, "scheduler", "demo-day-2")).ConfigureAwait(false));
        foreach (var r in day2.Items) Summarize(app, r);

        Console.WriteLine($"== 4. What each user has in Drive, written to {Path.GetFullPath(outDir)}");
        foreach (var (id, email) in new[] { (jordan, "jordan.rivera@example.com"), (sam, "sam.okafor@example.com") })
        {
            var dir = Path.Combine(outDir, email.Split('@')[0]);
            Directory.CreateDirectory(dir);
            var w = app.Tracker.Get(id)!;
            await File.WriteAllTextAsync(Path.Combine(dir, "Job Tracker.csv"), Csv(google.SheetRows(email, w.TrackerSheetId))).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(dir, "Activity Log.csv"), Csv(google.SheetRows(email, w.ActivitySheetId))).ConfigureAwait(false);
            var files = google.FilesOf(email);
            string PathOf(FakeGoogle.FakeFile f) => f.Parent is { } p && files.TryGetValue(p, out var parent) ? PathOf(parent) + "/" + f.Name : f.Name;
            var listing = new StringBuilder();
            foreach (var f in files.Values.OrderBy(PathOf, StringComparer.Ordinal))
            {
                listing.AppendLine($"{(f.Kind == FakeGoogle.Folder ? "[folder]" : f.Kind == FakeGoogle.Binary ? "[pdf]   " : f.Kind == FakeGoogle.Sheet ? "[sheet] " : "[doc]   ")} {PathOf(f)}");
                if (f.Kind == FakeGoogle.Binary)
                    await File.WriteAllBytesAsync(Path.Combine(dir, Safe(f.Name)), Convert.FromBase64String(f.Content)).ConfigureAwait(false);
                if (f.Kind == FakeGoogle.Doc && f.Name.Contains("(editable)", StringComparison.Ordinal))
                    await File.WriteAllTextAsync(Path.Combine(dir, Safe(f.Name) + ".txt"), f.Content).ConfigureAwait(false);
            }
            await File.WriteAllTextAsync(Path.Combine(dir, "Drive listing.txt"), listing.ToString()).ConfigureAwait(false);
            Console.WriteLine($"   {email}: {files.Count} Drive items, tracker + activity log + PDFs in {dir}");
        }
        await File.WriteAllTextAsync(Path.Combine(outDir, "run-summaries.json"), JsonSerializer.Serialize(day1.Concat(day2.Items), new JsonSerializerOptions(JobSearch.Store.Json.Options) { WriteIndented = true })).ConfigureAwait(false);
        Console.WriteLine("== Done. Nothing was submitted, sent or shared; every external system here was simulated.");
        return 0;
    }

    private sealed record RunSummaryList(Runs.RunSummary[] Items);

    private static void Summarize(App app, Runs.RunSummary r)
    {
        var user = app.Accounts.GetUser(r.UserId);
        Console.WriteLine($"   {user.DisplayName}: {r.Status}; {r.PostingsRead} postings read, {r.NewJobs} new, {r.Drafted.Count} drafted, " +
                          $"{r.ImportedEdits} sheet edits imported, {r.Frozen.Count} frozen as submitted, {r.Archived.Count} archived");
        foreach (var b in r.Boards) Console.WriteLine($"     board {b.Key}: {b.Value}");
        foreach (var c in r.StatusChanges) Console.WriteLine($"     listing {c}");
        foreach (var c in r.CouldNotComplete) Console.WriteLine($"     could not complete: {c}");
    }

    private static string Safe(string name) => string.Concat(name.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) || ch == '/' ? '_' : ch));

    private static string Csv(List<List<string>> rows) =>
        string.Join("\n", rows.Select(r => string.Join(",", r.Select(c => c.Contains(',') || c.Contains('"') || c.Contains('\n') ? "\"" + c.Replace("\"", "\"\"") + "\"" : c))));
}
