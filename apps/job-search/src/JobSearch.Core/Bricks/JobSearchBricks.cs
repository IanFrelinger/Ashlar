using System.Globalization;
using System.Text.Json;
using Ashlar.Bricks.ApiAnything.Jobs;
using Ashlar.Core.Domain.Bricks;
using Ashlar.Core.Domain.Execution;
using JobSearch.Accounts;
using JobSearch.Materials;
using JobSearch.Profile;
using JobSearch.Screening;
using JobSearch.Store;

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

/// <summary><c>jobsearch.screen</c>: requirements, evidence, gaps and a recommendation for one posting.</summary>
public sealed class ScreenBrick : Ashlar.Core.Domain.Bricks.Brick
{
    /// <summary>Creates the brick.</summary>
    public ScreenBrick()
    {
        Id = "jobsearch.screen";
        Name = "Screen a job posting";
        Version = "1.0.0";
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
                new BrickOutputDefinition("recommendation", "string", "strong_fit, conditional_fit, stretch, not_currently_eligible; empty when not screened"),
                new BrickOutputDefinition("assessment", "object", "Assessment"),
                new BrickOutputDefinition("draft", "bool", "Whether materials should be drafted"),
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
            output.Set("screened", false);
            output.Set("recommendation", "");
            output.Set("draft", false);
            output.Summary = $"not screened: the posting was not read ({signal})";
            return Task.FromResult(output);
        }
        var assessment = Screener.Assess(posting, candidate.Profile(), candidate.Preferences, candidate.TodayDate());
        var draft = candidate.AllowDraft && posting.ProvesOpen && candidate.Preferences.Materials.DraftFor.Contains(assessment.Recommendation);
        output.Set("screened", true);
        output.Set("recommendation", assessment.Recommendation);
        output.Set("assessment", assessment);
        output.Set("draft", draft);
        output.Summary = $"{assessment.Recommendation} (coverage {assessment.Coverage?.ToString("P0", CultureInfo.InvariantCulture) ?? "n/a"}, {assessment.Gaps.Count} gaps, {assessment.Unknowns.Count} unknowns)";
        return Task.FromResult(output);
    }
}

/// <summary><c>jobsearch.tailor</c>: a job-specific résumé and cover letter from verified facts only.</summary>
public sealed class TailorBrick : Ashlar.Core.Domain.Bricks.Brick
{
    /// <summary>Creates the brick.</summary>
    public TailorBrick()
    {
        Id = "jobsearch.tailor";
        Name = "Draft tailored materials";
        Version = "1.0.0";
        Icon = "✍️";
        Category = BrickCategory.Generation;
        Description = "Drafts a tailored résumé (the résumé's own bullets, reordered) and a cover letter with placeholders only the user can fill.";
        Implementations = CodeImplementation.Deterministic("Assembles verified résumé facts; no model.");
        Interface = new BrickInterface
        {
            Inputs =
            [
                new BrickInputDefinition("proceed", "bool", "jobsearch.screen draft", required: false, defaultValue: false),
                new BrickInputDefinition("posting", "object", "JobPosting", required: false),
                new BrickInputDefinition("assessment", "object", "Assessment", required: false),
                new BrickInputDefinition("candidate", "string", "CandidateContext JSON", required: true),
            ],
            Outputs =
            [
                new BrickOutputDefinition("drafted", "bool", "Whether drafts were produced"),
                new BrickOutputDefinition("resume", "object", "Draft"),
                new BrickOutputDefinition("coverLetter", "object", "Draft"),
            ],
        };
    }

    /// <inheritdoc />
    public override Task<BrickOutput> ExecuteAsync(BrickInput input, ImplementationType implementation, IExecutionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var output = new BrickOutput();
        // Ashlar's ConditionalNode does not gate downstream nodes, so the gate is this input.
        if (!input.Get("proceed", false) || !input.TryGet<JobPosting>("posting", out var posting) || posting is null ||
            !input.TryGet<Assessment>("assessment", out var assessment) || assessment is null)
        {
            output.Set("drafted", false);
            output.Summary = "skipped: not selected for drafting";
            return Task.FromResult(output);
        }
        var candidate = CandidateContext.Parse(input.Get<string>("candidate"));
        var profile = candidate.Profile();
        output.Set("drafted", true);
        output.Set("resume", Tailor.Resume(profile, assessment));
        output.Set("coverLetter", Tailor.CoverLetter(profile, posting, assessment, candidate.TodayDate()));
        output.Summary = $"drafted résumé and cover letter for {posting.Employer} — {posting.Title}";
        return Task.FromResult(output);
    }
}

/// <summary>
/// <c>jobsearch.validate-materials</c>: checks every claim in the drafts against the résumé and confirmed profile,
/// renders the PDFs and proves their text is extractable.
/// </summary>
public sealed class ValidateMaterialsBrick : Ashlar.Core.Domain.Bricks.Brick
{
    /// <summary>Creates the brick.</summary>
    public ValidateMaterialsBrick()
    {
        Id = "jobsearch.validate-materials";
        Name = "Validate tailored materials";
        Version = "1.0.0";
        Icon = "✅";
        Category = BrickCategory.Validation;
        Description = "Traces each claim in a draft to the résumé or the user's confirmed profile, lists what needs the user, renders PDFs and checks their text extracts.";
        Implementations = CodeImplementation.Deterministic("Rule-based claim tracing, QuestPDF rendering, PdfPig read-back; no model.");
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
    public override Task<BrickOutput> ExecuteAsync(BrickInput input, ImplementationType implementation, IExecutionContext context, CancellationToken cancellationToken = default)
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
            return Task.FromResult(output);
        }
        var profile = CandidateContext.Parse(input.Get<string>("candidate")).Profile();
        var title = $"{posting.Employer} — {posting.Title}";
        var resumePdf = Pdf.Render(resume, $"Résumé — {title}");
        var coverPdf = Pdf.Render(cover, $"Cover letter — {title}");
        var rv = Validator.Validate(resume, profile, posting, assessment, Pdf.CheckExtractable(resumePdf, resume.Text));
        var cv = Validator.Validate(cover, profile, posting, assessment, Pdf.CheckExtractable(coverPdf, cover.Text));
        var passed = rv.Passed && cv.Passed && rv.Pdf!.Passed && cv.Pdf!.Passed;
        output.Set("validated", true);
        output.Set("passed", passed);
        output.Set("resumeValidation", rv);
        output.Set("coverValidation", cv);
        output.Set("resumePdf", resumePdf);
        output.Set("coverPdf", coverPdf);
        output.Summary = $"{(passed ? "passed" : "needs attention")}: {rv.Unsupported.Count + cv.Unsupported.Count} unsupported claims, " +
                         $"{rv.NeedsConfirmation.Count + cv.NeedsConfirmation.Count} items for the user, PDF text {rv.Pdf!.Ratio:P0}/{cv.Pdf!.Ratio:P0}";
        return Task.FromResult(output);
    }
}
