using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Ashlar.Abstractions.Security.Egress;
using Ashlar.Agents.TestKit;
using Ashlar.Core.Domain.Bricks;
using Ashlar.Core.Domain.Execution;
using Ashlar.WebOperations;
using Ashlar.WebOperations.Bricks;
using Ashlar.WebOperations.Jobs;
using Ashlar.WebOperations.Learning;
using Ashlar.WebOperations.Specs;
using FluentAssertions;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Bricks;

/// <summary>Ashlar.WebOperations behind the real egress guard, against a stub transport routed by host and path.</summary>
public sealed class WebOperationsTests
{
    private static readonly IExecutionContext Ctx = new FakeExecutionContext();
    private static readonly IEgressGuard Allow = new EgressGuard("full", "report");

    private sealed class Web : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, (HttpStatusCode Status, string Body, string? Type)> _answer;
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string> Bodies { get; } = [];

        public Web(Func<HttpRequestMessage, (HttpStatusCode, string, string?)> answer) => _answer = answer;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            var (status, body, type) = _answer(request);
            var r = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, type ?? "application/json") };
            if (status == HttpStatusCode.TooManyRequests) r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(42));
            return r;
        }
    }

    private static (HttpStatusCode, string, string?) Json(object o) => (HttpStatusCode.OK, System.Text.Json.JsonSerializer.Serialize(o), null);

    private static WebOperationClient Client(Web web, SpecStore? store = null, IEgressGuard? guard = null, List<TimeSpan>? waits = null) =>
        new(store ?? new SpecStore(), guard ?? Allow, web, new WebOperationsOptions(), delay: (d, _) => { waits?.Add(d); return Task.CompletedTask; });

    private static Dictionary<string, string> A(params (string K, string V)[] kv) => kv.ToDictionary(x => x.K, x => x.V, StringComparer.Ordinal);

    private static SiteSpec Demo(string extract = "items", List<string>? credentials = null, string kind = "read", string method = "GET") => SiteSpec.Parse($$"""
        { "site": "demo", "title": "Demo", "hosts": ["api.demo.test"],
          "operations": { "search": { "kind": "{{kind}}", "params": { "q": { "pattern": "[a-z]+" } },
            "request": { "method": "{{method}}", "url": "https://api.demo.test/v1/search?q={q}", "credentials": {{System.Text.Json.JsonSerializer.Serialize(credentials ?? [])}} },
            "response": { "format": "json", "extract": "{{extract}}", "list": true, "shape": ["id", "name"], "fields": { "id": "id", "name": "name" } } } } }
        """);

    private static SpecStore Store(SiteSpec spec)
    {
        var store = new SpecStore(includeBundled: false);
        store.Save(spec with { Version = 0 });
        return store;
    }

    // ── specs ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_bundled_specs_load_and_validate()
    {
        SpecStore.Bundled().Select(s => s.Site).Should().BeEquivalentTo(["ashby", "greenhouse", "lever", "schema-org"]);
        SpecStore.Bundled().SelectMany(s => s.Operations.Values).Should().OnlyContain(o => o.Kind == "read" && o.Request.Credentials.Count == 0);
    }

    [Theory]
    [InlineData("""{"Authorization": "Bearer abc"}""", "credential")]
    [InlineData("""{"X-Trace": "a8F3kQ9zL2mN7pR4tV6wY1bC5dE0gH"}""", "credential")]
    public void A_spec_holding_a_credential_is_refused(string headers, string message)
    {
        var act = () => SiteSpec.Parse($$"""{ "site": "x", "hosts": ["a.test"], "operations": { "o": { "request": { "url": "https://a.test/", "headers": {{headers}} } } } }""");
        act.Should().Throw<InvalidDataException>().WithMessage($"*{message}*");
    }

    [Fact]
    public void A_template_placeholder_without_a_param_and_an_open_host_write_are_refused()
    {
        var unknown = () => SiteSpec.Parse("""{ "site": "x", "hosts": ["a.test"], "operations": { "o": { "request": { "url": "https://a.test/{who}" } } } }""");
        unknown.Should().Throw<InvalidDataException>().WithMessage("*{who}*");
        var write = () => SiteSpec.Parse("""{ "site": "x", "hosts": ["*"], "operations": { "o": { "kind": "write", "request": { "method": "POST", "url": "https://a.test/" } } } }""");
        write.Should().Throw<InvalidDataException>().WithMessage("*any host*");
    }

    // ── nothing is sent unless it may be ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_egress_guard_decides_on_the_real_host_and_a_refusal_sends_nothing()
    {
        var airGapped = new EgressGuard("air-gapped", "enforce");
        airGapped.Evaluate(new EgressRequest(EgressFamilies.Http, WebOperationClient.EgressSite, new Uri("https://api.demo.test/"))).Refused.Should().BeTrue("the premise");
        var web = new Web(_ => Json(new { items = Array.Empty<object>() }));
        using var client = Client(web, Store(Demo()), airGapped);

        var r = await client.CallAsync("demo", "search", A(("q", "abc")));

        r.Class.Should().Be("refused");
        r.Reason.Should().Contain("egress guard");
        web.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData("ABC", "does not match")]
    [InlineData("", "missing argument")]
    public async Task A_bad_argument_is_input_and_sends_nothing(string q, string reason)
    {
        var web = new Web(_ => Json(new { }));
        using var client = Client(web, Store(Demo()));
        (await client.CallAsync("demo", "search", A(("q", q)))).Reason.Should().Contain(reason);
        web.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task An_open_host_read_cannot_target_loopback_or_an_undeclared_host()
    {
        var web = new Web(_ => (HttpStatusCode.OK, "<html></html>", "text/html"));
        using var client = Client(web);
        (await client.CallAsync("schema-org", "getJobPosting", A(("url", "https://127.0.0.1/job")))).Class.Should().Be("refused");
        var store = Store(Demo() with { Hosts = ["other.test"] });
        using var c2 = Client(web, store);
        (await c2.CallAsync("demo", "search", A(("q", "abc")))).Reason.Should().Contain("not a host");
        web.Requests.Should().BeEmpty();
    }

    // ── classification ───────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(404, "{}", "application/json", "input", "after_fix")]
    [InlineData(429, "{}", "application/json", "rate", "after_wait")]
    [InlineData(503, "<html><title>Just a moment...</title><script src=/cdn-cgi/challenge-platform/x.js></script></html>", "text/html", "blocked", "no")]
    [InlineData(500, "oops", "text/plain", "error", "after_wait")]
    [InlineData(200, """{"items":[{"id":1}]}""", "application/json", "drift", "after_fix")]
    [InlineData(200, "<html>maintenance</html>", "text/html", "drift", "after_fix")]
    public async Task Every_answer_is_classified(int status, string body, string type, string cls, string retry)
    {
        var web = new Web(_ => ((HttpStatusCode)status, body, type));
        using var client = Client(web, Store(Demo()));

        var r = await client.CallAsync("demo", "search", A(("q", "abc")), new CallOptions { AutoHeal = false });

        (r.Class, r.Retry).Should().Be((cls, retry));
        if (cls == "rate") r.RetryAfter.Should().Be(TimeSpan.FromSeconds(42));
    }

    [Fact]
    public async Task Data_that_moved_is_repaired_from_the_same_response_saved_as_a_new_version_and_revertible()
    {
        var web = new Web(_ => Json(new { data = new { results = new[] { new { id = 1, name = "a" }, new { id = 2, name = "b" } } }, meta = new { total = 2 } }));
        var store = Store(Demo());
        using var client = Client(web, store);

        var r = await client.CallAsync("demo", "search", A(("q", "abc")));

        r.Ok.Should().BeTrue();
        r.Healed.Should().Contain("data.results");
        r.Data!.AsArray().Should().HaveCount(2);
        web.Requests.Should().HaveCount(1, "a repair reads the response it already has");
        store.Get("demo")!.Version.Should().Be(2);
        store.Revert("demo")!.Version.Should().Be(1);
    }

    // ── credentials, cache, pacing, writes ────────────────────────────────────────────────────────────────

    private sealed class Creds(string scope, Dictionary<string, string> values) : ICredentialSource
    {
        public string Scope => scope;
        public string? Resolve(string reference) => values.GetValueOrDefault(reference);
    }

    [Fact]
    public async Task Credentials_are_resolved_per_call_never_stored_and_never_shared_through_the_cache()
    {
        var web = new Web(_ => Json(new { items = new[] { new { id = 1, name = "mine" } } }));
        var store = Store(Demo(credentials: ["cookie:sid", "header:X-Csrf-Token"]));
        using var client = Client(web, store);

        (await client.CallAsync("demo", "search", A(("q", "abc")))).Class.Should().Be("auth");
        web.Requests.Should().BeEmpty("a call missing its credential is not sent");

        var alice = new Creds("alice", new() { ["cookie:sid"] = "s-alice", ["header:X-Csrf-Token"] = "t-alice" });
        var bob = new Creds("bob", new() { ["cookie:sid"] = "s-bob", ["header:X-Csrf-Token"] = "t-bob" });
        (await client.CallAsync("demo", "search", A(("q", "abc")), new CallOptions { Credentials = alice, MaxAge = TimeSpan.FromHours(1) })).Ok.Should().BeTrue();
        (await client.CallAsync("demo", "search", A(("q", "abc")), new CallOptions { Credentials = bob, MaxAge = TimeSpan.FromHours(1) })).Cached.Should().BeFalse();
        (await client.CallAsync("demo", "search", A(("q", "abc")), new CallOptions { Credentials = alice, MaxAge = TimeSpan.FromHours(1) })).Cached.Should().BeTrue();

        web.Requests.Should().HaveCount(2);
        web.Requests[0].Headers.GetValues("Cookie").Single().Should().Be("sid=s-alice");
        web.Requests[1].Headers.GetValues("X-Csrf-Token").Single().Should().Be("t-bob");
        store.Get("demo")!.ToJson().Should().NotContain("s-alice").And.NotContain("t-bob");
    }

    [Fact]
    public async Task Requests_to_one_host_are_paced()
    {
        var waits = new List<TimeSpan>();
        var web = new Web(_ => Json(new { items = new[] { new { id = 1, name = "a" } } }));
        using var client = Client(web, Store(Demo()), waits: waits);
        await client.CallAsync("demo", "search", A(("q", "abc")));
        await client.CallAsync("demo", "search", A(("q", "abd")));
        waits.Should().ContainSingle().Which.Should().BeGreaterThan(TimeSpan.FromMilliseconds(500));
    }

    [Fact]
    public async Task A_write_needs_permission_and_a_key_is_sent_once_and_never_retried()
    {
        var web = new Web(_ => (HttpStatusCode.InternalServerError, "", "text/plain"));
        using var client = Client(web, Store(Demo(kind: "write", method: "POST")));
        var args = A(("q", "abc"));

        (await client.CallAsync("demo", "search", args)).Write.Should().Be("not_sent");
        (await client.CallAsync("demo", "search", args, new CallOptions { AllowWrites = true })).Reason.Should().Contain("idempotency key");
        var first = await client.CallAsync("demo", "search", args, new CallOptions { AllowWrites = true, IdempotencyKey = "k1" });
        (first.Class, first.Retry).Should().Be(("error", "no"));
        (await client.CallAsync("demo", "search", args, new CallOptions { AllowWrites = true, IdempotencyKey = "k1" })).Class.Should().Be("duplicate");
        web.Requests.Should().HaveCount(1);
    }

    // ── learning ─────────────────────────────────────────────────────────────────────────────────────────

    private static CapturedExchange Capture(string company, string extraQuery = "", Dictionary<string, string>? headers = null) => new()
    {
        Url = $"https://api.boards.test/v2/companies/{company}/openings?format=json{extraQuery}",
        Headers = headers ?? new(StringComparer.OrdinalIgnoreCase) { ["Accept"] = "application/json", ["User-Agent"] = "Mozilla/5.0" },
        ResponseBody = $$"""{"page":1,"openings":[{"ref":"{{company}}-1","role":"Engineer","city":"Denver"},{"ref":"{{company}}-2","role":"Analyst","city":"Remote"}]}""",
        Args = new() { ["company"] = company },
    };

    [Fact]
    public void Learning_finds_the_argument_slots_and_the_data_and_keeps_constants()
    {
        var outcome = OperationLearner.Learn([Capture("acme"), Capture("globex")]);

        outcome.Refusal.Should().BeNull();
        var op = outcome.Operation!;
        op.Request.Url.Should().Be("https://api.boards.test/v2/companies/{company}/openings?format=json");
        op.Request.Headers.Should().ContainKey("Accept").And.NotContainKey("User-Agent");
        (op.Response.Extract, op.Response.List).Should().Be(("openings", true));
        op.Response.Shape.Should().BeEquivalentTo(["city", "ref", "role"]);
    }

    [Fact]
    public void Learning_turns_cookies_and_secret_headers_into_references_and_refuses_what_it_cannot_explain()
    {
        Dictionary<string, string> H(string token) => new(StringComparer.OrdinalIgnoreCase) { ["Cookie"] = "sid=abc; theme=dark", ["X-Api-Key"] = token };
        var signedIn = OperationLearner.Learn([Capture("acme", headers: H("k1")), Capture("globex", headers: H("k1"))]);
        signedIn.Operation!.Request.Credentials.Should().BeEquivalentTo(["cookie:sid", "cookie:theme", "header:X-Api-Key"]);
        signedIn.Operation.Request.Headers.Should().NotContainKey("Cookie").And.NotContainKey("X-Api-Key");

        OperationLearner.Learn([Capture("acme", "&nonce=8812"), Capture("globex", "&nonce=1934")]).Refusal.Should().Contain("nonce");
        var buster = OperationLearner.Learn([Capture("acme", "&_=1700000001"), Capture("globex", "&_=1700000002")]);
        buster.Operation!.Request.Url.Should().NotContain("_=");
        OperationLearner.Learn([Capture("acme", "&key=Zx81Kq09Lm72Np34Rs56Tu"), Capture("globex", "&key=Zx81Kq09Lm72Np34Rs56Tu")])
            .Operation!.Request.Credentials.Should().Contain("query:key");
        OperationLearner.Learn([Capture("acme"), Capture("acme")]).Refusal.Should().Contain("same value");
    }

    [Fact]
    public async Task A_learned_operation_is_saved_only_after_a_live_call_with_new_arguments_answers()
    {
        var web = new Web(req => req.RequestUri!.AbsolutePath.Contains("/initech/", StringComparison.Ordinal)
            ? Json(new { page = 1, openings = new[] { new { @ref = "i-1", role = "Engineer", city = "Austin" } } })
            : (HttpStatusCode.NotFound, "{}", null));
        var store = new SpecStore(includeBundled: false);
        using var client = Client(web, store);
        var learned = OperationLearner.Learn([Capture("acme"), Capture("globex")]).Operation!;

        var (failed, refusal) = await OperationLearner.VerifyAndSaveAsync(client, "boards", "openings", learned, A(("company", "nobody")));
        failed.Should().BeNull();
        refusal.Class.Should().Be("input");
        store.Get("boards").Should().BeNull();

        var (saved, ok) = await OperationLearner.VerifyAndSaveAsync(client, "boards", "openings", learned, A(("company", "initech")));
        ok.Ok.Should().BeTrue();
        saved!.Version.Should().Be(1);
        (await client.CallAsync("boards", "openings", A(("company", "initech")))).Data!.AsArray().Should().ContainSingle();
    }

    [Fact]
    public async Task The_learn_brick_reads_a_HAR_export()
    {
        var har = new JsonObject
        {
            ["log"] = new JsonObject
            {
                ["entries"] = new JsonArray(new[] { "acme", "globex" }.Select(c => (JsonNode)new JsonObject
                {
                    ["request"] = new JsonObject { ["method"] = "GET", ["url"] = $"https://api.boards.test/v2/companies/{c}/openings?format=json", ["headers"] = new JsonArray() },
                    ["response"] = new JsonObject { ["status"] = 200, ["content"] = new JsonObject { ["text"] = Capture(c).ResponseBody } },
                }).ToArray()),
            },
        }.ToJsonString();
        var web = new Web(_ => Json(new { openings = new[] { new { @ref = "i-1", role = "Engineer", city = "Austin" } } }));
        var store = new SpecStore(includeBundled: false);
        using var client = Client(web, store);
        var input = new BrickInput();
        input.Set("site", "boards");
        input.Set("operation", "openings");
        input.Set("har", har);
        input.Set("harArgs", """[{"company":"acme"},{"company":"globex"}]""");
        input.Set("verifyArgs", """{"company":"initech"}""");

        var output = await new WebLearnBrick(client).ExecuteAsync(input, ImplementationType.Deterministic, Ctx);

        output.Get<bool>("saved").Should().BeTrue(output.Get<string>("reason"));
        store.Get("boards")!.Operation("openings").Request.Url.Should().Contain("{company}");
    }

    // ── job specs ────────────────────────────────────────────────────────────────────────────────────────

    private static Web Boards() => new(req =>
    {
        var u = req.RequestUri!;
        return (u.Host, u.AbsolutePath) switch
        {
            ("boards-api.greenhouse.io", "/v1/boards/acme/jobs") => Json(new
            {
                jobs = new[] { new { id = 4001001, title = "Senior Data Engineer", updated_at = "2026-11-02T15:00:00-05:00", location = new { name = "Denver, CO" }, absolute_url = "https://job-boards.greenhouse.io/acme/jobs/4001001", content = "&lt;p&gt;Hybrid.&lt;/p&gt;" } },
                meta = new { total = 1 },
            }),
            ("boards-api.greenhouse.io", "/v1/boards/acme/jobs/4001001") => Json(new
            {
                id = 4001001, title = "Senior Data Engineer", updated_at = "2026-11-02T15:00:00-05:00", location = new { name = "Denver, CO" },
                absolute_url = "https://job-boards.greenhouse.io/acme/jobs/4001001", requisition_id = "AA-2210",
                content = "&lt;p&gt;Requirements&lt;/p&gt;&lt;ul&gt;&lt;li&gt;Python &amp;amp; SQL&lt;/li&gt;&lt;/ul&gt;",
                pay_input_ranges = new[] { new { min_cents = 15000000, max_cents = 18000000, currency_type = "USD" } },
            }),
            ("api.lever.co", "/v0/postings/northwind/8f14e45f-ceea-467a-9e3a-7c2c1f0a0001") => Json(new
            {
                id = "8f14e45f-ceea-467a-9e3a-7c2c1f0a0001", text = "Clinical Informatics Nurse", workplaceType = "onsite", createdAt = 1793577600000L,
                categories = new { location = "Columbus, OH", team = "Clinical Systems", commitment = "Full-time" }, hostedUrl = "https://jobs.lever.co/northwind/8f14e45f-ceea-467a-9e3a-7c2c1f0a0001",
                descriptionPlain = "Support Epic optimization.", lists = new[] { new { text = "Requirements", content = "<li>Epic EHR experience</li><li>BLS</li>" } }, additionalPlain = "",
                salaryRange = new { min = 85000, max = 105000, currency = "USD", interval = "per-year-salary" },
            }),
            ("api.ashbyhq.com", "/posting-api/job-board/civic") => Json(new
            {
                jobs = new[]
                {
                    new { id = "0b6b2a4c-1d2e-4f00-8a00-00000000c001", title = "Nurse Case Manager", location = "Remote", isRemote = true, workplaceType = "Remote", publishedAt = "2026-10-20T00:00:00Z", jobUrl = "https://jobs.ashbyhq.com/civic/0b6b2a4c-1d2e-4f00-8a00-00000000c001", descriptionPlain = "Ability to obtain a Public Trust clearance.", compensation = new { compensationTierSummary = "$78K – $92K" } },
                },
            }),
            ("careers.initech.test", "/jobs/77") => (HttpStatusCode.OK, """
                <html><head><script type="application/ld+json">{"@context":"https://schema.org","@type":"JobPosting","title":"Data Engineer",
                "hiringOrganization":{"@type":"Organization","name":"Initech"},"datePosted":"2026-11-01","validThrough":"2026-12-15T00:00",
                "description":"&lt;p&gt;Python and SQL.&lt;/p&gt;","jobLocationType":"TELECOMMUTE","applicantLocationRequirements":{"@type":"Country","name":"USA"},
                "identifier":{"@type":"PropertyValue","value":"R-77"},"baseSalary":{"@type":"MonetaryAmount","currency":"USD","value":{"@type":"QuantitativeValue","minValue":120000,"maxValue":150000,"unitText":"YEAR"}}}
                </script></head><body>Job</body></html>
                """, "text/html"),
            _ => (HttpStatusCode.NotFound, "{}", null),
        };
    });

    [Fact]
    public async Task A_greenhouse_board_lists_and_a_posting_reads_with_pay_and_requisition()
    {
        using var client = Client(Boards());
        var list = new BrickInput();
        list.Set("board", "greenhouse:acme|Acme Analytics");
        var listed = await new JobBoardBrick(client).ExecuteAsync(list, ImplementationType.Deterministic, Ctx);
        var entry = listed.Get<List<BoardEntry>>("entries").Should().ContainSingle().Subject;
        (entry.Employer, entry.PostedDate, entry.Source.Key()).Should().Be(("Acme Analytics", "2026-11-02", "greenhouse.getJob:company=acme&jobId=4001001"));

        var get = new BrickInput();
        get.Set("posting", entry.Source.Key());
        var read = await new JobPostingBrick(client).ExecuteAsync(get, ImplementationType.Deterministic, Ctx);
        read.Get<string>("signal").Should().Be("found", read.Get<string>("detail"));
        var p = read.Get<JobPosting>("posting");
        (p.RequisitionId, p.PayText).Should().Be(("AA-2210", "USD 150,000–180,000"));
        p.Description.Should().Contain("- Python & SQL");
    }

    [Fact]
    public async Task A_lever_posting_reads_sections_salary_and_arrangement()
    {
        using var client = Client(Boards());
        var input = new BrickInput();
        input.Set("url", "https://jobs.lever.co/northwind/8f14e45f-ceea-467a-9e3a-7c2c1f0a0001");
        var p = (await new JobPostingBrick(client).ExecuteAsync(input, ImplementationType.Deterministic, Ctx)).Get<JobPosting>("posting");
        (p.Employer, p.WorkArrangement, p.PostedDate, p.PayText).Should().Be(("Northwind", "onsite", "2026-11-02", "USD 85,000–105,000 per year-salary"));
        p.Description.Should().Contain("- Epic EHR experience");
    }

    [Fact]
    public async Task An_ashby_posting_is_read_from_the_board_and_one_no_longer_listed_is_gone()
    {
        using var client = Client(Boards());
        var found = new BrickInput();
        found.Set("url", "https://jobs.ashbyhq.com/civic/0b6b2a4c-1d2e-4f00-8a00-00000000c001");
        var p = (await new JobPostingBrick(client).ExecuteAsync(found, ImplementationType.Deterministic, Ctx)).Get<JobPosting>("posting");
        (p.WorkArrangement, p.PayText).Should().Be(("remote", "$78K – $92K"));

        var missing = new BrickInput();
        missing.Set("url", "https://jobs.ashbyhq.com/civic/0b6b2a4c-1d2e-4f00-8a00-00000000c999");
        (await new JobPostingBrick(client).ExecuteAsync(missing, ImplementationType.Deterministic, Ctx)).Get<string>("signal").Should().Be("gone");
    }

    [Fact]
    public async Task Any_career_page_with_schema_org_JobPosting_reads_and_an_expired_one_is_gone()
    {
        using var client = Client(Boards());
        var input = new BrickInput();
        input.Set("url", "https://careers.initech.test/jobs/77");
        input.Set("today", "2026-11-08");
        var output = await new JobPostingBrick(client).ExecuteAsync(input, ImplementationType.Deterministic, Ctx);
        output.Get<string>("signal").Should().Be("found", output.Get<string>("detail"));
        var p = output.Get<JobPosting>("posting");
        (p.Employer, p.WorkArrangement, p.Deadline, p.RequisitionId, p.RemoteEligibility, p.PayText)
            .Should().Be(("Initech", "remote", "2026-12-15", "R-77", "USA", "USD 120,000–150,000 per year"));

        input.Set("today", "2026-12-16");
        (await new JobPostingBrick(client).ExecuteAsync(input, ImplementationType.Deterministic, Ctx)).Get<string>("signal").Should().Be("gone");
    }

    [Theory]
    [InlineData("Requirements\n- 5+ years", null)]
    [InlineData("Requisition requirements follow", null)]
    [InlineData("Req ID: AA-2210", "AA-2210")]
    [InlineData("Requisition #R1042 opened", "R1042")]
    [InlineData("Job ID 77812", "77812")]
    public void A_requisition_id_in_text_needs_a_label_and_a_digit(string text, string? expected) =>
        JobPostings.RequisitionFromText(text).Should().Be(expected);

    [Theory]
    [InlineData("https://job-boards.greenhouse.io/acme/jobs/123", "greenhouse.getJob:company=acme&jobId=123")]
    [InlineData("https://jobs.lever.co/acme/2A55D6C9-C564-443A-BE3E-A3851DB5A93B", "lever.getJob:company=acme&postingId=2a55d6c9-c564-443a-be3e-a3851db5a93b")]
    [InlineData("https://careers.example.com/jobs/9?src=x", "schema-org.getJobPosting:url=https%3A%2F%2Fcareers.example.com%2Fjobs%2F9%3Fsrc%3Dx")]
    public void Posting_urls_map_to_their_read(string url, string key) => JobPostings.FromUrl(url)!.Value.Ref.Key().Should().Be(key);

    [Theory]
    [InlineData("http://jobs.lever.co/acme/x")]
    [InlineData("https://user:pw@careers.example.com/job")]
    [InlineData("not a url")]
    public void Anything_else_is_not_a_posting(string url) => JobPostings.FromUrl(url).Should().BeNull();

    [Fact]
    public void Every_brick_declares_its_deterministic_implementation_so_a_workflow_can_run_it()
    {
        using var client = Client(new Web(_ => Json(new { })));
        Ashlar.Core.Domain.Bricks.Brick[] bricks = [new WebCallBrick(client), new WebLearnBrick(client), new JobBoardBrick(client), new JobPostingBrick(client)];
        bricks.Should().OnlyContain(b => b.Implementations.HasDeterministic);
    }
}
