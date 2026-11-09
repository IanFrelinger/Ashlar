using System.Text.Json;
using Ashlar.Core.Application.Execution.Ports;
using Ashlar.Infrastructure.Execution.Sandbox;
using Ashlar.Infrastructure.Scaling;
using Microsoft.Extensions.DependencyInjection;

if (args.Length != 1 || !args[0].StartsWith("sha256:", StringComparison.Ordinal))
    throw new ArgumentException("Pass the preloaded worker's immutable local image ID.");

Environment.SetEnvironmentVariable("ASHLAR_DOGFOOD_HOST_ONLY", "host-sentinel");
var services = new ServiceCollection();
services.AddAshlarIsolatedTasks();
using var provider = services.BuildServiceProvider();
var runner = provider.GetRequiredService<IIsolatedTaskRunner>();
var process = provider.GetRequiredService<IProcessCommandRunner>();
var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
const string literalSku = "washer; $(printf unexpected)";
var input = JsonSerializer.Serialize(new { csv = $"sku,quantity\n{literalSku},2\nblue bolt,3\n" });
var request = new IsolatedTaskRequest(args[0], new[] { "python3", "/app/parser.py", input })
{
    MemoryBytes = 128L * 1024 * 1024,
    Cpus = 0.5m,
    Pids = 32,
    Timeout = TimeSpan.FromSeconds(30),
    MaxOutputBytes = 4096,
};

var first = await runner.RunAsync(request);
var second = await runner.RunAsync(request);
foreach (var result in new[] { first, second })
{
    Check(result.Command.Succeeded, "Valid inventory failed: " + result.Command.StdErr);
    var parsed = JsonSerializer.Deserialize<InventorySummary>(result.Command.StdOut, jsonOptions)
        ?? throw new InvalidOperationException("Parser returned no JSON value.");
    Check(parsed.TotalQuantity == 5, "Wrong inventory total.");
    Check(parsed.Skus.SequenceEqual(new[] { literalSku, "blue bolt" }), "Literal argv did not round trip.");
    Check(parsed.ScratchWasFresh, "Task inherited scratch from another invocation.");
    Check(parsed.RootWasReadOnly, "Image-owned application directory was writable.");
    Check(parsed.HostEnvironmentWasAbsent, "Host-only environment reached the workload.");
    Check(result.Environment.ImageDigest == args[0], "Observed image differs from the preloaded image.");
    Check(result.Environment.EffectiveNetworkMode == "none", "Network mode was not none.");
    Check(result.Environment.EffectiveReadOnlyRootFilesystem == true, "Root was not read-only.");
    Check(result.Environment.FindShortfalls().Count == 0, "Effective resource budget was weaker than requested.");
}
Check(first.Environment.SessionId != second.Environment.SessionId, "Invocations shared a session.");

var invalidInput = JsonSerializer.Serialize(new { csv = "sku,quantity\nbad item,nope\n" });
var rejected = await runner.RunAsync(request with { Command = new[] { "python3", "/app/parser.py", invalidInput } });
Check(rejected.Command.ExitCode == 2, "Invalid inventory must return the parser's nonzero exit.");
Check(rejected.Command.StdOut.Length == 0, "Invalid inventory must not return success JSON.");
Check(rejected.Command.StdErr.Contains("quantity must be a positive integer", StringComparison.Ordinal),
    "Parser failure diagnostic was lost.");

foreach (var result in new[] { first, second, rejected })
{
    using var inspectionBudget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    var remaining = await process.RunAsync("docker", new[]
    {
        "ps", "--all", "--filter", $"name={result.Environment.SessionId}", "--format", "{{.Names}}",
    }, inspectionBudget.Token);
    Check(remaining.Succeeded, "Could not verify cleanup against the Docker daemon.");
    Check(string.IsNullOrWhiteSpace(remaining.StdOut), "Task container remained after RunAsync returned.");
}

Console.WriteLine(JsonSerializer.Serialize(new
{
    verdict = "passed",
    scenario = "default DI host -> packaged inventory parser -> typed JSON result",
    validInvocations = 2,
    rejectedInvocations = 1,
    invalidInputExitCode = rejected.Command.ExitCode,
    imageId = args[0],
    maxOutputBytes = request.MaxOutputBytes,
    environment = first.Environment,
    removedSessionIds = new[] { first.Environment.SessionId, second.Environment.SessionId, rejected.Environment.SessionId },
}));

static void Check(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

internal sealed record InventorySummary(
    string[] Skus, int TotalQuantity, bool ScratchWasFresh, bool RootWasReadOnly, bool HostEnvironmentWasAbsent);
