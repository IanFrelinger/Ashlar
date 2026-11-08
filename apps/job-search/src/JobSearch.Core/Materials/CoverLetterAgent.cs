using Ashlar.Core.Application.Generation.Ports;
using Ashlar.Core.Application.Orchestration;
using Ashlar.Core.Domain.Bricks;
using Ashlar.Core.Domain.Bricks.Ports;
using JobSearch.Certified;
using JobSearch.Privacy;
using Microsoft.Extensions.DependencyInjection;

namespace JobSearch.Materials;

/// <summary>
/// The agent profile behind model-worded cover letters, run by Ashlar's generative pipeline (the first-party
/// <c>generative-artifact</c> brick): the private model on this server drafts the letter body (the résumé is personal
/// data, so it never goes to an external model), the certified <c>jobsearch.claims-check</c> brick judges every draft,
/// refused drafts go back to the model with the reasons, and model drafts always require a person's review. The app uses
/// a model draft only when the pipeline marks it verified; otherwise it keeps the template letter.
/// </summary>
public sealed class CoverLetterProfile : IAgentProfileFactory
{
    /// <summary>The profile's target id.</summary>
    public const string TargetId = "jobsearch.cover-letter";
    /// <summary>Override key carrying the <see cref="ClaimsContext"/>.</summary>
    public const string ClaimsKey = "claims";
    /// <summary>Override key carrying the template body text (the deterministic drafter's answer).</summary>
    public const string TemplateKey = "template";
    /// <summary>Artifact file carrying the claims context, so the validator judges the draft against the same facts.</summary>
    public const string ClaimsFile = "claims-context.json";
    /// <summary>Longest body accepted, in words.</summary>
    public const int MaxWords = 300;

    /// <summary>What the model is told. The certified check enforces it; the prompt only makes refusals rarer.</summary>
    public const string SystemPrompt =
        "You write the body of a cover letter for the candidate whose résumé you are given. Rules, all strict: " +
        "state only facts the résumé states; do not name any skill, tool, employer, title, number, credential, degree or " +
        "clearance the résumé does not name; a line that starts with \"- \" must copy one résumé bullet exactly; keep the line " +
        "\"[CONFIRM: one or two sentences, in your own words, on why this role and team interest you.]\" so the candidate " +
        "writes their own motivation; no salutation, no sign-off, no header; plain text, at most 300 words.";

    /// <inheritdoc />
    public AgentProfile Create(IServiceProvider services)
    {
        var rules = services.GetRequiredService<ICertifiedRules>();
        return new AgentProfile
        {
            TargetId = TargetId,
            // Resolved when a letter is drafted, not when the profile is built: the app must start without a model
            // configured, since model wording is off unless the user turns it on. Only the private model: PII stays on the host.
            Drafter = new ModelDrafter(() => services.GetRequiredService<IPrivateModel>()),
            DeterministicDrafter = new TemplateDrafter(),
            Validators = [new ClaimsValidator(rules)],
            Llm = new LLMConfig { SystemPrompt = SystemPrompt },
            Tunables = new GenerationTunables { PreferDeterministic = false, MaxRepairAttempts = 3, RequireHumanReviewForModelDrafts = true },
            Capabilities = new AgentProfileCapabilities { SupportsDeterministic = true },
        };
    }

    /// <summary>A body as draft lines: "- " lines are bullets.</summary>
    public static List<(string Style, string Text)> ParseBody(string content) =>
        content.Replace("\r\n", "\n", StringComparison.Ordinal).Trim().Split('\n')
            .Select(l => l.Trim())
            .Select(l => l.StartsWith("- ", StringComparison.Ordinal) ? ("li", l[2..].Trim()) : ("p", l))
            .ToList();

    private static ClaimsContext Claims(GenerationRequest request) =>
        request.Overrides.TryGetValue(ClaimsKey, out var c) && c is ClaimsContext claims
            ? claims
            : throw new InvalidOperationException($"the {TargetId} request carries no claims context");

    private static Dictionary<string, string> Files(GenerationRequest request) =>
        new(StringComparer.Ordinal) { [ClaimsFile] = Store.Json.Write(Claims(request)) };

    /// <summary>Asks the private model for the body, passing back why earlier drafts were refused.</summary>
    internal sealed class ModelDrafter(Func<IPrivateModel> model) : IArtifactDrafter
    {
        public async Task<GeneratedArtifact> DraftAsync(GenerationRequest request, IReadOnlyList<string> priorFeedback, CancellationToken cancellationToken = default)
        {
            var user = request.Grounding;
            if (priorFeedback.Count > 0)
                user += "\n\nYour previous draft was refused for these unsupported claims; write it again without them:\n" + string.Join("\n", priorFeedback);
            // The grounding holds the résumé: a personal frame, which the guard lets reach only this host.
            using var frame = Pii.EnterPersonalFrame("cover-letter");
            var output = await model().CompleteTextAsync(SystemPrompt, user, cancellationToken).ConfigureAwait(false);
            return new GeneratedArtifact { Content = output.Text.Trim(), Files = Files(request) };
        }
    }

    /// <summary>The template body; used by the pipeline only when it is asked to prefer the deterministic path.</summary>
    internal sealed class TemplateDrafter : IDeterministicDrafter
    {
        public bool TryDraft(GenerationRequest request, out GeneratedArtifact? artifact)
        {
            artifact = request.Overrides.TryGetValue(TemplateKey, out var t) && t is string body
                ? new GeneratedArtifact { Content = body, Files = Files(request) }
                : null;
            return artifact is not null;
        }
    }

    /// <summary>The post-validator: the certified claims check, plus the body's shape.</summary>
    internal sealed class ClaimsValidator(ICertifiedRules rules) : IPostValidator<GeneratedArtifact>
    {
        public async ValueTask<(bool ok, string? reason)> ValidateAsync(GeneratedArtifact output, CancellationToken ct)
        {
            var body = output.Content.Trim();
            if (body.Length == 0)
                return (false, "the draft is empty");
            if (body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length > MaxWords)
                return (false, $"the draft is longer than {MaxWords} words");
            var lines = ParseBody(body);
            if (lines.Any(l => l.Text.StartsWith("Dear ", StringComparison.Ordinal) || l.Text.StartsWith("Sincerely", StringComparison.Ordinal)))
                return (false, "write the body only: no salutation and no sign-off");
            if (!output.Files.TryGetValue(ClaimsFile, out var json) || Store.Json.Read<ClaimsContext>(json) is not { } claims)
                return (false, "the draft carries no claims context to check it against");
            var check = await CertifiedClaims.CheckAsync(rules, CertifiedClaims.ClaimText(new Draft("cover_letter_body", lines)), claims, ct).ConfigureAwait(false);
            return check.Passed ? (true, null) : (false, string.Join("; ", check.Unsupported));
        }
    }
}
