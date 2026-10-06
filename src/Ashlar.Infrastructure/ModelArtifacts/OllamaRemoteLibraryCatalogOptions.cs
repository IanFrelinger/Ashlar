namespace Ashlar.Infrastructure.ModelArtifacts;

/// <summary>Options for fetching installable models from the public Ollama library API.</summary>
public sealed class OllamaRemoteLibraryCatalogOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Ashlar:ModelArtifactCatalog:OllamaRemoteLibrary";

    /// <summary>
    /// When false, <see cref="OllamaRemoteLibraryModelArtifactCatalogSource"/> reports unavailable (no outbound call).
    /// The property default is true, so a Full host lists the library out of the box.
    /// <c>AddModelArtifactCatalog</c> sets this false on AirGapped when the <c>Enabled</c> key is absent.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>HTTPS origin for the library <c>/api/tags</c> listing (default ollama.com).</summary>
    public string BaseUrl { get; set; } = "https://ollama.com";

    /// <summary>Request timeout.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(60);
}
