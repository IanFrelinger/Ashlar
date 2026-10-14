using System.Text;
using Ashlar.Abstractions;
using Ashlar.Abstractions.Security.Egress;
using Ashlar.Core.Application.Environments;
using Ashlar.Core.Application.Environments.Ports;

namespace Ashlar.Infrastructure.Environments;

/// <summary>
/// Uses <see cref="IModel"/> to normalize messy OSM/GeoJSON text when payload size is within limits.
/// Falls back to pass-through on failure or oversized input; an egress refusal is rethrown instead (SPEC-007 4.8b).
/// </summary>
public sealed class ModelBackedVectorMapIntelligenceService : IVectorMapIntelligenceService
{
    private readonly IModel _model;
    private readonly int _maxInputChars;

    /// <summary>Initializes a new model backed vector map intelligence service.</summary>
    public ModelBackedVectorMapIntelligenceService(IModel model, int maxInputChars = 120_000)
    {
        _model = model;
        _maxInputChars = maxInputChars;
    }

    /// <summary>Refine asynchronously.</summary>
    public async Task<VectorMapIntelligenceResult> RefineAsync(
        VectorMapIntelligenceRequest request,
        CancellationToken cancellationToken = default)
    {
        var raw = request.RawPayload;
        if (raw.Length > request.MaxOutputBytes || raw.Length > _maxInputChars)
        {
            return new VectorMapIntelligenceResult(raw, request.FormatHint,
                Notes: "skipped_ai: payload exceeds size limit", WasModified: false);
        }

        var text = Encoding.UTF8.GetString(raw.Span);
        if (text.Length > _maxInputChars)
        {
            return new VectorMapIntelligenceResult(raw, request.FormatHint,
                Notes: "skipped_ai: decoded text exceeds limit", WasModified: false);
        }

        var sys = """
            You repair messy open geospatial text (OSM XML or GeoJSON). Output ONLY valid document text,
            no markdown fences or commentary. Preserve topology; fix obvious tag noise, duplicate ids,
            and malformed fragments where possible. If input is already valid, return it unchanged.
            """;
        var user = $"Format: {request.FormatHint}\n\n{text}";

        try
        {
            var output = await _model.CompleteAsync(
                new ModelInput([("system", sys), ("user", user)]),
                cancellationToken).ConfigureAwait(false);

            var refined = output.Text.Trim();
            if (string.IsNullOrEmpty(refined))
                return new VectorMapIntelligenceResult(raw, request.FormatHint, Notes: "model_empty", WasModified: false);

            var bytes = Encoding.UTF8.GetBytes(refined);
            if (bytes.Length > request.MaxOutputBytes)
            {
                return new VectorMapIntelligenceResult(raw, request.FormatHint,
                    Notes: "model_output_too_large", WasModified: false);
            }

            var modified = !refined.AsSpan().SequenceEqual(text.AsSpan());
            return new VectorMapIntelligenceResult(bytes, request.FormatHint,
                Notes: modified ? "model_refined" : "unchanged", WasModified: modified);
        }
        catch (Exception ex)
        {
            // A refusal is the final error, never a reason to pass the input through (SPEC-007 D23, 4.8b).
            EgressRefusal.ThrowIfPresent(ex);
            return new VectorMapIntelligenceResult(raw, request.FormatHint,
                Notes: "model_error:" + ex.Message, WasModified: false);
        }
    }
}
