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
//   Ashlar__Mcp__Server__RepoRoot                world-snapshot root (defaults to the working directory).
//   Ashlar__Mcp__Server__ArgumentOverrides__repo.fs.read__root
//                                              pin caller-visible args, e.g. force 'root' for repo tools.
//   Ashlar__Mcp__Bricks__ExposedBrickIds__0      brick allowlist, e.g. "github.pr-status" — empty exposes zero bricks.
//   Ashlar__Audit__File__Directory              where the per-call brick audit records (JSONL) go; defaults to
//                                              <LocalApplicationData>/ashlar/mcp-audit. Ashlar__Audit__Sinks__0
//                                              selects the sink (File by default here; StructuredLog or NoOp).
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Ashlar.Abstractions;
using Ashlar.Bricks.GitHub;
using Ashlar.Hosting;
using Ashlar.Hosting.Sdk.Extensions;
using Ashlar.Mcp.Bricks;
using Ashlar.Mcp.Server;
using Ashlar.Runtime;
using Ashlar.Tools.Dev;

var builder = Host.CreateApplicationBuilder(args);

// Lowest-priority defaults: everything (appsettings, env vars, args) overrides them.
builder.Configuration.Sources.Insert(0, new MemoryConfigurationSource
{
    InitialData = new Dictionary<string, string?>
    {
        [$"{AshlarMcpServerOptions.SectionPath}:Enabled"] = "true",
        // One durable record per brick call, flushed before the call returns, outside the
        // working directory the IDE spawned this process in.
        ["Ashlar:Audit:Sinks:0"] = "File",
        ["Ashlar:Audit:File:Directory"] = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ashlar", "mcp-audit"),
        ["Ashlar:Audit:File:FilePrefix"] = "mcp-brick-calls",
        ["Ashlar:Audit:File:FlushEveryEvent"] = "true",
    },
});

// stdout belongs to the MCP stdio transport — a single stray log line corrupts the JSON-RPC
// stream, so every console log goes to stderr.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

builder.Services.AddAshlar();

// Read-only repo tools available for allowlisting out of the box. Nothing is exposed until the
// operator lists ids in ExposedToolIds; mutating tools (repo.fs.write, git commit, dotnet run)
// are deliberately not pre-registered — hosts that want them must add them explicitly.
builder.Services.AddSingleton<ITool, RepoFsReadTool>();
builder.Services.AddSingleton<ITool, RepoFsListTool>();

// Bricks available for allowlisting out of the box, on the same terms: registered here, exposed only
// when listed in Ashlar:Mcp:Bricks:ExposedBrickIds. Both call api.github.com through Octokit.
builder.Services.AddAshlarSdk(sdk => sdk
    .RegisterBrick<PrStatusBrick>()
    .RegisterBrick<CiStatusBrick>());
builder.Services.AddBarrierAuditSinks(builder.Configuration);
builder.Services.AddAshlarMcpBricks(builder.Configuration);

builder.Services.AddAshlarMcpServer(builder.Configuration).WithStdioServerTransport();

await builder.Build().RunAsync();
