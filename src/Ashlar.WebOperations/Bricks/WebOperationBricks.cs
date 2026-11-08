using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ashlar.Core.Domain.Bricks;
using Ashlar.Core.Domain.Execution;
using Ashlar.WebOperations.Jobs;
using Ashlar.WebOperations.Learning;
using Ashlar.WebOperations.Specs;

namespace Ashlar.WebOperations.Bricks;

/// <summary>Shared plumbing: the client, the declared implementation, the caller's credentials.</summary>
public abstract class WebOperationBrick : Ashlar.Core.Domain.Bricks.Brick
{
    /// <summary>The execution-context variable a host sets to give a call the caller's credentials.</summary>
    public const string CredentialsVariable = "webCredentials";

    /// <summary>Creates the brick.</summary>
    protected WebOperationBrick(WebOperationClient client, string description)
    {
        Client = client ?? throw new ArgumentNullException(nameof(client));
        // Declared, so a WorkflowExecutor runs it: the implementation chain keeps only declared implementations.
        Implementations = new BrickImplementations
        {
            Deterministic = new DeterministicImplementation { Id = "code", Name = "Code", Executor = "code", Description = description },
        };
    }

    /// <summary>The client.</summary>
    protected WebOperationClient Client { get; }

    /// <summary>The caller's credential source for this execution, if the host supplied one.</summary>
    protected static ICredentialSource? Credentials(IExecutionContext context) =>
        context?.Variables is { } v && v.TryGetValue(CredentialsVariable, out var c) ? c as ICredentialSource : null;

    /// <summary>Parses a JSON object of strings, or returns the error.</summary>
    protected static (Dictionary<string, string>? Args, string? Error) ParseArgs(string json)
    {
        try
        {
            return (JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [], null);
        }
        catch (JsonException ex)
        {
            return (null, $"must be a JSON object of strings: {ex.Message}");
        }
    }
}

/// <summary><c>web.call</c>: runs one declared operation and answers with its class, data and next step.</summary>
public sealed class WebCallBrick : WebOperationBrick
{
    /// <summary>Creates the brick.</summary>
    public WebCallBrick(WebOperationClient client) : base(client, "Plain HTTP from a declared spec; no model.")
    {
        Id = "web.call";
        Name = "Web operation";
        Version = "1.0.0";
        Icon = "🔌";
        Category = BrickCategory.Input;
        Description = "Runs a website operation declared in a spec (its own data request), behind the egress guard, and answers with a classified result.";
        Interface = new BrickInterface
        {
            Inputs =
            [
                new BrickInputDefinition("site", "string", "Site name", required: true),
                new BrickInputDefinition("operation", "string", "Operation name", required: true),
                new BrickInputDefinition("args", "string", "Arguments as a JSON object of strings", required: false, defaultValue: "{}"),
                new BrickInputDefinition("maxAgeSeconds", "int", "Answer from a result this recent (reads only); 0 = always ask", required: false, defaultValue: 0),
            ],
            Outputs =
            [
                new BrickOutputDefinition("class", "string", "ok, input, drift, auth, rate, blocked, refused, duplicate, error"),
                new BrickOutputDefinition("data", "string", "The data as JSON; empty when none"),
                new BrickOutputDefinition("reason", "string", "Why it is not ok"),
                new BrickOutputDefinition("next", "string", "The one step to take next"),
                new BrickOutputDefinition("host", "string", "The host the request went to"),
                new BrickOutputDefinition("healed", "string", "What a repair during this call changed; empty when none"),
            ],
        };
    }

    /// <inheritdoc />
    public override async Task<BrickOutput> ExecuteAsync(BrickInput input, ImplementationType implementation, IExecutionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var output = new BrickOutput();
        var (args, error) = ParseArgs(input.Get("args", "{}") ?? "{}");
        WebResult r;
        if (args is null)
        {
            r = new WebResult { Class = "input", Reason = $"args {error}", Next = "fix the arguments" };
        }
        else
        {
            var maxAge = input.Get("maxAgeSeconds", 0);
            try
            {
                r = await Client.CallAsync(input.Get<string>("site"), input.Get<string>("operation"), args,
                    new CallOptions { Credentials = Credentials(context), MaxAge = maxAge > 0 ? TimeSpan.FromSeconds(maxAge) : null }, cancellationToken).ConfigureAwait(false);
            }
            catch (KeyNotFoundException ex)
            {
                r = new WebResult { Class = "input", Reason = ex.Message, Next = "list the sites and operations the store holds" };
            }
        }
        output.Set("class", r.Class);
        output.Set("data", r.Data?.ToJsonString() ?? "");
        output.Set("reason", r.Reason ?? "");
        output.Set("next", r.Next ?? "");
        output.Set("host", r.Host);
        output.Set("healed", r.Healed ?? "");
        output.Summary = r.Ok ? $"ok from {r.Host} (spec v{r.SpecVersion}{(r.Healed is null ? "" : ", repaired")})" : $"{r.Class}: {r.Reason}";
        return output;
    }
}

/// <summary>
/// <c>web.learn</c>: learns an operation from captured requests (a HAR export or explicit exchanges), verifies it with a
/// live call using arguments the captures did not use, and saves it as a new spec version only if that call answers.
/// </summary>
public sealed class WebLearnBrick : WebOperationBrick
{
    /// <summary>Creates the brick.</summary>
    public WebLearnBrick(WebOperationClient client) : base(client, "Compares captured requests; verifies with one live call; no model.")
    {
        Id = "web.learn";
        Name = "Learn a web operation";
        Version = "1.0.0";
        Icon = "🧭";
        Category = BrickCategory.Generation;
        Description = "Turns two or more captured requests into a declared operation (credentials become references), verifies it live, and saves it.";
        Interface = new BrickInterface
        {
            Inputs =
            [
                new BrickInputDefinition("site", "string", "Site name to add the operation to", required: true),
                new BrickInputDefinition("operation", "string", "Operation name", required: true),
                new BrickInputDefinition("har", "string", "A HAR export holding only the requests to learn from", required: false, defaultValue: ""),
                new BrickInputDefinition("harArgs", "string", "JSON array: the argument values behind each HAR entry, in order", required: false, defaultValue: "[]"),
                new BrickInputDefinition("exchanges", "string", "JSON array of CapturedExchange, instead of a HAR", required: false, defaultValue: ""),
                new BrickInputDefinition("hints", "string", "LearnHints JSON", required: false, defaultValue: "{}"),
                new BrickInputDefinition("verifyArgs", "string", "Argument values for the live verification call (not used in the captures)", required: true),
            ],
            Outputs =
            [
                new BrickOutputDefinition("saved", "bool", "Whether a new spec version was saved"),
                new BrickOutputDefinition("version", "int", "The saved version; 0 when not saved"),
                new BrickOutputDefinition("spec", "string", "The learned operation as JSON"),
                new BrickOutputDefinition("credentials", "string[]", "Credential references the operation needs from callers"),
                new BrickOutputDefinition("notes", "string[]", "What learning decided and why"),
                new BrickOutputDefinition("reason", "string", "Why it was not learned or not saved"),
            ],
        };
    }

    /// <inheritdoc />
    public override async Task<BrickOutput> ExecuteAsync(BrickInput input, ImplementationType implementation, IExecutionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var output = new BrickOutput();
        BrickOutput Done(bool saved, int version, string spec, IReadOnlyList<string> creds, IReadOnlyList<string> notes, string reason)
        {
            output.Set("saved", saved);
            output.Set("version", version);
            output.Set("spec", spec);
            output.Set("credentials", creds.ToArray());
            output.Set("notes", notes.ToArray());
            output.Set("reason", reason);
            output.Summary = saved ? $"learned and verified; saved as v{version}" : $"not saved: {reason}";
            return output;
        }

        IReadOnlyList<CapturedExchange> samples;
        LearnHints hints;
        Dictionary<string, string> verifyArgs;
        try
        {
            var har = input.Get("har", "") ?? "";
            samples = har.Length > 0
                ? CapturedExchange.FromHar(har, JsonSerializer.Deserialize<List<Dictionary<string, string>>>(input.Get("harArgs", "[]") ?? "[]") ?? [])
                : JsonSerializer.Deserialize<List<CapturedExchange>>(input.Get("exchanges", "") ?? "", SpecJson.Options) ?? [];
            hints = JsonSerializer.Deserialize<LearnHints>(input.Get("hints", "{}") ?? "{}", SpecJson.Options) ?? new LearnHints();
            verifyArgs = JsonSerializer.Deserialize<Dictionary<string, string>>(input.Get<string>("verifyArgs")) ?? [];
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or NullReferenceException or InvalidOperationException)
        {
            return Done(false, 0, "", [], [], $"could not read the captures: {ex.Message}");
        }

        var learned = OperationLearner.Learn(samples, hints);
        if (!learned.Learned)
            return Done(false, 0, "", [], learned.Notes, learned.Refusal!);
        var json = JsonSerializer.Serialize(learned.Operation, SpecJson.Indented);
        var (saved, verification) = await OperationLearner.VerifyAndSaveAsync(Client, input.Get<string>("site"), input.Get<string>("operation"), learned.Operation!, verifyArgs,
            new CallOptions { Credentials = Credentials(context) }, cancellationToken).ConfigureAwait(false);
        return saved is null
            ? Done(false, 0, json, learned.CredentialReferences, learned.Notes, $"the verification call answered {verification.Class}: {verification.Reason ?? "no data"}")
            : Done(true, saved.Version, json, learned.CredentialReferences, learned.Notes, "");
    }
}

/// <summary><c>jobs.list-board</c>: the postings on one employer board (Greenhouse, Lever or Ashby), from the bundled specs.</summary>
public sealed class JobBoardBrick : WebOperationBrick
{
    /// <summary>Creates the brick.</summary>
    public JobBoardBrick(WebOperationClient client) : base(client, "Reads an employer's public board API through web operations; no model.")
    {
        Id = "jobs.list-board";
        Name = "Employer job board";
        Version = "2.0.0";
        Icon = "🗂️";
        Category = BrickCategory.Input;
        Description = "Lists the open postings on a public employer board (greenhouse, lever, ashby).";
        Interface = new BrickInterface
        {
            Inputs =
            [
                new BrickInputDefinition("board", "string", "greenhouse:<slug>, lever:<slug> or ashby:<slug>, optionally |Display Name", required: true),
                new BrickInputDefinition("maxAgeSeconds", "int", "Share a board read this recent between callers (public data only)", required: false, defaultValue: 900),
            ],
            Outputs =
            [
                new BrickOutputDefinition("signal", "string", "ok, or the class of the failure (input, drift, rate, blocked, refused, error)"),
                new BrickOutputDefinition("entries", "object[]", "BoardEntry list"),
                new BrickOutputDefinition("detail", "string", "What happened"),
            ],
        };
    }

    /// <inheritdoc />
    public override async Task<BrickOutput> ExecuteAsync(BrickInput input, ImplementationType implementation, IExecutionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var output = new BrickOutput();
        var entries = new List<BoardEntry>();
        string signal, detail;
        if (!BoardRef.TryParse(input.Get<string>("board"), out var board, out var error))
        {
            (signal, detail) = ("input", error);
        }
        else
        {
            var maxAge = input.Get("maxAgeSeconds", 900);
            var r = await Client.CallAsync(board!.Site, "listJobs", new Dictionary<string, string>(StringComparer.Ordinal) { ["company"] = board.Slug },
                new CallOptions { MaxAge = maxAge > 0 ? TimeSpan.FromSeconds(maxAge) : null }, cancellationToken).ConfigureAwait(false);
            if (r.Ok && r.Data is JsonArray items)
            {
                entries.AddRange(items.OfType<JsonObject>().Select(i => JobPostings.Entry(board, i)));
                (signal, detail) = ("ok", $"{entries.Count} postings on {board.Site}:{board.Slug}{(r.Healed is null ? "" : $" (spec repaired: {r.Healed})")}");
            }
            else
            {
                (signal, detail) = (r.Class, $"{board.Site}:{board.Slug} answered {r.Class}: {r.Reason} (next: {r.Next})");
            }
        }
        output.Set("signal", signal);
        output.Set("entries", entries);
        output.Set("detail", detail);
        output.Summary = $"{signal}: {detail}";
        return output;
    }
}

/// <summary>
/// <c>jobs.get-posting</c>: reads one posting at its source. <c>found</c>: the source answered with it today.
/// <c>gone</c>: the source answered <c>input</c> (closed or unknown) or the posting declares an expiry date that has
/// passed: a signal to corroborate, not proof of closure. Anything else: not read, with the class and reason.
/// </summary>
public sealed class JobPostingBrick : WebOperationBrick
{
    /// <summary>Creates the brick.</summary>
    public JobPostingBrick(WebOperationClient client) : base(client, "Reads one posting through web operations; no model.")
    {
        Id = "jobs.get-posting";
        Name = "Employer job posting";
        Version = "2.0.0";
        Icon = "📄";
        Category = BrickCategory.Input;
        Description = "Reads one posting from the employer's applicant-tracking API, or from the schema.org JobPosting a career page declares.";
        Interface = new BrickInterface
        {
            Inputs =
            [
                new BrickInputDefinition("url", "string", "A posting URL", required: false, defaultValue: ""),
                new BrickInputDefinition("posting", "string", "A PostingRef key, instead of url", required: false, defaultValue: ""),
                new BrickInputDefinition("employer", "string", "Employer display name", required: false, defaultValue: ""),
                new BrickInputDefinition("today", "string", "yyyy-MM-dd, for declared expiry dates (default: UTC today)", required: false, defaultValue: ""),
            ],
            Outputs =
            [
                new BrickOutputDefinition("signal", "string", "found, gone, or the class of the failure"),
                new BrickOutputDefinition("provesOpen", "bool", "Whether found is evidence the posting is open"),
                new BrickOutputDefinition("posting", "object", "JobPosting when found"),
                new BrickOutputDefinition("detail", "string", "What the source answered"),
            ],
        };
    }

    /// <inheritdoc />
    public override async Task<BrickOutput> ExecuteAsync(BrickInput input, ImplementationType implementation, IExecutionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var output = new BrickOutput();
        BrickOutput Fill(string signal, bool open, JobPosting? posting, string detail)
        {
            output.Set("signal", signal);
            output.Set("provesOpen", open);
            if (posting is not null) output.Set("posting", posting);
            output.Set("detail", detail);
            output.Summary = $"{signal}: {detail}";
            return output;
        }

        var url = input.Get("url", "") ?? "";
        var employer = input.Get("employer", "") is { Length: > 0 } e ? e : null;
        PostingRef? source;
        if (url.Length > 0)
        {
            var parsed = JobPostings.FromUrl(url);
            source = parsed?.Ref;
            employer ??= parsed?.Employer;
        }
        else
        {
            source = PostingRef.TryParse(input.Get("posting", ""));
        }
        if (source is null)
            return Fill("input", false, null, "not an https posting URL or posting reference");

        var today = DateOnly.TryParseExact(input.Get("today", "") ?? "", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)
            ? t : DateOnly.FromDateTime(DateTime.UtcNow);
        WebResult r;
        try
        {
            r = await Client.CallAsync(source.Site, source.Operation, source.Args, new CallOptions { Credentials = Credentials(context) }, cancellationToken).ConfigureAwait(false);
        }
        catch (KeyNotFoundException ex)
        {
            return Fill("input", false, null, ex.Message);
        }
        if (r.Class == "input")
            return Fill("gone", false, null, $"{source.Site} answered input: {r.Reason}");
        if (!r.Ok)
            return Fill(r.Class, false, null, $"{source.Site} answered {r.Class}: {r.Reason} (next: {r.Next})");
        var posting = JobPostings.Posting(source, employer, r.Data, today);
        if (posting is null)
            return Fill("gone", false, null, $"{source.Site} returned no posting");
        return posting.ProvesOpen
            ? Fill("found", true, posting, $"{source.Site} {source.Operation} ok from {r.Host}{(r.Healed is null ? "" : $" (spec repaired: {r.Healed})")}")
            : Fill("gone", false, posting, $"the posting's declared expiry date {posting.Deadline} has passed");
    }
}
