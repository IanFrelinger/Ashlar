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

/// <summary>HTTP request body for patching mesh task status.</summary>
public sealed record MeshTaskStatusPatchRequest(
    MeshTaskStatus Status,
    string? Reason = null,
    string? CorrelationId = null,
    string? ResultSummary = null,
    string? ResultHandle = null,
    string? LeaseToken = null,
    byte[]? ResultPayload = null);
