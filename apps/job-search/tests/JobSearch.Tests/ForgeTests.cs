using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Ashlar.Abstractions.Security.Egress;
using Dapper;
using FluentAssertions;
using JobSearch.Host.Studio;
using JobSearch.Privacy;
using JobSearch.Studio;
using Xunit;

#pragma warning disable ASHLAREXP002 // EgressGuard and EgressRefusedException are the experimental egress surface the privacy routing is tested through.

namespace JobSearch.Tests;

/// <summary>An HTTP handler that answers like a model API and records every request that reached it.</summary>
public sealed class RecordingHandler(Func<string, string> respond) : HttpMessageHandler
{
    public List<(Uri Url, string Body)> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        lock (Requests)
            Requests.Add((request.RequestUri!, body));
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(respond(body), Encoding.UTF8, "application/json") };
    }
}

/// <summary>A step writer that answers from a script and records what it was asked.</summary>
public sealed class ScriptedWriter(Func<string, string> answer) : IStepWriter
{
    public List<string> Prompts { get; } = [];

    public string Describe() => "scripted writer";

    public Task<ModelReply> WriteJsonAsync(string system, string user, string jsonSchema, CancellationToken ct)
    {
        lock (Prompts)
            Prompts.Add(user);
        return Task.FromResult(new ModelReply(answer(user), "scripted", 100, 200));
    }
}

/// <summary>New steps from plain words, and where personal data may and may not go on the way.</summary>
public sealed class ForgeTests
{
    private static readonly EgressGuard Enforcing = new("full", "enforce");

    /// <summary>Whether the guard refused: the refusal may arrive wrapped by the HTTP stack or the SDK.</summary>
    private static bool Refused(Exception? e)
    {
        for (; e is not null; e = e.InnerException)
            if (e is EgressRefusedException)
                return true;
        return false;
    }

    /// <summary>An Anthropic Messages API response whose text is <paramref name="text"/>.</summary>
    public static string AnthropicResponse(string text) => new JsonObject
    {
        ["id"] = "msg_test", ["type"] = "message", ["role"] = "assistant", ["model"] = "claude-opus-5-5",
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        ["stop_reason"] = "end_turn", ["stop_sequence"] = null,
        ["usage"] = new JsonObject { ["input_tokens"] = 1200, ["output_tokens"] = 800 },
    }.ToJsonString();

    private static string SyntheticCode() => SyntheticStepWriter_Answer();

    private static string SyntheticStepWriter_Answer() =>
        new SyntheticStepWriter().WriteJsonAsync("", "", "", default).GetAwaiter().GetResult().Text;

    private static string Proposal(string decision, string reuseId, string[] settings, JsonObject? design = null) => new JsonObject
    {
        ["decision"] = decision, ["reuseStepId"] = reuseId, ["settings"] = new JsonArray([.. settings.Select(s => (JsonNode)JsonValue.Create(s)!)]),
        ["question"] = "", ["design"] = design ?? SyntheticPrivateModel.FallbackDesign.DeepClone(),
    }.ToJsonString();

    [Fact]
    public async Task A_model_call_from_a_frame_that_read_user_data_is_refused()
    {
        var handler = new RecordingHandler(_ => AnthropicResponse("{}"));
        var writer = new ClaudeStepWriter("test-key", null, Enforcing, handler);

        var fromPersonal = async () =>
        {
            using (Pii.EnterPersonalFrame("test"))
                await writer.WriteJsonAsync("s", "u", "{\"type\":\"object\"}", default);
        };
        Refused((await fromPersonal.Should().ThrowAsync<Exception>()).Which).Should().BeTrue();
        var outsideAnyFrame = () => writer.WriteJsonAsync("s", "u", "{\"type\":\"object\"}", default);
        Refused((await outsideAnyFrame.Should().ThrowAsync<Exception>()).Which).Should().BeTrue("no frame reads as SystemHigh");
        handler.Requests.Should().BeEmpty("nothing left the host");

        // From a frame that has read nothing personal, the same call goes out.
        using (Pii.EnterPiiFreeFrame("test"))
            (await writer.WriteJsonAsync("s", "u", "{\"type\":\"object\"}", default)).Text.Should().Be("{}");
        handler.Requests.Should().ContainSingle().Which.Url.Host.Should().Be("api.anthropic.com");

        // Reading the profile inside a PII-free frame raises it: the next call is refused.
        using var h = new Harness();
        var (jordan, _) = h.Jordan();
        var afterRead = async () =>
        {
            using (Pii.EnterPiiFreeFrame("test"))
            {
                h.App.Profiles.Load(jordan);
                await writer.WriteJsonAsync("s", "u", "{\"type\":\"object\"}", default);
            }
        };
        Refused((await afterRead.Should().ThrowAsync<Exception>()).Which).Should().BeTrue();
        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task The_private_model_must_be_on_this_host_and_may_receive_personal_data_there()
    {
        var offHost = () => new OllamaPrivateModel(new Uri("http://10.0.0.5:11434"), "m", Enforcing);
        offHost.Should().Throw<InvalidOperationException>().WithMessage("*loopback*");
        var named = () => new OllamaPrivateModel(new Uri("http://ollama:11434"), "m", Enforcing);
        named.Should().Throw<InvalidOperationException>();

        var handler = new RecordingHandler(_ => "{\"message\":{\"content\":\"{\\\"ok\\\":true}\"},\"prompt_eval_count\":5,\"eval_count\":7}");
        using var model = new OllamaPrivateModel(new Uri("http://127.0.0.1:11434"), "llama3.1:8b", Enforcing, handler);
        using (Pii.EnterPersonalFrame("test"))
        {
            var reply = await model.CompleteJsonAsync("s", "my résumé says Jordan Rivera", "{\"type\":\"object\"}", default);
            reply.Text.Should().Be("{\"ok\":true}");
            reply.OutputTokens.Should().Be(7);
        }
        handler.Requests.Should().ContainSingle().Which.Body.Should().Contain("Jordan Rivera", "personal data may reach a model on this host");
    }

    [Fact]
    public async Task The_health_probe_is_ready_only_when_the_server_has_the_configured_model()
    {
        var tags = "{\"models\":[{\"name\":\"llama3.1:8b\"},{\"name\":\"phi3:latest\"}]}";
        using (var ready = new OllamaPrivateModel(new Uri("http://127.0.0.1:11434"), "llama3.1:8b", Enforcing, new RecordingHandler(_ => tags)))
            (await ready.ReadyAsync(default)).Should().BeTrue();
        using (var untagged = new OllamaPrivateModel(new Uri("http://127.0.0.1:11434"), "phi3", Enforcing, new RecordingHandler(_ => tags)))
            (await untagged.ReadyAsync(default)).Should().BeTrue("Ollama lists a model pulled without a tag as name:latest");
        using (var notPulled = new OllamaPrivateModel(new Uri("http://127.0.0.1:11434"), "llama3.1:70b", Enforcing, new RecordingHandler(_ => tags)))
            (await notPulled.ReadyAsync(default)).Should().BeFalse("the server answers but has not pulled the configured model");
        using (var garbled = new OllamaPrivateModel(new Uri("http://127.0.0.1:11434"), "llama3.1:8b", Enforcing, new RecordingHandler(_ => "<html>")))
            (await garbled.ReadyAsync(default)).Should().BeFalse();
        (await new UnavailablePrivateModel().ReadyAsync(default)).Should().BeFalse();
    }

    [Fact]
    public async Task A_request_a_library_step_covers_reuses_it_without_the_writer()
    {
        var writer = new ScriptedWriter(_ => throw new InvalidOperationException("the writer must not be called"));
        var exclude = Harness.SeedId("exclude-words");
        var model = new ScriptedModel(_ => Proposal("reuse", exclude, ["on-call, pager", "", ""]));
        using var h = new Harness(model: model, writer: writer);
        await h.SeedAsync();
        var (jordan, _) = h.Jordan();

        var id = h.App.Forge.Submit(jordan, jordan, "skip anything that says on-call or pager", makePrivate: false);
        (await h.App.Forge.RunNextAsync(default)).Should().BeTrue();

        var job = h.App.Forge.Get(jordan, id)!;
        job.Status.Should().Be("done", job.Error);
        job.Reused.Should().BeTrue();
        writer.Prompts.Should().BeEmpty("a step already in the library costs no writer tokens");
        var step = h.App.Workflows.Get(jordan).Steps.Should().ContainSingle().Subject;
        step.StepId.Should().Be(exclude);
        step.Settings.Should().Equal("on-call, pager", "", "");
        model.Calls.Should().ContainSingle().Which.User.Should().Contain("skip anything that says on-call or pager").And.Contain(exclude);
        h.App.Forge.Allowance(jordan).Used.Should().Be(0, "reuse does not count against the monthly allowance");
        h.App.Db.Read(c => c.QuerySingle<string?>("SELECT request_text FROM forge_jobs WHERE id=@id", new { id })).Should().BeNull("the request text is dropped once the job finishes");
        h.App.Forge.Usage(jordan).Should().OnlyContain(u => u.Tier == "private");
    }

    [Fact]
    public async Task A_new_step_is_designed_privately_written_by_Claude_from_the_generic_design_certified_and_shared()
    {
        var anthropic = new RecordingHandler(_ => AnthropicResponse(SyntheticCode()));
        var writer = new ClaudeStepWriter("test-key", null, Enforcing, anthropic);
        var model = new ScriptedModel(_ => Proposal("new", "", ["intern", "", ""]));
        using var h = new Harness(model: model, writer: writer);
        await h.SeedAsync();
        var (jordan, _) = h.Jordan();
        var (sam, _) = h.Sam();
        const string Request = "I'm Jordan Rivera (jordan.rivera@example.com) and I don't want internships, flag any title with intern";

        var id = h.App.Forge.Submit(jordan, jordan, Request, makePrivate: false);
        await h.App.Forge.RunNextAsync(default);
        var job = h.App.Forge.Get(jordan, id)!;
        job.Status.Should().Be("done", job.Error + " | " + string.Join(" / ", job.Log));
        job.Reused.Should().BeFalse();
        job.Private.Should().BeFalse();
        job.Examples.Should().NotBeEmpty();

        // What left the host: the generic design and the contract. Not the request, not the résumé, not the person.
        var sent = anthropic.Requests.Should().ContainSingle().Subject.Body;
        sent.Should().Contain("Flag postings whose title contains a word");
        sent.Should().NotContainAny("Jordan", "jordan.rivera@example.com", "internships", "Acme Logistics", "Built Airflow pipelines");
        sent.Should().Contain("\"effort\":\"high\"").And.Contain("\"fallbacks\":\"default\"");

        var entry = h.App.Library.Get(job.StepId!)!;
        entry.Visibility.Should().Be("shared");
        entry.Origin.Should().Be("claude");
        h.App.Library.Visible(sam).Should().Contain(e => e.Id == entry.Id, "a shared step is there for the next user, at no token cost");
        h.App.Workflows.Get(jordan).Steps.Should().ContainSingle(s => s.StepId == entry.Id && s.Settings[0] == "intern");
        h.App.Forge.Allowance(jordan).Used.Should().Be(1);
        h.App.Forge.Usage(jordan).Should().Contain(u => u.Tier == "external" && u.Tokens == 2000);
    }

    [Fact]
    public async Task A_design_that_identifies_the_user_never_leaves_the_host_and_its_step_stays_private()
    {
        var writer = new ScriptedWriter(_ => throw new InvalidOperationException("the external writer must not be called"));
        var dirty = SyntheticPrivateModel.FallbackDesign.DeepClone().AsObject();
        dirty["description"] = "Flags titles for Jordan Rivera, who left Acme Logistics.";
        var model = new ScriptedModel(user =>
            user.StartsWith("DESIGN:", StringComparison.Ordinal) ? SyntheticCode()
            : user.StartsWith("Rewrite this step design", StringComparison.Ordinal) ? dirty.ToJsonString()
            : Proposal("new", "", ["intern", "", ""], dirty));
        using var h = new Harness(model: model, writer: writer);
        await h.SeedAsync();
        var (jordan, _) = h.Jordan();
        var (sam, _) = h.Sam();

        var id = h.App.Forge.Submit(jordan, jordan, "flag intern titles", makePrivate: false);
        await h.App.Forge.RunNextAsync(default);
        var job = h.App.Forge.Get(jordan, id)!;
        job.Status.Should().Be("done", job.Error + " | " + string.Join(" / ", job.Log));
        job.Private.Should().BeTrue();
        writer.Prompts.Should().BeEmpty();
        model.Calls.Should().Contain(c => c.User.StartsWith("Rewrite this step design", StringComparison.Ordinal), "the private model was asked to make it generic first");
        var entry = h.App.Library.Get(job.StepId!)!;
        entry.Visibility.Should().Be("private");
        entry.Origin.Should().Be("private-model");
        h.App.Library.Visible(sam).Should().NotContain(e => e.Id == entry.Id);
        h.App.Forge.Usage(jordan).Should().OnlyContain(u => u.Tier == "private");
    }

    [Fact]
    public async Task A_refused_certification_goes_back_to_the_writer_with_the_gates_reasons()
    {
        var full = JsonNode.Parse(SyntheticCode())!.AsObject();
        var weak = full.DeepClone().AsObject();
        // Without the two-letter case, turning 0 into 2 in "word.Length > 0" survives: the gate must refuse it.
        weak["cases"] = new JsonArray([.. full["cases"]!.AsArray().Where(c => c!["title"]!.GetValue<string>() != "Go Developer").Select(c => c!.DeepClone())]);
        var writer = new ScriptedWriter(user => user.Contains("WAS REFUSED", StringComparison.Ordinal) ? full.ToJsonString() : weak.ToJsonString());
        using var h = new Harness(model: new ScriptedModel(_ => Proposal("new", "", ["intern", "", ""])), writer: writer);
        await h.SeedAsync();
        var (jordan, _) = h.Jordan();

        var id = h.App.Forge.Submit(jordan, jordan, "flag intern titles", makePrivate: false);
        await h.App.Forge.RunNextAsync(default);
        var job = h.App.Forge.Get(jordan, id)!;
        job.Status.Should().Be("done", job.Error + " | " + string.Join(" / ", job.Log));
        writer.Prompts.Should().HaveCount(2);
        writer.Prompts[1].Should().Contain("refused the step at the mutation check").And.Contain("mutate-int-literal");
        job.Log.Should().Contain(l => l.Contains("refused at the mutation check"));
    }

    [Fact]
    public async Task The_monthly_allowance_stops_new_steps_but_not_reuse()
    {
        var exclude = Harness.SeedId("exclude-words");
        var model = new ScriptedModel(user => user.Contains("on-call", StringComparison.Ordinal)
            ? Proposal("reuse", exclude, ["on-call", "", ""]) : Proposal("new", "", ["intern", "", ""]));
        using var h = new Harness(model: model, writer: new ScriptedWriter(_ => SyntheticCode()), limits: new ForgeLimits { MonthlyNewSteps = 0 });
        await h.SeedAsync();
        var (jordan, _) = h.Jordan();

        var fresh = h.App.Forge.Submit(jordan, jordan, "flag intern titles", makePrivate: false);
        await h.App.Forge.RunNextAsync(default);
        h.App.Forge.Get(jordan, fresh)!.Error.Should().Contain("used all 0 new steps");
        var reuse = h.App.Forge.Submit(jordan, jordan, "skip on-call jobs", makePrivate: false);
        await h.App.Forge.RunNextAsync(default);
        h.App.Forge.Get(jordan, reuse)!.Status.Should().Be("done");
    }

    [Fact]
    public void Only_the_user_may_ask_for_a_step()
    {
        using var h = new Harness();
        var (jordan, _) = h.Jordan();
        var (sam, _) = h.Sam();
        var asOther = () => h.App.Forge.Submit(jordan, sam, "anything", makePrivate: false);
        asOther.Should().Throw<Accounts.AccessDeniedException>();
    }

    [Fact]
    public void The_PII_gate_finds_identifier_shapes_and_the_users_own_strings_without_echoing_them()
    {
        using var h = new Harness();
        var (jordan, _) = h.Jordan();
        var gate = new PiiGate(h.App.Profiles.Load(jordan), ["Acme Corp"]);
        var findings = gate.Inspect(
        [
            ("a", "write to jordan.rivera@example.com or call (555) 010-0101"),
            ("b", "SSN 123-45-6789, card 4111 1111 1111 1111, see linkedin.com/in/someone"),
            ("c", "Jordan worked at Acme Corp in Denver, CO"),
            ("d", "Flag postings whose title contains a word"),
        ]);
        findings.Select(f => f.Kind).Should().Contain(["email", "phone", "national-id", "card-number", "profile-link", "your name", "your setting"]);
        findings.Should().NotContain(f => f.Where == "d");
        PiiGate.Describe(findings).Should().NotContainAny("jordan.rivera", "123-45-6789", "Acme Corp");
    }
}
