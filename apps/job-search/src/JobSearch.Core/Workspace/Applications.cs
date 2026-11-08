using Dapper;
using JobSearch.Accounts;
using JobSearch.Store;

namespace JobSearch.Workspace;

/// <summary>Application status and notes: user-owned. The system never asserts an application happened.</summary>
public sealed class ApplicationService(Db db, AccessControl access)
{
    /// <summary>Allowed statuses.</summary>
    public static readonly string[] Statuses = ["not_applied", "preparing", "applied", "interviewing", "offer", "rejected", "withdrawn", "not_pursuing"];

    /// <summary>Statuses that assert something happened; only the user can set them, with their own evidence.</summary>
    public static readonly string[] Assertions = ["applied", "interviewing", "offer", "rejected"];

    /// <summary>Thrown when an assertion arrives without the user's evidence.</summary>
    public sealed class EvidenceRequiredException(string message) : Exception(message);

    /// <summary>Sets the status. Returns true when it changed to <c>applied</c> (the caller then freezes the materials).</summary>
    /// <param name="userId">Whose application.</param>
    /// <param name="actorId">Who is acting.</param>
    /// <param name="jobId">Which job.</param>
    /// <param name="status">New status.</param>
    /// <param name="evidence">The user's statement (required for <see cref="Assertions"/>).</param>
    /// <param name="via">cli | sheet.</param>
    public bool SetStatus(string userId, string actorId, string jobId, string status, string? evidence, string via)
    {
        if (!Statuses.Contains(status))
            throw new ArgumentException($"unknown status '{status}'; use one of {string.Join(", ", Statuses)}");
        if (Assertions.Contains(status))
        {
            access.Authorize(actorId, userId, Scope.AssertApplicationStatus); // owner only
            if (string.IsNullOrWhiteSpace(evidence))
                throw new EvidenceRequiredException($"'{status}' records that something happened; give the user's own evidence (e.g. confirmation email date)");
        }
        else
        {
            access.Authorize(actorId, userId, Scope.EditNotes);
        }
        return db.Write((c, tx) =>
        {
            var before = c.QuerySingleOrDefault<string>("SELECT status FROM applications WHERE job_id=@jobId AND user_id=@userId", new { jobId, userId }, tx)
                ?? throw new KeyNotFoundException($"no job {jobId} for this user");
            var appliedDate = status == "applied" ? Clock.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) : null;
            c.Execute("""
                UPDATE applications SET status=@status, status_evidence=@evidence, status_set_by=@by, status_set_at=@at,
                    applied_date=COALESCE(applied_date, @appliedDate), updated_at=@at WHERE job_id=@jobId AND user_id=@userId
                """, new { jobId, userId, status, evidence, by = $"{actorId} via {via}", at = Clock.Iso(), appliedDate }, tx);
            Db.Log(c, tx, userId, actorId, "application.status", jobId, new { from = before, to = status, evidence, via });
            return status == "applied" && before != "applied";
        });
    }

    /// <summary>Sets notes and/or next steps (null leaves a field unchanged).</summary>
    public void SetNotes(string userId, string actorId, string jobId, string? notes, string? nextSteps, string via)
    {
        access.Authorize(actorId, userId, Scope.EditNotes);
        db.Write((c, tx) =>
        {
            var n = c.Execute("""
                UPDATE applications SET user_notes=COALESCE(@notes, user_notes), next_steps=COALESCE(@nextSteps, next_steps), updated_at=@at
                WHERE job_id=@jobId AND user_id=@userId
                """, new { jobId, userId, notes, nextSteps, at = Clock.Iso() }, tx);
            if (n == 0) throw new KeyNotFoundException($"no job {jobId} for this user");
            Db.Log(c, tx, userId, actorId, "application.notes", jobId, new { notes = notes is not null, nextSteps = nextSteps is not null, via });
        });
    }

    /// <summary>Sets the applied date the user gave.</summary>
    public void SetAppliedDate(string userId, string actorId, string jobId, string? date, string via)
    {
        access.Authorize(actorId, userId, Scope.AssertApplicationStatus);
        db.Write((c, tx) =>
        {
            c.Execute("UPDATE applications SET applied_date=@date, updated_at=@at WHERE job_id=@jobId AND user_id=@userId",
                new { jobId, userId, date = string.IsNullOrWhiteSpace(date) ? null : date, at = Clock.Iso() }, tx);
            Db.Log(c, tx, userId, actorId, "application.applied_date", jobId, new { date, via });
        });
    }
}
