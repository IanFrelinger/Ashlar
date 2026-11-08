using JobSearch.Accounts;
using JobSearch.Host.Studio;

namespace JobSearch.Host.Web;

/// <summary>
/// Synthetic mode's made-up people (STUDIO.md I15): QA agents and demos sign in as them. Nothing about them is real,
/// which is what lets QA agents send screenshots of the app to an external model.
/// </summary>
public static class Synthetic
{
    /// <summary>The personas: email, name, time zone, résumé fixture, target roles, boards.</summary>
    public static readonly (string Email, string Name, string Timezone, string Resume, string[] Roles, string[] Boards)[] Personas =
    [
        ("jordan.rivera@example.com", "Jordan Rivera", "America/Denver", "jordan-rivera.txt", ["data engineer", "analytics engineer"],
            ["greenhouse:acme-analytics|Acme Analytics", "lever:northwind|Northwind Health", "ashby:civic-ledger|Civic Ledger"]),
        ("sam.okafor@example.com", "Sam Okafor", "America/New_York", "sam-okafor.txt", ["informatics nurse", "nurse case manager"],
            ["lever:northwind|Northwind Health", "ashby:civic-ledger|Civic Ledger"]),
        ("new.user@example.com", "Casey New", "America/Chicago", "", [], []),
    ];

    /// <summary>Creates any persona that does not exist yet. Casey is left un-onboarded, to exercise the first-run screens.</summary>
    public static void EnsurePersonas(App app, string fixturesDir)
    {
        if (!app.Synthetic)
            throw new InvalidOperationException("personas exist only in synthetic mode");
        foreach (var p in Personas)
        {
            if (app.Accounts.FindUser(p.Email) is not null)
                continue;
            var id = app.Accounts.CreateUser(p.Email, p.Name, p.Timezone);
            if (p.Resume.Length == 0)
                continue;
            foreach (var kind in new[] { "privacy-notice", "terms", "age-16-or-over" })
                app.Compliance.Grant(id, kind);
            app.Profiles.StoreResume(id, id, p.Resume, File.ReadAllText(Path.Combine(fixturesDir, "resumes", p.Resume)));
            app.Accounts.SetPreferences(id, id, app.Accounts.GetPreferences(id) with { TargetRoles = [.. p.Roles], Boards = [.. p.Boards] });
            app.ConnectFakeGoogle(id, p.Email);
        }
    }
}
