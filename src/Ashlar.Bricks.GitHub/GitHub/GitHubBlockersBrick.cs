using Microsoft.Extensions.Logging;
using Ashlar.Core.Domain.Bricks;
using Ashlar.Core.Domain.Execution;

namespace Ashlar.Bricks.GitHub;

/// <summary>
/// Answers "what is blocking this pull request or release?" for a github.com URL: each blocker with a link to its
/// evidence, and, separately, every fact that could not be read and why.
/// </summary>
/// <remarks>
/// A pull request is checked for draft state, conflicts, an out-of-date branch, requested changes, required
/// approvals, and failing, pending or missing required checks. A release is checked for its tag, a published (not
/// draft) release, and CI on the tagged commit. Only https://github.com URLs are accepted; any other host throws
/// <see cref="ArgumentException"/>. Requests go only to api.github.com, through the egress guard.
/// </remarks>
public sealed class GitHubBlockersBrick : Ashlar.Core.Domain.Bricks.Brick
{
    private readonly ILogger<GitHubBlockersBrick> _logger;
    private readonly Func<string?, Octokit.IGitHubClient> _clientFor;
    private readonly Func<TimeSpan, Task>? _delay;
    private readonly GitHubReadAccess? _access;

    public GitHubBlockersBrick(ILogger<GitHubBlockersBrick> logger)
        : this(logger, access: null)
    {
    }

    public GitHubBlockersBrick(ILogger<GitHubBlockersBrick> logger, GitHubReadAccess? access)
        : this(logger, token => GitHubClientFactory.Create(token), access: access)
    {
    }

    internal GitHubBlockersBrick(
        ILogger<GitHubBlockersBrick> logger, Func<string?, Octokit.IGitHubClient> clientFor, Func<TimeSpan, Task>? delay = null,
        GitHubReadAccess? access = null)
    {
        _logger = logger;
        _clientFor = clientFor;
        _delay = delay;
        _access = access;

        Id = "github.blockers";
        Name = "GitHub blockers";
        Version = "1.4.0";
        Icon = "🚧";
        Category = BrickCategory.Analysis;
        Description = "Answers what is blocking a GitHub pull request or release, with an evidence link per blocker and the facts that could not be read.";

        Interface = new BrickInterface
        {
            Inputs =
            [
                new BrickInputDefinition("url", "string", "https://github.com/<owner>/<repo>/pull/<n> or https://github.com/<owner>/<repo>/releases/tag/<tag>", required: true),
                new BrickInputDefinition("collectionCheckName", "string", "Exact name of this collection job, when known; required self checks remain circular/unknown", required: false),
                new BrickInputDefinition("githubToken", "string", "GitHub token (optional; anonymous access has a low rate limit and cannot see private repositories)", required: false),
            ],
            Outputs =
            [
                new BrickOutputDefinition("verdict", "string", "blocked, ready, unknown, merged or closed"),
                new BrickOutputDefinition("blockers", "object[]", "Each blocker: reason and evidence URL"),
                new BrickOutputDefinition("unknowns", "object[]", "Each fact that could not be read: what and why"),
                new BrickOutputDefinition("notes", "object[]", "Non-blocking observations: text and evidence URL"),
                new BrickOutputDefinition("evidence", "string[]", "Every URL the answer relies on"),
                new BrickOutputDefinition("assurance", "string", "Scoped assurance: pass, fail, unknown or not-applicable; separate from the legacy verdict"),
                new BrickOutputDefinition("evidencePack", "object", "Versioned read-only pull-request snapshot, schema 1.0; null for a release URL"),
                new BrickOutputDefinition("evidenceMarkdown", "string", "Readable rendering of evidencePack; empty for a release URL"),
                new BrickOutputDefinition("answer", "string", "The answer as Markdown; names and text read from GitHub appear as inline code"),
            ],
        };
        if (_access is not null)
            Interface = GitHubReadAccess.WithoutCallerCredential(Interface);
    }

    public override async Task<BrickOutput> ExecuteAsync(
        BrickInput input,
        ImplementationType implementation,
        IExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var url = input.Get<string>("url");
        if (!GitHubTarget.TryParse(url, out var target, out var error))
            throw new ArgumentException(error, nameof(input));

        input.TryGet<string>("githubToken", out var token);
        token = _access?.ResolveToken(target!.Owner, target.Repo, token) ?? token;
        input.TryGet<string>("collectionCheckName", out var collectionCheckName);
        var started = DateTimeOffset.UtcNow;
        var snapshot = await new GitHubBlockerReader(_clientFor(token), _delay).ReadAsync(target!).ConfigureAwait(false);
        var answer = BlockerAnalysis.Analyze(snapshot);
        _logger.LogInformation("github.blockers {Target}: {Verdict}, {Blockers} blocker(s), {Unknowns} unknown(s)",
            target!.WebUrl, answer.Verdict, answer.Blockers.Count, answer.Unknowns.Count);

        var output = new BrickOutput { Summary = answer.Summary };
        output.Set("verdict", answer.Verdict);
        output.Set("blockers", answer.Blockers.Select(b => new { reason = b.Reason, evidence = b.Evidence }).ToList());
        output.Set("unknowns", answer.Unknowns.Select(u => new { what = u.What, why = u.Why }).ToList());
        output.Set("notes", answer.Notes.Select(n => new { text = n.Text, evidence = n.Evidence }).ToList());
        output.Set("evidence", answer.Evidence.ToList());
        output.Set("answer", answer.Markdown);
        var pack = target.Kind == GitHubTargetKind.PullRequest
            ? ReleaseEvidence.Create(snapshot, answer, started, DateTimeOffset.UtcNow,
                typeof(GitHubBlockersBrick).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                    .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "unknown",
                !string.IsNullOrWhiteSpace(token), collectionCheckName, brickVersion: Version)
            : null;
        output.Set("assurance", pack?.Assurance ?? "not-applicable");
        output.Set("evidencePack", System.Text.Json.JsonSerializer.SerializeToElement(pack, ReleaseEvidence.JsonOptions));
        output.Set("evidenceMarkdown", pack is null ? string.Empty : ReleaseEvidence.Render(pack));
        return output;
    }
}
