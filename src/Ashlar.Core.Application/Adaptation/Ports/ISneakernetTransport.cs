using Ashlar.Abstractions.Security.Egress;

namespace Ashlar.Core.Application.Adaptation.Ports;

/// <summary>
/// Sneakernet transport: export/import shared adaptations via file (USB, etc.).
/// P3.3: ashlar mesh export, ashlar mesh import.
/// </summary>
public interface ISneakernetTransport
{
    /// <summary>
    /// Exports shared adaptations to a file for physical transfer (.nxpkg format).
    /// </summary>
    /// <param name="outputPath">Path to the export file (e.g. ashlar-mesh.nxpkg).</param>
    /// <param name="componentId">Optional component/adaptation ID to export; null = export all.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ExportAsync(string outputPath, string? componentId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Imports shared adaptations from an export file and validates/adopts each.
    /// </summary>
    /// <param name="inputPath">Path to the export file.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Number of entries adopted.</returns>
    Task<int> ImportAsync(string inputPath, CancellationToken cancellationToken = default);
}

// The public port keeps its ordinary enforcing behavior. Only trusted CLI composition can
// carry an operator initiator to the implementation, without an ambient or public bypass flag.
internal interface ISneakernetExportInitiator
{
    Task ExportAsync(string outputPath, string? componentId, EgressInitiator initiator, CancellationToken cancellationToken);
}
