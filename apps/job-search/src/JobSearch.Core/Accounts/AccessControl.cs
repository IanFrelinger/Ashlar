using Dapper;
using JobSearch.Store;

namespace JobSearch.Accounts;

/// <summary>What an actor may do to a user's data.</summary>
public enum Scope
{
    /// <summary>View profile, tracker and runs.</summary>
    Read,
    /// <summary>Correct profile fields.</summary>
    EditProfile,
    /// <summary>Targets, rules, schedule.</summary>
    EditPreferences,
    /// <summary>Next steps and notes.</summary>
    EditNotes,
    /// <summary>Start a run.</summary>
    RunSearch,
    /// <summary>Generate, refresh, archive drafts.</summary>
    ManageMaterials,
    /// <summary>Connect Google Drive. Never delegated.</summary>
    ConnectProvider,
    /// <summary>Grant or revoke access. Never delegated.</summary>
    GrantAccess,
    /// <summary>Assert an application happened (applied, interviewing, offer). Never delegated.</summary>
    AssertApplicationStatus,
    /// <summary>Change Drive sharing. Never delegated; the system itself never does it.</summary>
    ChangeSharing,
}

/// <summary>Refused.</summary>
public sealed class AccessDeniedException(string message) : Exception(message);

/// <summary>
/// A user has every scope on their own data. A manager has only the scopes the user granted, until the grant expires or
/// is revoked. Connecting a provider, granting access, asserting an application happened and changing sharing are never
/// delegable. Every grant, revoke and delegated action is in the user's activity log.
/// </summary>
public sealed class AccessControl(Db db)
{
    private static readonly HashSet<Scope> NeverDelegated =
        [Scope.ConnectProvider, Scope.GrantAccess, Scope.AssertApplicationStatus, Scope.ChangeSharing];

    /// <summary>Grants <paramref name="scopes"/> to <paramref name="managerId"/>. Only the user can.</summary>
    public string Grant(string userId, string actorId, string managerId, IReadOnlyCollection<Scope> scopes, DateTimeOffset? expiresAt = null)
    {
        if (actorId != userId)
            throw new AccessDeniedException("only the user can grant access to their own data");
        var bad = scopes.Where(NeverDelegated.Contains).ToList();
        if (bad.Count > 0)
            throw new AccessDeniedException($"cannot be delegated: {string.Join(", ", bad)}");
        var id = Ids.New("grant");
        db.Write((c, tx) =>
        {
            c.Execute("INSERT INTO access_grants (id, user_id, manager_id, scopes_json, granted_at, expires_at) VALUES (@id,@userId,@managerId,@s,@at,@exp)",
                new { id, userId, managerId, s = Json.Write(scopes.Select(x => x.ToString()).OrderBy(x => x, StringComparer.Ordinal)), at = Clock.Iso(), exp = expiresAt is null ? null : Clock.Iso(expiresAt) }, tx);
            Db.Log(c, tx, userId, actorId, "access.grant", managerId, new { scopes = scopes.Select(x => x.ToString()), expiresAt });
        });
        return id;
    }

    /// <summary>Revokes a grant. Only the user can.</summary>
    public void Revoke(string userId, string actorId, string grantId)
    {
        if (actorId != userId)
            throw new AccessDeniedException("only the user can revoke access to their own data");
        db.Write((c, tx) =>
        {
            c.Execute("UPDATE access_grants SET revoked_at=@at WHERE id=@grantId AND user_id=@userId", new { grantId, userId, at = Clock.Iso() }, tx);
            Db.Log(c, tx, userId, actorId, "access.revoke", grantId);
        });
    }

    /// <summary>Throws unless <paramref name="actorId"/> may do <paramref name="scope"/> to <paramref name="userId"/>'s data.</summary>
    /// <returns>"owner" or "manager".</returns>
    public string Authorize(string actorId, string userId, Scope scope)
    {
        if (actorId == userId)
            return "owner";
        if (NeverDelegated.Contains(scope))
            throw new AccessDeniedException($"'{scope}' can only be done by the user themselves");
        var now = Clock.Now;
        var grants = db.Read(c => c.Query<(string Scopes, string? Expires)>(
            "SELECT scopes_json, expires_at FROM access_grants WHERE user_id=@userId AND manager_id=@actorId AND revoked_at IS NULL",
            new { userId, actorId }).AsList());
        foreach (var (scopes, expires) in grants)
        {
            if (Clock.Parse(expires) is { } e && e <= now)
                continue;
            if ((Json.Read<List<string>>(scopes) ?? []).Contains(scope.ToString()))
                return "manager";
        }
        throw new AccessDeniedException($"{actorId} has no active '{scope}' grant for {userId}");
    }
}
