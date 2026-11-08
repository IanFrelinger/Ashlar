using System.Text.Json;
using Dapper;
using JobSearch.Accounts;
using JobSearch.Store;

namespace JobSearch.Profile;

/// <summary>One profile field with its provenance. A null value is unknown.</summary>
public sealed record ProfileField(JsonElement? Value, string? Source, string Status, string? Evidence)
{
    /// <summary>Usable by screening: set by the user, or derived and not rejected.</summary>
    public bool Usable => Value is not null && Status != "rejected" && Status != "unknown";
    /// <summary>Confirmed or user-set.</summary>
    public bool Confirmed => Value is not null && Status == "confirmed";
}

/// <summary>The candidate as the system may use them: fields with provenance, plus the current résumé.</summary>
public sealed record CandidateProfile(string UserId, IReadOnlyDictionary<string, ProfileField> Fields, string ResumeText, string ResumeSha, ResumeFacts Facts)
{
    /// <summary>The field, or unknown.</summary>
    public ProfileField Field(string name) =>
        Fields.TryGetValue(name, out var f) ? f : new ProfileField(null, null, "unknown", null);

    /// <summary>A usable string value, or null.</summary>
    public string? Text(string name) => Field(name) is { Usable: true, Value: { } v } ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText() : null;

    /// <summary>A confirmed (user-set or user-confirmed) string value, or null.</summary>
    public string? ConfirmedText(string name) => Field(name) is { Confirmed: true, Value: { } v } ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText() : null;

    /// <summary>A usable number, or null.</summary>
    public double? Number(string name) => Field(name) is { Usable: true, Value: { ValueKind: JsonValueKind.Number } v } ? v.GetDouble() : null;
}

/// <summary>Résumé storage, derived fields and the user's corrections.</summary>
public sealed class ProfileService(Db db, AccessControl access)
{
    /// <summary>Fields a résumé cannot establish; they start unknown and only the user sets them.</summary>
    public static readonly string[] UserOnlyFields =
        ["work_authorization", "requires_sponsorship", "clearance_status", "willing_to_relocate", "max_travel_pct", "earliest_start_date"];

    /// <summary>Stores a résumé version and (re)derives the fields the user has not set, confirmed or rejected.</summary>
    public (string Sha, ResumeFacts Facts) StoreResume(string userId, string actorId, string filename, string text, string? driveFileId = null)
    {
        access.Authorize(actorId, userId, Scope.EditProfile);
        var sha = ResumeParser.Sha256(text);
        var facts = ResumeParser.Parse(text);
        var derived = Derive(facts, DateOnly.FromDateTime(Clock.Now.UtcDateTime));
        db.Write((c, tx) =>
        {
            c.Execute("UPDATE resume_versions SET is_current=0 WHERE user_id=@userId", new { userId }, tx);
            var updated = c.Execute("UPDATE resume_versions SET is_current=1 WHERE user_id=@userId AND sha256=@sha", new { userId, sha }, tx);
            if (updated == 0)
                c.Execute("INSERT INTO resume_versions (id, user_id, sha256, text, filename, drive_file_id, is_current, created_at) VALUES (@id,@userId,@sha,@text,@filename,@driveFileId,1,@at)",
                    new { id = Ids.New("res"), userId, sha, text, filename, driveFileId, at = Clock.Iso() }, tx);
            foreach (var (field, (value, evidence)) in derived)
            {
                var existing = c.QuerySingleOrDefault<(string Source, string Status)?>(
                    "SELECT source, status FROM profile_fields WHERE user_id=@userId AND field=@field", new { userId, field }, tx);
                if (existing is { } e && (e.Source == "user" || e.Status is "confirmed" or "rejected"))
                    continue; // never overwrite what the user set, confirmed or rejected
                c.Execute("""
                    INSERT INTO profile_fields (user_id, field, value_json, source, status, evidence, updated_at) VALUES (@userId,@field,@v,'resume','derived',@evidence,@at)
                    ON CONFLICT(user_id, field) DO UPDATE SET value_json=excluded.value_json, evidence=excluded.evidence, updated_at=excluded.updated_at
                    """, new { userId, field, v = Json.Write(value), evidence, at = Clock.Iso() }, tx);
            }
            Db.Log(c, tx, userId, actorId, "resume.store", filename, new { sha, employment = facts.Employment.Count, unparsedLines = facts.Unparsed.Count });
        });
        return (sha, facts);
    }

    /// <summary>The fields a résumé establishes, each with its evidence. Unknowns are absent.</summary>
    public static Dictionary<string, (object Value, string Evidence)> Derive(ResumeFacts f, DateOnly today)
    {
        var d = new Dictionary<string, (object, string)>(StringComparer.Ordinal);
        void One(string key, Sourced? s)
        {
            if (s is not null)
                d[key] = (s.Value, $"résumé line {s.Line}");
        }
        One("name", f.Name);
        One("email", f.Email);
        One("phone", f.Phone);
        One("location", f.Location);
        if (f.Skills.Count > 0) d["skills"] = (f.Skills.Select(s => s.Value).ToList(), "résumé skills section");
        if (f.Employment.Count > 0)
        {
            d["employment"] = (f.Employment.Select(j => new { j.Title, j.Employer, j.Start, j.End }).ToList(), "résumé experience section");
            if (ResumeParser.TotalYears(f.Employment, today) is { } years)
                d["years_experience"] = (years, "computed from résumé employment dates");
        }
        if (f.Education.Count > 0) d["education"] = (f.Education.Select(e => e.Value).ToList(), "résumé education section");
        if (f.Certifications.Count > 0) d["certifications"] = (f.Certifications.Select(e => e.Value).ToList(), "résumé certifications section");
        // A claim to confirm; screening never treats it as current status.
        One("clearance_claim", f.ClearanceClaim);
        return d;
    }

    /// <summary>Sets a field as the user (confirmed).</summary>
    public void SetField(string userId, string actorId, string field, object? value)
    {
        access.Authorize(actorId, userId, Scope.EditProfile);
        db.Write((c, tx) =>
        {
            c.Execute("""
                INSERT INTO profile_fields (user_id, field, value_json, source, status, evidence, updated_at) VALUES (@userId,@field,@v,'user','confirmed',@ev,@at)
                ON CONFLICT(user_id, field) DO UPDATE SET value_json=excluded.value_json, source='user', status='confirmed', evidence=excluded.evidence, updated_at=excluded.updated_at
                """, new { userId, field, v = value is null ? null : Json.Write(value), ev = $"set by {actorId}", at = Clock.Iso() }, tx);
            Db.Log(c, tx, userId, actorId, "profile.set", field, new { value });
        });
    }

    /// <summary>Confirms or rejects a derived field.</summary>
    public void Review(string userId, string actorId, string field, bool confirm)
    {
        access.Authorize(actorId, userId, Scope.EditProfile);
        db.Write((c, tx) =>
        {
            var n = c.Execute("UPDATE profile_fields SET status=@s, updated_at=@at WHERE user_id=@userId AND field=@field",
                new { userId, field, s = confirm ? "confirmed" : "rejected", at = Clock.Iso() }, tx);
            if (n == 0)
                throw new KeyNotFoundException($"no profile field '{field}'");
            Db.Log(c, tx, userId, actorId, confirm ? "profile.confirm" : "profile.reject", field);
        });
    }

    /// <summary>The profile, with user-only fields shown as unknown when absent. Null when no résumé is stored.</summary>
    public CandidateProfile? Load(string userId)
    {
        var resume = db.Read(c => c.QuerySingleOrDefault<(string Text, string Sha)?>(
            "SELECT text, sha256 FROM resume_versions WHERE user_id=@userId AND is_current=1", new { userId }));
        var rows = db.Read(c => c.Query<(string Field, string? Value, string Source, string Status, string? Evidence)>(
            "SELECT field, value_json, source, status, evidence FROM profile_fields WHERE user_id=@userId", new { userId }).AsList());
        var fields = rows.ToDictionary(r => r.Field,
            r => new ProfileField(r.Value is null ? null : JsonDocument.Parse(r.Value).RootElement.Clone(), r.Source, r.Status, r.Evidence), StringComparer.Ordinal);
        foreach (var f in UserOnlyFields)
            fields.TryAdd(f, new ProfileField(null, null, "unknown", null));
        if (resume is not { } r0)
            return null;
        return new CandidateProfile(userId, fields, r0.Text, r0.Sha, ResumeParser.Parse(r0.Text));
    }
}
