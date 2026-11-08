using Ashlar.Abstractions.Security.Egress;
using Ashlar.Bricks.ApiAnything;
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
    public StubApiAnything Boards { get; } = new(Path.Combine(Fixtures, "boards.json"));
    public App App { get; }
    public List<TimeSpan> Waits { get; } = [];

    public Harness(IEgressGuard? apiGuard = null)
    {
        var client = new ApiAnythingClient(new ApiAnythingOptions { Token = "stub", MaxAge = null }, apiGuard ?? new EgressGuard("full", "report"), Boards, (_, _) => Task.CompletedTask);
        App = new App(new AppOptions
        {
            DataDir = Dir, GoogleMode = "fake", Fake = Google, ApiAnything = client,
            Delay = (d, _) => { lock (Waits) Waits.Add(d); return Task.CompletedTask; },
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

    public void Dispose()
    {
        App.Dispose();
        try { Directory.Delete(Dir, recursive: true); } catch (IOException) { }
    }
}
