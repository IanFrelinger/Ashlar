using Ashlar.Core.Domain.Execution;
using Dapper;
using JobSearch.Accounts;
using JobSearch.Store;

namespace JobSearch.Runs;

/// <summary>The execution context a run's direct brick calls carry: deterministic, no model provider.</summary>
public sealed class RunContext(string userId, string runId) : IExecutionContext
{
    /// <inheritdoc />
    public string AgentId => "jobsearch";
    /// <inheritdoc />
    public string BehaviorId => $"run:{runId}";
    /// <inheritdoc />
    public bool IsAirGapped => false;
    /// <inheritdoc />
    public bool AuditMode => true;
    /// <inheritdoc />
    public string Provider => "none";
    /// <inheritdoc />
    public IReadOnlyDictionary<string, object> Variables { get; } = new Dictionary<string, object> { ["userId"] = userId, ["runId"] = runId };
}

/// <summary>
/// Decides who is due and runs them. Each user has their own schedule in their own time zone (default 08:00); a user is
/// due once their local time has passed it today and today's run has not succeeded. Ashlar's background-agent
/// scheduler takes one static cron per agent, in UTC, so per-user local schedules are computed here (DOGFOOD-FINDINGS).
/// </summary>
public sealed class Dispatcher(Db db, AccountService accounts, RunEngine engine)
{
    /// <summary>Users due at <paramref name="now"/>, with their daily key.</summary>
    public IReadOnlyList<(User User, string Key)> Due(DateTimeOffset now)
    {
        var due = new List<(User, string)>();
        foreach (var user in accounts.ListUsers())
        {
            var prefs = accounts.GetPreferences(user.Id);
            if (!prefs.Schedule.Enabled || accounts.GetConnection(user.Id, "google") is not { Status: "active" })
                continue;
            var local = TimeZoneInfo.ConvertTime(now, TimeZoneInfo.FindSystemTimeZoneById(user.Timezone));
            if (local.TimeOfDay < new TimeSpan(prefs.Schedule.Hour, prefs.Schedule.Minute, 0))
                continue;
            var key = RunEngine.DailyKey(user, now);
            var done = db.Read(c => c.ExecuteScalar<long>("SELECT COUNT(*) FROM runs WHERE user_id=@id AND idempotency_key=@key AND status='succeeded'", new { id = user.Id, key }));
            if (done == 0)
                due.Add((user, key));
        }
        return due;
    }

    /// <summary>The next scheduled time for a user, in UTC.</summary>
    public static DateTimeOffset NextRun(User user, Preferences prefs, DateTimeOffset now)
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById(user.Timezone);
        var local = TimeZoneInfo.ConvertTime(now, tz);
        var at = new DateTime(local.Year, local.Month, local.Day, prefs.Schedule.Hour, prefs.Schedule.Minute, 0, DateTimeKind.Unspecified);
        if (local.DateTime >= at)
            at = at.AddDays(1);
        return new DateTimeOffset(at, tz.GetUtcOffset(at));
    }

    /// <summary>Runs every due user, at most <paramref name="parallelism"/> at a time. Users never share a run.</summary>
    public async Task<IReadOnlyList<RunSummary>> RunDueAsync(DateTimeOffset now, int parallelism = 4, CancellationToken ct = default)
    {
        var due = Due(now);
        var results = new List<RunSummary>();
        await Parallel.ForEachAsync(due, new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = ct }, async (d, token) =>
        {
            var r = await engine.RunAsync(d.User.Id, "scheduler", d.Key, token).ConfigureAwait(false);
            lock (results) results.Add(r);
        }).ConfigureAwait(false);
        return results;
    }
}
