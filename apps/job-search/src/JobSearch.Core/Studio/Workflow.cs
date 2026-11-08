using Dapper;
using JobSearch.Accounts;
using JobSearch.Store;

namespace JobSearch.Studio;

/// <summary>
/// The contracts a library step implements. A step is one of a few fixed kinds so that any user's step can be wired into
/// any user's workflow without hand wiring, tested on made-up postings, and shared: the inputs are a posting's public
/// fields plus up to three settings that belong to the user who adds the step.
/// </summary>
public static class StepContract
{
    /// <summary>A step that passes, flags or excludes a posting, with a note.</summary>
    public const string Check = "check";
    /// <summary>A step that computes a value shown as a tracker column.</summary>
    public const string Column = "column";

    /// <summary>The kinds a step may be.</summary>
    public static readonly string[] Kinds = [Check, Column];

    /// <summary>The posting fields every step receives, in order.</summary>
    public static readonly string[] PostingInputs =
        ["title", "employer", "location", "arrangement", "description", "payMin", "payMax", "skillsRequired", "skillsMissing"];

    /// <summary>The settings every step receives, in order; the user's own values, never part of shared code.</summary>
    public static readonly string[] SettingInputs = ["setting1", "setting2", "setting3"];

    /// <summary>Every input, in the order the step's method takes them.</summary>
    public static readonly string[] Inputs = [.. PostingInputs, .. SettingInputs];

    /// <summary>The verdicts a check may return.</summary>
    public static readonly string[] Verdicts = ["pass", "flag", "exclude"];

    /// <summary>The outputs of a kind.</summary>
    public static string[] Outputs(string kind) => kind switch
    {
        Check => ["verdict", "note"],
        Column => ["value"],
        _ => throw new ArgumentException($"unknown step kind {kind}", nameof(kind)),
    };

    /// <summary>What a description of each input says, for the designer and for the step writer.</summary>
    public static readonly IReadOnlyDictionary<string, string> InputHelp = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["title"] = "the posting's job title",
        ["employer"] = "the employer's name",
        ["location"] = "the location as the posting states it; empty when not stated",
        ["arrangement"] = "remote, hybrid or onsite; empty when the posting does not say",
        ["description"] = "the posting's full text, as plain text",
        ["payMin"] = "the lowest pay figure the posting states, digits only; empty when not stated",
        ["payMax"] = "the highest pay figure the posting states, digits only; empty when not stated",
        ["skillsRequired"] = "the skills the posting requires, lowercase, one per line",
        ["skillsMissing"] = "the required skills the candidate's résumé does not show, lowercase, one per line",
        ["setting1"] = "the user's first setting for this step, as text; empty when unused",
        ["setting2"] = "the user's second setting, as text; empty when unused",
        ["setting3"] = "the user's third setting, as text; empty when unused",
    };
}

/// <summary>One step in a user's workflow: a library step, its settings, and whether it is on.</summary>
public sealed record UserStep
{
    /// <summary>Stable key within the user's workflow, e.g. <c>s1a2b3c</c>.</summary>
    public string Key { get; init; } = "";
    /// <summary>The library step (its brick id).</summary>
    public string StepId { get; init; } = "";
    /// <summary>Whether the step runs.</summary>
    public bool Enabled { get; init; } = true;
    /// <summary>The user's values for setting1–setting3.</summary>
    public List<string> Settings { get; init; } = ["", "", ""];
    /// <summary>The user's own label for the step; null shows the library name.</summary>
    public string? Label { get; init; }
}

/// <summary>A user's workflow: the steps they added to the per-posting pipeline, in order.</summary>
public sealed record WorkflowDocument
{
    /// <summary>Document version.</summary>
    public int Version { get; init; } = 1;
    /// <summary>The user's steps, in order.</summary>
    public List<UserStep> Steps { get; init; } = [];
}

/// <summary>A step resolved for one run: everything the compiler needs.</summary>
public sealed record CompiledStep(string Key, string BrickId, string Kind, string Name, IReadOnlyList<string> Settings);

/// <summary>What one of the user's steps said about one posting, kept on the assessment.</summary>
public sealed record StepResult(string Key, string Name, string Kind, string Verdict, string Text);

/// <summary>The steps a user's runs execute. The studio supplies them; without it a workflow has no user steps.</summary>
public interface IWorkflowSource
{
    /// <summary>The user's enabled, available steps, in order.</summary>
    IReadOnlyList<CompiledStep> StepsFor(string userId);
}

/// <summary>No user steps (the CLI without the studio).</summary>
public sealed class NoUserSteps : IWorkflowSource
{
    /// <summary>Shared instance.</summary>
    public static NoUserSteps Instance { get; } = new();

    /// <inheritdoc />
    public IReadOnlyList<CompiledStep> StepsFor(string userId) => [];
}

/// <summary>Stores each user's workflow document.</summary>
public sealed class WorkflowStore(Db db, AccessControl access)
{
    /// <summary>Most steps a workflow may hold.</summary>
    public const int MaxSteps = 20;

    /// <summary>The user's workflow; an empty one when they have none.</summary>
    public WorkflowDocument Get(string userId) =>
        Json.Read<WorkflowDocument>(db.Read(c => c.QuerySingleOrDefault<string>("SELECT doc_json FROM workflows WHERE user_id=@userId", new { userId })))
        ?? new WorkflowDocument();

    /// <summary>Replaces the user's workflow after checking its shape. Library membership is the caller's check.</summary>
    public void Save(string userId, string actorId, WorkflowDocument doc)
    {
        access.Authorize(actorId, userId, Scope.EditPreferences);
        Validate(doc);
        db.Write((c, tx) =>
        {
            c.Execute("""
                INSERT INTO workflows (user_id, doc_json, updated_at) VALUES (@userId, @json, @at)
                ON CONFLICT(user_id) DO UPDATE SET doc_json=excluded.doc_json, updated_at=excluded.updated_at
                """, new { userId, json = Json.Write(doc), at = Clock.Iso() }, tx);
            Db.Log(c, tx, userId, actorId, "workflow.saved", null, new { steps = doc.Steps.Count });
        });
    }

    /// <summary>Refuses a document the compiler could not use.</summary>
    public static void Validate(WorkflowDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        if (doc.Steps.Count > MaxSteps)
            throw new ArgumentException($"a workflow holds at most {MaxSteps} steps");
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in doc.Steps)
        {
            if (s.Key.Length is 0 or > 24 || !s.Key.All(ch => char.IsAsciiLetterOrDigit(ch)))
                throw new ArgumentException($"step key '{s.Key}' must be 1-24 letters or digits");
            if (!keys.Add(s.Key))
                throw new ArgumentException($"step key '{s.Key}' appears twice");
            if (string.IsNullOrWhiteSpace(s.StepId))
                throw new ArgumentException($"step '{s.Key}' names no library step");
            if (s.Settings.Count != StepContract.SettingInputs.Length)
                throw new ArgumentException($"step '{s.Key}' must carry exactly {StepContract.SettingInputs.Length} settings");
            if (s.Settings.Any(v => v is null || v.Length > 2000))
                throw new ArgumentException($"step '{s.Key}' has a setting longer than 2000 characters");
            if (s.Label is { Length: > 120 })
                throw new ArgumentException($"step '{s.Key}' has a label longer than 120 characters");
        }
    }

    /// <summary>A new step key.</summary>
    public static string NewKey() => "s" + Guid.NewGuid().ToString("N")[..8];
}
