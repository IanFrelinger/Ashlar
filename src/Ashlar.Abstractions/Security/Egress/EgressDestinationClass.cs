namespace Ashlar.Abstractions.Security.Egress;

/// <summary>
/// What kind of destination an egress decision was made for. The class decides the destination's label.
/// </summary>
/// <remarks>
/// The numeric values are stable and new classes are only appended, so a stored value keeps its meaning.
/// The zero value is <see cref="Unknown"/>, which fails closed to the bottom label.
/// </remarks>
public enum EgressDestinationClass
{
    /// <summary>
    /// A destination the guard cannot place, for example an unrecognised family. Labelled
    /// <see cref="SecurityLabel.Public"/>: an unknown destination fails closed to the bottom.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// Inside the host boundary: a loopback host, a <c>unix</c> or <c>npipe</c> URI, or a name starting with
    /// <c>host:</c>. Labelled <see cref="SecurityLabel.SystemHigh"/>.
    /// </summary>
    Host = 1,

    /// <summary>A model reached over the network (families <c>model.*</c>). Labelled Internal.</summary>
    ExternalModel = 2,

    /// <summary>A web search provider (family <c>web-search</c>). Labelled Confidential.</summary>
    WebSearch = 3,

    /// <summary>Any other network or export path (A2A, gRPC, MCP, HTTP, mesh, file export, process,
    /// telemetry). Labelled Internal.</summary>
    NetworkExport = 4,
}
