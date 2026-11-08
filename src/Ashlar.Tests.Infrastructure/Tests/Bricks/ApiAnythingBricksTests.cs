using System.Net;
using System.Text;
using System.Text.Json;
using Ashlar.Abstractions.Security.Egress;
using Ashlar.Agents.TestKit;
using Ashlar.Bricks.ApiAnything;
using Ashlar.Bricks.ApiAnything.Jobs;
using Ashlar.Core.Domain.Bricks;
using Ashlar.Core.Domain.Execution;
using FluentAssertions;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Bricks;

/// <summary>
/// The API Anything bricks against a stub of <c>api-anything serve</c>'s REST contract (docs/SERVE.md upstream), behind
/// the real egress guard. The JSON shapes are the field names each bundled ATS site's <c>pick</c> produces.
/// </summary>
public sealed class ApiAnythingBricksTests
{
    private static readonly IExecutionContext Ctx = new FakeExecutionContext();
    private static readonly IEgressGuard Allow = new EgressGuard("full", "report");
    private static readonly IEgressGuard AirGapped = new EgressGuard("air-gapped", "enforce");

    private sealed class Server : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, string, (HttpStatusCode, string)> _answer;
        public List<(string Method, string Path, string Body, string? Auth)> Requests { get; } = [];

        public Server(Func<HttpRequestMessage, string, (HttpStatusCode, string)> answer) => _answer = answer;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.Method.Method, request.RequestUri!.AbsolutePath, body, request.Headers.Authorization?.ToString()));
            var (status, json) = _answer(request, body);
            return new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    private static ApiAnythingClient Client(Server server, IEgressGuard? guard = null) =>
        new(new ApiAnythingOptions { Token = "t0k", MaxAge = null }, guard ?? Allow, server, (_, _) => Task.CompletedTask);

    private static (HttpStatusCode, string) Ok(object data) =>
        (HttpStatusCode.OK, JsonSerializer.Serialize(new { ok = true, @class = "ok", tier = 1, data }));

    private static (HttpStatusCode, string) Fail(string cls, string reason) =>
        (HttpStatusCode.OK, JsonSerializer.Serialize(new { ok = false, @class = cls, reason, next = "do the thing", retry = "after_fix" }));

    private const string LeverId = "2a55d6c9-c564-443a-be3e-a3851db5a93b";

    private static object LeverPosting() => new
    {
        postingId = LeverId, title = "Senior Data Engineer", team = "Data", location = "Denver, CO", workplaceType = "hybrid",
        createdAt = 1_793_577_600_000L, description = "Build pipelines.\nReq ID: R-1042", url = $"https://jobs.lever.co/acme/{LeverId}",
        sections = new[] { new { text = "Requirements", content = "<li>5+ years of experience with Python and SQL</li>" } },
        salaryRange = new { min = 140000, max = 175000, currency = "USD", interval = "per-year-salary" },
    };

    // ── client rules ──────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("https://api-anything.example.com")]
    [InlineData("http://10.0.0.5:8787")]
    [InlineData("http://user@127.0.0.1:8787")]
    [InlineData("ftp://127.0.0.1")]
    public void A_server_that_is_not_loopback_is_refused(string url)
    {
        var act = () => new ApiAnythingClient(new ApiAnythingOptions { BaseUrl = new Uri(url) }, Allow, new Server((_, _) => Ok(1)));
        act.Should().Throw<ArgumentException>().WithMessage("*loopback*");
    }

    [Fact]
    public async Task The_guard_decides_on_the_website_and_a_refusal_sends_nothing_to_the_server()
    {
        AirGapped.Evaluate(new EgressRequest(EgressFamilies.Http, ApiAnythingClient.EgressSite, new Uri("https://api.lever.co/")))
            .Refused.Should().BeTrue("the premise: an air-gapped enforcing guard refuses an external host");
        var server = new Server((_, _) => Ok(LeverPosting()));
        var brick = new JobPostingBrick(Client(server, AirGapped));
        var input = new BrickInput();
        input.Set("url", $"https://jobs.lever.co/acme/{LeverId}");

        var output = await brick.ExecuteAsync(input, ImplementationType.Deterministic, Ctx);

        output.Get<string>("signal").Should().Be("refused");
        output.Get<string>("detail").Should().Contain("egress guard");
        server.Requests.Should().BeEmpty("the call to the loopback server must not be made once the website is refused");
    }

    [Fact]
    public async Task A_site_whose_website_cannot_be_determined_is_not_called()
    {
        var server = new Server((req, _) => req.RequestUri!.AbsolutePath.StartsWith("/v1/sites/", StringComparison.Ordinal)
            ? (HttpStatusCode.OK, """{"name":"mystery","operations":[]}""")
            : Ok(1));
        var brick = new ApiAnythingCallBrick(Client(server));
        var input = new BrickInput();
        input.Set("site", "mystery");
        input.Set("op", "read");

        var output = await brick.ExecuteAsync(input, ImplementationType.Deterministic, Ctx);

        output.Get<string>("class").Should().Be("error");
        output.Get<string>("reason").Should().Contain("Cannot tell which website");
        server.Requests.Should().NotContain(r => r.Path.StartsWith("/v1/call/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_site_described_by_the_server_is_guarded_against_its_base_url_host()
    {
        var server = new Server((req, _) => req.RequestUri!.AbsolutePath.StartsWith("/v1/sites/", StringComparison.Ordinal)
            ? (HttpStatusCode.OK, """{"name":"hacker-news","baseUrl":"https://hn.algolia.com/api"}""")
            : Ok(new[] { new { title = "x" } }));
        var brick = new ApiAnythingCallBrick(Client(server));
        var input = new BrickInput();
        input.Set("site", "hacker-news");
        input.Set("op", "search");
        input.Set("args", """{"query":"sqlite"}""");

        var output = await brick.ExecuteAsync(input, ImplementationType.Deterministic, Ctx);

        output.Get<bool>("ok").Should().BeTrue();
        output.Get<string>("upstream").Should().Be("hn.algolia.com");
        var call = server.Requests.Single(r => r.Path == "/v1/call/hacker-news/search");
        call.Auth.Should().Be("Bearer t0k");
        using var body = JsonDocument.Parse(call.Body);
        body.RootElement.GetProperty("maxTier").GetInt32().Should().Be(1);
        body.RootElement.GetProperty("args").GetProperty("query").GetString().Should().Be("sqlite");
    }

    [Fact]
    public async Task A_pending_call_is_polled_through_its_job_and_never_resent()
    {
        var server = new Server((req, _) => req.RequestUri!.AbsolutePath switch
        {
            "/v1/call/lever/getJob" => (HttpStatusCode.OK, """{"ok":false,"class":"pending","job":"j1"}"""),
            "/v1/jobs/j1" => Ok(LeverPosting()),
            _ => (HttpStatusCode.NotFound, "{}"),
        });
        using var client = Client(server);

        var result = await client.CallAsync("lever", "getJob", JobSources.Args(("company", "acme"), ("postingId", LeverId)));

        result.Ok.Should().BeTrue();
        server.Requests.Count(r => r.Path == "/v1/call/lever/getJob").Should().Be(1);
        server.Requests.Count(r => r.Path == "/v1/jobs/j1").Should().Be(1);
    }

    [Fact]
    public async Task A_busy_server_is_a_rate_result_with_its_wait_not_an_exception()
    {
        var server = new Server((_, _) => (HttpStatusCode.TooManyRequests,
            """{"ok":false,"class":"rate","reason":"server busy","retry":"after_wait","retryAfterMs":2000}"""));
        using var client = Client(server);

        var result = await client.CallAsync("lever", "listJobs", JobSources.Args(("company", "acme")));

        result.Class.Should().Be("rate");
        result.RetryAfter.Should().Be(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task A_refused_token_is_an_error_that_names_the_fix()
    {
        var server = new Server((_, _) => (HttpStatusCode.Unauthorized, """{"ok":false}"""));
        var brick = new JobBoardBrick(Client(server));
        var input = new BrickInput();
        input.Set("board", "lever:acme");

        var output = await brick.ExecuteAsync(input, ImplementationType.Deterministic, Ctx);

        output.Get<string>("signal").Should().Be("error");
        output.Get<string>("detail").Should().Contain("API_ANYTHING_TOKEN");
    }

    // ── job bricks ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_lever_posting_is_read_with_pay_arrangement_requisition_and_date()
    {
        var server = new Server((_, _) => Ok(LeverPosting()));
        var brick = new JobPostingBrick(Client(server));
        var input = new BrickInput();
        input.Set("url", $"https://jobs.lever.co/acme/{LeverId}");
        input.Set("employer", "Acme Analytics");

        var output = await brick.ExecuteAsync(input, ImplementationType.Deterministic, Ctx);

        output.Get<string>("signal").Should().Be("found");
        output.Get<bool>("provesOpen").Should().BeTrue();
        var p = output.Get<JobPosting>("posting");
        (p.Employer, p.Title, p.WorkArrangement, p.RequisitionId).Should().Be(("Acme Analytics", "Senior Data Engineer", "hybrid", "R-1042"));
        p.PayText.Should().Be("USD 140,000–175,000 per year-salary");
        p.PostedDate.Should().Be("2026-11-02");
        p.Description.Should().Contain("5+ years of experience with Python and SQL");
    }

    [Fact]
    public async Task An_input_answer_is_gone_which_is_a_signal_not_a_verdict_of_closed()
    {
        var server = new Server((_, _) => Fail("input", "posting not found (404)"));
        var brick = new JobPostingBrick(Client(server));
        var input = new BrickInput();
        input.Set("posting", $"lever:company=acme&postingId={LeverId}");

        var output = await brick.ExecuteAsync(input, ImplementationType.Deterministic, Ctx);

        output.Get<string>("signal").Should().Be("gone");
        output.TryGet<JobPosting>("posting", out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("drift")]
    [InlineData("blocked")]
    [InlineData("error")]
    [InlineData("auth")]
    public async Task Any_other_failure_is_an_error_never_gone(string cls)
    {
        var server = new Server((_, _) => Fail(cls, "site changed"));
        var brick = new JobPostingBrick(Client(server));
        var input = new BrickInput();
        input.Set("url", "https://job-boards.greenhouse.io/acme/jobs/123");

        var output = await brick.ExecuteAsync(input, ImplementationType.Deterministic, Ctx);

        output.Get<string>("signal").Should().Be("error");
        output.Get<string>("detail").Should().Contain(cls);
    }

    [Fact]
    public async Task A_workday_read_does_not_prove_the_posting_open()
    {
        var server = new Server((_, _) => Ok(new
        {
            title = "Integration Consultant", location = "Hong Kong", remoteType = "Flex", jobReqId = "JR-0110821",
            externalUrl = "https://workday.wd5.myworkdayjobs.com/Workday/job/Hong-Kong/Technical-Consultant_JR-0110821",
        }));
        var brick = new JobPostingBrick(Client(server));
        var input = new BrickInput();
        input.Set("url", "https://workday.wd5.myworkdayjobs.com/Workday/job/Hong-Kong/Technical-Consultant_JR-0110821");

        var output = await brick.ExecuteAsync(input, ImplementationType.Deterministic, Ctx);

        output.Get<string>("signal").Should().Be("found");
        output.Get<bool>("provesOpen").Should().BeFalse();
        var p = output.Get<JobPosting>("posting");
        (p.RequisitionId, p.WorkArrangement, p.WorkArrangementLabel).Should().Be(("JR-0110821", null, "Flex"));
    }

    [Fact]
    public async Task A_greenhouse_board_is_paged_until_an_empty_page()
    {
        var server = new Server((_, body) =>
        {
            var page = JsonDocument.Parse(body).RootElement.GetProperty("args").GetProperty("page").GetString();
            return page == "1"
                ? Ok(new[] { new { jobId = "11", title = "Data Engineer", location = "Remote (US)", url = "https://job-boards.greenhouse.io/acme/jobs/11", updatedAt = "2026-11-01T10:00:00Z" } })
                : Ok(Array.Empty<object>());
        });
        var brick = new JobBoardBrick(Client(server));
        var input = new BrickInput();
        input.Set("board", "greenhouse:acme|Acme Corp");

        var output = await brick.ExecuteAsync(input, ImplementationType.Deterministic, Ctx);

        output.Get<string>("signal").Should().Be("ok");
        var entries = output.Get<IReadOnlyList<BoardEntry>>("entries");
        entries.Should().ContainSingle().Which.Should().Match<BoardEntry>(e =>
            e.Employer == "Acme Corp" && e.PostedDate == "2026-11-01" && e.Source.Key() == "greenhouse:company=acme&jobId=11");
        server.Requests.Count(r => r.Path == "/v1/call/greenhouse/listJobs").Should().Be(2);
    }

    [Fact]
    public async Task A_later_page_failure_keeps_what_was_read_and_says_partial()
    {
        var server = new Server((_, body) =>
            JsonDocument.Parse(body).RootElement.GetProperty("args").GetProperty("page").GetString() == "1"
                ? Ok(new[] { new { jobId = "11", title = "Data Engineer" } })
                : Fail("drift", "board layout changed"));
        var brick = new JobBoardBrick(Client(server));
        var input = new BrickInput();
        input.Set("board", "greenhouse:acme");

        var output = await brick.ExecuteAsync(input, ImplementationType.Deterministic, Ctx);

        output.Get<string>("signal").Should().Be("partial");
        output.Get<IReadOnlyList<BoardEntry>>("entries").Should().HaveCount(1);
    }

    [Theory]
    [InlineData("Requirements\n- 5+ years", null)]
    [InlineData("Requisition requirements follow", null)]
    [InlineData("Req ID: AA-2210", "AA-2210")]
    [InlineData("Requisition #R1042 opened", "R1042")]
    [InlineData("Job ID 77812", "77812")]
    public async Task A_requisition_id_needs_a_label_and_a_digit(string description, string? expected)
    {
        var server = new Server((_, _) => Ok(new[] { new { title = "Engineer", location = "Remote", description } }));
        var brick = new JobPostingBrick(Client(server));
        var input = new BrickInput();
        input.Set("url", "https://job-boards.greenhouse.io/acme/jobs/9");

        var output = await brick.ExecuteAsync(input, ImplementationType.Deterministic, Ctx);

        output.Get<JobPosting>("posting").RequisitionId.Should().Be(expected);
    }

    [Fact]
    public void Every_brick_declares_its_deterministic_implementation_so_a_workflow_can_run_it()
    {
        using var client = Client(new Server((_, _) => Ok(1)));
        Ashlar.Core.Domain.Bricks.Brick[] bricks = [new ApiAnythingCallBrick(client), new JobBoardBrick(client), new JobPostingBrick(client)];
        bricks.Should().OnlyContain(b => b.Implementations.HasDeterministic);
    }

    // ── parsing rules ─────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("https://job-boards.greenhouse.io/acme/jobs/123", "greenhouse:company=acme&jobId=123")]
    [InlineData("https://boards.greenhouse.io/acme/jobs/123?gh_src=x", "greenhouse:company=acme&jobId=123")]
    [InlineData("https://jobs.lever.co/acme/2A55D6C9-C564-443A-BE3E-A3851DB5A93B", "lever:company=acme&postingId=2a55d6c9-c564-443a-be3e-a3851db5a93b")]
    [InlineData("https://jobs.ashbyhq.com/linear/6dd5500a-4bd4-4f57-ba09-7b07acefecf4", "ashby:company=linear&jobId=6dd5500a-4bd4-4f57-ba09-7b07acefecf4")]
    [InlineData("https://apply.workable.com/thrive/j/FCAAE7F7E9/", "workable:company=thrive&shortcode=FCAAE7F7E9")]
    public void Supported_posting_urls_map_to_their_getJob_call(string url, string key) =>
        JobSources.FromUrl(url)!.Value.Ref.Key().Should().Be(key);

    [Theory]
    [InlineData("http://jobs.lever.co/acme/2a55d6c9-c564-443a-be3e-a3851db5a93b")]
    [InlineData("https://jobs.lever.co.evil.example/acme/2a55d6c9-c564-443a-be3e-a3851db5a93b")]
    [InlineData("https://www.linkedin.com/jobs/view/123")]
    [InlineData("https://acme.wd1.myworkdayjobs.com/x/job/y/Engineer_JR-1")]
    [InlineData("not a url")]
    public void Anything_else_is_unsupported(string url) => JobSources.FromUrl(url).Should().BeNull();

    [Theory]
    [InlineData("lever:acme", true)]
    [InlineData("greenhouse:acme|Acme Corp", true)]
    [InlineData("workable:acme", false)]
    [InlineData("lever:../etc", false)]
    [InlineData("lever:", false)]
    public void Board_specs_are_validated(string spec, bool valid) =>
        BoardRef.TryParse(spec, out _, out _).Should().Be(valid);
}
