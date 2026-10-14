using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ashlar.Abstractions;
using Ashlar.Abstractions.Security.Egress;
using Ashlar.Core.Application.Environments;
using Ashlar.Core.Application.Environments.Ports;

namespace Ashlar.Infrastructure.Environments;

/// <summary>
/// Uses <see cref="IModel"/> to propose materials and texture-slot prompts from a style preset and tags.
/// Parses strict JSON from the model; on failure falls back to heuristic materials derived from tags. An egress
/// refusal is rethrown instead (SPEC-007 4.8b).
/// </summary>
public sealed class ModelBackedMaterialIntelligenceService : IMaterialIntelligenceService
{
    private readonly IModel _model;

    /// <summary>Initializes a new model-backed material intelligence service.</summary>
    public ModelBackedMaterialIntelligenceService(IModel model) => _model = model;

    /// <summary>Suggest materials asynchronously.</summary>
    public async Task<MaterialIntelligenceResult> SuggestMaterialsAsync(
        MaterialIntelligenceRequest request,
        CancellationToken cancellationToken = default)
    {
        var heuristic = HeuristicMaterials(request);
        try
        {
            var sys =
                """
                You output ONLY a JSON array (no markdown, no prose) of material objects for a game engine.
                Each object fields: id (string), name (string), shaderName (string, use "Universal Render Pipeline/Lit"),
                colorHex (#RRGGBB), metallic (0-1 number), smoothness (0-1), renderMode ("Opaque"|"Cutout"|"Transparent"),
                textureSlotHints (object: shader slot name -> short texture generation prompt).
                Cover building facades, roads, water, ground as relevant to tags and stylePreset.
                Max 
                """ + request.MaxMaterials.ToString(CultureInfo.InvariantCulture) +
                """
                 objects.
                """;
            var tagsJson = JsonSerializer.Serialize(request.Tags);
            var user = $"stylePreset: {request.StylePreset}\ntags: {tagsJson}";

            var output = await _model.CompleteAsync(
                new ModelInput([("system", sys), ("user", user)]),
                cancellationToken).ConfigureAwait(false);

            var trimmed = output.Text.Trim();
            var jsonStart = trimmed.IndexOf('[');
            var jsonEnd = trimmed.LastIndexOf(']');
            if (jsonStart >= 0 && jsonEnd > jsonStart)
                trimmed = trimmed.Substring(jsonStart, jsonEnd - jsonStart + 1);

            var dtoList = JsonSerializer.Deserialize<List<MaterialDto>>(trimmed, SerializerOptions);
            if (dtoList is not { Count: > 0 })
                return HeuristicResult(heuristic, "model_returned_empty_fallback_heuristic");

            var materials = new List<SuggestedMaterialSpec>();
            foreach (var d in dtoList.Take(request.MaxMaterials))
            {
                if (string.IsNullOrWhiteSpace(d.Id))
                    continue;
                var hints = d.TextureSlotHints ?? new Dictionary<string, string>();
                materials.Add(new SuggestedMaterialSpec(
                    d.Id.Trim(),
                    string.IsNullOrWhiteSpace(d.Name) ? d.Id : d.Name.Trim(),
                    string.IsNullOrWhiteSpace(d.ShaderName) ? "Universal Render Pipeline/Lit" : d.ShaderName.Trim(),
                    string.IsNullOrWhiteSpace(d.ColorHex) ? "#CCCCCC" : d.ColorHex.Trim(),
                    Clamp01(d.Metallic),
                    Clamp01(d.Smoothness),
                    string.IsNullOrWhiteSpace(d.RenderMode) ? "Opaque" : d.RenderMode.Trim(),
                    hints));
            }

            if (materials.Count == 0)
                return HeuristicResult(heuristic, "parsed_empty_fallback_heuristic");

            return new MaterialIntelligenceResult(materials, Summary: "model_json");
        }
        catch (Exception ex)
        {
            // A refusal is the final error, never a reason for the heuristic materials (SPEC-007 D23, 4.8b).
            EgressRefusal.ThrowIfPresent(ex);
            return HeuristicResult(heuristic, "model_error_fallback_heuristic:" + ex.Message);
        }
    }

    private static MaterialIntelligenceResult HeuristicResult(IReadOnlyList<SuggestedMaterialSpec> materials, string summary) =>
        new(materials, summary);

    private static IReadOnlyList<SuggestedMaterialSpec> HeuristicMaterials(MaterialIntelligenceRequest request)
    {
        var tags = request.Tags;
        var list = new List<SuggestedMaterialSpec>();
        var style = request.StylePreset.Trim();

        void Add(string id, string name, string color, double met, double smooth, string mode, params (string slot, string hint)[] hints)
        {
            list.Add(new SuggestedMaterialSpec(
                id, name, "Universal Render Pipeline/Lit", color, met, smooth, mode,
                hints.ToDictionary(static x => x.slot, static x => x.hint)));
        }

        if (tags.Keys.Any(k => string.Equals(k, "building", StringComparison.OrdinalIgnoreCase)))
            Add("mat_building_facade", "Building facade", "#B0B0B0", 0.1, 0.35, "Opaque",
                ("_MainTex", $"{style} building facade windows plaster"));

        if (tags.Keys.Any(k => string.Equals(k, "highway", StringComparison.OrdinalIgnoreCase)))
            Add("mat_road_asphalt", "Road asphalt", "#2A2A2A", 0.05, 0.15, "Opaque",
                ("_MainTex", $"{style} asphalt road wear markings"));

        if (tags.Keys.Any(k => string.Equals(k, "natural", StringComparison.OrdinalIgnoreCase) &&
                               tags[k]?.Contains("water", StringComparison.OrdinalIgnoreCase) == true)
            || tags.Keys.Any(k => string.Equals(k, "waterway", StringComparison.OrdinalIgnoreCase)))
            Add("mat_water", "Water surface", "#1E7ACC", 0.6, 0.95, "Transparent",
                ("_MainTex", $"{style} calm water ripples"));

        if (list.Count == 0)
            Add("mat_default_ground", "Ground", "#6B8E4E", 0, 0.2, "Opaque",
                ("_MainTex", $"{style} grass soil blend"));

        return list.Take(request.MaxMaterials).ToList();
    }

    private static double Clamp01(double x)
    {
        if (double.IsNaN(x)) return 0;
        return Math.Clamp(x, 0, 1);
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    private sealed class MaterialDto
    {
        /// <summary>Id.</summary>
        public string? Id { get; set; }
        /// <summary>Name.</summary>
        public string? Name { get; set; }
        /// <summary>Shader name.</summary>
        public string? ShaderName { get; set; }
        /// <summary>Color hex.</summary>
        public string? ColorHex { get; set; }
        /// <summary>Metallic.</summary>
        public double Metallic { get; set; }
        /// <summary>Smoothness.</summary>
        public double Smoothness { get; set; }
        /// <summary>Render mode.</summary>
        public string? RenderMode { get; set; }
        /// <summary>Texture slot hints.</summary>
        public Dictionary<string, string>? TextureSlotHints { get; set; }
    }
}
