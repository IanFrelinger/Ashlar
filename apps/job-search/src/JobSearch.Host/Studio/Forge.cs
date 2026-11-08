using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using JobSearch.Accounts;
using JobSearch.Privacy;
using JobSearch.Profile;
using JobSearch.Store;
using JobSearch.Studio;

#pragma warning disable ASHLAREXP002 // EgressRefusedException is the experimental egress surface; a refused model call is reported to the user by design.

namespace JobSearch.Host.Studio;

/// <summary>Limits on new steps (STUDIO.md I3).</summary>
public sealed record ForgeLimits
{
    /// <summary>New steps a user may have written per calendar month; reuse does not count.</summary>
    public int MonthlyNewSteps { get; init; } = 10;
    /// <summary>Tokens the external writer may spend per UTC day, across all users.</summary>
    public long DailyExternalTokens { get; init; } = 2_000_000;
    /// <summary>Write attempts per step (each certification refusal goes back to the writer).</summary>
    public int MaxAttempts { get; init; } = 3;
    /// <summary>Longest request a user may type.</summary>
    public int MaxRequestLength { get; init; } = 2000;
}

/// <summary>A forge job as the designer shows it.</summary>
public sealed record ForgeJob
{
    /// <summary>Id.</summary>
    public string Id { get; init; } = "";
    /// <summary>queued, designing, writing, certifying, done, needs-input, failed.</summary>
    public string Status { get; init; } = "";
    /// <summary>What happened, in order, for the user.</summary>
    public List<string> Log { get; init; } = [];
    /// <summary>The step added to the workflow, when done.</summary>
    public string? StepId { get; init; }
    /// <summary>The workflow key it was added under.</summary>
    public string? StepKey { get; init; }
    /// <summary>True when an existing library step was reused (no tokens).</summary>
    public bool Reused { get; init; }
    /// <summary>True when the new step stays private.</summary>
    public bool Private { get; init; }
    /// <summary>A question for the user (needs-input).</summary>
    public string? Question { get; init; }
    /// <summary>Why it failed.</summary>
    public string? Error { get; init; }
    /// <summary>Example cases of the new step, for the user to check.</summary>
    public List<StepCase> Examples { get; init; } = [];
    /// <summary>When it was asked for.</summary>
    public string CreatedAt { get; init; } = "";
}

/// <summary>
/// Turns what a user types into a step: reuse one from the library, or design, write, certify and publish a new one.
/// Personal data stays on this host: the user's words go only to the private model; the external writer receives a
/// generic design after the PII gate passes it, inside a PII-free egress frame (STUDIO.md §3, §4).
/// </summary>
public sealed class ForgeService
{
    private const string Schema = """
        CREATE TABLE IF NOT EXISTS forge_jobs (
            id TEXT PRIMARY KEY, user_id TEXT NOT NULL REFERENCES users(id), status TEXT NOT NULL, request_text TEXT, request_sha TEXT NOT NULL,
            make_private INTEGER NOT NULL, wrote INTEGER NOT NULL DEFAULT 0, job_json TEXT NOT NULL, created_at TEXT NOT NULL, updated_at TEXT NOT NULL);
        CREATE INDEX IF NOT EXISTS forge_jobs_user ON forge_jobs(user_id, created_at);
        CREATE TABLE IF NOT EXISTS forge_usage (
            id INTEGER PRIMARY KEY AUTOINCREMENT, user_id TEXT NOT NULL, job_id TEXT NOT NULL, at TEXT NOT NULL, tier TEXT NOT NULL,
            model TEXT NOT NULL, purpose TEXT NOT NULL, input_tokens INTEGER NOT NULL, output_tokens INTEGER NOT NULL);
        """;

    private readonly Db _db;
    private readonly ProfileService _profiles;
    private readonly WorkflowStore _workflows;
    private readonly StepLibrary _library;
    private readonly StepCertifier _certifier;
    private readonly IPrivateModel _private;
    private readonly IStepWriter _writer;
    private readonly ForgeLimits _limits;
    private readonly string _workDir;
    private readonly SemaphoreSlim _signal = new(0);

    /// <summary>Wires the forge.</summary>
    public ForgeService(Db db, ProfileService profiles, WorkflowStore workflows, StepLibrary library, StepCertifier certifier,
        IPrivateModel privateModel, IStepWriter writer, ForgeLimits limits, string workDir)
    {
        _db = db;
        _profiles = profiles;
        _workflows = workflows;
        _library = library;
        _certifier = certifier;
        _private = privateModel;
        _writer = writer;
        _limits = limits;
        _workDir = workDir;
        db.Read(c => c.Execute(Schema));
    }

    /// <summary>The private model, for the designer's status line.</summary>
    public string PrivateModel => _private.Describe();
    /// <summary>The external writer.</summary>
    public string Writer => _writer.Describe();

    /// <summary>Queues a request. Only the user themselves may ask (it spends their allowance and reads their words).</summary>
    public string Submit(string userId, string actorId, string request, bool makePrivate)
    {
        if (actorId != userId)
            throw new AccessDeniedException("only the user can ask for a new step");
        request = (request ?? "").Trim();
        if (request.Length is 0)
            throw new ArgumentException("describe what the step should do");
        if (request.Length > _limits.MaxRequestLength)
            throw new ArgumentException($"keep the description under {_limits.MaxRequestLength} characters");
        var id = "fj" + Guid.NewGuid().ToString("N")[..12];
        var job = new ForgeJob { Id = id, Status = "queued", Log = ["Queued."], Private = makePrivate, CreatedAt = Clock.Iso() };
        _db.Write((c, tx) =>
        {
            c.Execute("""
                INSERT INTO forge_jobs (id, user_id, status, request_text, request_sha, make_private, job_json, created_at, updated_at)
                VALUES (@id, @userId, 'queued', @request, @sha, @makePrivate, @json, @at, @at)
                """, new { id, userId, request, sha = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(request))), makePrivate = makePrivate ? 1 : 0, json = JobSearch.Store.Json.Write(job), at = Clock.Iso() }, tx);
            Db.Log(c, tx, userId, actorId, "forge.requested", id, new { makePrivate });
        });
        _signal.Release();
        return id;
    }

    /// <summary>A job of the user's, or null.</summary>
    public ForgeJob? Get(string userId, string jobId) =>
        JobSearch.Store.Json.Read<ForgeJob>(_db.Read(c => c.QuerySingleOrDefault<string>("SELECT job_json FROM forge_jobs WHERE id=@jobId AND user_id=@userId", new { jobId, userId })));

    /// <summary>The user's recent jobs.</summary>
    public IReadOnlyList<ForgeJob> Recent(string userId, int limit = 20) =>
        _db.Read(c => c.Query<string>("SELECT job_json FROM forge_jobs WHERE user_id=@userId ORDER BY created_at DESC LIMIT @limit", new { userId, limit }))
            .Select(j => JobSearch.Store.Json.Read<ForgeJob>(j)!).ToList();

    /// <summary>New steps the user has had written this month, and the limit.</summary>
    public (int Used, int Limit) Allowance(string userId)
    {
        var month = Clock.Now.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        var used = _db.Read(c => c.ExecuteScalar<int>("SELECT COUNT(*) FROM forge_jobs WHERE user_id=@userId AND wrote=1 AND substr(created_at,1,7)=@month", new { userId, month }));
        return (used, _limits.MonthlyNewSteps);
    }

    /// <summary>Tokens the user's requests have used, by tier.</summary>
    public IReadOnlyList<(string Tier, long Tokens)> Usage(string userId) =>
        _db.Read(c => c.Query<(string, long)>("SELECT tier, SUM(input_tokens + output_tokens) FROM forge_usage WHERE user_id=@userId GROUP BY tier", new { userId }).AsList());

    /// <summary>Runs queued jobs until cancelled (the background worker).</summary>
    public async Task WorkAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (!await RunNextAsync(ct).ConfigureAwait(false))
            {
                try { await _signal.WaitAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    /// <summary>Runs the oldest queued job; false when there was none.</summary>
    public async Task<bool> RunNextAsync(CancellationToken ct)
    {
        var next = _db.Read(c => c.QuerySingleOrDefault<(string Id, string UserId, string? Request, long MakePrivate)?>(
            "SELECT id, user_id, request_text, make_private FROM forge_jobs WHERE status='queued' ORDER BY created_at LIMIT 1"));
        if (next is not { } job)
            return false;
        await ProcessAsync(job.Id, job.UserId, job.Request ?? "", job.MakePrivate == 1, ct).ConfigureAwait(false);
        return true;
    }

    private async Task ProcessAsync(string jobId, string userId, string request, bool makePrivate, CancellationToken ct)
    {
        var job = Get(userId, jobId)!;
        void Update(Func<ForgeJob, ForgeJob> change, bool? wrote = null)
        {
            job = change(job);
            _db.Write((c, tx) => c.Execute(
                "UPDATE forge_jobs SET status=@status, job_json=@json, updated_at=@at" + (wrote is true ? ", wrote=1" : "") + " WHERE id=@jobId",
                new { status = job.Status, json = JobSearch.Store.Json.Write(job), at = Clock.Iso(), jobId }, tx));
        }
        void Say(string status, string line) => Update(j => j with { Status = status, Log = [.. j.Log, line] });

        try
        {
            // 1. Match or design, on the private model: the user's words are personal data and never leave the host.
            Say("designing", $"Reading your request on the private model ({_private.Describe()}).");
            Proposal proposal;
            CandidateProfile? profile;
            using (Pii.EnterPersonalFrame("forge-design"))
            {
                profile = _profiles.Load(userId);
                var candidates = _library.Search(request, userId, 12);
                var reply = await _private.CompleteJsonAsync(Prompts.DesignSystem, Prompts.DesignUser(request, candidates), Prompts.DesignSchema, ct).ConfigureAwait(false);
                Record(userId, jobId, "private", reply, "design");
                proposal = Prompts.ParseProposal(reply.Text);
            }
            var settings = proposal.Settings.Concat(Enumerable.Repeat("", 3)).Take(3).ToList();

            if (proposal.Decision == "unclear")
            {
                Update(j => j with { Status = "needs-input", Question = proposal.Question, Log = [.. j.Log, "The request needs one more detail."] });
                Finish(jobId);
                return;
            }
            if (proposal.Decision == "reuse" && _library.Get(proposal.ReuseStepId) is { } existing && StepLibrary.VisibleTo(existing, userId) && existing.Status == "active")
            {
                var key = AddToWorkflow(userId, existing.Id, settings);
                Update(j => j with { Status = "done", StepId = existing.Id, StepKey = key, Reused = true, Log = [.. j.Log, $"Reused \"{existing.Name}\" from the shared library: no new code, no Claude tokens."] });
                Finish(jobId);
                return;
            }

            // 2. A new step. Check the allowance and the operator's daily budget before spending anything off-host.
            var (used, limit) = Allowance(userId);
            if (used >= limit)
                throw new ForgeRefusal($"you have used all {limit} new steps for this month; reusing library steps is still free");
            var design = Prompts.Validate(proposal.Design);

            // 3. The PII gate, before the design may leave the host. One private rewrite, then the private model writes it.
            var gate = new PiiGate(profile, settings);
            var findings = gate.Inspect(Prompts.DesignParts(design));
            if (findings.Count > 0)
            {
                Say("designing", $"The design still held identifying details ({PiiGate.Describe(findings)}); asking the private model to make it generic.");
                using (Pii.EnterPersonalFrame("forge-redesign"))
                {
                    var reply = await _private.CompleteJsonAsync(Prompts.DesignSystem, Prompts.Generalize(design, findings), Prompts.DesignOnlySchema, ct).ConfigureAwait(false);
                    Record(userId, jobId, "private", reply, "generalize");
                    design = Prompts.Validate(Prompts.ParseDesign(reply.Text));
                }
                findings = gate.Inspect(Prompts.DesignParts(design));
            }
            var keepOnHost = findings.Count > 0;
            if (keepOnHost)
            {
                makePrivate = true;
                Say("writing", "The design could not be made generic, so the private model writes the step on this server and it stays private to you.");
            }

            // 4. Write, certify, repair.
            var stepId = StepSource.NewId(design.Name);
            Update(j => j, wrote: true);
            string? feedback = null;
            StepCode? code = null;
            CertifyOutcome? outcome = null;
            for (var attempt = 1; attempt <= _limits.MaxAttempts; attempt++)
            {
                Say("writing", keepOnHost
                    ? $"Attempt {attempt}: the private model is writing the step."
                    : $"Attempt {attempt}: {_writer.Describe()} is writing the step from the generic design (no personal data).");
                ModelReply written;
                if (keepOnHost)
                {
                    using (Pii.EnterPersonalFrame("forge-write-private"))
                        written = await _private.CompleteJsonAsync(Prompts.WriteSystem(design.Kind), Prompts.WriteUser(design, feedback), Prompts.WriteSchema(design.Kind), ct).ConfigureAwait(false);
                    Record(userId, jobId, "private", written, "write");
                }
                else
                {
                    if (ExternalTokensToday() >= _limits.DailyExternalTokens)
                        throw new ForgeRefusal("the studio's daily budget for writing new steps is used up; try again tomorrow");
                    // Nothing in this frame has read personal data; the guard refuses the call if anything did.
                    using (Pii.EnterPiiFreeFrame("forge-write"))
                        written = await _writer.WriteJsonAsync(Prompts.WriteSystem(design.Kind), Prompts.WriteUser(design, feedback), Prompts.WriteSchema(design.Kind), ct).ConfigureAwait(false);
                    Record(userId, jobId, "external", written, "write");
                }
                code = Prompts.ParseCode(design.Kind, written.Text);
                var problems = StepSource.Lint(design.Kind, code.Code).Concat(StepSource.CheckCases(design.Kind, code.Cases)).ToList();
                if (problems.Count > 0)
                {
                    feedback = "The code or cases were refused before certification:\n- " + string.Join("\n- ", problems);
                    Say("writing", $"Attempt {attempt} refused before certification: {string.Join("; ", problems)}");
                    continue;
                }
                Say("certifying", $"Attempt {attempt}: certifying (compile, IL fence, {code.Cases.Count} witness cases, mutation testing, determinism).");
                outcome = await _certifier.CertifyAsync(stepId, design, code, Path.Combine(_workDir, jobId, $"attempt-{attempt}"), ct).ConfigureAwait(false);
                if (outcome.Admitted)
                    break;
                feedback = $"Ashlar's certification gate refused the step at the {outcome.Check} check:\n{outcome.Reason}\n\nThe source it judged, with line numbers:\n{Numbered(StepSource.Render(stepId, design, code.Code))}";
                Say("writing", $"Attempt {attempt} refused at the {outcome.Check} check.");
            }
            if (outcome is not { Admitted: true } || code is null)
                throw new ForgeRefusal($"the step could not be certified in {_limits.MaxAttempts} attempts");

            // 5. Publish: shared unless the user asked otherwise or the code holds anything the gate recognises.
            var shared = !makePrivate;
            if (shared)
            {
                // The writer saw only the gated design, so it cannot have learned the user's setting values; a match on
                // one of those would be coincidence. Profile strings and identifier shapes are still refused, as defence
                // in depth.
                var leaked = new PiiGate(profile).Inspect([.. Prompts.DesignParts(design), ("code", code.Code), ("examples", JsonSerializer.Serialize(code.Cases))]);
                if (leaked.Count > 0)
                {
                    shared = false;
                    Say("certifying", $"The certified step mentions details from your profile or settings ({PiiGate.Describe(leaked)}), so it stays private to you.");
                }
            }
            var entry = _library.Publish(new LibraryEntry
            {
                Id = stepId, Kind = design.Kind, Name = design.Name, Description = design.Description, Settings = design.Settings, Behavior = design.Behavior,
                ContentHash = outcome.ContentHash, Visibility = shared ? "shared" : "private", OwnerUserId = userId,
                Origin = keepOnHost ? "private-model" : "claude", Mutants = outcome.Mutants,
            }, outcome.Folder);
            var stepKey = AddToWorkflow(userId, entry.Id, settings);
            Update(j => j with
            {
                Status = "done", StepId = entry.Id, StepKey = stepKey, Private = !shared, Examples = code.Cases.Take(8).ToList(),
                Log = [.. j.Log, shared
                    ? $"Certified ({outcome.Mutants} mutants killed) and added to the shared library and to your workflow."
                    : $"Certified ({outcome.Mutants} mutants killed) and added to your workflow; only you can see it."],
            });
            _db.Write((c, tx) => Db.Log(c, tx, userId, userId, "forge.published", entry.Id, new { jobId, shared, origin = entry.Origin }));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var message = ex switch
            {
                ForgeRefusal r => r.Message,
                Ashlar.Abstractions.Security.Egress.EgressRefusedException => "a model call was refused by the privacy guard; nothing was sent",
                _ => $"{ex.GetType().Name}: {ex.Message}",
            };
            Update(j => j with { Status = "failed", Error = message, Log = [.. j.Log, "Stopped: " + message] });
        }
        finally
        {
            Finish(jobId);
        }
    }

    /// <summary>Data minimisation: the request text is dropped once the job has finished (only its hash is kept).</summary>
    private void Finish(string jobId) =>
        _db.Write((c, tx) => c.Execute("UPDATE forge_jobs SET request_text=NULL WHERE id=@jobId AND status IN ('done','failed','needs-input')", new { jobId }, tx));

    private string AddToWorkflow(string userId, string stepId, List<string> settings)
    {
        var doc = _workflows.Get(userId);
        var key = WorkflowStore.NewKey();
        _workflows.Save(userId, userId, doc with { Steps = [.. doc.Steps, new UserStep { Key = key, StepId = stepId, Settings = settings }] });
        _library.CountUse(stepId);
        return key;
    }

    private void Record(string userId, string jobId, string tier, ModelReply reply, string purpose) =>
        _db.Write((c, tx) => c.Execute("""
            INSERT INTO forge_usage (user_id, job_id, at, tier, model, purpose, input_tokens, output_tokens)
            VALUES (@userId, @jobId, @at, @tier, @model, @purpose, @input, @output)
            """, new { userId, jobId, at = Clock.Iso(), tier, model = reply.Model, purpose, input = reply.InputTokens, output = reply.OutputTokens }, tx));

    private long ExternalTokensToday()
    {
        var day = Clock.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return _db.Read(c => c.ExecuteScalar<long?>("SELECT SUM(input_tokens + output_tokens) FROM forge_usage WHERE tier='external' AND substr(at,1,10)=@day", new { day })) ?? 0;
    }

    private static string Numbered(string source) =>
        string.Join("\n", source.Split('\n').Select((l, i) => $"{(i + 1).ToString(CultureInfo.InvariantCulture),4}: {l}"));

    /// <summary>A refusal the user should read as written.</summary>
    public sealed class ForgeRefusal(string message) : Exception(message);
}

/// <summary>The private model's answer to a request.</summary>
public sealed record Proposal(string Decision, string ReuseStepId, List<string> Settings, string Question, StepDesign Design);

/// <summary>The prompts and schemas of the forge.</summary>
public static class Prompts
{
    /// <summary>System prompt for the private model's match-or-design call.</summary>
    public const string DesignSystem = """
        You help a job seeker add one step to their automated job-search pipeline. Every step runs on every job posting and is
        one of two kinds: "check" answers pass, flag or exclude with a short note; "column" computes a short text shown as a
        column in their tracker. A step receives only these inputs: title, employer, location, arrangement (remote, hybrid,
        onsite or empty), description (the posting's text), payMin and payMax (digits or empty), skillsRequired and
        skillsMissing (lower-case, one per line), and up to three text settings.

        First decide whether a LIBRARY step already does what the REQUEST asks, possibly with settings. If one does, answer
        decision "reuse" with its id, and put the values the request implies into settings (setting1, setting2, setting3 in
        the library step's order). If the request is unclear, answer decision "unclear" with one short question.
        Otherwise answer decision "new" with a design that works for anyone: every specific value in the request (a company,
        a place, a word, an amount) becomes a setting, its value goes into settings, and the design never contains it. The
        design must not mention the user, their name, their history, or any person. "behavior" states the exact rules in
        terms of the inputs and settings, including the exact note or value text to return.
        Answer with JSON only.
        """;

    /// <summary>The user message: the request and the library candidates.</summary>
    public static string DesignUser(string request, IReadOnlyList<LibraryEntry> candidates)
    {
        var sb = new StringBuilder("REQUEST:\n").Append(request).Append("\n\nLIBRARY:\n");
        foreach (var e in candidates)
            sb.Append(e.Id).Append(" | ").Append(e.Name).Append(" | ").Append(e.Description).Append(" | kind: ").Append(e.Kind)
              .Append(" | settings: ").Append(e.Settings.Count == 0 ? "none" : string.Join("; ", e.Settings.Select(s => s.Label))).Append('\n');
        if (candidates.Count == 0)
            sb.Append("(no close matches)\n");
        return sb.Append("\nEND").ToString();
    }

    private const string DesignObject = """
        {"type":"object","additionalProperties":false,"required":["kind","name","description","settings","behavior"],
         "properties":{"kind":{"type":"string","enum":["check","column"]},"name":{"type":"string"},"description":{"type":"string"},
          "settings":{"type":"array","maxItems":3,"items":{"type":"object","additionalProperties":false,"required":["label","help","example"],
            "properties":{"label":{"type":"string"},"help":{"type":"string"},"example":{"type":"string"}}}},
          "behavior":{"type":"string"}}}
        """;

    /// <summary>Schema of the match-or-design answer.</summary>
    public static readonly string DesignSchema = """
        {"type":"object","additionalProperties":false,"required":["decision","reuseStepId","settings","question","design"],
         "properties":{"decision":{"type":"string","enum":["reuse","new","unclear"]},"reuseStepId":{"type":"string"},
          "settings":{"type":"array","items":{"type":"string"},"maxItems":3},"question":{"type":"string"},
          "design":
        """ + DesignObject + "}}";

    /// <summary>Schema of a design alone.</summary>
    public static readonly string DesignOnlySchema = DesignObject;

    /// <summary>The prompt asking the private model to make a design generic.</summary>
    public static string Generalize(StepDesign design, IReadOnlyList<PiiFinding> findings) =>
        "Rewrite this step design so that it identifies no one. It currently contains: " + PiiGate.Describe(findings) +
        ". Replace every specific value with a setting and describe the rule generically. Answer with the design JSON only.\n\n" +
        JsonSerializer.Serialize(design, JobSearch.Store.Json.Options);

    /// <summary>The parts of a design the PII gate inspects.</summary>
    public static IEnumerable<(string Where, string Text)> DesignParts(StepDesign d) =>
    [
        ("name", d.Name), ("description", d.Description), ("behavior", d.Behavior),
        .. d.Settings.SelectMany(s => new[] { ("setting label", s.Label), ("setting help", s.Help), ("setting example", s.Example) }),
    ];

    /// <summary>Reads the private model's answer.</summary>
    public static Proposal ParseProposal(string json)
    {
        var o = JsonNode.Parse(StripFence(json))!.AsObject();
        return new Proposal(
            o["decision"]?.GetValue<string>() ?? "unclear",
            o["reuseStepId"]?.GetValue<string>() ?? "",
            o["settings"]?.AsArray().Select(v => v?.GetValue<string>() ?? "").ToList() ?? [],
            o["question"]?.GetValue<string>() ?? "",
            o["design"] is JsonObject d ? DesignFrom(d) : new StepDesign());
    }

    /// <summary>Reads a design.</summary>
    public static StepDesign ParseDesign(string json) => DesignFrom(JsonNode.Parse(StripFence(json))!.AsObject());

    private static StepDesign DesignFrom(JsonObject d) => new()
    {
        Kind = d["kind"]?.GetValue<string>() ?? "",
        Name = d["name"]?.GetValue<string>() ?? "",
        Description = d["description"]?.GetValue<string>() ?? "",
        Behavior = d["behavior"]?.GetValue<string>() ?? "",
        Settings = d["settings"]?.AsArray().OfType<JsonObject>().Select(s => new StepSetting(
            s["label"]?.GetValue<string>() ?? "", s["help"]?.GetValue<string>() ?? "", s["example"]?.GetValue<string>() ?? "")).Take(3).ToList() ?? [],
    };

    /// <summary>Refuses a design the template cannot use.</summary>
    public static StepDesign Validate(StepDesign d)
    {
        if (!StepContract.Kinds.Contains(d.Kind))
            throw new ForgeService.ForgeRefusal($"the design's kind must be check or column, not \"{d.Kind}\"");
        if (d.Name.Trim().Length is 0 or > 80)
            throw new ForgeService.ForgeRefusal("the design needs a name of at most 80 characters");
        if (d.Behavior.Trim().Length is 0 or > 2000)
            throw new ForgeService.ForgeRefusal("the design needs a rule of at most 2000 characters");
        return d with
        {
            Name = d.Name.Trim(),
            Description = d.Description.Trim().Length > 300 ? d.Description.Trim()[..300] : d.Description.Trim(),
            Settings = [.. d.Settings.Take(3)],
        };
    }

    /// <summary>System prompt for writing a step's code and witness cases.</summary>
    public static string WriteSystem(string kind) => $$"""
        You write one step of a job-search pipeline as C# 12, to be certified by a gate that compiles it, runs your witness
        cases, mutation-tests it and checks that it is deterministic. Write exactly this method, as a private instance method:

            {{StepSource.MethodSignature(kind)}}

        {{(kind == StepContract.Check
            ? "It returns \"pass\", \"flag\" or \"exclude\" and sets note (empty when it passes)."
            : "It returns the column's text (at most 200 characters).")}}
        Rules the gate enforces:
        - Write only methods. Every helper must also be a private instance method. Never use the word static.
        - Use only System, System.Text (StringBuilder), System.Globalization and System.Linq. No regular expressions, files,
          network, reflection, environment, threads, time, randomness or GUIDs.
        - Do not use foreach over List, Dictionary or HashSet (their enumerators are refused); use for loops with indexes
          over arrays and strings, or LINQ.
        - Treat every input as possibly empty. Compare text case-insensitively with ToLowerInvariant and ordinal methods.
        Witness cases: write 8 to 20. Give every input in every case (use "" for unused ones) and the exact expected output.
        The gate mutates the method (flips comparisons, turns integer literals 0 into 2 and 1 into 3, changes string
        literals, negates if conditions, removes the first statement of blocks, swaps && and ||); every mutant must change
        the output of at least one case. So cover both sides of every condition, exact boundaries, every string you return,
        empty settings, and settings of one and two characters.
        Answer with JSON only.
        """;

    /// <summary>The user message for writing: the design, and the gate's feedback on a previous attempt.</summary>
    public static string WriteUser(StepDesign design, string? feedback)
    {
        var sb = new StringBuilder("DESIGN:\n").Append(JsonSerializer.Serialize(design, JobSearch.Store.Json.Options));
        sb.Append("\n\nINPUTS:\n");
        foreach (var i in StepContract.Inputs)
            sb.Append("- ").Append(i).Append(": ").Append(StepContract.InputHelp[i]).Append('\n');
        if (feedback is not null)
            sb.Append("\nYOUR PREVIOUS ATTEMPT WAS REFUSED. Fix the code or add cases so that it passes:\n").Append(feedback).Append('\n');
        return sb.ToString();
    }

    /// <summary>Schema of the writer's answer.</summary>
    public static string WriteSchema(string kind)
    {
        var caseProps = new JsonObject();
        foreach (var i in StepContract.Inputs)
            caseProps[i] = new JsonObject { ["type"] = "string" };
        foreach (var o in StepContract.Outputs(kind))
            caseProps[o] = new JsonObject { ["type"] = "string" };
        var required = new JsonArray([.. StepContract.Inputs.Concat(StepContract.Outputs(kind)).Select(n => (JsonNode)JsonValue.Create(n)!)]);
        return new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["required"] = new JsonArray("code", "cases", "notes"),
            ["properties"] = new JsonObject
            {
                ["code"] = new JsonObject { ["type"] = "string" },
                ["notes"] = new JsonObject { ["type"] = "string" },
                ["cases"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject { ["type"] = "object", ["additionalProperties"] = false, ["required"] = required, ["properties"] = caseProps },
                },
            },
        }.ToJsonString();
    }

    /// <summary>Reads the writer's answer.</summary>
    public static StepCode ParseCode(string kind, string json)
    {
        var o = JsonNode.Parse(StripFence(json))!.AsObject();
        var flat = o["cases"]?.AsArray().OfType<JsonObject>()
            .Select(c => (IReadOnlyDictionary<string, string>)c.ToDictionary(kv => kv.Key, kv => kv.Value?.ToString() ?? "", StringComparer.Ordinal))
            .ToList() ?? [];
        return new StepCode { Code = o["code"]?.GetValue<string>() ?? "", Cases = StepSource.FromFlat(kind, flat) };
    }

    private static string StripFence(string text)
    {
        var t = text.Trim();
        if (t.StartsWith("```", StringComparison.Ordinal))
        {
            var first = t.IndexOf('\n');
            var last = t.LastIndexOf("```", StringComparison.Ordinal);
            if (first > 0 && last > first)
                t = t[(first + 1)..last];
        }
        return t;
    }
}
