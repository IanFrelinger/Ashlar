using System.Text.Json;
using Ashlar.Abstractions.Security.Egress;
using Ashlar.Bricks.ApiAnything.Jobs;
using Ashlar.Core.Domain.Bricks;
using Ashlar.Core.Domain.Execution;

#pragma warning disable ASHLAREXP002 // EgressRefusedException is the experimental egress surface; a refusal by the guard becomes the bricks' refused signal.

namespace Ashlar.Bricks.ApiAnything;

/// <summary>Shared client handling for the API Anything bricks.</summary>
public abstract class ApiAnythingBrickBase : Ashlar.Core.Domain.Bricks.Brick
{
    private readonly Lazy<ApiAnythingClient> _client;

    /// <summary>Creates the brick over a client, or over one built from the environment on first use.</summary>
    /// <param name="client">The client, or <see langword="null"/> for <see cref="ApiAnythingOptions.FromEnvironment"/>.</param>
    protected ApiAnythingBrickBase(ApiAnythingClient? client)
    {
        _client = new Lazy<ApiAnythingClient>(() => client ?? new ApiAnythingClient(ApiAnythingOptions.FromEnvironment()));
        // Declared so a WorkflowExecutor can run it: the implementation chain keeps only declared implementations.
        Implementations = new BrickImplementations
        {
            Deterministic = new DeterministicImplementation
            {
                Id = "code", Name = "Code", Executor = "code",
                Description = "Calls api-anything serve over HTTP; no model is involved.",
            },
        };
    }

    /// <summary>The client.</summary>
    protected ApiAnythingClient Client => _client.Value;

    /// <summary>Runs <paramref name="call"/>, turning a refusal or a client failure into <c>(signal, detail)</c>.</summary>
    /// <param name="call">The work.</param>
    /// <returns><see langword="null"/> when it ran; otherwise the signal (<c>refused</c> or <c>error</c>) and why.</returns>
    protected static async Task<(string Signal, string Detail)?> Guarded(Func<Task> call)
    {
        ArgumentNullException.ThrowIfNull(call);
        try
        {
            await call().ConfigureAwait(false);
            return null;
        }
        catch (EgressRefusedException ex)
        {
            return ("refused", $"The egress guard refused the website this call would reach: {ex.Message}");
        }
        catch (ApiAnythingException ex)
        {
            return ("error", ex.Message);
        }
    }
}

/// <summary>
/// <c>api-anything.call</c>: runs one read operation of any site <c>api-anything serve</c> knows, behind the egress
/// guard, and answers with the server's verdict fields rather than a bare dump.
/// </summary>
public sealed class ApiAnythingCallBrick : ApiAnythingBrickBase
{
    /// <summary>Creates the brick with a client built from the environment.</summary>
    public ApiAnythingCallBrick() : this(null) { }

    /// <summary>Creates the brick.</summary>
    /// <param name="client">The client.</param>
    public ApiAnythingCallBrick(ApiAnythingClient? client) : base(client)
    {
        Id = "api-anything.call";
        Name = "API Anything call";
        Version = "1.0.0";
        Icon = "🔌";
        Category = BrickCategory.Input;
        Description = "Runs one read operation that api-anything serve learned from a website's own requests. The website's host is checked by the egress guard before the call is sent.";
        Interface = new BrickInterface
        {
            Inputs =
            [
                new BrickInputDefinition("site", "string", "Site name, as `api-anything sites` lists it", required: true),
                new BrickInputDefinition("op", "string", "Operation name, as `api-anything ops <site>` lists it", required: true),
                new BrickInputDefinition("args", "string", "Arguments as a JSON object of strings", required: false, defaultValue: "{}"),
            ],
            Outputs =
            [
                new BrickOutputDefinition("ok", "bool", "Whether the site answered with data"),
                new BrickOutputDefinition("class", "string", "ok, input, drift, auth, rate, blocked, error, refused (refused also when the egress guard said no)"),
                new BrickOutputDefinition("tier", "int", "Transport tier that answered (1 = plain HTTP); 0 when it did not run"),
                new BrickOutputDefinition("data", "string", "The operation's data as JSON; empty when none"),
                new BrickOutputDefinition("reason", "string", "Why it failed, when it failed"),
                new BrickOutputDefinition("next", "string", "What to do next, in the server's words"),
                new BrickOutputDefinition("upstream", "string", "The website host the egress guard evaluated"),
            ],
        };
    }

    /// <inheritdoc />
    public override async Task<BrickOutput> ExecuteAsync(BrickInput input, ImplementationType implementation, IExecutionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var site = input.Get<string>("site");
        var op = input.Get<string>("op");
        var argsJson = input.Get("args", "{}") ?? "{}";
        var output = new BrickOutput();

        Dictionary<string, string> args;
        try
        {
            args = JsonSerializer.Deserialize<Dictionary<string, string>>(argsJson) ?? new Dictionary<string, string>();
        }
        catch (JsonException ex)
        {
            return Fill(output, false, "input", 0, "", $"args must be a JSON object of strings: {ex.Message}", "fix the args", "");
        }

        ApiAnythingResult? result = null;
        var failure = await Guarded(async () => result = await Client.CallAsync(site, op, args, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
        if (failure is { } f)
            return Fill(output, false, f.Signal == "refused" ? "refused" : "error", 0, "", f.Detail, "", "");

        var r = result!;
        return Fill(output, r.Ok, r.Class, r.Tier ?? 0, r.Data?.GetRawText() ?? "", r.Reason ?? "", r.Next ?? "", r.Upstream);
    }

    private static BrickOutput Fill(BrickOutput output, bool ok, string cls, int tier, string data, string reason, string next, string upstream)
    {
        output.Set("ok", ok);
        output.Set("class", cls);
        output.Set("tier", tier);
        output.Set("data", data);
        output.Set("reason", reason);
        output.Set("next", next);
        output.Set("upstream", upstream);
        output.Summary = ok ? $"{cls} (tier {tier}) from {upstream}" : $"{cls}: {reason}";
        return output;
    }
}

/// <summary><c>jobs.list-board</c>: the open postings on one employer board (Greenhouse, Lever or Ashby).</summary>
public sealed class JobBoardBrick : ApiAnythingBrickBase
{
    /// <summary>Creates the brick with a client built from the environment.</summary>
    public JobBoardBrick() : this(null) { }

    /// <summary>Creates the brick.</summary>
    /// <param name="client">The client.</param>
    public JobBoardBrick(ApiAnythingClient? client) : base(client)
    {
        Id = "jobs.list-board";
        Name = "Employer job board";
        Version = "1.0.0";
        Icon = "🗂️";
        Category = BrickCategory.Input;
        Description = "Lists the postings on a public employer board through api-anything (greenhouse, lever, ashby). Read-only and logged out.";
        Interface = new BrickInterface
        {
            Inputs =
            [
                new BrickInputDefinition("board", "string", "greenhouse:<slug>, lever:<slug> or ashby:<slug>, optionally |Display Name", required: true),
                new BrickInputDefinition("maxPages", "int", "Greenhouse pages to read (50 postings each)", required: false, defaultValue: 3),
            ],
            Outputs =
            [
                new BrickOutputDefinition("signal", "string", "ok, partial (a later page failed), error or refused"),
                new BrickOutputDefinition("entries", "object[]", "BoardEntry list"),
                new BrickOutputDefinition("detail", "string", "What happened, with the source's reason when it failed"),
            ],
        };
    }

    /// <inheritdoc />
    public override async Task<BrickOutput> ExecuteAsync(BrickInput input, ImplementationType implementation, IExecutionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var spec = input.Get<string>("board");
        var maxPages = Math.Clamp(input.Get("maxPages", 3), 1, 20);
        var output = new BrickOutput();
        var entries = new List<BoardEntry>();
        if (!BoardRef.TryParse(spec, out var board, out var error))
            return Fill(output, "error", entries, error);

        string signal = "ok", detail = "";
        var failure = await Guarded(async () =>
        {
            var pages = board!.Site == "greenhouse" ? maxPages : 1;
            for (var page = 1; page <= pages; page++)
            {
                var args = new Dictionary<string, string>(StringComparer.Ordinal) { ["company"] = board.Slug };
                if (board.Site == "greenhouse")
                    args["page"] = page.ToString(System.Globalization.CultureInfo.InvariantCulture);
                var result = await Client.CallAsync(board.Site, "listJobs", args, cancellationToken).ConfigureAwait(false);
                if (!result.Ok)
                {
                    (signal, detail) = entries.Count > 0 ? ("partial", $"page {page}: {result.Class}: {result.Reason}") : ("error", $"{result.Class}: {result.Reason} (next: {result.Next})");
                    return;
                }
                var parsed = JobSources.ParseBoard(board, result.Data);
                entries.AddRange(parsed);
                if (parsed.Count == 0)
                    break;
            }
            detail = $"{entries.Count} postings on {board.Site}:{board.Slug}";
        }).ConfigureAwait(false);
        if (failure is { } f)
            (signal, detail) = f;
        return Fill(output, signal, entries, detail);
    }

    private static BrickOutput Fill(BrickOutput output, string signal, IReadOnlyList<BoardEntry> entries, string detail)
    {
        output.Set("signal", signal);
        output.Set("entries", entries);
        output.Set("detail", detail);
        output.Summary = $"{signal}: {detail}";
        return output;
    }
}

/// <summary>
/// <c>jobs.get-posting</c>: reads one posting at its source. <c>found</c> means the employer's own source answered
/// with it today; <c>gone</c> means the source answered <c>input</c> (closed or unknown, per each op's notes), which is
/// a signal to corroborate, not proof of closure on its own; <c>error</c> and <c>refused</c> mean it was not read.
/// </summary>
public sealed class JobPostingBrick : ApiAnythingBrickBase
{
    /// <summary>Creates the brick with a client built from the environment.</summary>
    public JobPostingBrick() : this(null) { }

    /// <summary>Creates the brick.</summary>
    /// <param name="client">The client.</param>
    public JobPostingBrick(ApiAnythingClient? client) : base(client)
    {
        Id = "jobs.get-posting";
        Name = "Employer job posting";
        Version = "1.0.0";
        Icon = "📄";
        Category = BrickCategory.Input;
        Description = "Reads one posting from the employer's applicant-tracking system through api-anything and says whether the source still has it.";
        Interface = new BrickInterface
        {
            Inputs =
            [
                new BrickInputDefinition("url", "string", "A posting URL on greenhouse, lever, ashby, workable or Workday's own tenant", required: false, defaultValue: ""),
                new BrickInputDefinition("posting", "string", "A PostingRef key (site:arg=value&...), instead of url", required: false, defaultValue: ""),
                new BrickInputDefinition("employer", "string", "Employer display name", required: false, defaultValue: ""),
            ],
            Outputs =
            [
                new BrickOutputDefinition("signal", "string", "found, gone, error or refused"),
                new BrickOutputDefinition("provesOpen", "bool", "Whether found is evidence the posting is open (false for Workday)"),
                new BrickOutputDefinition("posting", "object", "JobPosting when found"),
                new BrickOutputDefinition("detail", "string", "What the source answered"),
            ],
        };
    }

    /// <inheritdoc />
    public override async Task<BrickOutput> ExecuteAsync(BrickInput input, ImplementationType implementation, IExecutionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var url = input.Get("url", "") ?? "";
        var key = input.Get("posting", "") ?? "";
        var employer = input.Get("employer", "") ?? "";
        var output = new BrickOutput();

        PostingRef? source;
        string? defaultEmployer = null;
        if (url.Length > 0)
        {
            var parsed = JobSources.FromUrl(url);
            source = parsed?.Ref;
            defaultEmployer = parsed?.Employer;
        }
        else
        {
            source = PostingRefs.TryParseKey(key);
        }
        if (source is null)
            return Fill(output, "error", false, null, "unsupported or malformed posting URL/ref");

        ApiAnythingResult? result = null;
        var failure = await Guarded(async () => result = await Client.CallAsync(source.Site, "getJob", source.Args, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
        if (failure is { } f)
            return Fill(output, f.Signal, false, null, f.Detail);

        var r = result!;
        if (!r.Ok)
        {
            return r.Class == "input"
                ? Fill(output, "gone", false, null, $"{source.Site} answered input: {r.Reason}")
                : Fill(output, "error", false, null, $"{source.Site} answered {r.Class}: {r.Reason} (next: {r.Next})");
        }
        var posting = JobSources.ParsePosting(source, employer.Length > 0 ? employer : defaultEmployer, r.Data);
        return posting is null
            ? Fill(output, "gone", false, null, $"{source.Site} returned no posting")
            : Fill(output, "found", posting.ProvesOpen, posting, $"{source.Site} getJob ok (tier {r.Tier})");
    }

    private static BrickOutput Fill(BrickOutput output, string signal, bool provesOpen, JobPosting? posting, string detail)
    {
        output.Set("signal", signal);
        output.Set("provesOpen", provesOpen);
        if (posting is not null)
            output.Set("posting", posting);
        output.Set("detail", detail);
        output.Summary = $"{signal}: {detail}";
        return output;
    }
}

/// <summary>Parsing for <see cref="PostingRef.Key"/>.</summary>
public static class PostingRefs
{
    /// <summary>Parses <c>site:k=v&amp;k=v</c>.</summary>
    /// <param name="key">The key.</param>
    /// <returns>The ref, or <see langword="null"/>.</returns>
    public static PostingRef? TryParseKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return null;
        var colon = key.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0)
            return null;
        var site = key[..colon];
        if (!ApiAnythingClient.KnownUpstreams.ContainsKey(site))
            return null;
        var args = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in key[(colon + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
                return null;
            args[pair[..eq]] = pair[(eq + 1)..];
        }
        return args.Count == 0 ? null : new PostingRef(site, args);
    }
}
