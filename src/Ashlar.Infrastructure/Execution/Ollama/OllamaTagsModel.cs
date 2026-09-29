using System.Text.Json.Serialization;

namespace Ashlar.Infrastructure.Execution.Ollama;

/// <summary>Single model entry in an Ollama tags response.</summary>
internal sealed record OllamaTagsModel
{
    /// <summary>Model name including tag (e.g. <c>llama3:8b</c>).</summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>On-disk size in bytes.</summary>
    [JsonPropertyName("size")]
    public long Size { get; init; }

    /// <summary>Last modification timestamp from Ollama.</summary>
    [JsonPropertyName("modified_at")]
    public DateTimeOffset? ModifiedAt { get; init; }
}
