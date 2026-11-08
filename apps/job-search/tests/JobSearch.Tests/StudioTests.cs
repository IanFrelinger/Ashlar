using System.Text.Json;
using Dapper;
using FluentAssertions;
using JobSearch.Host.Studio;
using JobSearch.Studio;
using Xunit;

namespace JobSearch.Tests;

/// <summary>The shared step library, the isolated step host, and user steps in the posting workflow.</summary>
public sealed class StudioTests
{
    [Fact]
    public async Task Starter_steps_are_certified_in_a_child_process_signed_by_the_platform_and_published_shared()
    {
        using var h = new Harness();
        await h.SeedAsync();
        var steps = h.App.Library.Visible("anyone");
        steps.Select(s => s.Name).Should().BeEquivalentTo(
        [
            "Exclude postings that mention words", "Flag postings that pay less than an amount", "Required skills you're missing",
            "Keep postings in these places or remote",
        ]);
        foreach (var s in steps)
        {
            s.Visibility.Should().Be("shared");
            s.Origin.Should().Be("seed");
            s.Mutants.Should().BeGreaterThan(0);
            using var record = JsonDocument.Parse(File.ReadAllText(Path.Combine(h.App.Library.Folder(s.Id), "certification.json")));
            record.RootElement.GetProperty("admitted").GetBoolean().Should().BeTrue();
            record.RootElement.GetProperty("survivingMutants").GetInt32().Should().Be(0);
            record.RootElement.GetProperty("ed25519PublicKey").GetString().Should().Be(h.App.Keys.PublicKey, "the platform signs what it certifies");
            JobSearch.Host.CertifiedBricks.VerifyOnly(s.Id, h.App.Library.Folder(s.Id), h.App.Keys.Policy).Should().BeNull();
        }
        // Seeding again adds nothing: ids are stable.
        (await h.App.Library.EnsureSeedsAsync(h.App.Certifier, h.App.SeedsDir, Path.Combine(h.Dir, "forge"), default)).Should().Be(0);
    }

    [Fact]
    public void A_tampered_library_step_does_not_verify_and_is_not_published()
    {
        using var h = new Harness();
        var staged = Path.Combine(h.Dir, "staged");
        Directory.CreateDirectory(staged);
        File.WriteAllText(Path.Combine(staged, "GeneratedStep.cs"), "// not certified");
        var publish = () => h.App.Library.Publish(new LibraryEntry { Id = "step.fake.00000000", Kind = "check", Name = "fake" }, staged);
        publish.Should().Throw<InvalidOperationException>().WithMessage("*does not verify*");
        h.App.Library.Get("step.fake.00000000").Should().BeNull();
    }

    [Fact]
    public async Task A_users_step_excludes_a_posting_through_the_step_host_and_stops_its_drafts()
    {
        using var h = new Harness();
        await h.SeedAsync();
        var (jordan, _) = h.Jordan();
        var exclude = Harness.SeedId("exclude-words");
        var missing = Harness.SeedId("missing-skills");
        h.App.Workflows.Save(jordan, jordan, new WorkflowDocument
        {
            Steps =
            [
                new UserStep { Key = "nocall", StepId = exclude, Settings = ["kubernetes", "", ""] },
                new UserStep { Key = "skills", StepId = missing, Settings = ["", "", ""] },
            ],
        });
        var run = await h.App.Engine.RunAsync(jordan, jordan, "d1");
        run.WorkflowLog.Should().Contain(l => l.Contains(exclude + ": verdict=exclude"));

        var rows = h.App.Db.Read(c => c.Query<(string Title, string? ExcludedBy, string Json)>(
            "SELECT j.title, s.excluded_by, s.assessment_json FROM assessments s JOIN jobs j ON j.id=s.job_id WHERE s.user_id=@jordan", new { jordan }).AsList());
        var platform = rows.Single(r => r.Title == "Data Engineer, Platform");
        platform.ExcludedBy.Should().Be("Exclude postings that mention words");
        var a = JobSearch.Store.Json.Read<Screening.Assessment>(platform.Json)!;
        a.Steps.Should().Contain(s => s.Key == "nocall" && s.Verdict == "exclude" && s.Text == "mentions \"kubernetes\"");
        a.Steps.Should().Contain(s => s.Key == "skills" && s.Kind == "column" && s.Text.Contains("kubernetes"));
        // Excluded by the user's step: no drafts for it, though the certified recommendation alone would have allowed them.
        var platformId = h.App.Db.Read(c => c.QuerySingle<string>("SELECT id FROM jobs WHERE user_id=@jordan AND title='Data Engineer, Platform'", new { jordan }));
        h.App.Db.Read(c => c.ExecuteScalar<long>("SELECT COUNT(*) FROM documents WHERE job_id=@platformId", new { platformId })).Should().Be(0);
        // Postings the step passes still get drafts.
        rows.Should().Contain(r => r.ExcludedBy == null);
        run.Drafted.Should().NotBeEmpty();
    }

    /// <summary>
    /// A step whose witness never takes its loop: certification passes (mutation testing cannot reach the loop, and no
    /// case spins), but it never ends on one real input. Certification is not a termination proof; the step host is.
    /// </summary>
    private const string SpinCode = """
        private string Evaluate(string title, string employer, string location, string arrangement, string description, string payMin, string payMax, string skillsRequired, string skillsMissing, string setting1, string setting2, string setting3, out string note)
        {
            note = "";
            var t = title.ToLowerInvariant();
            if (t.Length == 0)
            {
                note = "no title";
                return "flag";
            }
            if (t.Length > 100)
            {
                note = "long title";
                return "flag";
            }
            if (t == "x")
            {
                note = "x title";
                return "exclude";
            }
            if (t == "y")
            {
                note = "y title";
                return "exclude";
            }
            while (setting1 == "spin-forever")
            {
                note = "spinning";
            }
            return "pass";
        }
        """;

    private static List<StepCase> SpinCases() => StepSource.FromFlat(StepContract.Check, new (string Title, string Verdict, string Note)[]
    {
        ("", "flag", "no title"), (new string('a', 101), "flag", "long title"), (new string('a', 100), "pass", ""), ("x", "exclude", "x title"),
        ("y", "exclude", "y title"), ("Data Engineer", "pass", ""), ("ab", "pass", ""), ("X", "exclude", "x title"),
    }.Select(c => (IReadOnlyDictionary<string, string>)new Dictionary<string, string> { ["title"] = c.Title, ["verdict"] = c.Verdict, ["note"] = c.Note }));

    [Fact]
    public async Task A_step_that_never_ends_on_a_real_posting_is_killed_quarantined_and_the_posting_flagged()
    {
        using var h = new Harness(stepTimeout: TimeSpan.FromSeconds(1));
        var design = new StepDesign
        {
            Kind = StepContract.Check, Name = "Spin test", Description = "A test step that loops forever on one setting.",
            Settings = [new StepSetting("Mode", "spin-forever loops", "")], Behavior = "test",
        };
        var id = StepSource.NewId("spin test", "deadbeef");
        var outcome = await h.App.Certifier.CertifyAsync(id, design, new StepCode { Code = SpinCode, Cases = SpinCases() }, Path.Combine(h.Dir, "spin"), default);
        outcome.Admitted.Should().BeTrue($"{outcome.Check}: {outcome.Reason}");
        h.App.Library.Publish(new LibraryEntry { Id = id, Kind = "check", Name = design.Name, Description = design.Description, Settings = design.Settings, ContentHash = outcome.ContentHash, Origin = "test" }, outcome.Folder);

        var (jordan, _) = h.Jordan();
        h.App.Workflows.Save(jordan, jordan, new WorkflowDocument { Steps = [new UserStep { Key = "spin", StepId = id, Settings = ["spin-forever", "", ""] }] });
        var startsBefore = h.App.StepHosts.Starts;
        await h.App.Engine.RunAsync(jordan, jordan, "d1");

        var entry = h.App.Library.Get(id)!;
        entry.Status.Should().Be("quarantined", "a step that overruns is switched off for everyone");
        entry.StatusDetail.Should().StartWith("timeout");
        h.App.StepHosts.Starts.Should().BeGreaterThan(startsBefore, "the stuck host was killed and a fresh one started");
        var assessments = h.App.Db.Read(c => c.Query<string>("SELECT assessment_json FROM assessments WHERE user_id=@jordan", new { jordan }).AsList())
            .Select(j => JobSearch.Store.Json.Read<Screening.Assessment>(j)!).ToList();
        assessments.Should().NotBeEmpty();
        assessments.Should().OnlyContain(a => a.Steps.Single().Verdict == "flag", "a step that cannot run flags the posting; it never passes it silently");

        // With another setting the same step runs normally once an operator reinstates it.
        h.App.Library.Reinstate(id);
        var outputs = await h.App.StepHosts.RunAsync(id, h.App.Library.Folder(id),
            StepContract.Inputs.ToDictionary(i => i, i => i == "title" ? "Data Engineer" : ""), default);
        outputs["verdict"].Should().Be("pass");
    }

    [Fact]
    public void The_workflow_compiler_wires_each_user_step_to_the_posting_fields_its_settings_and_the_decide_node()
    {
        var steps = new List<CompiledStep>
        {
            new("a1", "step.one.11111111", StepContract.Check, "One", ["x", "", ""]),
            new("b2", "step.two.22222222", StepContract.Column, "Two", ["", "", ""]),
        };
        var def = Runs.PostingWorkflow.Build("ref", "Employer", new Bricks.CandidateContext { Today = "2026-01-01" }, steps);
        def.Nodes.Should().Contain(n => n.Id == "fields");
        foreach (var st in steps)
        {
            var node = Runs.PostingWorkflow.StepNode(st);
            foreach (var input in StepContract.PostingInputs)
                def.Connections.Should().ContainSingle(c => c.FromNodeId == "fields" && c.ToNodeId == node && c.ToPortId == $"in:{input}");
            foreach (var setting in StepContract.SettingInputs)
                def.Connections.Should().ContainSingle(c => c.FromNodeId == $"{node}-{setting}" && c.ToNodeId == node && c.ToPortId == $"in:{setting}");
            foreach (var o in StepContract.Outputs(st.Kind))
                def.Connections.Should().ContainSingle(c => c.FromNodeId == node && c.ToNodeId == "decide" && c.ToPortId == $"in:{o}.{st.Key}");
        }
        def.Nodes.OfType<Ashlar.Core.Domain.Workflows.InputNode>().Single(n => n.Id == "user-a1-setting1").Content.Should().Be("x");
        // No user steps: no fields node, and the pipeline is the built-in one.
        Runs.PostingWorkflow.Build("ref", "Employer", new Bricks.CandidateContext { Today = "2026-01-01" }).Nodes.Should().NotContain(n => n.Id == "fields");
    }

    [Fact]
    public void A_step_that_returns_nothing_usable_is_flagged_never_passed()
    {
        var input = new Ashlar.Core.Domain.Execution.BrickInput(new Dictionary<string, object>
        {
            ["steps"] = JsonSerializer.Serialize(new[] { new { key = "a", name = "A", kind = "check" }, new { key = "b", name = "B", kind = "check" }, new { key = "c", name = "C", kind = "column" } }),
            ["verdict.a"] = "EXCLUDE", ["note.a"] = "why",
            ["verdict.b"] = "maybe",
        });
        var results = Bricks.DecideBrick.StepResults(input);
        results[0].Should().Be(new StepResult("a", "A", "check", "exclude", "why"));
        results[1].Verdict.Should().Be("flag");
        results[1].Text.Should().Contain("unknown verdict");
        results[2].Should().Be(new StepResult("c", "C", "column", "value", ""));
    }

    [Fact]
    public void The_step_lint_refuses_static_helpers_nested_types_and_a_missing_method_but_ignores_words_in_strings()
    {
        var ok = StepSource.MethodSignature(StepContract.Column) + " { return \"static class namespace\"; }";
        StepSource.Lint(StepContract.Column, ok).Should().BeEmpty();
        StepSource.Lint(StepContract.Column, ok + "\nprivate static int Helper() { return 1; }").Should().Contain(p => p.Contains("static"));
        StepSource.Lint(StepContract.Column, ok + "\nclass Nested { }").Should().Contain(p => p.Contains("nested"));
        StepSource.Lint(StepContract.Check, ok).Should().Contain(p => p.Contains("exactly once"));
        StepSource.Lint(StepContract.Column, ok.Replace("private", "public", StringComparison.Ordinal)).Should().NotBeEmpty();
    }

    [Fact]
    public void Secrets_are_encrypted_at_rest_and_bound_to_their_reference()
    {
        using var h = new Harness();
        var (jordan, token) = h.Jordan();
        var file = File.ReadAllText(Path.Combine(h.Dir, "secrets.json"));
        file.Should().NotContain(token, "a backup of the data directory must hold no usable token");
        file.Should().Contain("enc:v1:");
        h.App.Secrets.Get($"google:{jordan}")!["access_token"].Should().Be(token);
        // A sealed value moved to another reference does not open.
        var all = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(file)!;
        all["google:someone-else"] = all[$"google:{jordan}"];
        File.WriteAllText(Path.Combine(h.Dir, "secrets.json"), JsonSerializer.Serialize(all));
        var moved = () => h.App.Secrets.Get("google:someone-else");
        moved.Should().Throw<System.Security.Cryptography.CryptographicException>();
    }
}
