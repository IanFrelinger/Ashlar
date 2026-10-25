// Ashlar MCP stdio server host.
//
// Boots the Ashlar kernel and serves the MCP tool bridge over stdio for local AI clients
// (Claude Desktop / Claude Code / IDEs spawn this binary and speak MCP over stdin/stdout).
// Mirrors Ashlar.Transport.Grpc.Server.Host: a thin delivery vehicle over Ashlar.Hosting.
//
// Configuration (env vars or appsettings.json):
//   Ashlar__Mcp__Server__Enabled                 defaults to true here — running this binary IS the opt-in;
//                                              still overridable to false, and refused under AirGapped.
//   Ashlar__Mcp__Server__ExposedToolIds__0       allowlist, e.g. "repo.fs.read" — empty exposes zero tools.
//   Ashlar__Mcp__Server__ResponseLabels__repo.fs.read
//                                              trusted operator classification; absent is SystemHigh, not Public.
//                                              This stdio host has no authenticated principal and receives Public
//                                              responses only. Do not mark private repositories Public to enable it.
//   Ashlar__Mcp__Server__RepoRoot                world-snapshot root (defaults to the working directory).
//   Ashlar__Mcp__Server__ArgumentOverrides__repo.fs.read__root
//                                              pin caller-visible args, e.g. force 'root' for repo tools.
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Ashlar.Abstractions;
using Ashlar.Hosting;
using Ashlar.Mcp.Server;
using Ashlar.Tools.Dev;

var builder = Host.CreateApplicationBuilder(args);

// Lowest-priority defaults: everything (appsettings, env vars, args) overrides them.
builder.Configuration.Sources.Insert(0, new MemoryConfigurationSource
{
    InitialData = new Dictionary<string, string?>
    {
        [$"{AshlarMcpServerOptions.SectionPath}:Enabled"] = "true",
    },
});

// stdout belongs to the MCP stdio transport — a single stray log line corrupts the JSON-RPC
// stream, so every console log goes to stderr.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.AddAshlar();

// Read-only repo tools available for allowlisting out of the box. Nothing is exposed until the
// operator lists ids in ExposedToolIds and configures reviewed response classifications. Mutating tools (repo.fs.write, git commit, dotnet run)
// are deliberately not pre-registered — hosts that want them must add them explicitly.
builder.Services.AddSingleton<ITool, RepoFsReadTool>();
builder.Services.AddSingleton<ITool, RepoFsListTool>();

builder.Services.AddAshlarMcpServer(builder.Configuration).WithStdioServerTransport();

await builder.Build().RunAsync();
