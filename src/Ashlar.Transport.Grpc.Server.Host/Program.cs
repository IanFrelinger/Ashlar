using Ashlar.Infrastructure;
// gRPC transport server host entry point.
// Listen address, TLS and the client-side GrpcTransportOptions are documented in docs/GrpcHost.md.
// The loopback default below applies only when nothing else set a URL: ASPNETCORE_URLS, --urls and
// Kestrel__Certificates__Default__* override it per environment. (A "Urls" key in appsettings.json
// would NOT be overridable by ASPNETCORE_URLS - appsettings loads after the host configuration - so
// the default lives here, not there.)
using Ashlar.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Ashlar.Runtime;
using Ashlar.Transport.Grpc;
using Ashlar.Transport.Grpc.Server;

var builder = WebApplication.CreateBuilder(args);
if (string.IsNullOrWhiteSpace(builder.Configuration["urls"]))
{
    builder.WebHost.UseUrls("http://127.0.0.1:5001");
}

builder.WebHost.ConfigureKestrel(options =>
{
    options.ConfigureEndpointDefaults(endpoint => endpoint.Protocols = HttpProtocols.Http2);
});

builder.Services.Configure<GrpcTransportOptions>(
    builder.Configuration.GetSection("Ashlar:GrpcTransport"));
builder.Services.AddAshlarRuntimeRouting(builder.Configuration);
builder.Services.AddAshlar();
builder.Services.AddAshlarGrpcServer();

// SPEC-007 Q6: validate configured listeners before bind, then verify Kestrel's actual addresses.
builder.Services.AddAshlarInboundListenerValidation(
    AshlarInboundListenerPolicy.CollectEndpoints(
        builder.Configuration,
        builder.WebHost.GetSetting(WebHostDefaults.ServerUrlsKey)));
builder.Services.AddHostedService<Ashlar.API.Security.LoopbackListenerVerifier>();

var app = builder.Build();
app.MapAshlarGrpcServer();
app.MapGet("/", () => "Ashlar.Transport.Grpc.Server is running.");

app.Run();

/// <summary>Entry point for integration tests and hosting.</summary>
public partial class GrpcHostProgram;
