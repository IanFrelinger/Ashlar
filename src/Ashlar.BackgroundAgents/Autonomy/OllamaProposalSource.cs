using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Ashlar.Core.Application.Autonomy;
using Ashlar.Abstractions.Security.Egress;

namespace Ashlar.BackgroundAgents.Autonomy;

/// <summary>Configuration for <see cref="OllamaProposalSource"/>. Everything model-shaped is here, not compiled in.</summary>
[Experimental(AutonomyExperimental.DiagnosticId, UrlFormat = AutonomyExperimental.UrlFormat)]
public sealed class OllamaProposalOptions
{
    /// <summary>Base URL of the ollama daemon.</summary>
    public string BaseUrl { get; set; } = "http://localhost:11434";

    /// <summary>Model tag. The proposer signature embeds it, so it is hash-bound into the certificate.</summary>
    public string Model { get; set; } = "codellama:7b";

    /// <summary>Sampling temperature. Low by default: certification wants convergence, not variety.</summary>
    public double Temperature { get; set; } = 0.2;

    /// <summary>Max tokens generated. Small models over-run; the gate rejects garbage either way.</summary>
    public int MaxTokens { get; set; } = 1600;

    /// <summary>Per-call timeout. Local 7B models can take a minute on a cold load; a 27B with CPU offload can take much longer.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// For "thinking" models (Qwen3 family and later): whether the model may think before
    /// answering. Null omits the field (the daemon's default for the model). False is the
    /// setting a certification proposer usually wants — the loop asks for one code block, and
    /// a thinking pass spends the token budget (<see cref="MaxTokens"/>) before the code starts.
    /// </summary>
    public bool? Think { get; set; }

    /// <summary>
    /// How the request is framed. <see cref="PromptStyle.SkeletonCompletion"/> hands the model
    /// a complete file with the method body missing (small completion models follow this;
    /// they ignore prose instructions like "include the constructor" and simply omit it —
    /// which is exactly how the swap host's identity check caught a ctor-less proposal in
    /// the P6 campaign). <see cref="PromptStyle.ContractOnly"/> states the contract and lets
    /// the model author the whole file; suits larger instruction-following models.
    /// </summary>
    public PromptStyle Style { get; set; } = PromptStyle.SkeletonCompletion;

    /// <summary>
    /// Optional operator preamble prepended to every prompt (house rules, forbidden APIs).
    /// Kept separate from the objective so it can vary per deployment without editing
    /// objectives.
    /// </summary>
    public string? SystemPreamble { get; set; }

    /// <summary>
    /// On REPAIR, hand the model only the objective's <c>Contract:</c> section rather than
    /// the whole body. Measured, not assumed: on codellama:7b the same repair went from 3/3
    /// with a two-line contract to 0/3 when the full 18-line objective narrative surrounded
    /// it — the signal was buried, not missing. Larger models are unlikely to care; small
    /// ones need the contract to be the loudest thing on the page. Falls back to the full
    /// body when the objective has no <c>Contract:</c> heading.
    ///
    /// <para>Applies to CERTIFICATION repairs only. A BUILD repair always gets the whole
    /// body: dogfood campaign 2 showed a compile failure's fix usually needs the objective's
    /// skeleton and API notes (<c>output.Set(...)</c>, the exact <c>TryDecode</c> shape),
    /// which the contract bullets do not carry — stripping them left the model re-emitting
    /// the same broken source three times over.</para>
    /// </summary>
    public bool RepairWithContractOnly { get; set; } = true;
}

/// <summary>Prompt framings; see <see cref="OllamaProposalOptions.Style"/>.</summary>
[Experimental(AutonomyExperimental.DiagnosticId, UrlFormat = AutonomyExperimental.UrlFormat)]
public enum PromptStyle
{
    /// <summary>Complete file with the method body missing; model fills the body.</summary>
    SkeletonCompletion,

    /// <summary>Contract stated in prose; model authors the whole file.</summary>
    ContractOnly,
}

/// <summary>
/// A live proposer over a local ollama daemon — the <c>model:ollama:isolation-enforced</c>
/// production seam. Witness-blind by construction: it receives a <see cref="ProposalRequest"/>,
/// which carries the objective's declarations and, on repair, the policy-projected feedback
/// and the proposer's own previous source. It never sees a witness. Whatever it returns goes
/// to the gate; the gate decides.
///
/// <para>The proposer signature embeds provider, model, mode (initial/repair) and attempt,
/// and rides the lineage into the certificate's <c>generation-depth</c> input — so "which
/// model produced this, and was it a repair" is evidence, not prose.</para>
/// </summary>
[Experimental(AutonomyExperimental.DiagnosticId, UrlFormat = AutonomyExperimental.UrlFormat)]
public sealed class OllamaProposalSource : IProposalSource
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly HttpClient _http;
    private readonly OllamaProposalOptions _options;
    private readonly ILogger<OllamaProposalSource>? _logger;

    /// <summary>Creates the proposer over the given client and options.</summary>
    public OllamaProposalSource(HttpClient http, OllamaProposalOptions options, ILogger<OllamaProposalSource>? logger = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;
        _http.Timeout = _options.Timeout;
    }

    /// <inheritdoc />
    public async Task<ProposedSource?> ProposeAsync(ProposalRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var prompt = BuildPrompt(request);
        var body = new Dictionary<string, object?>
        {
            ["model"] = _options.Model,
            ["prompt"] = prompt,
            ["stream"] = false,
            ["options"] = new { temperature = _options.Temperature, num_predict = _options.MaxTokens },
        };
        if (_options.Think is { } think)
            body["think"] = think; // only when set: older daemons and non-thinking models reject an unknown field

        var url = $"{_options.BaseUrl.TrimEnd('/')}/api/generate";
        // SPEC-007 EG-MDL-11: the objective, the gate's feedback and the previous source go to this daemon.
        EgressGuard.ProcessDefault.Evaluate(new EgressRequest(EgressFamilies.ModelLegacy, "EG-MDL-11", url)).ThrowIfRefused();
        using var response = await _http.PostAsJsonAsync(url, body, Json, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<GenerateResponse>(Json, cancellationToken).ConfigureAwait(false);
        var text = payload?.Response ?? string.Empty;

        var source = ExtractSource(text);
        if (source is null)
        {
            _logger?.LogInformation("Objective {Id}: model returned no code block", request.ObjectiveId);
            return null; // "No proposal" — a normal outcome, never evidence.
        }

        var typeName = DeriveTypeName(source);
        if (typeName is null)
        {
            _logger?.LogInformation("Objective {Id}: model source has no class declaration", request.ObjectiveId);
            return null;
        }

        var mode = request.Repair is null ? "initial" : $"repair{request.Repair.Attempt}";
        var signature = $"model:ollama:{_options.Model.Replace(':', '-')}:{mode}:{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}";
        return new ProposedSource(source, typeName, signature);
    }

    internal string BuildPrompt(ProposalRequest request)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(_options.SystemPreamble))
            sb.AppendLine(_options.SystemPreamble).AppendLine();

        if (request.Repair is { } repair)
        {
            // Repair framing: the proposer's own previous source, and ONLY the projected
            // feedback. The witness is not here because it was never given to us.
            sb.AppendLine("Your previous implementation was REJECTED by a certification gate. Fix it.");
            sb.AppendLine();
            sb.AppendLine("The gate reported:");
            sb.AppendLine();
            foreach (var line in repair.Feedback.Split('\n'))
                sb.Append("    ").AppendLine(line.TrimEnd());
            sb.AppendLine();
            sb.AppendLine("Your previous implementation:");
            sb.AppendLine();
            sb.AppendLine("```csharp");
            sb.AppendLine(repair.PreviousSource.TrimEnd());
            sb.AppendLine("```");
            sb.AppendLine();
            // Repair scaffold kept deliberately terse: contract, then a one-sentence ask.
            // Measured on codellama:7b (see docs/certification-evidence.md, S3): single-shot
            // repair rate on this exact shape swung 0/3..3/3 with formatting noise alone, so
            // no scaffold is "the" right one for a small model; through the loop's bounded
            // retry, 3/5 objectives converged at temperature 0.2 and 0.7 alike. Tune the
            // policy's attempt cap and these options per model rather than this text.
            // Build repairs always carry the whole objective — its skeleton and API notes are
            // what a compile fix needs; contract-only is for certification repairs.
            var contractOnly = _options.RepairWithContractOnly && repair.Kind != RepairKind.Build;
            sb.AppendLine(contractOnly ? "The contract it must satisfy:" : "The objective it must satisfy, including the skeleton and API notes:");
            sb.AppendLine(contractOnly ? ExtractContract(request.Body) : request.Body.Trim());
            sb.AppendLine();
            sb.Append("Output the COMPLETE corrected file as one ```csharp code block, every line included, nothing else. ")
              .Append("Do not add, remove, or reorder any member. ")
              .AppendLine("Deterministic only: no DateTime.Now, no Random, no Guid.NewGuid, no I/O, no empty catch blocks.");
            return sb.ToString();
        }

        sb.AppendLine("Objective: ").AppendLine(request.Title.Trim());
        sb.AppendLine();
        sb.AppendLine(request.Body.Trim());
        sb.AppendLine();
        if (_options.Style == PromptStyle.SkeletonCompletion)
        {
            sb.AppendLine("Author the brick as a complete C# file: the objective above states the class name, namespace,");
            sb.AppendLine("inputs and outputs. Include the constructor that sets Id, Name, Description and Interface,");
            sb.AppendLine("and override ExecuteAsync. Output ONLY one ```csharp code block, nothing else.");
        }
        else
        {
            sb.AppendLine("Implement this as a Ashlar brick (a class deriving DomainBrick with an ExecuteAsync override).");
            sb.AppendLine("Output ONLY one ```csharp code block containing the complete file, nothing else.");
        }
        AppendConstraints(sb);
        return sb.ToString();
    }

    private static void AppendConstraints(StringBuilder sb)
    {
        sb.AppendLine();
        sb.AppendLine("Hard constraints (the gate rejects violations):");
        sb.AppendLine("- Deterministic only: no DateTime.Now, no Random, no Guid.NewGuid, no file or network I/O, no static mutable state, no empty catch blocks.");
        sb.AppendLine("- Every loop must provably terminate.");
        sb.AppendLine("- Read only inputs the interface declares; write only outputs it declares.");
    }

    /// <summary>
    /// The objective's <c>Contract:</c> section — the heading through the next blank-line-
    /// separated paragraph that is not a list item — or the whole body when there is none.
    /// </summary>
    internal static string ExtractContract(string body)
    {
        var lines = body.Replace("\r", "").Split('\n');
        var start = Array.FindIndex(lines, l => l.Trim().Equals("Contract:", StringComparison.OrdinalIgnoreCase));
        if (start < 0)
            return body.Trim();

        var sb = new StringBuilder();
        var seenItem = false;
        for (var i = start + 1; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                if (seenItem) break; // end of the list block
                continue;
            }

            if (!trimmed.StartsWith("- ", StringComparison.Ordinal) && !trimmed.StartsWith("* ", StringComparison.Ordinal))
            {
                if (seenItem) break;
                continue;
            }

            seenItem = true;
            sb.AppendLine(trimmed);
        }

        return seenItem ? sb.ToString().TrimEnd() : body.Trim();
    }

    private static string? ExtractSource(string text)
    {
        var fence = Regex.Match(text, "```(?:csharp|cs|c#)?\\s*\\n(.*?)```", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (fence.Success && fence.Groups[1].Value.Trim().Length > 0)
            return fence.Groups[1].Value.Trim() + "\n";

        var start = text.IndexOf("using ", StringComparison.Ordinal);
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)].Trim() + "\n" : null;
    }

    private static string? DeriveTypeName(string source)
    {
        var ns = Regex.Match(source, @"namespace\s+([\w.]+)");
        var cls = Regex.Match(source, @"class\s+(\w+)");
        if (!cls.Success)
            return null;
        return ns.Success ? $"{ns.Groups[1].Value}.{cls.Groups[1].Value}" : cls.Groups[1].Value;
    }

    private sealed record GenerateResponse([property: JsonPropertyName("response")] string? Response);
}
