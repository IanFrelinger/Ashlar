using System.Globalization;
using System.Text.Json;
using Ashlar.WebOperations.Jobs;
using Ashlar.Core.Application.Generation;
using Ashlar.Core.Domain.Bricks;
using Ashlar.Core.Domain.Execution;
using JobSearch.Accounts;
using JobSearch.Certified;
using JobSearch.Materials;
using JobSearch.Profile;
using JobSearch.Screening;
using JobSearch.Store;
using JobSearch.Studio;

namespace JobSearch.Bricks;

/// <summary>
/// What a per-posting workflow knows about the candidate. Carried into the workflow as the text of an InputNode,
/// because Ashlar's workflow executor passes runtime values to brick nodes only through connected ports.
/// </summary>
public sealed record CandidateContext
{
    /// <summary>The user.</summary>
    public string UserId { get; init; } = "";
    /// <summary>Profile fields with provenance.</summary>
    public Dictionary<string, ProfileField> Fields { get; init; } = new(StringComparer.Ordinal);
    /// <summary>The current résumé.</summary>
    public string ResumeText { get; init; } = "";
    /// <summary>Its hash.</summary>
    public string ResumeSha { get; init; } = "";
    /// <summary>The user's preferences and rules.</summary>
    public Preferences Preferences { get; init; } = new();
    /// <summary>The engine's decision: this run may still draft for this posting (budget left, no current draft).</summary>
    public bool AllowDraft { get; init; }
    /// <summary>"Today" in the user's time zone, yyyy-MM-dd, so the workflow is deterministic.</summary>
    public string Today { get; init; } = "";

    /// <summary>The profile.</summary>
    public CandidateProfile Profile() => new(UserId, Fields, ResumeText, ResumeSha, ResumeParser.Parse(ResumeText));

    /// <summary>Today as a date.</summary>
    public DateOnly TodayDate() => DateOnly.ParseExact(Today, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Parses the InputNode text.</summary>
    public static CandidateContext Parse(string json) => Json.Read<CandidateContext>(json) ?? throw new JsonException("empty candidate context");
}

/// <summary>The implementation every app brick declares, so Ashlar's workflow executor will run it.</summary>
internal static class CodeImplementation
{
    /// <summary>A deterministic, code-only implementation.</summary>
    public static BrickImplementations Deterministic(string description) => new()
    {
        Deterministic = new DeterministicImplementation { Id = "code", Name = "Code", Executor = "code", Description = description },
    };
}

/// <summary>
/// <c>jobsearch.screen</c>: requirements, evidence, gaps and unknowns for one posting, and the counts the certified
/// <c>jobsearch.recommendation</c> brick decides from. It does not decide the recommendation itself.
/// </summary>
public sealed class ScreenBrick : Ashlar.Core.Domain.Bricks.Brick
{
    /// <summary>The decision ports, named as the certified recommendation brick's inputs.</summary>
    public static readonly string[] DecisionPorts = [.. new DecisionInputs().ToPorts().Keys];

    /// <summary>Creates the brick.</summary>
    public ScreenBrick()
    {
        Id = "jobsearch.screen";
        Name = "Screen a job posting";
        Version = "2.0.0";
        Icon = "🔎";
        Category = BrickCategory.Analysis;
        Description = "Extracts what a posting requires and checks it against the candidate's résumé and confirmed profile. Unknowns stay unknown.";
        Implementations = CodeImplementation.Deterministic("Rule-based extraction and comparison; no model.");
        Interface = new BrickInterface
        {
            Inputs =
            [
                new BrickInputDefinition("signal", "string", "jobs.get-posting signal", required: false, defaultValue: "error"),
                new BrickInputDefinition("posting", "object", "JobPosting, when found", required: false),
                new BrickInputDefinition("candidate", "string", "CandidateContext JSON", required: true),
            ],
            Outputs =
            [
                new BrickOutputDefinition("screened", "bool", "Whether a posting was screened"),
                new BrickOutputDefinition("assessment", "object", "Assessment, without a recommendation"),
                .. DecisionPorts.Select(p => new BrickOutputDefinition(p, "int|bool", "jobsearch.recommendation input")),
            ],
        };
    }

    /// <inheritdoc />
    public override Task<BrickOutput> ExecuteAsync(BrickInput input, ImplementationType implementation, IExecutionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var output = new BrickOutput();
        var candidate = CandidateContext.Parse(input.Get<string>("candidate"));
        var signal = input.Get("signal", "error") ?? "error";
        if (signal != "found" || !input.TryGet<JobPosting>("posting", out var posting) || posting is null)
        {
            // The certified node downstream still runs (a workflow has no skip); it gets neutral counts and its answer is
            // discarded by jobsearch.decide because nothing was screened.
            output.Set("screened", false);
            foreach (var (k, v) in new DecisionInputs().ToPorts())
                output.Set(k, v);
            output.Summary = $"not screened: the posting was not read ({signal})";
            return Task.FromResult(output);
        }
        var assessment = Screener.Assess(posting, candidate.Profile(), candidate.Preferences, candidate.TodayDate());
        output.Set("screened", true);
        output.Set("assessment", assessment);
        foreach (var (k, v) in assessment.Decision.ToPorts())
            output.Set(k, v);
        output.Summary = $"coverage {assessment.Coverage?.ToString("P0", CultureInfo.InvariantCulture) ?? "n/a"}, {assessment.Gaps.Count} gaps, {assessment.Unknowns.Count} unknowns, {assessment.Blockers.Count} blockers";
        return Task.FromResult(output);
    }
}

/// <summary>
/// <c>jobsearch.decide</c>: applies the certified recommendation to the assessment and decides whether to draft.
/// </summary>
public sealed class DecideBrick : Ashlar.Core.Domain.Bricks.Brick
{
    private readonly ICertifiedRules _rules;

    /// <summary>Creates the brick.</summary>
    public DecideBrick(ICertifiedRules rules)
    {
        _rules = rules;
        Id = "jobsearch.decide";
        Name = "Apply the certified recommendation";
        Version = "1.0.0";
        Icon = "⚖️";
        Category = BrickCategory.Analysis;
        Description = "Records the certified jobsearch.recommendation verdict (with its certificate hash) on the assessment, and whether to draft.";
        Implementations = CodeImplementation.Deterministic("Copies the certified verdict; no rule of its own.");
        Interface = new BrickInterface
        {
            Inputs =
            [
                new BrickInputDefinition("screened", "bool", "jobsearch.screen screened", required: false, defaultValue: false),
                new BrickInputDefinition("assessment", "object", "Assessment", required: false),
                new BrickInputDefinition("recommendation", "string", "jobsearch.recommendation recommendation", required: false, defaultValue: ""),
                new BrickInputDefinition("reason", "string", "jobsearch.recommendation reason", required: false, defaultValue: ""),
                new BrickInputDefinition("posting", "object", "JobPosting", required: false),
                new BrickInputDefinition("candidate", "string", "CandidateContext JSON", required: true),
                new BrickInputDefinition("steps", "string", "The user's steps as JSON [{key,name,kind}]; their results arrive as verdict.KEY, note.KEY, value.KEY", required: false, defaultValue: "[]"),
            ],
            Outputs =
            [
                new BrickOutputDefinition("recommendation", "string", "The certified recommendation; empty when not screened"),
                new BrickOutputDefinition("assessment", "object", "Assessment with the recommendation"),
                new BrickOutputDefinition("draft", "bool", "Whether materials should be drafted"),
            ],
        };
    }

    /// <inheritdoc />
    public override Task<BrickOutput> ExecuteAsync(BrickInput input, ImplementationType implementation, IExecutionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var output = new BrickOutput();
        if (!input.Get("screened", false) || !input.TryGet<Assessment>("assessment", out var screened) || screened is null ||
            !input.TryGet<JobPosting>("posting", out var posting) || posting is null)
        {
            output.Set("recommendation", "");
            output.Set("draft", false);
            output.Summary = "not screened";
            return Task.FromResult(output);
        }
        var recommendation = input.Get("recommendation", "") ?? "";
        if (recommendation.Length == 0)
            throw new InvalidOperationException("jobsearch.recommendation returned no recommendation for a screened posting");
        var by = $"{CertifiedIds.Recommendation} (certified, content hash {_rules.ContentHash(CertifiedIds.Recommendation)})";
        var results = StepResults(input);
        var excludedBy = results.FirstOrDefault(r => r.Kind == StepContract.Check && r.Verdict == "exclude")?.Name;
        var assessment = Screener.WithDecision(screened, recommendation, input.Get("reason", "") ?? "", by) with { Steps = results, ExcludedBy = excludedBy };
        var candidate = CandidateContext.Parse(input.Get<string>("candidate"));
        // A user's own step can rule a posting out; it never rules one in past the certified recommendation.
        var draft = candidate.AllowDraft && posting.ProvesOpen && candidate.Preferences.Materials.DraftFor.Contains(recommendation) && excludedBy is null;
        output.Set("recommendation", recommendation);
        output.Set("assessment", assessment);
        output.Set("draft", draft);
        output.Summary = excludedBy is null
            ? $"{recommendation}: {assessment.RecommendationReason}"
            : $"{recommendation}, excluded by your step \"{excludedBy}\"";
        return Task.FromResult(output);
    }

    /// <summary>The user's steps' results, in workflow order. A step that returned nothing usable is flagged, never passed.</summary>
    internal static List<StepResult> StepResults(BrickInput input)
    {
        var meta = Json.Read<List<StepMeta>>(input.Get("steps", "[]") ?? "[]") ?? [];
        var results = new List<StepResult>(meta.Count);
        foreach (var m in meta)
        {
            if (m.Kind == StepContract.Column)
            {
                var value = input.TryGet<string>($"value.{m.Key}", out var v) && v is not null ? v : "";
                results.Add(new StepResult(m.Key, m.Name, m.Kind, "value", Bound(value)));
                continue;
            }
            var verdict = input.TryGet<string>($"verdict.{m.Key}", out var raw) && raw is not null ? raw.Trim().ToLowerInvariant() : "";
            var note = input.TryGet<string>($"note.{m.Key}", out var n) && n is not null ? n : "";
            if (!StepContract.Verdicts.Contains(verdict))
                (verdict, note) = ("flag", verdict.Length == 0 ? "the step returned no verdict" : $"the step returned an unknown verdict \"{Bound(verdict, 40)}\"");
            results.Add(new StepResult(m.Key, m.Name, m.Kind, verdict, Bound(note)));
        }
        return results;
    }

    private static string Bound(string text, int max = 300) => text.Length <= max ? text : text[..max] + "…";

    /// <summary>One user step, as the workflow tells the decide node about it.</summary>
    public sealed record StepMeta(string Key, string Name, string Kind);
}

/// <summary>
/// <c>jobsearch.posting-fields</c>: a posting's fields as plain text, the inputs every user step receives
/// (<see cref="StepContract.PostingInputs"/>). Empty strings when nothing was read.
/// </summary>
public sealed class PostingFieldsBrick : Ashlar.Core.Domain.Bricks.Brick
{
    /// <summary>Longest description handed to a step.</summary>
    public const int MaxDescription = 20000;

    /// <summary>Creates the brick.</summary>
    public PostingFieldsBrick()
    {
        Id = "jobsearch.posting-fields";
        Name = "Posting fields for your steps";
        Version = "1.0.0";
        Icon = "🧾";
        Category = BrickCategory.Analysis;
        Description = "Gives a posting's title, employer, location, arrangement, text, pay and skills to the user's own steps as plain text.";
        Implementations = CodeImplementation.Deterministic("Copies posting and screening fields; no model.");
        Interface = new BrickInterface
        {
            Inputs =
            [
                new BrickInputDefinition("posting", "object", "JobPosting, when read", required: false),
                new BrickInputDefinition("assessment", "object", "Assessment, when screened", required: false),
            ],
            Outputs = [.. StepContract.PostingInputs.Select(f => new BrickOutputDefinition(f, "string", StepContract.InputHelp[f]))],
        };
    }

    /// <inheritdoc />
    public override Task<BrickOutput> ExecuteAsync(BrickInput input, ImplementationType implementation, IExecutionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var output = new BrickOutput();
        var posting = input.TryGet<JobPosting>("posting", out var p) ? p : null;
        var assessment = input.TryGet<Assessment>("assessment", out var a) ? a : null;
        foreach (var (k, v) in Fields(posting, assessment))
            output.Set(k, v);
        output.Summary = posting is null ? "no posting" : $"fields of {posting.Employer} — {posting.Title}";
        return Task.FromResult(output);
    }

    /// <summary>The fields, keyed by input name. Exposed for the designer's preview and for tests.</summary>
    public static Dictionary<string, string> Fields(JobPosting? p, Assessment? a)
    {
        string Pay(decimal? d) => d is { } x ? decimal.Round(x).ToString("0", CultureInfo.InvariantCulture) : "";
        var required = a?.Requirements.SkillsRequired ?? [];
        var matched = a?.MatchedSkills ?? [];
        var description = p?.Description ?? "";
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["title"] = p?.Title ?? "",
            ["employer"] = p?.Employer ?? "",
            ["location"] = p?.Location ?? "",
            ["arrangement"] = p?.WorkArrangement ?? "",
            ["description"] = description.Length <= MaxDescription ? description : description[..MaxDescription],
            ["payMin"] = Pay(a?.Requirements.PayMin),
            ["payMax"] = Pay(a?.Requirements.PayMax),
            ["skillsRequired"] = string.Join("\n", required),
            ["skillsMissing"] = string.Join("\n", required.Where(r => !matched.Contains(r, StringComparer.Ordinal))),
        };
    }
}

/// <summary>
/// <c>jobsearch.tailor</c>: a job-specific résumé and cover letter from verified facts only. When the user has opted in,
/// the cover letter's body is worded by a model through Ashlar's generative pipeline and is used only if the certified
/// claims check passed it; otherwise the template letter is used and the reason is recorded.
/// </summary>
public sealed class TailorBrick : Ashlar.Core.Domain.Bricks.Brick
{
    private readonly GenerativeArtifactBrick _generative;

    /// <summary>Creates the brick.</summary>
    public TailorBrick(GenerativeArtifactBrick generative)
    {
        _generative = generative;
        Id = "jobsearch.tailor";
        Name = "Draft tailored materials";
        Version = "2.0.0";
        Icon = "✍️";
        Category = BrickCategory.Generation;
        Description = "Drafts a tailored résumé (the résumé's own bullets, reordered) and a cover letter with placeholders only the user can fill.";
        Implementations = CodeImplementation.Deterministic("Assembles verified résumé facts; the optional model letter goes through the generative pipeline and the certified claims check.");
        Interface = new BrickInterface
        {
            Inputs =
            [
                new BrickInputDefinition("proceed", "bool", "jobsearch.decide draft", required: false, defaultValue: false),
                new BrickInputDefinition("posting", "object", "JobPosting", required: false),
                new BrickInputDefinition("assessment", "object", "Assessment", required: false),
                new BrickInputDefinition("candidate", "string", "CandidateContext JSON", required: true),
            ],
            Outputs =
            [
                new BrickOutputDefinition("drafted", "bool", "Whether drafts were produced"),
                new BrickOutputDefinition("resume", "object", "Draft"),
                new BrickOutputDefinition("coverLetter", "object", "Draft"),
                new BrickOutputDefinition("coverLetterOrigin", "string", "template or model, and why"),
            ],
        };
    }

    /// <inheritdoc />
    public override async Task<BrickOutput> ExecuteAsync(BrickInput input, ImplementationType implementation, IExecutionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var output = new BrickOutput();
        // Ashlar's ConditionalNode does not gate downstream nodes, so the gate is this input.
        if (!input.Get("proceed", false) || !input.TryGet<JobPosting>("posting", out var posting) || posting is null ||
            !input.TryGet<Assessment>("assessment", out var assessment) || assessment is null)
        {
            output.Set("drafted", false);
            output.Summary = "skipped: not selected for drafting";
            return output;
        }
        var candidate = CandidateContext.Parse(input.Get<string>("candidate"));
        var profile = candidate.Profile();
        var today = candidate.TodayDate();
        var cover = Tailor.CoverLetter(profile, posting, assessment, today);
        var origin = "template";
        if (candidate.Preferences.Materials.ModelCoverLetter)
            (cover, origin) = await ModelLetterAsync(profile, posting, assessment, today, cover, context, cancellationToken).ConfigureAwait(false);
        output.Set("drafted", true);
        output.Set("resume", Tailor.Resume(profile, assessment));
        output.Set("coverLetter", cover);
        output.Set("coverLetterOrigin", origin);
        output.Summary = $"drafted résumé and cover letter ({origin}) for {posting.Employer} — {posting.Title}";
        return output;
    }

    private async Task<(Draft Letter, string Origin)> ModelLetterAsync(
        Profile.CandidateProfile profile, JobPosting posting, Assessment assessment, DateOnly today, Draft template, IExecutionContext context, CancellationToken ct)
    {
        var templateBody = string.Join("\n", Tailor.CoverLetterBody(profile, posting, assessment).Select(l => l.Style == "li" ? "- " + l.Text : l.Text));
        var grounding = $"Job: {posting.Title} at {posting.Employer}\n" +
                        $"Required skills the résumé shows: {(assessment.MatchedSkills.Count == 0 ? "none" : string.Join(", ", assessment.MatchedSkills))}\n\n" +
                        $"Résumé:\n{profile.ResumeText}";
        var request = new BrickInput(new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["target"] = CoverLetterProfile.TargetId,
            ["grounding"] = grounding,
            ["preferDeterministic"] = false,
            ["overrides"] = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                [CoverLetterProfile.ClaimsKey] = CertifiedClaims.Context(profile, posting),
                [CoverLetterProfile.TemplateKey] = templateBody,
            },
        });
        BrickOutput generated;
        try
        {
            generated = await _generative.ExecuteAsync(request, ImplementationType.Agentic, context, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (template, $"template: the model could not be used ({ex.GetType().Name}: {ex.Message})");
        }
        var artifact = generated.Get<Ashlar.Core.Domain.Bricks.Ports.GeneratedArtifact>("artifact");
        var provenance = artifact.Provenance;
        if (!generated.Get<bool>("verified") || provenance is null || !provenance.Verified)
        {
            var why = provenance?.Warnings is { Count: > 0 } w ? string.Join(" | ", w) : "not verified";
            return (template, $"template: the model's drafts were refused by {CertifiedIds.ClaimsCheck} ({why})");
        }
        var letter = Tailor.CoverLetterWith(profile, posting, today, CoverLetterProfile.ParseBody(artifact.Content), origin: "model");
        return (letter, $"model: worded by a model, passed {CertifiedIds.ClaimsCheck}; needs your review");
    }
}

/// <summary>
/// <c>jobsearch.validate-materials</c>: checks every claim in the drafts against the résumé and confirmed profile,
/// renders the PDFs and proves their text is extractable.
/// </summary>
public sealed class ValidateMaterialsBrick : Ashlar.Core.Domain.Bricks.Brick
{
    private readonly ICertifiedRules _rules;

    /// <summary>Creates the brick.</summary>
    public ValidateMaterialsBrick(ICertifiedRules rules)
    {
        _rules = rules;
        Id = "jobsearch.validate-materials";
        Name = "Validate tailored materials";
        Icon = "✅";
        Category = BrickCategory.Validation;
        Version = "2.0.0";
        Description = "Traces each claim in a draft to the résumé or the user's confirmed profile, runs the certified jobsearch.claims-check, lists what needs the user, renders PDFs and checks their text extracts.";
        Implementations = CodeImplementation.Deterministic("Rule-based claim tracing, the certified claims check, QuestPDF rendering, PdfPig read-back; no model.");
        Interface = new BrickInterface
        {
            Inputs =
            [
                new BrickInputDefinition("drafted", "bool", "jobsearch.tailor drafted", required: false, defaultValue: false),
                new BrickInputDefinition("resume", "object", "Draft", required: false),
                new BrickInputDefinition("coverLetter", "object", "Draft", required: false),
                new BrickInputDefinition("posting", "object", "JobPosting", required: false),
                new BrickInputDefinition("assessment", "object", "Assessment", required: false),
                new BrickInputDefinition("candidate", "string", "CandidateContext JSON", required: true),
            ],
            Outputs =
            [
                new BrickOutputDefinition("validated", "bool", "Whether drafts were validated"),
                new BrickOutputDefinition("passed", "bool", "No unsupported claim and both PDFs extract"),
                new BrickOutputDefinition("resumeValidation", "object", "ValidationRecord"),
                new BrickOutputDefinition("coverValidation", "object", "ValidationRecord"),
                new BrickOutputDefinition("resumePdf", "object", "byte[]"),
                new BrickOutputDefinition("coverPdf", "object", "byte[]"),
            ],
        };
    }

    /// <inheritdoc />
    public override async Task<BrickOutput> ExecuteAsync(BrickInput input, ImplementationType implementation, IExecutionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var output = new BrickOutput();
        if (!input.Get("drafted", false) || !input.TryGet<Draft>("resume", out var resume) || resume is null ||
            !input.TryGet<Draft>("coverLetter", out var cover) || cover is null ||
            !input.TryGet<JobPosting>("posting", out var posting) || posting is null ||
            !input.TryGet<Assessment>("assessment", out var assessment) || assessment is null)
        {
            output.Set("validated", false);
            output.Set("passed", false);
            output.Summary = "skipped: nothing drafted";
            return output;
        }
        var profile = CandidateContext.Parse(input.Get<string>("candidate")).Profile();
        var title = $"{posting.Employer} — {posting.Title}";
        var resumePdf = Pdf.Render(resume, $"Résumé — {title}");
        var coverPdf = Pdf.Render(cover, $"Cover letter — {title}");
        var rv = Validator.WithCertified(Validator.Validate(resume, profile, posting, assessment, Pdf.CheckExtractable(resumePdf, resume.Text)),
            await CertifiedClaims.CheckAsync(_rules, resume, profile, posting, cancellationToken).ConfigureAwait(false));
        var cv = Validator.WithCertified(Validator.Validate(cover, profile, posting, assessment, Pdf.CheckExtractable(coverPdf, cover.Text)),
            await CertifiedClaims.CheckAsync(_rules, cover, profile, posting, cancellationToken).ConfigureAwait(false));
        var passed = rv.Passed && cv.Passed && rv.Pdf!.Passed && cv.Pdf!.Passed;
        output.Set("validated", true);
        output.Set("passed", passed);
        output.Set("resumeValidation", rv);
        output.Set("coverValidation", cv);
        output.Set("resumePdf", resumePdf);
        output.Set("coverPdf", coverPdf);
        output.Summary = $"{(passed ? "passed" : "needs attention")}: {rv.Unsupported.Count + cv.Unsupported.Count} unsupported claims, " +
                         $"{rv.NeedsConfirmation.Count + cv.NeedsConfirmation.Count} items for the user, PDF text {rv.Pdf!.Ratio:P0}/{cv.Pdf!.Ratio:P0}";
        return output;
    }
}
