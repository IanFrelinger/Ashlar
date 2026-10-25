using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Ashlar.Abstractions.Security.Egress;
using Ashlar.Bricks.GitHub;
using Ashlar.Core.Domain.Bricks;
using Ashlar.Core.Domain.Execution;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using ExecutionContext = Ashlar.Infrastructure.Execution.ExecutionContext;

namespace Ashlar.Tests.Infrastructure.Tests.Bricks;

/// <summary>
/// <c>github.blockers</c>: the URL rule, the analysis rules on fixed facts, and the whole brick over Octokit with a
/// stub transport behind the real egress guard, so the JSON GitHub sends is what is being read.
/// </summary>
public sealed class GitHubBlockersBrickTests
{
    private const string Pr = "https://github.com/o/r/pull/7";
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";

    // ── URL rule ─────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("https://github.com/o/r/pull/7", "PullRequest", "o", "r", 7, "")]
    [InlineData("https://www.GitHub.com/o.x/r-y/pull/12/", "PullRequest", "o.x", "r-y", 12, "")]
    [InlineData("https://github.com/o/r/releases/tag/v0.4.0", "Release", "o", "r", 0, "v0.4.0")]
    [InlineData("https://github.com/o/r/releases/tag/release/2026-10", "Release", "o", "r", 0, "release/2026-10")]
    public void A_github_com_pull_request_or_release_url_parses(string url, string kind, string owner, string repo, int number, string tag)
    {
        GitHubTarget.TryParse(url, out var target, out var error).Should().BeTrue(error);
        target!.Kind.ToString().Should().Be(kind);
        (target.Owner, target.Repo, target.Number, target.Tag).Should().Be((owner, repo, number, tag));
    }

    [Theory]
    [InlineData("http://github.com/o/r/pull/7")]
    [InlineData("https://github.com.evil.example/o/r/pull/7")]
    [InlineData("https://ghe.example.com/o/r/pull/7")]
    [InlineData("https://token@github.com/o/r/pull/7")]
    [InlineData("https://github.com:8443/o/r/pull/7")]
    [InlineData("https://github.com/o/r/issues/7")]
    [InlineData("https://github.com/o/r/pull/0")]
    [InlineData("https://github.com/o/r/pull/7/files")]
    [InlineData("https://github.com/o/r/releases/tag/")]
    [InlineData("not a url")]
    public void Anything_else_is_refused(string url)
    {
        GitHubTarget.TryParse(url, out var target, out var error).Should().BeFalse();
        target.Should().BeNull();
        error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task The_brick_refuses_a_url_off_github_com_before_any_request()
    {
        var github = new StubGitHub();
        var brick = Brick(github);

        var act = () => brick.ExecuteAsync(Input("https://ghe.example.com/o/r/pull/7"), ImplementationType.Deterministic, Context());

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*github.com*");
        github.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task PrStatusBrick_refuses_a_url_off_github_com_too()
    {
        var brick = new PrStatusBrick(NullLogger<PrStatusBrick>.Instance);
        var input = new BrickInput();
        input.Set("prUrl", "https://github.com.evil.example/o/r/pull/7");

        var output = await brick.ExecuteAsync(input, ImplementationType.Deterministic, Context());

        output.Get<string>("state").Should().Be("error");
        output.Summary.Should().Contain("github.com");
    }

    // ── Analysis on fixed facts ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_reviewer_whose_latest_review_requests_changes_blocks_with_a_link_to_that_review()
    {
        var reviews = new[]
        {
            new ReviewFact("alice", "CHANGES_REQUESTED", "https://github.com/o/r/pull/7#r1", At(1)),
            new ReviewFact("alice", "APPROVED", "https://github.com/o/r/pull/7#r2", At(2)),
            new ReviewFact("bob", "APPROVED", "https://github.com/o/r/pull/7#r3", At(1)),
            new ReviewFact("bob", "CHANGES_REQUESTED", "https://github.com/o/r/pull/7#r4", At(2)),
            new ReviewFact("bob", "COMMENTED", "https://github.com/o/r/pull/7#r5", At(3)),
        };

        var answer = Analyze(OpenPr(reviews: reviews, state: "blocked"));

        answer.Verdict.Should().Be("blocked");
        answer.Blockers.Should().ContainSingle(b => b.Reason.Contains("requested changes"))
            .Which.Should().Be(new Blocker("bob requested changes.", "https://github.com/o/r/pull/7#r4"));
    }

    [Fact]
    public void Too_few_approvals_block_when_protection_is_readable()
    {
        var reviews = new[] { new ReviewFact("alice", "APPROVED", null, At(1)) };

        var answer = Analyze(OpenPr(reviews: reviews, state: "blocked", protection: new ProtectionFacts(Array.Empty<string>(), 2)));

        answer.Blockers.Should().ContainSingle().Which.Reason.Should().Be("1 of 2 required approving reviews.");
    }

    [Fact]
    public void A_required_check_that_never_reported_blocks_and_a_failing_optional_one_is_only_a_note()
    {
        var ci = Ci(
            checks: [new CheckFact("build", "completed", "success", "https://github.com/o/r/runs/1"),
                     new CheckFact("docs", "completed", "failure", "https://github.com/o/r/runs/2")]);

        var answer = Analyze(OpenPr(ci: ci, state: "blocked", protection: new ProtectionFacts(["build", "cert-gate"], 0)));

        answer.Verdict.Should().Be("blocked");
        answer.Blockers.Should().ContainSingle().Which.Should().Be(new Blocker(
            "The required check cert-gate has not reported on 0123456789ab.", $"https://github.com/o/r/commit/{Sha}"));
        answer.Notes.Should().ContainSingle().Which.Evidence.Should().Be("https://github.com/o/r/runs/2");
        answer.Unknowns.Should().BeEmpty();
    }

    [Fact]
    public void Without_protection_a_failing_check_blocks_unless_github_says_the_merge_is_allowed()
    {
        var ci = Ci(checks: [new CheckFact("build", "completed", "failure", "https://github.com/o/r/runs/1")]);
        var unreadable = new ProtectionFacts(null, null);

        var blocked = Analyze(OpenPr(ci: ci, state: "blocked", protection: unreadable));
        blocked.Verdict.Should().Be("blocked");
        blocked.Blockers.Should().ContainSingle().Which.Should().Be(
            new Blocker("The check build concluded failure.", "https://github.com/o/r/runs/1"));
        blocked.Unknowns.Select(u => u.What).Should().BeEquivalentTo("Required approvals", "Which checks are required");

        var unstable = Analyze(OpenPr(ci: ci, state: "unstable", protection: unreadable));
        unstable.Verdict.Should().Be("ready");
        unstable.Blockers.Should().BeEmpty();
        unstable.Notes.Should().ContainSingle().Which.Text.Should().Contain("not required");
        unstable.Unknowns.Should().BeEmpty();
    }

    [Fact]
    public void Mergeability_not_yet_computed_is_an_unknown_not_a_conflict()
    {
        var answer = Analyze(OpenPr(mergeable: null, state: "unknown", protection: new ProtectionFacts(Array.Empty<string>(), 0)));

        answer.Verdict.Should().Be("unknown");
        answer.Blockers.Should().BeEmpty();
        answer.Unknowns.Should().ContainSingle().Which.What.Should().Be("Mergeability");
    }

    [Fact]
    public void A_blocked_state_nothing_explains_is_still_a_blocker()
    {
        var answer = Analyze(OpenPr(state: "blocked", protection: new ProtectionFacts(Array.Empty<string>(), 0)));

        answer.Verdict.Should().Be("blocked");
        answer.Blockers.Should().ContainSingle().Which.Reason.Should().Contain("ruleset");
    }

    [Fact]
    public void Checks_not_all_read_are_an_unknown()
    {
        var ci = Ci(checks: [new CheckFact("build", "completed", "success", null)], checksReported: 3);

        var answer = Analyze(OpenPr(ci: ci, state: "clean"));

        answer.Verdict.Should().Be("unknown");
        answer.Unknowns.Should().ContainSingle().Which.Why.Should().Be("2 of 3 check runs were not read.");
    }

    [Fact]
    public void A_merged_pull_request_has_no_blockers()
    {
        var answer = Analyze(OpenPr() with { Merged = true, Closed = true });

        answer.Verdict.Should().Be("merged");
        answer.Blockers.Should().BeEmpty();
    }

    // ── The brick over Octokit, behind the real egress guard ─────────────────────────────────────────────

    [Fact]
    public async Task A_blocked_pull_request_is_answered_with_evidence_links_and_the_protection_it_could_not_read()
    {
        var github = new StubGitHub()
            .Json("/repos/o/r/pulls/7", PullJson(mergeable: "true", state: "blocked"))
            .Json("/repos/o/r/pulls/7/reviews", "[]")
            .Json($"/repos/o/r/commits/{Sha}/check-runs", """
                {"total_count":2,"check_runs":[
                  {"id":1,"name":"build","status":"completed","conclusion":"success","html_url":"https://github.com/o/r/actions/runs/1/job/1"},
                  {"id":2,"name":"cert-gate","status":"completed","conclusion":"failure","html_url":"https://github.com/o/r/actions/runs/1/job/2"}]}
                """)
            .Json($"/repos/o/r/commits/{Sha}/status", """{"state":"success","total_count":0,"statuses":[]}""")
            .Json("/repos/o/r/branches/master/protection/required_status_checks", """{"message":"Must have admin rights to Repository."}""", HttpStatusCode.NotFound)
            .Json("/repos/o/r/branches/master/protection/required_pull_request_reviews", """{"message":"Resource not accessible by integration"}""", HttpStatusCode.Forbidden);

        var output = await Brick(github).ExecuteAsync(Input(Pr), ImplementationType.Deterministic, Context());

        output.Get<string>("verdict").Should().Be("blocked");
        var json = JsonSerializer.Serialize(output.Get<object>("blockers"));
        json.Should().Contain("The check cert-gate concluded failure.").And.Contain("https://github.com/o/r/actions/runs/1/job/2");
        JsonSerializer.Serialize(output.Get<object>("unknowns")).Should().Contain("Which checks are required").And.Contain("Required approvals");
        output.Get<List<string>>("evidence").Should().Contain([Pr, "https://github.com/o/r/actions/runs/1/job/2"]);
        output.Get<string>("answer").Should().StartWith("**What is blocking PR #7?** PR #7 is BLOCKED by 1 blocker(s); 2 unknown(s).");
        github.Requests.Should().OnlyContain(u => u.Host == "api.github.com");
    }

    [Fact]
    public async Task A_pull_request_that_cannot_be_read_is_unknown_with_githubs_reason()
    {
        var github = new StubGitHub();

        var output = await Brick(github).ExecuteAsync(Input(Pr), ImplementationType.Deterministic, Context());

        output.Get<string>("verdict").Should().Be("unknown");
        JsonSerializer.Serialize(output.Get<object>("unknowns")).Should().Contain("not visible to this token");
    }

    [Fact]
    public async Task A_rate_limited_read_says_so()
    {
        var github = new StubGitHub()
            .Json("/repos/o/r/pulls/7", """{"message":"API rate limit exceeded"}""", HttpStatusCode.Forbidden,
                ("X-RateLimit-Limit", "60"), ("X-RateLimit-Remaining", "0"), ("X-RateLimit-Reset", "1893456000"));

        var output = await Brick(github).ExecuteAsync(Input(Pr), ImplementationType.Deterministic, Context());

        output.Get<string>("verdict").Should().Be("unknown");
        JsonSerializer.Serialize(output.Get<object>("unknowns")).Should().Contain("rate limit");
    }

    [Fact]
    public async Task A_missing_tag_blocks_the_release()
    {
        var github = new StubGitHub();

        var output = await Brick(github).ExecuteAsync(Input("https://github.com/o/r/releases/tag/v9"), ImplementationType.Deterministic, Context());

        output.Get<string>("verdict").Should().Be("blocked");
        output.Summary.Should().Be("Release v9 is BLOCKED by 1 blocker(s).");
        JsonSerializer.Serialize(output.Get<object>("blockers")).Should().Contain("The tag v9 does not exist.").And.Contain("https://github.com/o/r/tags");
    }

    [Fact]
    public async Task A_draft_release_with_green_ci_is_blocked_only_by_being_a_draft()
    {
        var github = new StubGitHub()
            .Json("/repos/o/r/git/ref/tags/v1", $$$"""{"ref":"refs/tags/v1","object":{"sha":"{{{Sha}}}","type":"commit"}}""")
            .Json("/repos/o/r/releases", """[{"id":1,"tag_name":"v1","draft":true,"prerelease":false,"html_url":"https://github.com/o/r/releases/tag/untagged-1","assets":[]}]""")
            .Json($"/repos/o/r/commits/{Sha}/check-runs", """{"total_count":1,"check_runs":[{"id":1,"name":"build","status":"completed","conclusion":"success"}]}""")
            .Json($"/repos/o/r/commits/{Sha}/status", """{"state":"success","total_count":0,"statuses":[]}""");

        var output = await Brick(github).ExecuteAsync(Input("https://github.com/o/r/releases/tag/v1"), ImplementationType.Deterministic, Context());

        output.Get<string>("verdict").Should().Be("blocked");
        var blockers = JsonSerializer.Serialize(output.Get<object>("blockers"));
        blockers.Should().Contain("The release is still a draft.").And.NotContain("check");
    }

    [Fact]
    public async Task Every_request_is_decided_by_the_egress_guard_as_site_EG_HTTP_08()
    {
        var github = new StubGitHub().Json("/repos/o/r/pulls/7", PullJson(mergeable: "true", state: "clean", merged: true));
        var guard = new RecordingGuard();
        var client = GitHubClientFactory.Create(null, () => github, guard);

        await new GitHubBlockersBrick(NullLogger<GitHubBlockersBrick>.Instance, _ => client)
            .ExecuteAsync(Input(Pr), ImplementationType.Deterministic, Context());

        github.Requests.Should().NotBeEmpty();
        guard.Requests.Should().HaveCount(github.Requests.Count)
            .And.OnlyContain(r => r.Site == "EG-HTTP-08" && r.Family == EgressFamilies.Http && r.Destination!.Host == "api.github.com");
    }

    [Fact]
    public async Task A_refusing_guard_stops_the_request_before_it_is_sent_and_the_refusal_is_not_swallowed()
    {
        var github = new StubGitHub().Json("/repos/o/r/pulls/7", PullJson(mergeable: "true", state: "clean"));
        var client = GitHubClientFactory.Create(null, () => github, new EgressGuard("secure-workstation", "enforce"));

        var act = () => new GitHubBlockersBrick(NullLogger<GitHubBlockersBrick>.Instance, _ => client)
            .ExecuteAsync(Input(Pr), ImplementationType.Deterministic, Context());

        await act.Should().ThrowAsync<EgressRefusedException>();
        github.Requests.Should().BeEmpty();
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────────────

    private static GitHubBlockersBrick Brick(StubGitHub github) =>
        new(NullLogger<GitHubBlockersBrick>.Instance, token => GitHubClientFactory.Create(token, () => github, new EgressGuard("full", "report")));

    private static BrickInput Input(string url)
    {
        var input = new BrickInput();
        input.Set("url", url);
        return input;
    }

    private static IExecutionContext Context() => new ExecutionContext { AgentId = "test", BehaviorId = "github.blockers" };

    private static DateTimeOffset At(int minute) => new(2026, 10, 25, 7, minute, 0, TimeSpan.Zero);

    private static BlockerAnswer Analyze(PullRequestFacts pr)
    {
        GitHubTarget.TryParse(Pr, out var target, out _);
        return BlockerAnalysis.Analyze(new BlockerSnapshot(target!, pr, null, Array.Empty<Unknown>()));
    }

    private static CiFacts Ci(CheckFact[]? checks = null, int? checksReported = null, StatusFact[]? statuses = null) =>
        new(Sha, checks ?? [], checksReported ?? (checks?.Length ?? 0), statuses ?? [], statuses?.Length ?? 0);

    private static PullRequestFacts OpenPr(
        IReadOnlyList<ReviewFact>? reviews = null,
        CiFacts? ci = null,
        bool? mergeable = true,
        string? state = "clean",
        ProtectionFacts? protection = null) =>
        new(Pr, "title", Closed: false, Merged: false, Draft: false, mergeable, state, "master",
            reviews ?? Array.Empty<ReviewFact>(), ci ?? Ci(), protection ?? new ProtectionFacts(null, null));

    private static string PullJson(string mergeable, string state, bool merged = false) => $$$"""
        {"number":7,"title":"t","state":"{{{(merged ? "closed" : "open")}}}","merged":{{{(merged ? "true" : "false")}}},"draft":false,
         "mergeable":{{{mergeable}}},"mergeable_state":"{{{state}}}","html_url":"{{{Pr}}}",
         "head":{"sha":"{{{Sha}}}","ref":"feature"},"base":{"ref":"master"}}
        """;

    private sealed class RecordingGuard : IEgressGuard
    {
        private readonly ConcurrentQueue<EgressRequest> _requests = new();

        public IReadOnlyList<EgressRequest> Requests => _requests.ToArray();

        public EgressDecision Evaluate(EgressRequest request)
        {
            _requests.Enqueue(request);
            return new EgressGuard("full", "report").Evaluate(request);
        }
    }

    /// <summary>Answers by path; anything not configured is GitHub's 404.</summary>
    private sealed class StubGitHub : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode Status, string Body, (string, string)[] Headers)> _routes = new(StringComparer.Ordinal);
        private readonly ConcurrentQueue<Uri> _requests = new();

        public IReadOnlyList<Uri> Requests => _requests.ToArray();

        public StubGitHub Json(string path, string body, HttpStatusCode status = HttpStatusCode.OK, params (string, string)[] headers)
        {
            _routes[path] = (status, body, headers);
            return this;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _requests.Enqueue(request.RequestUri!);
            var path = request.RequestUri!.AbsolutePath.Replace("/git/refs/", "/git/ref/", StringComparison.Ordinal);
            var (status, body, headers) = _routes.TryGetValue(path, out var route)
                ? route
                : (HttpStatusCode.NotFound, """{"message":"Not Found"}""", Array.Empty<(string, string)>());
            var response = new HttpResponseMessage(status)
            {
                RequestMessage = request,
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            foreach (var (name, value) in headers)
                response.Headers.TryAddWithoutValidation(name, value);
            return Task.FromResult(response);
        }
    }
}
