using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Ashlar.API.Security;
using Ashlar.Commercial.Fleet.Contracts.Models;
using Ashlar.Commercial.Fleet.Contracts.Ports;
using Ashlar.Commercial.Fleet.Infrastructure;

namespace Ashlar.Commercial.Fleet.Api;

/// <summary>HTTP request body for registering or updating a fleet node.</summary>
/// <remarks>
/// Every optional field here is nullable, and that is load-bearing rather than stylistic: this body
/// is upserted as a WHOLE document, so a field that cannot be absent overwrites whatever an operator
/// stored. <c>Drained</c> was a non-nullable <c>bool</c> defaulting to <c>false</c>, so a node
/// re-registering on its normal reconnect cycle — a body with no <c>drained</c> in it — reset an
/// operator's drain with no concurrency involved at all, and
/// <c>MeshTaskPlacementService</c> selects on <c>Admitted &amp;&amp; !Drained</c>. Null means
/// "leave what is stored"; <c>RegisterFleetNodeAsync</c> resolves each of these against the existing
/// document.
/// </remarks>
public sealed record MeshFleetNodeRequest(
    string PeerId,
    string ApiBaseUrl,
    IReadOnlyDictionary<string, string>? Labels = null,
    IReadOnlyList<string>? AdvertisedBrickIds = null,
    bool? Drained = null,
    int? ReportedQueueDepth = null,
    string? TrustTier = null,
    bool? Admitted = null,
    string? PeerRegistrationKey = null);
