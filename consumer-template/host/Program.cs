// TRUST BOUNDARY OF THIS TEMPLATE - read before copying it.
//
// This host executes an unverified brick. It performs no certification verification of any kind:
// no certification record is read here, no signature is checked, and no hash is compared. What it
// demonstrates is the SHAPE of a consumer host - AddAshlarBrick<T>() before AddAshlar(), a health
// probe, and a wire-DTO route onto Brick.ExecuteAsync. It is not a trust boundary, and the
// certification vocabulary in ../CONSUMING.md describes what the packaged verifier CAN do, not what
// this file DOES.
//
// Two separate facts, because only the first could be fixed by adding code to this file:
//
//  1. Nothing here calls CertificationTrustVerifier. A certification record could sit beside this
//     binary and change nothing about whether the host serves.
//
//  2. ExternalProductHost.csproj takes a <ProjectReference> on the brick project, so the brick that
//     runs is THIS HOST'S OWN COMPILE. The certifier never saw those bytes and no hash in any
//     record covers them. Adding a boot check here while that reference stands would verify a file
//     and then execute a different assembly - a worse state than this one, because it would print a
//     trusted verdict over an unbound program. Measured rather than argued: the test
//     JudgedArtifactIsTheExecutedArtifactTests.RecompilingTheVerifiedSource_DoesNotReproduceTheJudgedAssembly
//     shows that recompiling the very source a certificate covers yields a different assembly hash.
//
// Binding "what was judged" to "what runs" therefore takes both halves: drop the ProjectReference,
// and load the exported gate-emitted assembly instead. ../CONSUMING.md, under "Certification: what
// this template binds, and what it does not", gives that recipe and names the public primitives it
// is built from.
using Ashlar.Authoring;
using Ashlar.Brick.Contracts;
using Ashlar.Core.Application.Bricks;
using Ashlar.Core.Domain.Bricks;
using Ashlar.Core.Domain.Execution;
using Ashlar.Hosting;
using Ashlar.Infrastructure.Execution;
using __BRICK_NAMESPACE__;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddAshlarBrick<__BRICK_NAMESPACE__.__BRICK_CLASS__>();
builder.Services.AddAshlar(options =>
{
    options.RegisterBackgroundAgentHostedService = false;
});

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTimeOffset.UtcNow }));

app.MapPost("/api/bricks/{brickId}/execute", async (
    string brickId,
    BrickExecuteRequestDto request,
    IBrickRegistry brickRegistry,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(brickId))
        return Results.BadRequest(new { title = "brickId is required" });
    if (request is null)
        return Results.BadRequest(new { title = "Request body is required" });
    if (!string.IsNullOrEmpty(request.BrickId) &&
        !string.Equals(request.BrickId, brickId, StringComparison.OrdinalIgnoreCase))
    {
        return Results.BadRequest(new { title = "BrickId in body must match route" });
    }

    var brick = brickRegistry.GetBrick(brickId);
    if (brick is null)
        return Results.NotFound();

    if (!Enum.TryParse<ImplementationType>(request.Implementation, true, out var implementation))
        implementation = ImplementationType.Deterministic;

    var context = ToExecutionContext(request.ExecutionContext);
    var input = BrickValueSerializer.FromWireToBrickInput(request.Input);
    BrickInputDefaults.Apply(brick, input);

    try
    {
        var output = await brick.ExecuteAsync(input, implementation, context, cancellationToken).ConfigureAwait(false);
        return Results.Ok(new BrickExecuteResponseDto
        {
            Success = true,
            Summary = output.Summary,
            Output = BrickValueSerializer.ToWireDictionary(output)
        });
    }
    catch (Exception ex)
    {
        return Results.Ok(new BrickExecuteResponseDto
        {
            Success = false,
            Error = ex.Message
        });
    }
});

app.Run();

static Ashlar.Infrastructure.Execution.ExecutionContext ToExecutionContext(ExecutionContextDto? dto)
{
    if (dto is null)
        return new Ashlar.Infrastructure.Execution.ExecutionContext();

    return new Ashlar.Infrastructure.Execution.ExecutionContext
    {
        AgentId = dto.AgentId ?? string.Empty,
        BehaviorId = dto.BehaviorId ?? string.Empty,
        IsAirGapped = dto.IsAirGapped,
        AuditMode = dto.AuditMode,
        Provider = dto.Provider ?? "openai",
        Variables = dto.Variables is null
            ? new Dictionary<string, object>()
            : new Dictionary<string, object>(dto.Variables)
    };
}
