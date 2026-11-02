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

/// <summary>Real-wire regressions found by the separately authored release-evidence evaluation.</summary>
public sealed class GitHubEvaluationRegressionTests
{
    private const string Pr = "https://github.com/o/r/pull/7";
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";

    [Theory]
    [InlineData("queued")]
    [InlineData("in_progress")]
    public async Task An_unstarted_check_with_null_timestamps_is_observed_instead_of_crashing(string status)
    {
        var api = new ResponseHandler();
        api.Checks = $$"""{"total_count":1,"check_runs":[{"id":9,"name":"ci","status":"{{status}}","conclusion":null,"started_at":null,"completed_at":null,"html_url":"https://github.com/o/r/actions/runs/9"}]}""";
        api.RequiredChecks = """{"contexts":["ci"],"checks":[{"context":"ci","app_id":null}]}""";

        var output = await Execute(api);

        output.Get<string>("verdict").Should().Be("blocked");
        output.Get<string>("assurance").Should().NotBe("pass");
        var pack = Pack(output);
        pack.GetProperty("findings").EnumerateArray().Should().Contain(f =>
            f.GetProperty("explanation").GetString()!.Contains("ci")
            && f.GetProperty("sources").EnumerateArray().Any(s => s.GetString() == "https://github.com/o/r/actions/runs/9"));
        JsonSerializer.Serialize(output.Get<object>("unknowns")).Should().NotContain("Check runs");
    }

    [Fact]
    public async Task Check_pagination_observes_a_required_queued_check_on_the_second_page()
    {
        var api = new ResponseHandler
        {
            Checks = """{"total_count":2,"check_runs":[{"id":8,"name":"optional","status":"completed","conclusion":"success"}]}""",
            RequiredChecks = """{"contexts":["ci"],"checks":[{"context":"ci","app_id":null}]}""",
        };
        api.CheckLinks[""] = $"https://api.github.com/repos/o/r/commits/{Sha}/check-runs?page=2";
        api.CheckPages["?page=2"] = """{"total_count":2,"check_runs":[{"id":9,"name":"ci","status":"queued","conclusion":null,"started_at":null,"completed_at":null,"html_url":"https://github.com/o/r/actions/runs/9"}]}""";

        var output = await Execute(api);

        output.Get<string>("verdict").Should().Be("blocked");
        output.Get<string>("assurance").Should().Be("fail");
        api.CheckRequests.Should().HaveCount(2);
        JsonSerializer.Serialize(output.Get<object>("unknowns")).Should().NotContain("Check runs");
        output.Get<string>("evidenceMarkdown").Should().Contain("https://github.com/o/r/actions/runs/9");
    }

    [Fact]
    public async Task Canonical_repository_id_pagination_is_followed_only_when_bound_to_the_requested_repository()
    {
        var api = new ResponseHandler
        {
            Pulls = [Pull(repositoryId: 77)],
            Checks = """{"total_count":2,"check_runs":[{"id":8,"name":"optional","status":"completed","conclusion":"success"}]}""",
            RequiredChecks = """{"contexts":["ci"],"checks":[{"context":"ci","app_id":null}]}""",
        };
        api.CheckLinks[""] = $"https://api.github.com/repositories/77/commits/{Sha}/check-runs?page=2";
        api.CheckPages["?page=2"] = """{"total_count":2,"check_runs":[{"id":9,"name":"ci","status":"completed","conclusion":"success"}]}""";

        var output = await Execute(api);

        output.Get<string>("verdict").Should().Be("ready");
        output.Get<string>("assurance").Should().Be("pass");
        api.CheckRequests.Should().HaveCount(2);
        api.CheckRequests[1].AbsolutePath.Should().StartWith("/repositories/77/");
    }

    [Theory]
    [InlineData(null, "o/r", 77L)]
    [InlineData(77L, "o/r", 88L)]
    [InlineData(77L, "other/repo", 77L)]
    public async Task An_unbound_canonical_repository_id_is_not_followed(long? observedId, string observedName, long linkedId)
    {
        var api = new ResponseHandler
        {
            Pulls = [Pull(repositoryId: observedId, repositoryName: observedName)],
            RequiredChecks = """{"contexts":["ci"],"checks":[{"context":"ci","app_id":null}]}""",
        };
        api.CheckLinks[""] = $"https://api.github.com/repositories/{linkedId}/commits/{Sha}/check-runs?page=2";

        var output = await Execute(api);

        output.Get<string>("assurance").Should().Be("unknown");
        api.CheckRequests.Should().HaveCount(1);
    }

    [Fact]
    public async Task An_explicitly_conflicting_base_repository_invalidates_identity_without_pagination()
    {
        var output = await Execute(new ResponseHandler
        {
            Pulls = [Pull(repositoryId: 77, repositoryName: "other/repo")],
        });

        output.Get<string>("verdict").Should().Be("unknown");
        output.Get<string>("assurance").Should().Be("unknown");
        output.Get<string>("evidenceMarkdown").Should().Contain("mismatched PR identity metadata");
    }

    [Fact]
    public async Task A_release_lazily_resolves_repository_identity_before_its_canonical_next_page()
    {
        var api = new ResponseHandler
        {
            Checks = """{"total_count":2,"check_runs":[{"id":8,"name":"first","status":"completed","conclusion":"success"}]}""",
        };
        api.CheckLinks[""] = $"https://api.github.com/repositories/77/commits/{Sha}/check-runs?page=2";
        api.CheckPages["?page=2"] = """{"total_count":2,"check_runs":[{"id":9,"name":"second","status":"completed","conclusion":"success"}]}""";

        var output = await Execute(api, "https://github.com/o/r/releases/tag/v1");

        output.Get<string>("verdict").Should().Be("ready");
        api.CheckRequests.Should().HaveCount(2);
        api.RepositoryReads.Should().Be(1);
        JsonSerializer.Serialize(output.Get<object>("unknowns")).Should().Be("[]");
    }

    [Theory]
    [InlineData("__unreadable__")]
    [InlineData("{\"id\":77,\"full_name\":\"other/repo\"}")]
    [InlineData("{\"id\":88,\"full_name\":\"o/r\"}")]
    [InlineData("{\"id\":0,\"full_name\":\"o/r\"}")]
    public async Task A_release_does_not_follow_an_unavailable_or_mismatched_repository_identity(string repository)
    {
        var api = new ResponseHandler { Repository = repository };
        api.CheckLinks[""] = $"https://api.github.com/repositories/77/commits/{Sha}/check-runs?page=2";

        var output = await Execute(api, "https://github.com/o/r/releases/tag/v1");

        output.Get<string>("verdict").Should().Be("unknown");
        api.CheckRequests.Should().HaveCount(1);
        api.RepositoryReads.Should().Be(1);
    }

    [Fact]
    public async Task An_unpaginated_release_does_not_add_a_repository_lookup()
    {
        var api = new ResponseHandler();

        var output = await Execute(api, "https://github.com/o/r/releases/tag/v1");

        output.Get<string>("verdict").Should().Be("ready");
        api.RepositoryReads.Should().Be(0);
    }

    [Theory]
    [InlineData(8L)]
    [InlineData(null)]
    public async Task Overlapping_or_missing_run_identities_cannot_establish_complete_pagination(long? secondId)
    {
        var api = new ResponseHandler
        {
            Checks = """{"total_count":2,"check_runs":[{"id":8,"name":"ci","status":"completed","conclusion":"success"}]}""",
            RequiredChecks = """{"contexts":["ci"],"checks":[{"context":"ci","app_id":null}]}""",
        };
        api.CheckLinks[""] = $"https://api.github.com/repos/o/r/commits/{Sha}/check-runs?page=2";
        api.CheckPages["?page=2"] = JsonSerializer.Serialize(new
        {
            total_count = 2,
            check_runs = new[] { new { id = secondId, name = "ci", status = "completed", conclusion = "success" } },
        });

        var output = await Execute(api);

        output.Get<string>("assurance").Should().Be("unknown");
        api.CheckRequests.Should().HaveCount(2);
        output.Get<string>("evidenceMarkdown").Should().Contain("identities");
    }

    [Theory]
    [InlineData("success")]
    [InlineData("failure")]
    public async Task A_check_explicitly_returned_for_another_commit_cannot_satisfy_or_fail_the_current_requirement(string conclusion)
    {
        var api = new ResponseHandler
        {
            Checks = JsonSerializer.Serialize(new
            {
                total_count = 1,
                check_runs = new[] { new { id = 9, head_sha = new string('b', 40), name = "ci", status = "completed", conclusion } },
            }),
            RequiredChecks = """{"contexts":["ci"],"checks":[{"context":"ci","app_id":null}]}""",
        };

        var output = await Execute(api);

        output.Get<string>("verdict").Should().Be("unknown");
        output.Get<string>("assurance").Should().Be("unknown");
        output.Get<string>("evidenceMarkdown").Should().Contain("different commit");
    }

    [Theory]
    [InlineData("__unreadable__")]
    [InlineData("{\"total_count\":3,\"check_runs\":[]}")]
    public async Task A_failed_or_inconsistent_later_page_cannot_prove_a_required_check_absent(string secondPage)
    {
        var api = new ResponseHandler
        {
            // A claimed count matching this page cannot override an unresolved Next link.
            Checks = """{"total_count":1,"check_runs":[{"name":"optional","status":"completed","conclusion":"success"}]}""",
            RequiredChecks = """{"contexts":["ci"],"checks":[{"context":"ci","app_id":null}]}""",
        };
        api.CheckLinks[""] = $"https://api.github.com/repos/o/r/commits/{Sha}/check-runs?page=2";
        api.CheckPages["?page=2"] = secondPage;

        var output = await Execute(api);

        output.Get<string>("assurance").Should().Be("unknown");
        api.CheckRequests.Should().HaveCount(2);
        output.Get<string>("evidenceMarkdown").Should().Contain("not observed in incomplete CI results");
    }

    [Theory]
    [InlineData("https://evil.example/check-runs?page=2")]
    [InlineData("http://api.github.com/repos/o/r/commits/0123456789abcdef0123456789abcdef01234567/check-runs?page=2")]
    [InlineData("https://api.github.com/repos/other/repo/commits/0123456789abcdef0123456789abcdef01234567/check-runs?page=2")]
    [InlineData("https://api.github.com/repos/o/r/commits/0123456789abcdef0123456789abcdef01234567/check-runs?redirect=elsewhere")]
    public async Task An_untrusted_pagination_link_is_not_requested_or_treated_as_complete(string next)
    {
        var api = new ResponseHandler
        {
            RequiredChecks = """{"contexts":["ci"],"checks":[{"context":"ci","app_id":null}]}""",
        };
        api.CheckLinks[""] = next;

        var output = await Execute(api);

        output.Get<string>("assurance").Should().Be("unknown");
        api.CheckRequests.Should().HaveCount(1);
        output.Get<string>("evidenceMarkdown").Should().Contain("unsafe or repeated pagination link");
    }

    [Fact]
    public async Task A_repeated_check_page_is_stopped_and_reported_as_incomplete()
    {
        var api = new ResponseHandler
        {
            RequiredChecks = """{"contexts":["ci"],"checks":[{"context":"ci","app_id":null}]}""",
        };
        var next = $"https://api.github.com/repos/o/r/commits/{Sha}/check-runs?page=2";
        api.CheckLinks[""] = next;
        api.CheckLinks["?page=2"] = next;
        api.CheckPages["?page=2"] = api.Checks;

        var output = await Execute(api);

        output.Get<string>("assurance").Should().Be("unknown");
        api.CheckRequests.Should().HaveCount(2);
        output.Get<string>("evidenceMarkdown").Should().Contain("unsafe or repeated pagination link");
    }

    [Theory]
    [InlineData("javascript:alert(1)", "feature", "main")]
    [InlineData("https://github.com/o/r/pull/8", "feature", "main")]
    [InlineData(Pr, "topic\n::notice::forged", "main")]
    [InlineData(Pr, "feature", "main\r\n::error::forged")]
    public async Task Invalid_api_identity_fields_cannot_produce_a_positive_snapshot(string url, string headRef, string baseRef)
    {
        var api = new ResponseHandler { Pulls = [Pull(url, headRef, baseRef)] };

        var output = await Execute(api);

        output.Get<string>("verdict").Should().Be("unknown");
        output.Get<string>("assurance").Should().Be("unknown");
        output.Get<string>("answer").Should().NotContain("javascript:");
        Pack(output).GetProperty("findings").EnumerateArray().Should().Contain(f =>
            f.GetProperty("status").GetString() == "unknown"
            && f.GetProperty("explanation").GetString()!.Contains("identity metadata"));
    }

    [Fact]
    public async Task A_hostile_display_title_does_not_invalidate_an_otherwise_verified_identity()
    {
        var api = new ResponseHandler { Pulls = [Pull(title: "[release](javascript:alert(1)) <script>\n::error::forged")] };

        var output = await Execute(api);

        output.Get<string>("verdict").Should().Be("ready");
        output.Get<string>("assurance").Should().Be("pass");
        output.Get<string>("evidenceMarkdown").Should().NotContain("<script>").And.NotContain("::error::");
    }

    [Theory]
    [InlineData("head")]
    [InlineData("base")]
    [InlineData("mergeability")]
    [InlineData("unreadable")]
    public async Task The_legacy_verdict_never_calls_observed_stale_positive_evidence_ready(string changed)
    {
        var final = changed switch
        {
            "head" => Pull(headSha: new string('b', 40)),
            "base" => Pull(baseSha: new string('b', 40)),
            "mergeability" => Pull(state: "blocked"),
            _ => "__unreadable__",
        };
        var api = new ResponseHandler { Pulls = [Pull(), final] };

        var output = await Execute(api);

        output.Get<string>("verdict").Should().Be("unknown");
        output.Get<string>("assurance").Should().Be("unknown");
        output.Get<string>("answer").Should().Contain("Collection freshness");
    }

    [Fact]
    public async Task A_stable_complete_snapshot_preserves_ready()
    {
        var output = await Execute(new ResponseHandler());
        output.Get<string>("verdict").Should().Be("ready");
        output.Get<string>("assurance").Should().Be("pass");
    }

    [Fact]
    public void Stale_collection_does_not_erase_a_known_blocker_or_reinterpret_terminal_state()
    {
        GitHubTarget.TryParse(Pr, out var target, out _);
        var pr = new PullRequestFacts(Pr, "", false, false, true, true, "clean", "main", [],
            new CiFacts(Sha, [], 0, [], 0), new ProtectionFacts([], 0), Sha, Sha);
        BlockerAnalysis.Analyze(new BlockerSnapshot(target!, pr, null, [], "changed")).Verdict.Should().Be("blocked");
        BlockerAnalysis.Analyze(new BlockerSnapshot(target!, pr with { Merged = true }, null, [], "changed")).Verdict.Should().Be("merged");
        BlockerAnalysis.Analyze(new BlockerSnapshot(target!, pr with { Closed = true }, null, [], "changed")).Verdict.Should().Be("closed");
        BlockerAnalysis.Analyze(new BlockerSnapshot(target!, pr with { Draft = false }, null, [], "not-checked")).Verdict.Should().Be("ready");
    }

    private static JsonElement Pack(BrickOutput output) => JsonSerializer.SerializeToElement(output.Get<object>("evidencePack"));

    private static async Task<BrickOutput> Execute(ResponseHandler api, string url = Pr)
    {
        var brick = new GitHubBlockersBrick(NullLogger<GitHubBlockersBrick>.Instance,
            token => GitHubClientFactory.Create(token, () => api, new EgressGuard("full", "report")), _ => Task.CompletedTask);
        var input = new BrickInput();
        input.Set("url", url);
        return await brick.ExecuteAsync(input, ImplementationType.Deterministic,
            new ExecutionContext { AgentId = "evaluation-regression", BehaviorId = "github.blockers" });
    }

    private static string Pull(string url = Pr, string headRef = "feature", string baseRef = "main", string headSha = Sha,
        string baseSha = Sha, string state = "clean", string title = "display", long? repositoryId = null,
        string repositoryName = "o/r") => JsonSerializer.Serialize(new
    {
        number = 7, state = "open", merged = false, draft = false, mergeable = true, mergeable_state = state,
        html_url = url, title, head = new { sha = headSha, @ref = headRef },
        @base = new { sha = baseSha, @ref = baseRef, repo = repositoryId is { } id ? new { id, full_name = repositoryName } : null },
    });

    private sealed class ResponseHandler : HttpMessageHandler
    {
        public string[] Pulls { get; set; } = [Pull()];
        public string Checks { get; set; } = """{"total_count":0,"check_runs":[]}""";
        public string RequiredChecks { get; set; } = """{"contexts":[],"checks":[]}""";
        public string Repository { get; set; } = """{"id":77,"full_name":"o/r"}""";
        public int RepositoryReads { get; private set; }
        public Dictionary<string, string> CheckPages { get; } = [];
        public Dictionary<string, string> CheckLinks { get; } = [];
        public List<Uri> CheckRequests { get; } = [];
        private int _pullReads;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var isChecks = path.EndsWith("/check-runs", StringComparison.Ordinal);
            if (isChecks)
                CheckRequests.Add(request.RequestUri);
            if (path == "/repos/o/r")
                RepositoryReads++;
            var status = HttpStatusCode.OK;
            var body = path switch
            {
                "/repos/o/r/pulls/7" => Pulls[Math.Min(_pullReads++, Pulls.Length - 1)],
                "/repos/o/r/pulls/7/reviews" => "[]",
                "/repos/o/r" => Repository,
                "/repos/o/r/git/refs/tags/v1" => $$$"""{"ref":"refs/tags/v1","object":{"sha":"{{{Sha}}}","type":"commit"}}""",
                "/repos/o/r/releases/tags/v1" => """{"id":1,"tag_name":"v1","draft":false,"prerelease":false,"html_url":"https://github.com/o/r/releases/tag/v1","assets":[]}""",
                _ when isChecks => CheckPages.GetValueOrDefault(request.RequestUri.Query, Checks),
                _ when path.EndsWith("/status", StringComparison.Ordinal) => """{"state":"success","total_count":0,"statuses":[]}""",
                _ when path.EndsWith("/required_status_checks", StringComparison.Ordinal) => RequiredChecks,
                _ when path.EndsWith("/required_pull_request_reviews", StringComparison.Ordinal) => """{"required_approving_review_count":0,"require_code_owner_reviews":false,"require_last_push_approval":false,"dismiss_stale_reviews":false}""",
                "/repos/o/r/rules/branches/main" => "[]",
                "/repos/o/r/branches/main" => """{"protected":false}""",
                "/graphql" => """{"data":{"repository":{"pull_request":{"review_decision":"NONE","commits":{"nodes":[{"commit":{"oid":"0123456789abcdef0123456789abcdef01234567","status_check_rollup":{"contexts":{"nodes":[]}}}}]}}}}}""",
                _ => "__not_found__",
            };
            if (body == "__unreadable__")
            {
                status = HttpStatusCode.ServiceUnavailable;
                body = "{}";
            }
            if (body == "__not_found__")
            {
                status = HttpStatusCode.NotFound;
                body = "{}";
            }
            var response = new HttpResponseMessage(status)
            {
                RequestMessage = request,
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            if (isChecks && CheckLinks.TryGetValue(request.RequestUri.Query, out var next))
                response.Headers.TryAddWithoutValidation("Link", $"<{next}>; rel=\"next\"");
            return Task.FromResult(response);
        }
    }
}
