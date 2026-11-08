using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using FluentAssertions;
using JobSearch.Host.Web;
using Microsoft.AspNetCore.Builder;
using Xunit;

namespace JobSearch.Tests;

/// <summary>A studio web server on a free loopback port, over a synthetic harness.</summary>
public sealed class WebHarness : IAsyncDisposable
{
    public Harness H { get; }
    public WebApplication Web { get; }
    public Uri Base { get; }

    private WebHarness(Harness h, WebApplication web, Uri baseUri)
    {
        H = h;
        Web = web;
        Base = baseUri;
    }

    public static async Task<WebHarness> StartAsync(bool synthetic = true)
    {
        var h = new Harness(synthetic: synthetic);
        if (synthetic)
            Synthetic.EnsurePersonas(h.App, Harness.Fixtures);
        await h.SeedAsync();
        var web = StudioServer.Build(h.App, new StudioServerOptions { Urls = "http://127.0.0.1:0", PublicUrl = "http://127.0.0.1", Workers = false });
        await web.StartAsync();
        return new WebHarness(h, web, new Uri(web.Urls.First()));
    }

    /// <summary>A browser-like client: its own cookies, and the header the studio's own pages send on writes.</summary>
    public HttpClient Client(bool csrfHeader = true)
    {
        var client = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = false }) { BaseAddress = Base };
        if (csrfHeader)
            client.DefaultRequestHeaders.Add(StudioServer.CsrfHeader, "1");
        return client;
    }

    public async Task<HttpClient> SignInAsync(string email)
    {
        var client = Client();
        (await client.PostAsJsonAsync("/auth/dev", new { email })).EnsureSuccessStatusCode();
        return client;
    }

    public string UserId(string email) => H.App.Accounts.FindUser(email)!.Id;

    public async ValueTask DisposeAsync()
    {
        await Web.StopAsync();
        await Web.DisposeAsync();
        H.Dispose();
    }
}

/// <summary>The studio's HTTP surface: sign-in, consent, the designer API, isolation, export and erasure.</summary>
public sealed class WebTests
{
    private const string Jordan = "jordan.rivera@example.com";
    private const string Sam = "sam.okafor@example.com";
    private const string Casey = "new.user@example.com";

    [Fact]
    public async Task Signed_out_requests_are_refused_and_every_response_carries_the_privacy_headers()
    {
        await using var w = await WebHarness.StartAsync();
        using var anon = w.Client();
        (await anon.GetAsync("/api/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await anon.GetAsync("/api/workflow")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var meta = await anon.GetFromJsonAsync<JsonObject>("/api/meta");
        meta!["synthetic"]!.GetValue<bool>().Should().BeTrue();
        meta["privateModel"]!.GetValue<string>().Should().Contain("synthetic");
        (await anon.GetAsync("/health/model")).StatusCode.Should().Be(HttpStatusCode.OK);

        var page = await anon.GetAsync("/");
        page.StatusCode.Should().Be(HttpStatusCode.OK);
        var csp = page.Headers.GetValues("Content-Security-Policy").Single();
        csp.Should().Contain("default-src 'self'").And.Contain("connect-src 'self'").And.Contain("frame-ancestors 'none'");
        page.Headers.GetValues("Referrer-Policy").Single().Should().Be("no-referrer");
        page.Headers.GetValues("X-Content-Type-Options").Single().Should().Be("nosniff");
        var html = await page.Content.ReadAsStringAsync();
        html.Should().NotContainAny("https://", "http://", "cdn", "googleapis.com/css", "analytics");
    }

    [Fact]
    public async Task Writes_without_the_studio_header_are_refused()
    {
        await using var w = await WebHarness.StartAsync();
        using var bare = w.Client(csrfHeader: false);
        (await bare.PostAsJsonAsync("/auth/dev", new { email = Jordan })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using var jordan = await w.SignInAsync(Jordan);
        (await jordan.GetAsync("/api/me")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await jordan.PutAsJsonAsync("/api/workflow", new { steps = Array.Empty<object>() })).StatusCode.Should().Be(HttpStatusCode.OK, "the studio's own pages send the header");
        // The same session cookie without the header (what a cross-site form or script could send) changes nothing.
        jordan.DefaultRequestHeaders.Remove(StudioServer.CsrfHeader);
        (await jordan.PutAsJsonAsync("/api/workflow", new { steps = Array.Empty<object>() })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task The_persona_sign_in_does_not_exist_outside_synthetic_mode()
    {
        await using var w = await WebHarness.StartAsync(synthetic: false);
        using var client = w.Client();
        (await client.PostAsJsonAsync("/auth/dev", new { email = "anyone@example.com" })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.GetFromJsonAsync<JsonObject>("/api/meta"))!["personas"].Should().BeNull();
        // No private model configured: the probe says so, and nothing falls back to an external model.
        var probe = await client.GetAsync("/health/model");
        probe.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await probe.Content.ReadAsStringAsync()).Should().Contain("unavailable");
    }

    [Fact]
    public async Task A_new_user_must_consent_before_the_studio_does_anything_with_their_data()
    {
        await using var w = await WebHarness.StartAsync();
        using var casey = await w.SignInAsync(Casey);
        var me = await casey.GetFromJsonAsync<JsonObject>("/api/me");
        me!["onboarded"]!.GetValue<bool>().Should().BeFalse();
        var blocked = await casey.GetAsync("/api/workflow");
        blocked.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await blocked.Content.ReadFromJsonAsync<JsonObject>())!["error"]!.GetValue<string>().Should().Be("consent-required");
        (await casey.PostAsJsonAsync("/api/profile/resume", new { text = "Casey New" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        (await casey.PostAsJsonAsync("/api/me/consent", new { kinds = new[] { "privacy-notice", "terms", "age-16-or-over" }, timezone = "America/Chicago" })).EnsureSuccessStatusCode();
        (await casey.GetAsync("/api/workflow")).StatusCode.Should().Be(HttpStatusCode.OK);
        // Model wording needs its own consent, and withdrawing it turns the feature off.
        var prefs = await casey.GetFromJsonAsync<JsonObject>("/api/preferences");
        prefs!["materials"]!["modelCoverLetter"] = true;
        (await casey.PutAsJsonAsync("/api/preferences", prefs)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await casey.PostAsJsonAsync("/api/me/consent", new { kinds = new[] { "model-wording" } })).EnsureSuccessStatusCode();
        (await casey.PutAsJsonAsync("/api/preferences", prefs)).EnsureSuccessStatusCode();
        (await casey.PostAsJsonAsync("/api/me/consent/withdraw", new { kinds = new[] { "model-wording" } })).EnsureSuccessStatusCode();
        (await casey.GetFromJsonAsync<JsonObject>("/api/preferences"))!["materials"]!["modelCoverLetter"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task The_designer_reads_the_library_saves_a_workflow_shows_its_wiring_and_forges_a_step()
    {
        await using var w = await WebHarness.StartAsync();
        using var jordan = await w.SignInAsync(Jordan);
        var library = await jordan.GetFromJsonAsync<JsonArray>("/api/library");
        library!.Count.Should().Be(4);
        var exclude = Harness.SeedId("exclude-words");
        var detail = await jordan.GetFromJsonAsync<JsonObject>($"/api/library/{exclude}");
        detail!["examples"]!.AsArray().Should().NotBeEmpty("the designer shows what the step does on made-up postings");
        detail["writtenBy"]!.GetValue<string>().Should().Be("seed");

        (await jordan.PutAsJsonAsync("/api/workflow", new { steps = new[] { new { key = "nocall", stepId = exclude, enabled = true, settings = new[] { "on-call", "", "" } } } })).EnsureSuccessStatusCode();
        var workflow = await jordan.GetFromJsonAsync<JsonObject>("/api/workflow");
        workflow!["steps"]!.AsArray().Single()!["step"]!["name"]!.GetValue<string>().Should().Be("Exclude postings that mention words");
        (await jordan.PutAsJsonAsync("/api/workflow", new { steps = new[] { new { key = "x", stepId = "step.nope.00000000", enabled = true, settings = new[] { "", "", "" } } } }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var graph = await jordan.GetFromJsonAsync<JsonObject>("/api/workflow/graph");
        graph!["nodes"]!.AsArray().Should().Contain(n => n!["brickId"] != null && n["brickId"]!.GetValue<string>() == exclude);
        graph["edges"]!.AsArray().Should().Contain(e => e!["to"]!.GetValue<string>() == "decide" && e["toPort"]!.GetValue<string>() == "verdict.nocall");

        var submitted = await jordan.PostAsJsonAsync("/api/forge", new { request = "flag any title with \"trainee\"", @private = false });
        submitted.EnsureSuccessStatusCode();
        var id = (await submitted.Content.ReadFromJsonAsync<JsonObject>())!["id"]!.GetValue<string>();
        await w.H.App.Forge.RunNextAsync(default);
        var job = await jordan.GetFromJsonAsync<JsonObject>($"/api/forge/{id}");
        job!["status"]!.GetValue<string>().Should().Be("done", job.ToJsonString());
        (await jordan.GetFromJsonAsync<JsonObject>("/api/workflow"))!["steps"]!.AsArray().Should().HaveCount(2);
    }

    [Fact]
    public async Task One_user_cannot_reach_anothers_jobs_requests_or_private_steps()
    {
        await using var w = await WebHarness.StartAsync();
        using var jordan = await w.SignInAsync(Jordan);
        using var sam = await w.SignInAsync(Sam);
        var submitted = await jordan.PostAsJsonAsync("/api/forge", new { request = "flag any title with \"trainee\"", @private = true });
        var id = (await submitted.Content.ReadFromJsonAsync<JsonObject>())!["id"]!.GetValue<string>();
        await w.H.App.Forge.RunNextAsync(default);
        var stepId = (await jordan.GetFromJsonAsync<JsonObject>($"/api/forge/{id}"))!["stepId"]!.GetValue<string>();

        (await sam.GetAsync($"/api/forge/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await sam.GetAsync($"/api/library/{stepId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await sam.GetFromJsonAsync<JsonArray>("/api/library"))!.Should().NotContain(e => e!["id"]!.GetValue<string>() == stepId);
        (await sam.PutAsJsonAsync("/api/workflow", new { steps = new[] { new { key = "steal", stepId, enabled = true, settings = new[] { "", "", "" } } } }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        await w.H.App.Engine.RunAsync(w.UserId(Jordan), w.UserId(Jordan), "d1");
        (await jordan.GetFromJsonAsync<JsonArray>("/api/tracker"))!.Count.Should().BeGreaterThan(0);
        (await sam.GetFromJsonAsync<JsonArray>("/api/tracker"))!.Should().BeEmpty("Sam has not run a search; Jordan's jobs are not his");
    }

    [Fact]
    public async Task A_user_can_export_everything_and_then_erase_it_all()
    {
        await using var w = await WebHarness.StartAsync();
        var jordanId = w.UserId(Jordan);
        using var jordan = await w.SignInAsync(Jordan);
        await w.H.App.Engine.RunAsync(jordanId, jordanId, "d1");
        var token = w.H.App.Secrets.Get($"google:{jordanId}")!["access_token"];

        var export = await jordan.GetAsync("/api/me/export");
        export.Content.Headers.ContentDisposition!.ToString().Should().Contain("attachment");
        var text = await export.Content.ReadAsStringAsync();
        var data = JsonNode.Parse(text)!.AsObject();
        data["resume_versions"]!.AsArray().Single()!["text"]!.GetValue<string>().Should().Contain("Jordan Rivera");
        data["jobs"]!.AsArray().Should().NotBeEmpty();
        data["consents"]!.AsArray().Should().HaveCountGreaterThanOrEqualTo(3);
        text.Should().NotContain(token, "a token is never exported");

        (await jordan.PostAsJsonAsync("/api/me/delete", new { confirm = "nope" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await jordan.PostAsJsonAsync("/api/me/delete", new { confirm = "DELETE" })).EnsureSuccessStatusCode();
        (await jordan.GetAsync("/api/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var tables = w.H.App.Db.Read(c => c.Query<string>("SELECT name FROM sqlite_master WHERE type='table'").AsList());
        foreach (var table in tables)
        {
            var columns = w.H.App.Db.Read(c => c.Query<string>($"SELECT name FROM pragma_table_info('{table}')").AsList());
            foreach (var column in columns.Where(c => c is "user_id" or "id" or "manager_id" or "owner_user_id"))
                w.H.App.Db.Read(c => c.ExecuteScalar<long>($"SELECT COUNT(*) FROM {table} WHERE {column}=@jordanId", new { jordanId }))
                    .Should().Be(0, $"{table}.{column} must not keep the erased user's id");
        }
        File.ReadAllText(Path.Combine(w.H.Dir, "secrets.json")).Should().NotContain($"google:{jordanId}");
        w.H.App.Compliance.WasErased(jordanId).Should().BeTrue();
        w.H.App.Accounts.FindUser(Sam).Should().NotBeNull("erasing one user touches no one else");
    }

    [Fact]
    public async Task The_retention_sweep_removes_what_has_outlived_its_period_and_keeps_application_history()
    {
        using var h = new Harness();
        var (jordan, _) = h.Jordan();
        await h.App.Engine.RunAsync(jordan, jordan, "d1");
        var jobs = h.App.Db.Read(c => c.Query<string>("SELECT id FROM jobs WHERE user_id=@jordan ORDER BY id", new { jordan }).AsList());
        var old = DateTimeOffset.UtcNow.AddDays(-400).ToString("o");
        h.App.Db.Write((c, tx) =>
        {
            c.Execute("UPDATE jobs SET archived_at=@old WHERE id IN @ids", new { old, ids = jobs.Take(2) }, tx);
            c.Execute("INSERT INTO applications (job_id, user_id, status, updated_at) VALUES (@id, @jordan, 'applied', @old) ON CONFLICT(job_id) DO UPDATE SET status='applied'", new { id = jobs[1], jordan, old }, tx);
            c.Execute("UPDATE runs SET started_at=@old WHERE user_id=@jordan", new { old, jordan }, tx);
        });
        var removed = h.App.Compliance.Sweep(new JobSearch.Host.Studio.RetentionRules());
        removed["archived jobs"].Should().BeGreaterThan(0);
        removed["runs"].Should().BeGreaterThan(0);
        h.App.Db.Read(c => c.ExecuteScalar<long>("SELECT COUNT(*) FROM jobs WHERE id=@id", new { id = jobs[0] })).Should().Be(0);
        h.App.Db.Read(c => c.ExecuteScalar<long>("SELECT COUNT(*) FROM jobs WHERE id=@id", new { id = jobs[1] })).Should().Be(1, "a job the user applied to is their history, not a stale listing");
    }
}
