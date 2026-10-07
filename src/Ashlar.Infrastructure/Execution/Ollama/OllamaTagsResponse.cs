using System.Text.Json.Serialization;

namespace Ashlar.Infrastructure.Execution.Ollama;

/// <summary>Deserialized response from Ollama <c>/api/tags</c> listing installed models.</summary>
internal sealed record OllamaTagsResponse
{
    /// <summary>Models reported by the Ollama daemon.</summary>
    [JsonPropertyName("models")]
    public IReadOnlyList<OllamaTagsModel> Models { get; init; } = [];
}
