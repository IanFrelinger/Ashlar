using System.Globalization;
using System.Text;
using System.Text.Json;
using Ashlar.WebOperations;
using Dapper;
using JobSearch.Accounts;
using JobSearch.Runs;
using JobSearch.Store;

namespace JobSearch.Host;

/// <summary>The <c>jobsearch</c> command line: an operator tool over the services, which enforce the access rules.</summary>
public static class Program
{
    private const string Usage = """
        jobsearch [--data DIR] [--google fake|google] [--stub-boards FILE] <command> [options]

          demo                                     two synthetic users end to end (no network); writes ./demo-output
          user add --email E --name N [--tz Z]     create an account
          user list
          connect-fake --user E                    fake mode: give the user a simulated Google account
          connect-google --user E [--code C]       real mode: print the consent URL, then exchange the code
          resume --user E --file PATH              upload/replace the master résumé (.txt, .md)
          profile --user E [--set k=v] [--confirm k] [--reject k]
          prefs --user E [--roles "a;b"] [--locations "x;y"] [--arrangements remote,hybrid] [--salary-min N]
                         [--board greenhouse:slug|Name] [--link URL] [--hour 8] [--max-drafts 3]
          grant --user E --manager M --scopes Read,EditNotes,RunSearch [--days N]
          run --user E [--as A] [--key K]          one search now
          dispatch [--once] [--parallel 4]         run everyone due (every 15 min unless --once)
          tracker --user E                         the user's jobs as the app records them
          status --user E --job ID --set S [--evidence TEXT]
          notes --user E --job ID [--notes T] [--next T]
          approve --user E --job ID                mark the job's materials reviewed
          refresh-pdf --user E --job ID            re-render stale PDFs from the edited drafts
          conflicts --user E [--resolve ID --keep sheet|system]
          snapshot --user E --repo DIR [--push URL --confirmed-private]
          restore --user E --repo DIR --commit SHA [--apply]
          runs --user E                            run history with what each checked and could not complete

        Environment: GOOGLE_CLIENT_ID, and GOOGLE_CLIENT_SECRET unless a proxy injects it. Learned and repaired site specs are kept in <data>/specs.
        """;

    /// <summary>Main.</summary>
    public static async Task<int> Main(string[] argv)
    {
        var args = new Args(argv);
        if (args.Positional.Count == 0 || args.Has("help"))
        {
            Console.WriteLine(Usage);
            return args.Positional.Count == 0 ? 1 : 0;
        }
        try
        {
            if (args.Positional[0] == "demo")
                return await Demo.RunAsync(args.Get("out") ?? "demo-output").ConfigureAwait(false);
            using var app = Build(args);
            return await DispatchAsync(app, args).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is AccessDeniedException or ArgumentException or KeyNotFoundException or InvalidOperationException
                                   or Workspace.ApplicationService.EvidenceRequiredException)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 2;
        }
    }

    private static App Build(Args args)
    {
        var dataDir = args.Get("data") ?? Environment.GetEnvironmentVariable("JOBSEARCH_DATA") ?? ".jobsearch";
        WebOperationClient? client = null;
        if (args.Get("stub-boards") is { } boards)
            client = new WebOperationClient(new SpecStore(Path.Combine(dataDir, "specs")), null, new StubBoards(boards), new WebOperationsOptions { PerHostInterval = TimeSpan.Zero }, now: () => Clock.Now);
        return new App(new AppOptions
        {
            DataDir = dataDir,
            GoogleMode = args.Get("google") ?? "fake",
            GoogleClientId = Environment.GetEnvironmentVariable("GOOGLE_CLIENT_ID"),
            GoogleClientSecret = Environment.GetEnvironmentVariable("GOOGLE_CLIENT_SECRET"),
            Web = client,
        });
    }

    private static string UserId(App app, Args args, string key = "user") =>
        app.Accounts.FindUser(args.Require(key))?.Id ?? throw new KeyNotFoundException($"no user with email {args.Get(key)}");

    private static string Actor(App app, Args args, string userId) => args.Get("as") is { } a ? UserId(app, args, "as") : userId;

    private static void Print(object o) => Console.WriteLine(JsonSerializer.Serialize(o, new JsonSerializerOptions(JobSearch.Store.Json.Options) { WriteIndented = true }));

    private static async Task<int> DispatchAsync(App app, Args args)
    {
        var cmd = string.Join(' ', args.Positional.Take(2));
        switch (args.Positional[0])
        {
            case "user" when args.Positional.ElementAtOrDefault(1) == "add":
                Console.WriteLine(app.Accounts.CreateUser(args.Require("email"), args.Require("name"), args.Get("tz") ?? "UTC"));
                return 0;
            case "user":
                Print(app.Accounts.ListUsers());
                return 0;
            case "connect-fake":
            {
                var id = UserId(app, args);
                app.ConnectFakeGoogle(id, app.Accounts.GetUser(id).Email);
                Console.WriteLine("connected a simulated Google account (fake mode)");
                return 0;
            }
            case "connect-google":
                return await GoogleConnect.RunAsync(app, UserId(app, args), args.Get("code")).ConfigureAwait(false);
            case "resume":
            {
                var id = UserId(app, args);
                var file = args.Require("file");
                var (sha, facts) = app.Profiles.StoreResume(id, Actor(app, args, id), Path.GetFileName(file), await File.ReadAllTextAsync(file).ConfigureAwait(false));
                Console.WriteLine($"stored {sha[..12]}: {facts.Employment.Count} jobs, {facts.Skills.Count} skills, {facts.Unparsed.Count} lines not understood (kept as text)");
                Console.WriteLine("Review the derived profile: jobsearch profile --user " + args.Get("user"));
                return 0;
            }
            case "profile":
            {
                var id = UserId(app, args);
                var actor = Actor(app, args, id);
                if (args.Get("set") is { } kv && kv.Split('=', 2) is [var k, var v])
                    app.Profiles.SetField(id, actor, k, v);
                if (args.Get("confirm") is { } c) app.Profiles.Review(id, actor, c, true);
                if (args.Get("reject") is { } r) app.Profiles.Review(id, actor, r, false);
                app.Access.Authorize(actor, id, Scope.Read);
                var p = app.Profiles.Load(id);
                Print(p?.Fields.ToDictionary(f => f.Key, f => new { value = f.Value.Value?.ToString() ?? "unknown", f.Value.Status, f.Value.Source, f.Value.Evidence })
                      ?? (object)"no résumé yet");
                return 0;
            }
            case "prefs":
            {
                var id = UserId(app, args);
                var p = app.Accounts.GetPreferences(id);
                static List<string> L(string s, char sep = ';') => s.Split(sep, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                p = p with
                {
                    TargetRoles = args.Get("roles") is { } ro ? L(ro) : p.TargetRoles,
                    Locations = args.Get("locations") is { } lo ? L(lo) : p.Locations,
                    WorkArrangements = args.Get("arrangements") is { } ar ? L(ar, ',') : p.WorkArrangements,
                    SalaryMin = args.Get("salary-min") is { } sm ? decimal.Parse(sm, CultureInfo.InvariantCulture) : p.SalaryMin,
                    Boards = args.Get("board") is { } b ? [.. p.Boards.Where(x => x != b), b] : p.Boards,
                    JobLinks = args.Get("link") is { } li ? [.. p.JobLinks.Where(x => x != li), li] : p.JobLinks,
                    Schedule = args.Get("hour") is { } h ? p.Schedule with { Hour = int.Parse(h, CultureInfo.InvariantCulture) } : p.Schedule,
                    Materials = args.Get("max-drafts") is { } md ? p.Materials with { MaxNewDraftsPerRun = int.Parse(md, CultureInfo.InvariantCulture) } : p.Materials,
                };
                foreach (var board in p.Boards)
                    if (!Ashlar.WebOperations.Jobs.BoardRef.TryParse(board, out _, out var err))
                        throw new ArgumentException($"board '{board}': {err}");
                app.Accounts.SetPreferences(id, Actor(app, args, id), p);
                Print(p);
                return 0;
            }
            case "grant":
            {
                var id = UserId(app, args);
                var scopes = args.Require("scopes").Split(',').Select(s => Enum.Parse<Scope>(s.Trim(), ignoreCase: true)).ToList();
                var days = args.Get("days") is { } d ? Clock.Now.AddDays(int.Parse(d, CultureInfo.InvariantCulture)) : (DateTimeOffset?)null;
                Console.WriteLine(app.Access.Grant(id, Actor(app, args, id), UserId(app, args, "manager"), scopes, days));
                return 0;
            }
            case "run":
            {
                var id = UserId(app, args);
                var actor = Actor(app, args, id);
                app.Access.Authorize(actor, id, Scope.RunSearch);
                var s = await app.Engine.RunAsync(id, actor, args.Get("key")).ConfigureAwait(false);
                Print(s with { WorkflowLog = [] });
                return s.Status is "succeeded" or "skipped" ? 0 : 3;
            }
            case "dispatch":
            {
                var parallel = int.Parse(args.Get("parallel") ?? "4", CultureInfo.InvariantCulture);
                do
                {
                    foreach (var r in await app.Dispatcher.RunDueAsync(Clock.Now, parallel).ConfigureAwait(false))
                        Console.WriteLine($"{Clock.Iso()} {r.UserId} {r.Status} {r.Reason} new={r.NewJobs} read={r.PostingsRead} drafted={r.Drafted.Count} deferred={r.Deferred} incomplete={r.CouldNotComplete.Count}");
                    if (args.Has("once")) break;
                    await Task.Delay(TimeSpan.FromMinutes(15)).ConfigureAwait(false);
                } while (true);
                return 0;
            }
            case "tracker":
            {
                var id = UserId(app, args);
                app.Access.Authorize(Actor(app, args, id), id, Scope.Read);
                Print(app.Db.Read(c => c.Query("""
                    SELECT j.id, j.employer, j.title, j.listing_status, j.listing_status_detail, s.recommendation, a.status, a.user_notes
                    FROM jobs j JOIN applications a ON a.job_id=j.id LEFT JOIN assessments s ON s.job_id=j.id WHERE j.user_id=@id ORDER BY s.score DESC
                    """, new { id }).AsList()));
                return 0;
            }
            case "status":
            {
                var id = UserId(app, args);
                var job = args.Require("job");
                if (app.Applications.SetStatus(id, Actor(app, args, id), job, args.Require("set"), args.Get("evidence"), "cli"))
                    await app.Engine.FreezeSubmittedAsync(id, job).ConfigureAwait(false);
                Console.WriteLine("ok");
                return 0;
            }
            case "notes":
            {
                var id = UserId(app, args);
                app.Applications.SetNotes(id, Actor(app, args, id), args.Require("job"), args.Get("notes"), args.Get("next"), "cli");
                Console.WriteLine("ok");
                return 0;
            }
            case "approve":
            {
                var id = UserId(app, args);
                app.Engine.Approve(id, Actor(app, args, id), args.Require("job"));
                Console.WriteLine("marked reviewed. The system never submits applications; submit through the employer's site yourself.");
                return 0;
            }
            case "refresh-pdf":
            {
                var id = UserId(app, args);
                Console.WriteLine($"refreshed {await app.Engine.RefreshPdfsAsync(id, Actor(app, args, id), args.Require("job")).ConfigureAwait(false)} PDF(s)");
                return 0;
            }
            case "conflicts":
            {
                var id = UserId(app, args);
                if (args.Get("resolve") is { } cid)
                    app.Tracker.Resolve(id, Actor(app, args, id), long.Parse(cid, CultureInfo.InvariantCulture), args.Require("keep"), app.Access);
                Print(app.Tracker.OpenConflicts(id).Select(c => new { c.Id, c.JobId, c.Column, c.SheetValue, c.SystemValue, c.DetectedAt }));
                return 0;
            }
            case "snapshot":
            {
                var id = UserId(app, args);
                var repo = args.Require("repo");
                Console.WriteLine(app.Snapshots.Export(id, repo) ?? "no changes since the last snapshot");
                if (args.Get("push") is { } url)
                    Console.WriteLine(app.Snapshots.Push(id, repo, url, args.Has("confirmed-private")).Detail);
                return 0;
            }
            case "restore":
            {
                var id = UserId(app, args);
                var plan = app.Snapshots.RestorePlan(id, args.Require("repo"), args.Require("commit"));
                Print(plan);
                if (args.Has("apply"))
                    Console.WriteLine($"applied {app.Snapshots.ApplyRestore(id, Actor(app, args, id), plan, args.Require("commit"))} change(s)");
                return 0;
            }
            case "runs":
            {
                var id = UserId(app, args);
                Print(app.Db.Read(c => c.Query("SELECT id, idempotency_key, status, attempt, started_at, finished_at, error, summary_json FROM runs WHERE user_id=@id ORDER BY started_at DESC LIMIT 10", new { id }).AsList()));
                return 0;
            }
            default:
                Console.Error.WriteLine($"unknown command '{cmd}'\n\n{Usage}");
                return 1;
        }
    }
}

/// <summary>--key value arguments, positional words, and bare --flags.</summary>
public sealed class Args
{
    private readonly Dictionary<string, string?> _named = new(StringComparer.Ordinal);

    /// <summary>Positional words.</summary>
    public List<string> Positional { get; } = [];

    /// <summary>Parses.</summary>
    public Args(string[] argv)
    {
        for (var i = 0; i < argv.Length; i++)
        {
            if (argv[i].StartsWith("--", StringComparison.Ordinal))
            {
                var key = argv[i][2..];
                _named[key] = i + 1 < argv.Length && !argv[i + 1].StartsWith("--", StringComparison.Ordinal) ? argv[++i] : null;
            }
            else
            {
                Positional.Add(argv[i]);
            }
        }
    }

    /// <summary>The value, or null.</summary>
    public string? Get(string key) => _named.GetValueOrDefault(key);

    /// <summary>Whether the flag is present.</summary>
    public bool Has(string key) => _named.ContainsKey(key);

    /// <summary>The value, or an error naming it.</summary>
    public string Require(string key) => Get(key) ?? throw new ArgumentException($"--{key} is required");
}
