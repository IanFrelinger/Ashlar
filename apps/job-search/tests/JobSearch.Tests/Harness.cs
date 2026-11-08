using Ashlar.Abstractions.Security.Egress;
using Ashlar.WebOperations;
using JobSearch.Store;
using JobSearch.Accounts;
using JobSearch.Host;
using JobSearch.Workspace;

namespace JobSearch.Tests;

/// <summary>A whole app over a temp directory, simulated Google accounts and the stub boards.</summary>
public sealed class Harness : IDisposable
{
    public static readonly string Fixtures = Path.Combine(AppContext.BaseDirectory, "fixtures");
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "jobsearch-tests-" + Guid.NewGuid().ToString("N"));
    public FakeGoogle Google { get; } = new();
    public StubBoards Boards { get; } = new(Path.Combine(Fixtures, "boards.json"));
    public App App { get; }
    public List<TimeSpan> Waits { get; } = [];

    /// <summary>One certification cache for the whole test run: identical step source and witness certify once.</summary>
    public static readonly string CertCache = Path.Combine(Path.GetTempPath(), "jobsearch-cert-cache");

    /// <summary>A fixed test signing key, so cached certifications verify in every harness.</summary>
    public const string TestSigningKey = "dGVzdC1zaWduaW5nLWtleS0wMDAwMDAwMDAwMDAwMDA=";

    public Harness(IEgressGuard? apiGuard = null, JobSearch.Privacy.IPrivateModel? model = null, string? certifiedDir = null,
        JobSearch.Privacy.IStepWriter? writer = null, bool synthetic = false, JobSearch.Host.Studio.ForgeLimits? limits = null,
        TimeSpan? stepTimeout = null)
    {
        var client = new WebOperationClient(new SpecStore(Path.Combine(Dir, "specs")), apiGuard ?? new EgressGuard("full", "report"), Boards,
            new WebOperationsOptions { PerHostInterval = TimeSpan.Zero }, now: () => Clock.Now);
        App = new App(new AppOptions
        {
            DataDir = Dir, GoogleMode = "fake", Fake = Google, Web = client,
            Delay = (d, _) => { lock (Waits) Waits.Add(d); return Task.CompletedTask; },
            PrivateModel = model, StepWriter = writer, CertifiedDir = certifiedDir, Synthetic = synthetic,
            SigningKey = TestSigningKey, CertificationCacheDir = CertCache, ForgeLimits = limits ?? new(),
            SeedsDir = Path.Combine(AppContext.BaseDirectory, "library-seeds"), StepTimeout = stepTimeout ?? TimeSpan.FromSeconds(3),
        });
    }

    public const string JordanEmail = "jordan.rivera@example.com";
    public const string SamEmail = "sam.okafor@example.com";

    /// <summary>Jordan: data engineer, Denver; U.S. citizen (confirmed by the user); no clearance stated.</summary>
    public (string UserId, string Token) Jordan()
    {
        var id = App.Accounts.CreateUser(JordanEmail, "Jordan Rivera", "America/Denver");
        App.Profiles.StoreResume(id, id, "jordan-rivera.txt", File.ReadAllText(Path.Combine(Fixtures, "resumes", "jordan-rivera.txt")));
        App.Profiles.SetField(id, id, "work_authorization", "US citizen");
        App.Profiles.SetField(id, id, "requires_sponsorship", "no");
        App.Accounts.SetPreferences(id, id, App.Accounts.GetPreferences(id) with
        {
            TargetRoles = ["data engineer", "analytics engineer"], Locations = ["Denver, CO"], WorkArrangements = ["remote", "hybrid"],
            SalaryMin = 130000, Boards = ["greenhouse:acme-analytics|Acme Analytics", "lever:northwind|Northwind Health", "ashby:civic-ledger|Civic Ledger"],
        });
        return (id, App.ConnectFakeGoogle(id, JordanEmail));
    }

    /// <summary>Sam: nurse, Columbus; work authorization and clearance left unknown.</summary>
    public (string UserId, string Token) Sam()
    {
        var id = App.Accounts.CreateUser(SamEmail, "Sam Okafor", "America/New_York");
        App.Profiles.StoreResume(id, id, "sam-okafor.txt", File.ReadAllText(Path.Combine(Fixtures, "resumes", "sam-okafor.txt")));
        App.Accounts.SetPreferences(id, id, App.Accounts.GetPreferences(id) with
        {
            TargetRoles = ["informatics nurse", "nurse case manager"], Locations = ["Columbus, OH"],
            Boards = ["lever:northwind|Northwind Health", "ashby:civic-ledger|Civic Ledger"],
        });
        return (id, App.ConnectFakeGoogle(id, SamEmail));
    }

    private static readonly SemaphoreSlim SeedGate = new(1, 1);

    /// <summary>Puts the starter steps in this harness's library. The first call in a test run certifies them; the rest hit the cache.</summary>
    public async Task SeedAsync()
    {
        await SeedGate.WaitAsync();
        try { await App.Library.EnsureSeedsAsync(App.Certifier, App.SeedsDir, Path.Combine(Dir, "forge"), CancellationToken.None); }
        finally { SeedGate.Release(); }
    }

    /// <summary>The library id of a starter step by its file slug.</summary>
    public static string SeedId(string slug) => JobSearch.Host.Studio.StepLibrary.SeedId(
        System.Text.Json.JsonSerializer.Deserialize<JobSearch.Studio.StepSeed>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "library-seeds", slug + ".json")), JobSearch.Store.Json.Options)!);

    public void Dispose()
    {
        App.Dispose();
        try { Directory.Delete(Dir, recursive: true); } catch (IOException) { }
    }
}
