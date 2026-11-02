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

/// <summary>Real-wire bounded recollection; assertions use the replacement snapshot's facts.</summary>
public sealed class GitHubRecollectionTests
{
    private const string Pr = "https://github.com/o/r/pull/7";
    private const string A = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string B = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string C = "cccccccccccccccccccccccccccccccccccccccc";

    [Fact]
    public async Task A_single_head_change_replaces_earlier_failure_and_read_errors_with_fresh_facts()
    {
        var api = new Wire(new State(A), new State(B), new State(B));
        api.CheckBodies[A] = [Checks(A, "failure")];
        api.CheckBodies[B] = [Checks(B, "success")];
        api.Reviews = ["__unreadable__", "[]"];

        var output = await Execute(api);

        output.Get<string>("verdict").Should().Be("ready");
        output.Get<string>("assurance").Should().Be("pass");
        var pack = Pack(output);
        pack.GetProperty("headSha").GetString().Should().Be(B);
        Freshness(pack, 2, "stable");
        api.CheckedHeads.Should().Equal(A, B);
        api.PullReads.Should().Be(3, "the changed final read is reused as the new anchor");
        JsonSerializer.Serialize(output.Get<object>("unknowns")).Should().NotContain("Reviews");
        output.Get<string>("evidenceMarkdown").Should().Contain("Collection attempts: 2").And.NotContain("/runs/" + A);
    }

    [Fact]
    public async Task A_new_head_missing_a_required_check_cannot_borrow_the_old_heads_passing_result()
    {
        var api = new Wire(new State(A), new State(B), new State(B));
        api.CheckBodies[B] = ["""{"total_count":0,"check_runs":[]}"""];

        var output = await Execute(api);

        output.Get<string>("verdict").Should().Be("blocked");
        output.Get<string>("assurance").Should().Be("fail");
        Pack(output).GetProperty("headSha").GetString().Should().Be(B);
        output.Get<string>("evidenceMarkdown").Should().Contain("has not reported").And.Contain("/commit/" + B).And.NotContain("/runs/" + A);
        api.CheckedHeads.Should().Equal(A, B);
    }

    [Fact]
    public async Task A_changed_base_reloads_policy_for_the_new_base_instead_of_reusing_old_requirements()
    {
        var api = new Wire(new State(A), new State(A, "release", B, MergeState: "behind"),
            new State(A, "release", B, MergeState: "behind"));
        api.RequiredByBase["release"] = "deploy";

        var output = await Execute(api);

        output.Get<string>("verdict").Should().Be("blocked");
        output.Get<string>("assurance").Should().Be("fail");
        var pack = Pack(output);
        pack.GetProperty("baseRef").GetString().Should().Be("release");
        pack.GetProperty("baseSha").GetString().Should().Be(B);
        Freshness(pack, 2, "stable");
        output.Get<string>("evidenceMarkdown").Should().Contain("`deploy`").And.Contain("/branches/release/");
        api.PolicyBases.Should().Contain("release");
        api.CheckedHeads.Should().Equal(A, A);
    }

    [Fact]
    public async Task Changed_mergeability_on_the_same_head_reloads_checks_and_reviews()
    {
        var blocked = new State(A, MergeState: "blocked");
        var api = new Wire(new State(A), blocked, blocked);
        api.CheckBodies[A] = [Checks(A, "success"), Checks(A, "failure")];
        api.Reviews = ["[]", Reviews("CHANGES_REQUESTED", A)];
        api.ReviewDecisions = ["APPROVED", "CHANGES_REQUESTED"];

        var output = await Execute(api);

        output.Get<string>("verdict").Should().Be("blocked");
        output.Get<string>("assurance").Should().Be("fail");
        Freshness(Pack(output), 2, "stable");
        output.Get<string>("answer").Should().Contain("requested changes").And.Contain("concluded failure");
        api.CheckedHeads.Should().Equal(A, A);
        api.ReviewReads.Should().Be(2);
    }

    [Theory]
    [InlineData("head")]
    [InlineData("base")]
    [InlineData("mergeability")]
    [InlineData("draft")]
    public async Task Repeated_motion_stops_after_two_collections_and_cannot_give_a_current_decision(string changed)
    {
        var middle = changed switch
        {
            "base" => new State(A, BaseSha: B),
            "mergeability" => new State(A, MergeState: "blocked"),
            "draft" => new State(A, Draft: true),
            _ => new State(B),
        };
        var last = changed switch
        {
            "base" => new State(A, BaseSha: C),
            "mergeability" => new State(A),
            "draft" => new State(A, Draft: false),
            _ => new State(C),
        };
        var api = new Wire(new State(A), middle, last);
        api.CheckBodies[A] = [Checks(A, "failure")];
        api.CheckBodies[B] = [Checks(B, "failure")];

        var output = await Execute(api);

        output.Get<string>("verdict").Should().Be("unknown");
        output.Get<string>("assurance").Should().Be("unknown");
        var pack = Pack(output);
        pack.GetProperty("headSha").GetString().Should().Be(middle.Head);
        Freshness(pack, 2, "changed");
        pack.GetProperty("findings").EnumerateArray().Should().Contain(f => f.GetProperty("status").GetString() == "fail",
            "individual failed observations stay attributed to the collected anchor");
        api.CheckedHeads.Should().HaveCount(2).And.NotContain(C);
        api.PullReads.Should().Be(3);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(429)]
    [InlineData(503)]
    public async Task An_unreadable_first_verification_is_unknown_without_retrying_authorization_or_service_failures(int status)
    {
        var api = new Wire(new State(A), new State(A)) { FailingPullRead = 2, PullFailure = (HttpStatusCode)status };

        var output = await Execute(api);

        output.Get<string>("verdict").Should().Be("unknown");
        output.Get<string>("assurance").Should().Be("unknown");
        Freshness(Pack(output), 1, "unknown");
        api.CheckedHeads.Should().Equal(A);
        api.PullReads.Should().Be(2);
    }

    [Fact]
    public async Task An_unreadable_second_verification_is_unknown_even_with_a_failed_observation()
    {
        var api = new Wire(new State(A), new State(B), new State(B)) { FailingPullRead = 3 };
        api.CheckBodies[B] = [Checks(B, "failure")];

        var output = await Execute(api);

        output.Get<string>("verdict").Should().Be("unknown");
        output.Get<string>("assurance").Should().Be("unknown");
        Freshness(Pack(output), 2, "unknown");
        Pack(output).GetProperty("headSha").GetString().Should().Be(B);
        api.CheckedHeads.Should().Equal(A, B);
        api.PullReads.Should().Be(3);
    }

    [Fact]
    public async Task Malformed_metadata_on_the_second_final_read_cannot_establish_a_current_decision()
    {
        var api = new Wire(new State(A), new State(B), new State(B, Url: "javascript:alert(1)"));
        api.CheckBodies[B] = [Checks(B, "failure")];

        var output = await Execute(api);

        output.Get<string>("verdict").Should().Be("unknown");
        output.Get<string>("assurance").Should().Be("unknown");
        Freshness(Pack(output), 2, "unknown");
        Pack(output).GetProperty("headSha").GetString().Should().Be(B);
        api.CheckedHeads.Should().Equal(A, B);
        api.PullReads.Should().Be(3);
    }

    [Theory]
    [InlineData("javascript:alert(1)", B)]
    [InlineData(Pr, "bad-sha")]
    public async Task A_malformed_replacement_identity_is_never_used_as_a_recollection_anchor(string url, string sha)
    {
        var api = new Wire(new State(A), new State(sha, Url: url));

        var output = await Execute(api);

        output.Get<string>("assurance").Should().Be("unknown");
        Pack(output).GetProperty("headSha").GetString().Should().Be(A);
        Pack(output).GetProperty("freshness").GetProperty("collectionAttempts").GetInt32().Should().Be(1);
        api.CheckedHeads.Should().Equal(A);
    }

    [Fact]
    public async Task Recollection_does_not_make_an_old_commit_approval_current()
    {
        var api = new Wire(new State(A), new State(B), new State(B)) { RequiredApprovals = 1 };
        api.Reviews = [Reviews("APPROVED", A), Reviews("APPROVED", A)];
        api.ReviewDecisions = ["APPROVED", "__unreadable__"];

        var output = await Execute(api);

        output.Get<string>("assurance").Should().Be("unknown");
        var pack = Pack(output);
        pack.GetProperty("headSha").GetString().Should().Be(B);
        pack.GetProperty("findings").EnumerateArray().Should().Contain(f => f.GetProperty("id").GetString() == "stale-approvals");
        api.ReviewReads.Should().Be(2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_terminal_replacement_preserves_closed_or_merged_semantics_without_collecting_more_ci(bool merged)
    {
        var api = new Wire(new State(A), new State(B, Closed: true, Merged: merged));

        var output = await Execute(api);

        output.Get<string>("verdict").Should().Be(merged ? "merged" : "closed");
        output.Get<string>("assurance").Should().Be("not-applicable");
        Pack(output).GetProperty("headSha").GetString().Should().Be(B);
        api.CheckedHeads.Should().Equal(A);
        api.PullReads.Should().Be(2);
    }

    [Fact]
    public async Task Mergeability_polling_and_recollection_have_a_finite_shared_request_bound()
    {
        var unknownA = new State(A, Mergeable: null, MergeState: "unknown");
        var unknownB = new State(B, Mergeable: null, MergeState: "unknown");
        var api = new Wire(unknownA, unknownA, unknownA, unknownB, unknownB, unknownB, new State(C, Mergeable: null, MergeState: "unknown"));
        var waits = new List<TimeSpan>();

        var output = await Execute(api, waits);

        output.Get<string>("verdict").Should().Be("unknown");
        output.Get<string>("assurance").Should().Be("unknown");
        Freshness(Pack(output), 2, "changed");
        api.PullReads.Should().Be(7);
        waits.Should().HaveCount(4).And.OnlyContain(t => t == TimeSpan.FromSeconds(2));
        api.CheckedHeads.Should().Equal(A, B);
    }

    [Fact]
    public async Task A_stable_snapshot_does_not_recollect()
    {
        var api = new Wire(new State(A), new State(A));

        var output = await Execute(api);

        output.Get<string>("verdict").Should().Be("ready");
        output.Get<string>("assurance").Should().Be("pass");
        Freshness(Pack(output), 1, "stable");
        api.CheckedHeads.Should().Equal(A);
        api.PullReads.Should().Be(2);
    }

    private static void Freshness(JsonElement pack, int attempts, string consistency)
    {
        pack.GetProperty("freshness").GetProperty("collectionAttempts").GetInt32().Should().Be(attempts);
        pack.GetProperty("freshness").GetProperty("collectionConsistency").GetString().Should().Be(consistency);
    }

    private static JsonElement Pack(BrickOutput output) => JsonSerializer.SerializeToElement(output.Get<object>("evidencePack"));

    private static async Task<BrickOutput> Execute(Wire api, List<TimeSpan>? waits = null)
    {
        var brick = new GitHubBlockersBrick(NullLogger<GitHubBlockersBrick>.Instance,
            token => GitHubClientFactory.Create(token, () => api, new EgressGuard("full", "report")),
            wait => { waits?.Add(wait); return Task.CompletedTask; });
        var input = new BrickInput();
        input.Set("url", Pr);
        return await brick.ExecuteAsync(input, ImplementationType.Deterministic,
            new ExecutionContext { AgentId = "recollection-regression", BehaviorId = "github.blockers" });
    }

    private static string Checks(string head, string conclusion) => JsonSerializer.Serialize(new
    {
        total_count = 1,
        check_runs = new[] { new { id = 99, name = "ci", head_sha = head, status = "completed", conclusion,
            html_url = "https://github.com/o/r/runs/" + head, app = new { id = 42 } } },
    });

    private static string Reviews(string state, string head) => JsonSerializer.Serialize(new[]
    {
        new { id = 1, state, commit_id = head, user = new { login = "reviewer" },
            html_url = Pr + "#review-1", submitted_at = "2026-11-02T00:00:00Z" },
    });

    private sealed record State(string Head, string BaseRef = "main", string BaseSha = A,
        string MergeState = "clean", bool? Mergeable = true, bool Draft = false, bool Closed = false,
        bool Merged = false, string Url = Pr);

    private sealed class Wire(params State[] states) : HttpMessageHandler
    {
        public Dictionary<string, string[]> CheckBodies { get; } = [];
        public Dictionary<string, string> RequiredByBase { get; } = [];
        public string[] Reviews { get; set; } = ["[]"];
        public string[] ReviewDecisions { get; set; } = ["NONE"];
        public int RequiredApprovals { get; set; }
        public int FailingPullRead { get; set; }
        public HttpStatusCode PullFailure { get; set; } = HttpStatusCode.ServiceUnavailable;
        public int PullReads { get; private set; }
        public int ReviewReads { get; private set; }
        public List<string> CheckedHeads { get; } = [];
        public List<string> PolicyBases { get; } = [];
        private readonly Dictionary<string, int> _checkReads = [];
        private string _head = states[0].Head;
        private int _graphReads;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var code = HttpStatusCode.OK;
            string body;
            if (path == "/repos/o/r/pulls/7")
            {
                var state = states[Math.Min(PullReads++, states.Length - 1)];
                _head = state.Head;
                code = PullReads == FailingPullRead ? PullFailure : HttpStatusCode.OK;
                body = JsonSerializer.Serialize(new
                {
                    number = 7, html_url = state.Url, title = "display", state = state.Closed ? "closed" : "open",
                    merged = state.Merged, merged_at = state.Merged ? "2026-11-02T00:00:00Z" : null,
                    draft = state.Draft, mergeable = state.Mergeable, mergeable_state = state.MergeState,
                    head = new { sha = state.Head, @ref = "feature" },
                    @base = new { sha = state.BaseSha, @ref = state.BaseRef, repo = new { id = 1, full_name = "o/r" } },
                });
            }
            else if (path == "/repos/o/r/pulls/7/reviews")
                body = Reviews[Math.Min(ReviewReads++, Reviews.Length - 1)];
            else if (path.StartsWith("/repos/o/r/commits/", StringComparison.Ordinal) && path.EndsWith("/check-runs", StringComparison.Ordinal))
            {
                var head = path.Split('/')[5];
                CheckedHeads.Add(head);
                var index = _checkReads.GetValueOrDefault(head);
                _checkReads[head] = index + 1;
                body = CheckBodies.TryGetValue(head, out var sequence) ? sequence[Math.Min(index, sequence.Length - 1)] : Checks(head, "success");
            }
            else if (path.EndsWith("/status", StringComparison.Ordinal))
                body = """{"state":"success","total_count":0,"statuses":[]}""";
            else if (path.EndsWith("/required_status_checks", StringComparison.Ordinal))
            {
                var branch = path.Split('/')[5];
                PolicyBases.Add(branch);
                var name = RequiredByBase.GetValueOrDefault(branch, "ci");
                body = JsonSerializer.Serialize(new { contexts = new[] { name }, checks = new[] { new { context = name, app_id = (int?)null } } });
            }
            else if (path.EndsWith("/required_pull_request_reviews", StringComparison.Ordinal))
                body = JsonSerializer.Serialize(new { required_approving_review_count = RequiredApprovals,
                    require_code_owner_reviews = false, require_last_push_approval = false, dismiss_stale_reviews = false });
            else if (path.StartsWith("/repos/o/r/rules/branches/", StringComparison.Ordinal))
                body = "[]";
            else if (path == "/graphql")
            {
                var decision = ReviewDecisions[Math.Min(_graphReads++, ReviewDecisions.Length - 1)];
                body = decision == "__unreadable__" ? decision : JsonSerializer.Serialize(new
                {
                    data = new { repository = new { pull_request = new { review_decision = decision,
                        commits = new { nodes = new[] { new { commit = new { oid = _head,
                            status_check_rollup = new { contexts = new { nodes = Array.Empty<object>() } } } } } } } } },
                });
            }
            else
            {
                code = HttpStatusCode.NotFound;
                body = "{}";
            }
            if (body == "__unreadable__")
            {
                code = HttpStatusCode.ServiceUnavailable;
                body = "{}";
            }
            return Task.FromResult(new HttpResponseMessage(code)
            {
                RequestMessage = request,
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
