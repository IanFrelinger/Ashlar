using System.Runtime.Loader;
using System.Text.Json;
using Ashlar.Abstractions;
using Ashlar.Core.Domain.Bricks;
using Ashlar.Core.Domain.Execution;
using Dapper;
using FluentAssertions;
using JobSearch.Certified;
using JobSearch.Host;
using JobSearch.Materials;
using JobSearch.Screening;
using Xunit;

namespace JobSearch.Tests;

/// <summary>The certified bricks: loaded only after verification, and the ones that decide.</summary>
public sealed class CertifiedTests
{
    /// <summary>Runs the certified recommendation brick on a screened assessment, as the workflow's node does.</summary>
    public static async Task<string> DecideAsync(Harness h, Assessment a)
    {
        var output = await h.App.Certified.Get(CertifiedIds.Recommendation)
            .ExecuteAsync(new BrickInput(a.Decision.ToPorts()), ImplementationType.Deterministic, AppExecutionContext.Instance);
        return output.Get<string>("recommendation");
    }

    private static string CopyDeployed()
    {
        var dir = Path.Combine(Path.GetTempPath(), "jobsearch-certified-" + Guid.NewGuid().ToString("N"));
        foreach (var file in Directory.GetFiles(CertifiedBricks.DefaultDirectory, "*", SearchOption.AllDirectories))
        {
            var to = Path.Combine(dir, Path.GetRelativePath(CertifiedBricks.DefaultDirectory, file));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(file, to);
        }
        return dir;
    }

    private static string Folder(string dir, string brickId) =>
        Directory.GetDirectories(dir).Single(d => File.ReadAllText(Path.Combine(d, CertifiedBricks.RecordFile)).Contains($"\"{brickId}\"", StringComparison.Ordinal));

    [Fact]
    public void The_deployed_certified_bricks_verify_and_run_from_the_gate_emitted_assembly()
    {
        using var certified = CertifiedBricks.Load(CertifiedBricks.DefaultDirectory);
        foreach (var id in CertifiedIds.All)
        {
            var brick = certified.Get(id);
            brick.Id.Should().Be(id);
            // The instance comes from the verified bytes in a collectible context, not from anything this app compiled.
            AssemblyLoadContext.GetLoadContext(brick.GetType().Assembly)!.IsCollectible.Should().BeTrue();
            brick.GetType().Assembly.Should().NotBeSameAs(typeof(CertifiedBricks).Assembly);
            using var record = JsonDocument.Parse(File.ReadAllText(Path.Combine(Folder(CertifiedBricks.DefaultDirectory, id), CertifiedBricks.RecordFile)));
            certified.ContentHash(id).Should().Be(record.RootElement.GetProperty("contentHash").GetString());
            record.RootElement.GetProperty("admitted").GetBoolean().Should().BeTrue();
            record.RootElement.GetProperty("survivingMutants").GetInt32().Should().Be(0);
        }
    }

    [Theory]
    [InlineData("source")]
    [InlineData("assembly")]
    [InlineData("signer")]
    [InlineData("no-key")]
    public void A_tampered_or_unpinned_certified_brick_stops_the_app_from_starting(string tamper)
    {
        var dir = CopyDeployed();
        try
        {
            var folder = Folder(dir, CertifiedIds.Recommendation);
            switch (tamper)
            {
                case "source":
                    var src = Directory.GetFiles(folder, "*.cs").Single();
                    File.WriteAllText(src, File.ReadAllText(src).Replace("coverage >= strong", "coverage >= strong - 10", StringComparison.Ordinal));
                    break;
                case "assembly":
                    var dll = Path.Combine(folder, "gate-emitted-brick.dll");
                    var bytes = File.ReadAllBytes(dll);
                    bytes[^16] ^= 0x5A;
                    File.WriteAllBytes(dll, bytes);
                    break;
                case "signer":
                    // A valid Ed25519 public key that did not sign these records.
                    File.WriteAllText(Path.Combine(dir, CertifiedBricks.TrustedKeyFile), Convert.ToBase64String(new byte[32].Select((_, i) => (byte)(i + 1)).ToArray()));
                    break;
                case "no-key":
                    File.WriteAllText(Path.Combine(dir, CertifiedBricks.TrustedKeyFile), "# no key\n");
                    break;
            }
            var start = () => new Harness(certifiedDir: dir);
            start.Should().Throw<Exception>().Which.Message.Should().MatchRegex("refused|trusted|key|Ed25519");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task The_workflow_recommendation_is_the_certified_bricks_verdict()
    {
        using var h = new Harness();
        var (jordan, _) = h.Jordan();
        var run = await h.App.Engine.RunAsync(jordan, jordan, "d1");
        run.WorkflowLog.Should().Contain(l => l.Contains("jobsearch.recommendation: recommendation="));
        var hash = h.App.Certified.ContentHash(CertifiedIds.Recommendation);
        var assessments = h.App.Db.Read(c => c.Query<string>("SELECT assessment_json FROM assessments WHERE user_id=@jordan", new { jordan }).AsList())
            .Select(j => JobSearch.Store.Json.Read<Assessment>(j)!).ToList();
        assessments.Should().NotBeEmpty();
        foreach (var a in assessments)
        {
            a.DecidedBy.Should().Contain(hash);
            a.RecommendationReason.Should().NotBeEmpty();
            a.Recommendation.Should().Be(await DecideAsync(h, a));
        }
    }

    [Fact]
    public async Task A_strong_match_with_an_open_question_is_conditional_and_says_why()
    {
        using var h = new Harness();
        var (jordan, _) = h.Jordan();
        var posting = new Ashlar.WebOperations.Jobs.JobPosting("Acme Analytics", "Senior Data Engineer", "Denver, CO", null, null, "hybrid", null, null, null, null,
            "Requirements\n- Python, SQL and Airflow\n- Up to 10% travel", null, new("greenhouse", "getJob", new Dictionary<string, string>()), true);
        var a = Screener.Assess(posting, h.App.Profiles.Load(jordan)!, h.App.Accounts.GetPreferences(jordan), new DateOnly(2026, 11, 8));
        a.Coverage.Should().Be(1.0);
        a.Unknowns.Should().ContainSingle(u => u.Requirement == "10% travel");
        a.Decision.Unknowns.Should().Be(1);
        var output = await h.App.Certified.Get(CertifiedIds.Recommendation)
            .ExecuteAsync(new BrickInput(a.Decision.ToPorts()), ImplementationType.Deterministic, AppExecutionContext.Instance);
        output.Get<string>("recommendation").Should().Be("conditional_fit");
        output.Get<string>("reason").Should().Be("a strong match with questions for the user");
    }

    [Fact]
    public async Task A_users_edit_with_a_number_the_resume_does_not_have_is_flagged_by_the_certified_check()
    {
        using var h = new Harness();
        var (jordan, _) = h.Jordan();
        await h.App.Engine.RunAsync(jordan, jordan, "d1");
        var (jobId, drive) = h.App.Db.Read(c => c.QuerySingle<(string, string)>(
            "SELECT job_id, drive_file_id FROM documents WHERE user_id=@jordan AND kind='cover_letter' AND format='doc' LIMIT 1", new { jordan }));
        var text = h.Google.FilesOf(Harness.JordanEmail)[drive].Content;
        // Only the certified rule checks free-text numbers; the app's own validator does not.
        h.Google.UserEditDoc(Harness.JordanEmail, drive, text.Replace("Dear Hiring Team,", "Dear Hiring Team,\nI have shipped 12 production pipelines.", StringComparison.Ordinal));
        await h.App.Engine.RunAsync(jordan, jordan, "d2");
        (await h.App.Engine.RefreshPdfsAsync(jordan, jordan, jobId)).Should().Be(1);

        var v = JobSearch.Store.Json.Read<ValidationRecord>(h.App.Db.Read(c => c.QuerySingle<string>("SELECT validation_json FROM documents WHERE drive_file_id=@drive", new { drive })))!;
        v.Certified!.Passed.Should().BeFalse();
        v.Certified.Unsupported.Should().Equal("number without a source: 12");
        v.Passed.Should().BeFalse();
        v.Unsupported.Should().ContainSingle(u => u.Claim == "number without a source: 12");
    }

    [Fact]
    public async Task The_certified_claims_check_passes_template_drafts_and_refuses_an_invented_claim()
    {
        using var h = new Harness();
        var (jordan, _) = h.Jordan();
        var profile = h.App.Profiles.Load(jordan)!;
        var posting = new Ashlar.WebOperations.Jobs.JobPosting("Acme Analytics", "Senior Data Engineer", "Denver, CO", null, null, "hybrid", null, null, null, "R-778",
            "Requirements\n- 5+ years\n- Python, SQL, Airflow", null, new("greenhouse", "getJob", new Dictionary<string, string>()), true);
        var a = Screener.Assess(posting, profile, h.App.Accounts.GetPreferences(jordan), new DateOnly(2026, 11, 8));
        var resume = Tailor.Resume(profile, a);
        var cover = Tailor.CoverLetter(profile, posting, a, new DateOnly(2026, 11, 8));

        (await CertifiedClaims.CheckAsync(h.App.Certified, resume, profile, posting, default)).Passed.Should().BeTrue();
        var letter = await CertifiedClaims.CheckAsync(h.App.Certified, cover, profile, posting, default);
        letter.Passed.Should().BeTrue(string.Join("; ", letter.Unsupported));

        var invented = cover with { Lines = [.. cover.Lines.Take(9), ("p", "I ran Kubernetes for 7 years."), .. cover.Lines.Skip(9)] };
        var refused = await CertifiedClaims.CheckAsync(h.App.Certified, invented, profile, posting, default);
        refused.Passed.Should().BeFalse();
        refused.Unsupported.Should().BeEquivalentTo(["number without a source: 7", "skill not in the résumé: kubernetes"]);
        refused.ContentHash.Should().Be(h.App.Certified.ContentHash(CertifiedIds.ClaimsCheck));
    }
}

/// <summary>The committed certified sources, run back through Ashlar's certification gate.</summary>
public sealed class RecertificationTests
{
    private static string Repo()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "apps", "job-search", "certified")))
                return d.FullName;
        throw new DirectoryNotFoundException("apps/job-search/certified not found above the test output");
    }

    [Theory]
    [InlineData("recommendation")]
    [InlineData("claims-check")]
    public async Task Recertifying_the_committed_source_and_witness_is_admitted_with_the_committed_content_hash(string brick)
    {
        var dir = Path.Combine(Repo(), "apps", "job-search", "certified", brick);
        var request = await Ashlar.Infrastructure.Certification.BrickCertificationProjectLoader.LoadAsync(dir, Path.Combine(dir, $"{brick}.witness.json"));
        var decision = await new Ashlar.Infrastructure.Certification.CertificationGate(new Ashlar.Infrastructure.Certification.CertificationRecordSigner())
            .CertifyAsync(request);
        decision.Admitted.Should().BeTrue($"{decision.FailureCheck} {decision.Record.Reason}");
        decision.Record.SurvivingMutants.Should().Be(0);
        using var committed = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(dir, CertifiedBricks.RecordFile)));
        decision.Record.ContentHash.Should().Be(committed.RootElement.GetProperty("contentHash").GetString(),
            "the committed record must cover the committed source and witness; re-run the export after changing either");
    }
}

/// <summary>A model that answers from a script and records what it was asked.</summary>
public sealed class ScriptedModel(Func<ModelInput, string> answer) : IModel
{
    public List<ModelInput> Calls { get; } = [];

    public Task<ModelOutput> CompleteAsync(ModelInput input, CancellationToken ct)
    {
        lock (Calls)
            Calls.Add(input);
        return Task.FromResult(new ModelOutput(answer(input)));
    }
}

/// <summary>Model-worded cover letters through Ashlar's generative pipeline, gated by the certified claims check.</summary>
public sealed class ModelCoverLetterTests
{
    private const string Good =
        "I would like to bring my pipeline work to your team. [CONFIRM: one or two sentences, in your own words, on why this role and team interest you.]\n" +
        "Most recently I have built batch and streaming pipelines for analytics teams:\n" +
        "- Built Airflow pipelines in Python loading 2 TB/day into a Snowflake warehouse\n" +
        "- Cut nightly SQL transform runtime from 6 hours to 90 minutes by repartitioning Spark jobs\n" +
        "I would welcome the chance to discuss the role.";

    private const string Invented = "I have run Kubernetes clusters for 5 years and hold a Secret clearance.";

    private static async Task<(Harness H, List<(string Origin, ValidationRecord V, string Text)> Letters)> RunAsync(ScriptedModel model, bool optIn = true)
    {
        var h = new Harness(model: model);
        var (jordan, _) = h.Jordan();
        if (optIn)
        {
            var prefs = h.App.Accounts.GetPreferences(jordan);
            h.App.Accounts.SetPreferences(jordan, jordan, prefs with { Materials = prefs.Materials with { ModelCoverLetter = true } });
        }
        await h.App.Engine.RunAsync(jordan, jordan, "d1");
        var letters = h.App.Db.Read(c => c.Query<(string V, string Drive)>(
                "SELECT validation_json, drive_file_id FROM documents WHERE user_id=@jordan AND kind='cover_letter' AND format='doc'", new { jordan }).AsList())
            .Select(d => (JobSearch.Store.Json.Read<ValidationRecord>(d.V)!, h.Google.FilesOf(Harness.JordanEmail)[d.Drive].Content))
            .Select(x => (x.Item1.Origin, x.Item1, x.Content)).ToList();
        letters.Should().NotBeEmpty();
        return (h, letters);
    }

    [Fact]
    public async Task A_verified_model_letter_is_used_validated_and_flagged_for_review()
    {
        var model = new ScriptedModel(_ => Good);
        var (h, letters) = await RunAsync(model);
        using var _ = h;
        foreach (var (origin, v, text) in letters)
        {
            origin.Should().Be("model");
            text.Should().Contain("Most recently I have built batch and streaming pipelines");
            text.Should().StartWith("Jordan Rivera").And.Contain("Dear Hiring Team,").And.Contain("Sincerely,");
            v.Passed.Should().BeTrue(string.Join("; ", v.Unsupported.Select(u => u.Claim)));
            v.Certified!.Passed.Should().BeTrue();
            v.NeedsConfirmation.Should().Contain(n => n.StartsWith("A model worded this draft"));
        }
        model.Calls.Should().HaveCount(letters.Count, "each letter verified on the first draft");
        model.Calls[0].Messages[0].content.Should().Be(CoverLetterProfile.SystemPrompt);
        model.Calls[0].Messages[1].content.Should().Contain("Résumé:\nJordan Rivera");
    }

    [Fact]
    public async Task A_refused_draft_goes_back_to_the_model_with_the_certified_reasons()
    {
        // First drafts invent; a draft written after a refusal does not.
        var model = new ScriptedModel(i => i.Messages[1].content.Contains("previous draft was refused", StringComparison.Ordinal) ? Good : Invented);
        var (h, letters) = await RunAsync(model);
        using var _ = h;
        letters.Should().OnlyContain(l => l.Origin == "model" && l.V.Passed);
        model.Calls.Should().HaveCount(2 * letters.Count);
        var retry = model.Calls.Select(c => c.Messages[1].content).First(c => c.Contains("previous draft was refused", StringComparison.Ordinal));
        retry.Should().Contain("skill not in the résumé: kubernetes").And.Contain("number without a source: 5")
            .And.Contain("clearance mentioned but not as the user confirmed it");
    }

    [Fact]
    public async Task When_every_model_draft_is_refused_the_template_letter_is_used()
    {
        var model = new ScriptedModel(_ => Invented);
        var (h, letters) = await RunAsync(model);
        using var _ = h;
        foreach (var (origin, v, text) in letters)
        {
            origin.Should().Be("template");
            text.Should().NotContain("Kubernetes");
            v.Passed.Should().BeTrue();
        }
        model.Calls.Should().HaveCount(3 * letters.Count, "the profile allows three attempts");
    }

    [Fact]
    public async Task When_the_model_fails_the_template_letter_is_used()
    {
        var model = new ScriptedModel(_ => throw new HttpRequestException("no provider configured"));
        var (h, letters) = await RunAsync(model);
        using var _ = h;
        letters.Should().OnlyContain(l => l.Origin == "template" && l.V.Passed);
    }

    [Fact]
    public async Task Without_the_users_opt_in_no_model_is_asked()
    {
        var model = new ScriptedModel(_ => Good);
        var (h, letters) = await RunAsync(model, optIn: false);
        using var _ = h;
        model.Calls.Should().BeEmpty();
        letters.Should().OnlyContain(l => l.Origin == "template");
    }
}
