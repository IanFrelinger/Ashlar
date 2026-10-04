namespace Ashlar.Abstractions.Security.Egress;

/// <summary>
/// The families of egress path. A request's family decides its destination class unless the destination is
/// inside the host boundary.
/// </summary>
/// <remarks>
/// <para><c>model.*</c> is <see cref="EgressDestinationClass.ExternalModel"/>; <c>web-search</c> is
/// <see cref="EgressDestinationClass.WebSearch"/>; every other family here is
/// <see cref="EgressDestinationClass.NetworkExport"/>. A family not listed here is
/// <see cref="EgressDestinationClass.Unknown"/>.</para>
/// <para>The values are matched ordinally and are part of the decision records operators read, so they do not
/// change.</para>
/// </remarks>
public static class EgressFamilies
{
    /// <summary>A model called through the Microsoft.Extensions.AI pipeline.</summary>
    public const string ModelMeai = "model.meai";

    /// <summary>A model called through the provider factory.</summary>
    public const string ModelLegacy = "model.legacy";

    /// <summary>A web search provider.</summary>
    public const string WebSearch = "web-search";

    /// <summary>An A2A agent transport call.</summary>
    public const string A2A = "a2a";

    /// <summary>A gRPC agent transport call.</summary>
    public const string Grpc = "grpc";

    /// <summary>An MCP client call.</summary>
    public const string Mcp = "mcp";

    /// <summary>An HTTP client the code builds itself.</summary>
    public const string Http = "http";

    /// <summary>An <c>IHttpClientFactory</c> client.</summary>
    public const string HttpFactory = "http.factory";

    /// <summary>Publishing a mesh package.</summary>
    public const string MeshPublish = "mesh.publish";

    /// <summary>Serving a mesh package to a peer.</summary>
    public const string MeshServe = "mesh.serve";

    /// <summary>Pulling a mesh package from a peer.</summary>
    public const string MeshPull = "mesh.pull";

    /// <summary>Announcing this node for mesh discovery.</summary>
    public const string MeshDiscovery = "mesh.discovery";

    /// <summary>Writing an export to a file that leaves the process.</summary>
    public const string FileExport = "file.export";

    /// <summary>Starting a process that can reach the network.</summary>
    public const string Process = "process";

    /// <summary>Exporting telemetry.</summary>
    public const string Telemetry = "telemetry";
}
