namespace Ashlar.Infrastructure.ModelArtifacts;

/// <summary>Options for fetching installable models from the public Ollama library API.</summary>
public sealed class OllamaRemoteLibraryCatalogOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Ashlar:ModelArtifactCatalog:OllamaRemoteLibrary";

    /// <summary>
    /// When false, <see cref="OllamaRemoteLibraryModelArtifactCatalogSource"/> reports unavailable (no outbound call).
    /// Default is true so <c>ListInstallableAsync</c> works out of the box; <c>AddAshlar</c> defaults it to false on AirGapped.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>HTTPS origin for the library <c>/api/tags</c> listing (default ollama.com).</summary>
    public string BaseUrl { get; set; } = "https://ollama.com";

    /// <summary>Request timeout.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(60);
}
