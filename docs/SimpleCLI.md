# Ashlar.Hosting - Simple CLI Guide

Quick-start guide for building CLI tools that execute Ashlar bricks.

## The Problem

Building a simple CLI tool to execute a brick requires understanding Ashlar's hosting infrastructure, dependency injection, and execution context. For simple use cases, this is overwhelming.

**Before** (manual setup):
```csharp
// Complex setup just to execute a brick
var builder = Host.CreateDefaultBuilder();
builder.ConfigureServices((context, services) =>
{
    services.AddAshlar(); // What profile? What options?
    // ... more configuration
});
var host = builder.Build();
var context = host.Services.GetRequiredService<IExecutionContext>();
// Finally execute the brick...
```

## The Solution: Simple CLI Helpers

`AshlarSimpleCli` provides streamlined helpers for common CLI scenarios.

### Quick Start: Execute a Brick

```csharp
using Ashlar.Hosting;
using Ashlar.Core.Domain.Bricks;
using Ashlar.Core.Domain.Execution;

// Create your brick (e.g., from Ashlar.Bricks.GitHub)
var prStatusBrick = new PrStatusBrick(logger);

// Execute with fluent input configuration
var output = await prStatusBrick.ExecuteAsync(input =>
{
    input.Set("prUrl", "https://github.com/owner/repo/pull/123");
    input.Set("githubToken", Environment.GetEnvironmentVariable("GITHUB_TOKEN"));
});

// Access outputs with TryGet
if (output.TryGet<List<string>>("blockers", out var blockers))
{
    Console.WriteLine($"Blockers: {string.Join(", ", blockers)}");
}
```

### Pattern 1: One-Off Execution

For simple scripts that execute a single brick and exit:

```csharp
using Ashlar.Hosting;

var brick = new MyBrick();

// Extension method creates host, executes, and cleans up
var output = await brick.ExecuteAsync(input =>
{
    input.Set("param1", "value1");
    input.Set("param2", 42);
});

Console.WriteLine(output.Summary);
```

### Pattern 2: Reusable Host

For CLIs that execute multiple bricks or need custom services:

```csharp
using Ashlar.Hosting;
using Microsoft.Extensions.DependencyInjection;

// Create host once
using var host = AshlarSimpleCli.CreateSimpleHost(
    configureServices: services =>
    {
        // Add custom services
        services.AddSingleton<IMyCustomService, MyCustomService>();
    },
    configureLogging: logging =>
    {
        logging.SetMinimumLevel(LogLevel.Warning);
    }
);

// Get execution context
var context = host.Services.GetRequiredService<IExecutionContext>();

// Execute multiple bricks
var brick1 = new FirstBrick();
var output1 = await brick1.ExecuteAsync(input1, ImplementationType.Deterministic, context);

var brick2 = new SecondBrick();
var output2 = await brick2.ExecuteAsync(input2, ImplementationType.Deterministic, context);
```

### Pattern 3: CLI with Arguments

Full example: GitHub PR checker CLI

```csharp
using System.CommandLine;
using Ashlar.Hosting;
using Ashlar.Bricks.GitHub;
using Microsoft.Extensions.Logging;

class Program
{
    static async Task<int> Main(string[] args)
    {
        var rootCommand = new RootCommand("Check GitHub PR status");
        
        var prUrlArg = new Argument<string>("pr-url", "GitHub PR URL");
        var tokenOption = new Option<string?>("--token", "GitHub token");
        
        rootCommand.AddArgument(prUrlArg);
        rootCommand.AddOption(tokenOption);
        
        rootCommand.SetHandler(async (string prUrl, string? token) =>
        {
            // Create host with custom logging
            using var host = AshlarSimpleCli.CreateSimpleHost(
                configureLogging: logging => logging.SetMinimumLevel(LogLevel.Warning)
            );
            
            var context = host.Services.GetRequiredService<IExecutionContext>();
            var logger = host.Services.GetRequiredService<ILogger<PrStatusBrick>>();
            
            // Create and execute brick
            var brick = new PrStatusBrick(logger);
            var input = new BrickInput();
            input.Set("prUrl", prUrl);
            
            if (!string.IsNullOrEmpty(token))
            {
                input.Set("githubToken", token);
            }
            
            var output = await brick.ExecuteAsync(input, ImplementationType.Deterministic, context);
            
            // Print results
            Console.WriteLine(output.Summary);
            
            if (output.TryGet<List<string>>("blockers", out var blockers))
            {
                foreach (var blocker in blockers)
                {
                    Console.WriteLine($"  - {blocker}");
                }
            }
        }, prUrlArg, tokenOption);
        
        return await rootCommand.InvokeAsync(args);
    }
}
```

## API Reference

### AshlarSimpleCli.CreateSimpleHostBuilder

```csharp
public static IHostBuilder CreateSimpleHostBuilder(
    Action<IServiceCollection>? configureServices = null,
    Action<ILoggingBuilder>? configureLogging = null)
```

Creates a host builder with Ashlar's **System** deployment profile (minimal overhead).

### AshlarSimpleCli.CreateSimpleHost

```csharp
public static IHost CreateSimpleHost(
    Action<IServiceCollection>? configureServices = null,
    Action<ILoggingBuilder>? configureLogging = null)
```

Creates and builds a ready-to-use host.

`CreateSimpleHostBuilder` registers a default `IExecutionContext` (`AgentId` = `AshlarSimpleCli.SimpleCliAgentId`)
before running `configureServices`, so registering your own `IExecutionContext` there replaces it.

### Brick.ExecuteAsync Extension

```csharp
public static async Task<BrickOutput> ExecuteAsync(
    this Brick brick,
    Action<BrickInput> configureInputs,
    ImplementationType implementation = ImplementationType.Deterministic,
    CancellationToken cancellationToken = default)
```

One-liner execution: creates host, executes brick, cleans up.

## Reading Outputs Safely

Use `TryGet` from Iteration 1 for optional outputs:

```csharp
var output = await brick.ExecuteAsync(input);

// Safe access with TryGet
if (output.TryGet<string>("status", out var status))
{
    Console.WriteLine($"Status: {status}");
}

// Or use Get with default
var count = output.Get<int>("count", 0);
```

## Deployment Profile

`AshlarSimpleCli` uses the **System** profile by default, which includes:
- Core execution infrastructure
- Minimal overhead
- No orchestration or heavy subsystems

For custom profiles:

```csharp
using var host = Host.CreateDefaultBuilder()
    .ConfigureServices((context, services) =>
    {
        services.AddAshlarProfile(AshlarDeploymentProfile.Edge);
    })
    .Build();
```

## Error Handling

```csharp
try
{
    var output = await brick.ExecuteAsync(input);
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine($"Invalid input: {ex.Message}");
    return 1;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Execution failed: {ex.Message}");
    return 2;
}
```

## Best Practices

1. **Dispose hosts**: Use `using` for hosts created with `CreateSimpleHost`
2. **Reuse hosts**: For multiple brick executions, create one host and reuse the context
3. **Use TryGet**: Always use `TryGet` for optional outputs
4. **Validate inputs**: Check brick's `Interface.Inputs` for required parameters
5. **Log errors**: Configure logging level based on your CLI verbosity needs

## Formatting outputs

`BrickOutputFormatters` (namespace `Ashlar.Hosting`) renders any `BrickOutput` in one of four shapes, so
every CLI prints results the same way:

| Method | Shape | Notes |
|--------|-------|-------|
| `output.ToJson(indented: true)` | JSON object: `summary` (when set) plus every output key | Keys are written as stored (no camel-casing). An output named `summary` overwrites the summary field. |
| `output.ToMarkdown(title: null)` | `## title`, `### Summary`, `### Outputs` with `**key**: value` lines | Booleans render as `✅ true` / `❌ false`; arrays as `[a, b]`. |
| `output.ToPlainText(includeEmpty: false)` | `Summary:` then `Outputs:` with `  key: value` lines | Empty values are skipped unless `includeEmpty` is true; arrays render as `a, b`. |
| `output.ToTable()` | Box-drawn two-column table | Values longer than 48 characters are cut with `...`; arrays render as `[N items]`. |

`ToMarkdown`, `ToPlainText` and `ToTable` list outputs in key order; `ToJson` keeps insertion order. Nested objects are written with `ToString()` (except in `ToJson`), so
for structured values such as `PrStatusBrick`'s `checks`, prefer `ToJson`.

```csharp
using Ashlar.Hosting;

var output = await brick.ExecuteAsync(input => input.Set("prUrl", prUrl));

Console.WriteLine(format switch
{
    "json" => output.ToJson(),
    "markdown" => output.ToMarkdown("PR status"),
    "table" => output.ToTable(),
    _ => output.ToPlainText(),
});
```

For a quick look while developing, `output.PrintToConsole(includeAllOutputs: true)` writes the summary and
every output value; `output.WriteTo(writer, includeAllOutputs: true)` does the same to any `TextWriter`.

## Complete Example: PR Blocker CLI

Pattern 3 above is the complete example.

## Migration from Manual Setup

**Before:**
```csharp
var builder = Host.CreateDefaultBuilder();
builder.ConfigureServices((context, services) => services.AddAshlar());
var host = builder.Build();
var context = host.Services.GetRequiredService<IExecutionContext>();
var output = await brick.ExecuteAsync(input, ImplementationType.Deterministic, context);
```

**After:**
```csharp
var output = await brick.ExecuteAsync(input => {
    input.Set("param", "value");
});
```

**Savings**: ~6 lines → ~3 lines, no infrastructure knowledge required.
