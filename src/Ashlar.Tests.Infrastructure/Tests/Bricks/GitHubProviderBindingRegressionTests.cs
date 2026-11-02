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

public sealed partial class GitHubProviderAssuranceRegressionTests
{
    [Theory]
    [InlineData("success", "failure", "ready", "pass")]
    [InlineData("failure", "success", "blocked", "fail")]
    [InlineData(null, "success", "blocked", "fail")]
    public async Task A_required_app_is_evaluated_independently_of_a_same_name_optional_app(
        string? requiredConclusion, string otherConclusion, string verdict, string assurance)
    {
        var api = new ProviderApi
        {
            Checks = Checks(requiredConclusion is null ? [] : [Check(1, "ci", 444, "completed", requiredConclusion)],
                [Check(2, "ci", 555, "completed", otherConclusion)]),
        };
        var output = await ExecuteProvider(api);

        output.Get<string>("verdict").Should().Be(verdict);
        output.Get<string>("assurance").Should().Be(assurance);
        JsonSerializer.Serialize(output.Get<object>("blockers")).Should().NotContain("optional");
        if (otherConclusion == "failure")
            output.Get<string>("answer").Should().Contain("different app");
        ProviderPack(output).GetProperty("findings").EnumerateArray().Should().Contain(f =>
            f.GetProperty("explanation").GetString()!.Contains("GitHub App 444"));
    }

    [Theory]
    [InlineData("success")]
    [InlineData("failure")]
    public async Task An_unidentified_same_name_check_neither_satisfies_nor_fails_a_bound_requirement(string conclusion)
    {
        var output = await ExecuteProvider(new ProviderApi { Checks = Checks([Check(1, "ci", null, "completed", conclusion)]) });
        output.Get<string>("verdict").Should().Be("unknown");
        output.Get<string>("assurance").Should().Be("unknown");
        ProviderPack(output).GetProperty("findings").EnumerateArray().Should().NotContain(f => f.GetProperty("status").GetString() == "fail");
    }

    [Theory]
    [InlineData("\"444\"")]
    [InlineData("true")]
    [InlineData("444.5")]
    public async Task Malformed_observed_app_ids_are_not_coerced_into_a_required_provider(string appId)
    {
        var api = new ProviderApi
        {
            Checks = "{\"total_count\":1,\"check_runs\":[{\"id\":1,\"name\":\"ci\",\"status\":\"completed\",\"conclusion\":\"success\",\"app\":{\"id\":" + appId + "}}]}",
        };
        var output = await ExecuteProvider(api);
        output.Get<string>("assurance").Should().Be("unknown");
    }

    [Theory]
    [InlineData("queued")]
    [InlineData("in_progress")]
    public async Task A_pending_check_from_the_required_app_is_currently_unmet(string state)
    {
        var output = await ExecuteProvider(new ProviderApi { Checks = Checks([Check(1, "ci", 444, state, null)]) });
        output.Get<string>("verdict").Should().Be("blocked");
        output.Get<string>("assurance").Should().Be("fail");
    }

    [Theory]
    [InlineData("success")]
    [InlineData("failure")]
    public async Task A_commit_status_creator_is_not_an_app_identity(string state)
    {
        var api = new ProviderApi
        {
            Checks = Checks([]),
            Statuses = $$$"""{"state":"{{{state}}}","total_count":1,"statuses":[{"context":"ci","state":"{{{state}}}","creator":{"id":444,"type":"Bot"}}]}""",
        };
        var output = await ExecuteProvider(api);
        output.Get<string>("verdict").Should().Be("unknown");
        output.Get<string>("assurance").Should().Be("unknown");
    }

    [Fact]
    public async Task A_same_name_status_with_unverified_origin_prevents_a_bound_check_pass()
    {
        var output = await ExecuteProvider(new ProviderApi
        {
            Statuses = """{"state":"success","total_count":1,"statuses":[{"context":"ci","state":"success"}]}""",
        });
        output.Get<string>("assurance").Should().Be("unknown");
    }

    [Fact]
    public async Task An_unbound_name_requires_both_the_check_and_commit_status_to_pass()
    {
        var output = await ExecuteProvider(new ProviderApi
        {
            Policy = """{"contexts":["ci"],"checks":[{"context":"ci","app_id":null}]}""",
            Statuses = """{"state":"failure","total_count":1,"statuses":[{"context":"ci","state":"failure"}]}""",
        });
        output.Get<string>("verdict").Should().Be("blocked");
        output.Get<string>("assurance").Should().Be("fail");
    }

    [Theory]
    [InlineData("", "unknown")]
    [InlineData(",\"app_id\":null", "pass")]
    [InlineData(",\"app_id\":-1", "pass")]
    [InlineData(",\"app_id\":0", "unknown")]
    [InlineData(",\"app_id\":-2", "unknown")]
    [InlineData(",\"app_id\":444", "pass")]
    [InlineData(",\"app_id\":\"444\"", "unknown")]
    [InlineData(",\"app_id\":true", "unknown")]
    [InlineData(",\"app_id\":444.5", "unknown")]
    public async Task Branch_bindings_distinguish_explicit_any_app_from_omitted_or_invalid_metadata(string binding, string assurance)
    {
        var output = await ExecuteProvider(new ProviderApi
        {
            Policy = "{\"contexts\":[\"ci\"],\"checks\":[{\"context\":\"ci\"" + binding + "}]}",
        });
        output.Get<string>("assurance").Should().Be(assurance);
    }

    [Theory]
    [InlineData("", "unknown")]
    [InlineData(",\"integration_id\":null", "pass")]
    [InlineData(",\"integration_id\":-1", "unknown")]
    [InlineData(",\"integration_id\":0", "unknown")]
    [InlineData(",\"integration_id\":444", "pass")]
    [InlineData(",\"integration_id\":\"444\"", "unknown")]
    [InlineData(",\"integration_id\":true", "unknown")]
    [InlineData(",\"integration_id\":444.5", "unknown")]
    public async Task Ruleset_bindings_do_not_infer_missing_or_undocumented_negative_origins(string binding, string assurance)
    {
        var output = await ExecuteProvider(new ProviderApi
        {
            Policy = """{"contexts":[],"checks":[]}""",
            Rules = "[{\"type\":\"required_status_checks\",\"parameters\":{\"required_status_checks\":[{\"context\":\"ci\"" + binding + "}]}}]",
        });
        output.Get<string>("assurance").Should().Be(assurance);
    }

    [Theory]
    [InlineData("{\"context\":\"ci\",\"app_id\":444,\"integration_id\":555}")]
    [InlineData("{\"context\":\"ci\",\"app_id\":444},{\"context\":\"ci\",\"app_id\":555}")]
    [InlineData("{\"context\":\"ci\",\"app_id\":null},{\"context\":\"ci\",\"app_id\":444}")]
    public async Task Conflicting_origin_metadata_within_one_source_stays_unknown(string rows)
    {
        var output = await ExecuteProvider(new ProviderApi { Policy = "{\"contexts\":[\"ci\"],\"checks\":[" + rows + "]}" });
        output.Get<string>("assurance").Should().Be("unknown");
    }

    [Fact]
    public async Task A_context_without_origin_metadata_does_not_erase_another_contexts_confirmed_failure()
    {
        var output = await ExecuteProvider(new ProviderApi
        {
            Policy = """{"contexts":["ci","other"],"checks":[{"context":"ci","app_id":444},{"context":"other"}]}""",
            Checks = Checks([Check(1, "ci", 444, "completed", "failure"), Check(2, "other", 555, "completed", "success")]),
        });
        output.Get<string>("assurance").Should().Be("fail");
        var findings = ProviderPack(output).GetProperty("findings").EnumerateArray().ToList();
        findings.Should().Contain(f => f.GetProperty("status").GetString() == "fail" && f.GetProperty("explanation").GetString()!.Contains("GitHub App 444"));
        findings.Should().Contain(f => f.GetProperty("status").GetString() == "unknown" && f.GetProperty("explanation").GetString()!.Contains("other"));
    }

    [Fact]
    public async Task Independently_applying_branch_and_ruleset_bindings_are_both_evaluated()
    {
        var output = await ExecuteProvider(new ProviderApi
        {
            Rules = """[{"type":"required_status_checks","parameters":{"required_status_checks":[{"context":"ci","integration_id":555}]}}]""",
            Checks = Checks([Check(1, "ci", 444, "completed", "success"), Check(2, "ci", 555, "completed", "failure")]),
        });
        output.Get<string>("assurance").Should().Be("fail");
    }

    [Fact]
    public async Task An_unbound_source_cannot_erase_an_unknown_binding_from_another_source()
    {
        var output = await ExecuteProvider(new ProviderApi
        {
            Policy = """{"contexts":["ci"],"checks":[{"context":"ci","app_id":null}]}""",
            Rules = """[{"type":"required_status_checks","parameters":{"required_status_checks":[{"context":"ci"}]}}]""",
        });
        output.Get<string>("assurance").Should().Be("unknown");
    }

    private static JsonElement ProviderPack(BrickOutput output) => JsonSerializer.SerializeToElement(output.Get<object>("evidencePack"));

    private static object Check(long id, string name, long? app, string status, string? conclusion) =>
        new { id, name, status, conclusion, started_at = (string?)null, completed_at = (string?)null,
            head_sha = Head, html_url = $"https://github.com/o/r/checks/{id}", app = app is { } appId ? new { id = appId } : null };

    private static string Checks(object[] checks, object[]? extra = null)
    {
        var rows = checks.Concat(extra ?? []).ToArray();
        return JsonSerializer.Serialize(new { total_count = rows.Length, check_runs = rows });
    }

    private static async Task<BrickOutput> ExecuteProvider(ProviderApi api)
    {
        var brick = new GitHubBlockersBrick(NullLogger<GitHubBlockersBrick>.Instance,
            token => GitHubClientFactory.Create(token, () => api, new EgressGuard("full", "report")), _ => Task.CompletedTask);
        var input = new BrickInput(); input.Set("url", "https://github.com/o/r/pull/7");
        return await brick.ExecuteAsync(input, ImplementationType.Deterministic,
            new ExecutionContext { AgentId = "provider-regression", BehaviorId = "github.blockers" });
    }

    private sealed class ProviderApi : HttpMessageHandler
    {
        public string Policy { get; init; } = """{"contexts":["ci"],"checks":[{"context":"ci","app_id":444}]}""";
        public string Rules { get; init; } = "[]";
        public string Checks { get; init; } = GitHubProviderAssuranceRegressionTests.Checks([Check(1, "ci", 444, "completed", "success")]);
        public string Statuses { get; init; } = """{"state":"success","total_count":0,"statuses":[]}""";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = path switch
            {
                "/repos/o/r/pulls/7" => $$$"""{"number":7,"state":"open","merged":false,"draft":false,"mergeable":true,"mergeable_state":"clean","html_url":"https://github.com/o/r/pull/7","head":{"sha":"{{{Head}}}","ref":"feature"},"base":{"sha":"{{{Base}}}","ref":"main"}}""",
                "/repos/o/r/pulls/7/reviews" => "[]",
                _ when path.EndsWith("/check-runs", StringComparison.Ordinal) => Checks,
                _ when path.EndsWith("/status", StringComparison.Ordinal) => Statuses,
                _ when path.EndsWith("/required_status_checks", StringComparison.Ordinal) => Policy,
                _ when path.EndsWith("/required_pull_request_reviews", StringComparison.Ordinal) => """{"required_approving_review_count":0,"require_code_owner_reviews":false,"require_last_push_approval":false,"dismiss_stale_reviews":false}""",
                "/repos/o/r/rules/branches/main" => Rules,
                "/graphql" => """{"data":{"repository":{"pull_request":{"review_decision":"NONE","commits":{"nodes":[{"commit":{"oid":"HEAD_SHA","status_check_rollup":{"contexts":{"nodes":[]}}}}]}}}}}""".Replace("HEAD_SHA", Head, StringComparison.Ordinal),
                _ => null,
            };
            return Task.FromResult(new HttpResponseMessage(body is null ? HttpStatusCode.NotFound : HttpStatusCode.OK)
            { RequestMessage = request, Content = new StringContent(body ?? "{}", Encoding.UTF8, "application/json") });
        }
    }
}
