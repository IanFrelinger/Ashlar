using Dapper;
using JobSearch.Store;

namespace JobSearch.Accounts;

/// <summary>A user's search goals and rules. Empty lists and nulls mean "not stated", never "anything".</summary>
public sealed record Preferences
{
    /// <summary>e.g. "data engineer"; a title matches when it contains every significant word of one role.</summary>
    public List<string> TargetRoles { get; init; } = [];
    /// <summary>Industries of interest (shown; not used to filter, since boards do not state industry).</summary>
    public List<string> Industries { get; init; } = [];
    /// <summary>e.g. "Denver, CO".</summary>
    public List<string> Locations { get; init; } = [];
    /// <summary>Subset of remote, hybrid, onsite.</summary>
    public List<string> WorkArrangements { get; init; } = [];
    /// <summary>Annual, in <see cref="SalaryCurrency"/>.</summary>
    public decimal? SalaryMin { get; init; }
    /// <summary>Currency of <see cref="SalaryMin"/>.</summary>
    public string SalaryCurrency { get; init; } = "USD";
    /// <summary>Free-text requirements the user wants checked by hand; listed on every assessment.</summary>
    public List<string> OtherRequirements { get; init; } = [];
    /// <summary>Titles containing any of these are skipped.</summary>
    public List<string> ExcludeKeywords { get; init; } = [];
    /// <summary>Employer boards: greenhouse:slug, lever:slug, ashby:slug (optionally |Display Name).</summary>
    public List<string> Boards { get; init; } = [];
    /// <summary>Single posting URLs the user added.</summary>
    public List<string> JobLinks { get; init; } = [];
    /// <summary>When the daily search runs, in the user's time zone.</summary>
    public ScheduleRules Schedule { get; init; } = new();
    /// <summary>Screening thresholds and hard rules.</summary>
    public ScreeningRules Screening { get; init; } = new();
    /// <summary>Draft generation limits.</summary>
    public MaterialRules Materials { get; init; } = new();
    /// <summary>At most this many posting reads per run (board entries matching the targets beyond it wait for the next run).</summary>
    public int MaxPostingReadsPerRun { get; init; } = 25;
}

/// <summary>Schedule.</summary>
public sealed record ScheduleRules
{
    /// <summary>Whether the dispatcher runs this user.</summary>
    public bool Enabled { get; init; } = true;
    /// <summary>Local hour (0-23).</summary>
    public int Hour { get; init; } = 8;
    /// <summary>Local minute.</summary>
    public int Minute { get; init; }
}

/// <summary>Screening rules, configurable per user.</summary>
public sealed record ScreeningRules
{
    /// <summary>Share of required skills evidenced in the résumé for a strong fit.</summary>
    public double StrongThreshold { get; init; } = 0.7;
    /// <summary>Share for a conditional fit; below it is a stretch.</summary>
    public double ConditionalThreshold { get; init; } = 0.4;
    /// <summary>Years below the posting's minimum that still count as conditional.</summary>
    public double MaxYearsShort { get; init; } = 2;
    /// <summary>No successful re-check for this many days makes a listing stale.</summary>
    public int StaleAfterDays { get; init; } = 7;
    /// <summary>When true, a posted maximum below the salary floor is a blocker rather than a gap.</summary>
    public bool SalaryFloorIsHard { get; init; }
    /// <summary>Overrides the profile's travel limit when set.</summary>
    public int? MaxTravelPct { get; init; }
}

/// <summary>Draft limits, so the workspace stays manageable.</summary>
public sealed record MaterialRules
{
    /// <summary>Recommendations that get drafts.</summary>
    public List<string> DraftFor { get; init; } = ["strong_fit", "conditional_fit"];
    /// <summary>New job-specific drafts per run.</summary>
    public int MaxNewDraftsPerRun { get; init; } = 3;
    /// <summary>
    /// When true, a model words the cover letter's body through Ashlar's generative pipeline; the letter is used only if
    /// the certified claims check passes it, and it always needs the user's review. Off by default.
    /// </summary>
    public bool ModelCoverLetter { get; init; }
}

/// <summary>A user row.</summary>
public sealed record User(string Id, string Email, string DisplayName, string Timezone, string CreatedAt);

/// <summary>A provider connection row (no secret).</summary>
public sealed record Connection(string UserId, string Provider, string? AccountEmail, string TokenRef, string Status, string? StatusDetail);

/// <summary>Users, preferences and provider connections.</summary>
public sealed class AccountService(Db db, AccessControl access)
{
    /// <summary>Creates a user with default preferences.</summary>
    public string CreateUser(string email, string displayName, string timezone = "UTC")
    {
        _ = TimeZoneInfo.FindSystemTimeZoneById(timezone); // validates
        var id = Ids.New("usr");
        db.Write((c, tx) =>
        {
            c.Execute("INSERT INTO users (id, email, display_name, timezone, created_at) VALUES (@id,@email,@displayName,@timezone,@at)",
                new { id, email = email.Trim().ToLowerInvariant(), displayName, timezone, at = Clock.Iso() }, tx);
            c.Execute("INSERT INTO preferences (user_id, json, updated_at) VALUES (@id,@json,@at)",
                new { id, json = Json.Write(new Preferences()), at = Clock.Iso() }, tx);
            Db.Log(c, tx, id, id, "account.create", email.Trim().ToLowerInvariant());
        });
        return id;
    }

    /// <summary>The user.</summary>
    public User GetUser(string userId) =>
        db.Read(c => c.QuerySingleOrDefault<User>("SELECT id, email, display_name, timezone, created_at FROM users WHERE id=@userId", new { userId }))
        ?? throw new KeyNotFoundException($"no user {userId}");

    /// <summary>The user with this email, or null.</summary>
    public User? FindUser(string email) =>
        db.Read(c => c.QuerySingleOrDefault<User>("SELECT id, email, display_name, timezone, created_at FROM users WHERE email=@e",
            new { e = email.Trim().ToLowerInvariant() }));

    /// <summary>All users.</summary>
    public IReadOnlyList<User> ListUsers() =>
        db.Read(c => c.Query<User>("SELECT id, email, display_name, timezone, created_at FROM users ORDER BY created_at, id").AsList());

    /// <summary>The user's preferences.</summary>
    public Preferences GetPreferences(string userId) =>
        Json.Read<Preferences>(db.Read(c => c.QuerySingleOrDefault<string>("SELECT json FROM preferences WHERE user_id=@userId", new { userId })))
        ?? new Preferences();

    /// <summary>Replaces the user's preferences (the caller derives the new value from <see cref="GetPreferences"/>).</summary>
    public void SetPreferences(string userId, string actorId, Preferences prefs)
    {
        access.Authorize(actorId, userId, Scope.EditPreferences);
        db.Write((c, tx) =>
        {
            c.Execute("UPDATE preferences SET json=@json, updated_at=@at WHERE user_id=@userId",
                new { userId, json = Json.Write(prefs), at = Clock.Iso() }, tx);
            Db.Log(c, tx, userId, actorId, "preferences.update");
        });
    }

    /// <summary>Records that the user connected a provider. Only the user may do this.</summary>
    public void Connect(string userId, string actorId, string provider, string? accountEmail, string tokenRef)
    {
        access.Authorize(actorId, userId, Scope.ConnectProvider);
        db.Write((c, tx) =>
        {
            c.Execute("""
                INSERT INTO connections (user_id, provider, account_email, token_ref, status, updated_at)
                VALUES (@userId,@provider,@accountEmail,@tokenRef,'active',@at)
                ON CONFLICT(user_id, provider) DO UPDATE SET account_email=excluded.account_email, token_ref=excluded.token_ref,
                    status='active', status_detail=NULL, updated_at=excluded.updated_at
                """, new { userId, provider, accountEmail, tokenRef, at = Clock.Iso() }, tx);
            Db.Log(c, tx, userId, actorId, "connection.connect", provider, new { accountEmail });
        });
    }

    /// <summary>The connection, or null.</summary>
    public Connection? GetConnection(string userId, string provider) =>
        db.Read(c => c.QuerySingleOrDefault<Connection>(
            "SELECT user_id, provider, account_email, token_ref, status, status_detail FROM connections WHERE user_id=@userId AND provider=@provider",
            new { userId, provider }));

    /// <summary>Marks a connection (needs_reauth when a provider rejected the token).</summary>
    public void MarkConnection(string userId, string provider, string status, string detail) =>
        db.Write((c, tx) =>
        {
            c.Execute("UPDATE connections SET status=@status, status_detail=@detail, updated_at=@at WHERE user_id=@userId AND provider=@provider",
                new { userId, provider, status, detail, at = Clock.Iso() }, tx);
            Db.Log(c, tx, userId, "system", $"connection.{status}", provider, new { detail });
        });
}
