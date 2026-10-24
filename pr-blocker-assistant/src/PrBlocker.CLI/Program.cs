using Ashlar.Authoring;
using Ashlar.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PrBlocker.Brick;
using System.Text.Json;

// PR Blocker Assistant CLI
// A consumer application built on Ashlar 0.3.0 that answers: "What is blocking this PR or release?"

if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
{
    Console.WriteLine("PR Blocker Assistant");
    Console.WriteLine("====================");
    Console.WriteLine();
    Console.WriteLine("Usage: pr-blocker <github-url> [--token <token>]");
    Console.WriteLine();
    Console.WriteLine("Examples:");
    Console.WriteLine("  pr-blocker https://github.com/owner/repo/pull/123");
    Console.WriteLine("  pr-blocker owner/repo#123");
    Console.WriteLine("  pr-blocker https://github.com/owner/repo/releases/tag/v1.0.0");
    Console.WriteLine("  pr-blocker https://github.com/owner/repo/pull/123 --token ghp_xxx");
    Console.WriteLine();
    Console.WriteLine("Environment:");
    Console.WriteLine("  GITHUB_TOKEN - GitHub personal access token (optional, improves rate limits)");
    return 0;
}

var url = args[0];
string? token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");

// Parse command line args for token
for (var i = 1; i < args.Length; i++)
{
    if ((args[i] == "--token" || args[i] == "-t") && i + 1 < args.Length)
    {
        token = args[++i];
    }
}

Console.WriteLine($"Analyzing: {url}");
if (!string.IsNullOrWhiteSpace(token))
{
    Console.WriteLine("Using provided GitHub token");
}
Console.WriteLine();

var builder = Host.CreateApplicationBuilder();
builder.Logging.SetMinimumLevel(LogLevel.Warning);

// Register the brick
builder.Services.AddAshlarBrick<PrBlockerBrick>();

// Add Ashlar hosting
builder.Services.AddAshlar(options =>
{
    options.RegisterBackgroundAgentHostedService = false; // We're not using background agents
});

using var host = builder.Build();

// Get the brick registry
var registry = host.Services.GetRequiredService<Ashlar.Core.Domain.Execution.IBrickRegistry>();

// Find our brick
var brick = registry.GetBrick("pr-blocker-analyzer");
if (brick == null)
{
    Console.Error.WriteLine("ERROR: PrBlockerBrick not registered");
    return 1;
}

// Prepare input
var input = new Ashlar.Core.Domain.Execution.BrickInput();
input.Set("url", url);
if (!string.IsNullOrWhiteSpace(token))
{
    input.Set("githubToken", token);
}

// Execute the brick directly using Ashlar's ExecutionContext
var context = new Ashlar.Infrastructure.Execution.ExecutionContext
{
    AgentId = "pr-blocker-cli",
    Provider = "cli"
};

var result = await brick.ExecuteAsync(
    input,
    Ashlar.Core.Domain.Bricks.ImplementationType.Deterministic,
    context,
    CancellationToken.None);

// Display results
Console.WriteLine(result.Summary);
Console.WriteLine();

var ready = result.Get<bool>("ready");
Console.WriteLine($"Ready: {ready}");

var blockers = result.Get<List<object>>("blockers");
if (blockers != null && blockers.Any())
{
    Console.WriteLine();
    Console.WriteLine("Detailed blockers:");
    foreach (var blocker in blockers)
    {
        Console.WriteLine($"  - {blocker}");
    }
}

Console.WriteLine();
Console.WriteLine("Evidence (JSON):");
var evidence = result.Get<object>("evidence");
if (evidence != null)
{
    var json = JsonSerializer.Serialize(evidence, new JsonSerializerOptions
    {
        WriteIndented = true
    });
    Console.WriteLine(json);
}

return 0;
