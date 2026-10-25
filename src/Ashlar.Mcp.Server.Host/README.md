# Ashlar.Mcp.Server.Host

An MCP server over stdio that exposes **Ashlar bricks** (typed, deterministic .NET units of work) and
read-only repository tools to any MCP client: VS Code / GitHub Copilot, Claude Code, Cursor, Visual Studio.
Every brick call writes one audit record you can match to the exact request and response.

Nothing is exposed by default. You list the bricks (and tools) a client may call.

## Run it

Needs the .NET 10 SDK, which ships `dnx`.

```bash
Ashlar__Mcp__Bricks__ExposedBrickIds__0=github.blockers dnx Ashlar.Mcp.Server.Host --yes
```

VS Code (`.vscode/mcp.json`):

```json
{
  "servers": {
    "ashlar": {
      "type": "stdio",
      "command": "dnx",
      "args": ["Ashlar.Mcp.Server.Host", "--yes"],
      "env": {
        "Ashlar__Mcp__Bricks__ExposedBrickIds__0": "github.blockers",
        "Ashlar__Mcp__Bricks__ExposedBrickIds__1": "github.ci-status"
      }
    }
  }
}
```

Pin a version with `Ashlar.Mcp.Server.Host@<version>` in `args`.

### From a clone

The package is not on nuget.org before the first release that ships it (0.3.0 does not). Until then `dnx`
answers `ashlar.mcp.server.host is not found in NuGet feeds`, and you run the server from a clone:

```bash
dotnet build src/Ashlar.Mcp.Server.Host -c Release
Ashlar__Mcp__Bricks__ExposedBrickIds__0=github.blockers \
  dotnet src/Ashlar.Mcp.Server.Host/bin/Release/net10.0/Ashlar.Mcp.Server.Host.dll
```

In an MCP client configuration, set `"command": "dotnet"` and pass the `.dll` path as the only `args` entry.
Build once and start the built `.dll`, so a client does not wait for a build on every start.

## Built-in bricks

| Brick id | MCP tool name | What it does |
|----------|---------------|--------------|
| `github.blockers` | `github_blockers` | What is blocking a pull request or release: a verdict, each blocker with an evidence link, and every fact it could not read (with the reason). Input: `url` (`https://github.com/<owner>/<repo>/pull/<n>` or `.../releases/tag/<tag>`); optional `githubToken`. |
| `github.pr-status` | `github_pr-status` | Pull request state, mergeability, reviews, checks and the blockers they add up to. Input: `prUrl`; optional `githubToken`. |
| `github.ci-status` | `github_ci-status` | Workflow runs and check runs for a ref. Inputs: `owner`, `repo`, `ref`; optional `githubToken`. |

All three call `api.github.com` and accept only `https://github.com` URLs. Without a token GitHub allows 60 requests
an hour; pass `githubToken` for more, and to read private repositories.

## What a call returns

A successful call returns a JSON text block (also sent as structured content):

```json
{"brickId":"github.pr-status","brickVersion":"1.0.0","auditId":"3f2c…","summary":"…","outputs":{ … }}
```

Missing required inputs and arguments that are not a JSON object come back as tool errors (`isError: true`)
before the brick runs, and also carry an `auditId`. A brick that throws comes back the same way. A tool name the
server does not list is an MCP protocol error, not a tool result.

## Audit records

One JSON line per brick call (refusals included) in
`<LocalApplicationData>/ashlar/mcp-audit/mcp-brick-calls-<start time>.ndjson` (on Linux
`~/.local/share/ashlar/mcp-audit`), one file per server start. Set `Ashlar__Audit__File__Directory` to move it.
To find the record for one call: `grep <auditId> ~/.local/share/ashlar/mcp-audit/mcp-brick-calls-*.ndjson`. Each record has `eventType` `MCP_BRICK_INVOKED`,
`correlationId` = the `auditId` the caller saw, and a `detail` holding the brick id and version, the outcome
(`succeeded`, `failed`, `refused`), the duration, and the SHA-256 of the arguments as received and of the text
returned. **Argument and output values are not written**, so a token passed as `githubToken` does not land on disk.

## Configuration

| Variable | Meaning |
|----------|---------|
| `Ashlar__Mcp__Bricks__ExposedBrickIds__N` | Bricks to expose. An id with no registered brick stops the server at startup. |
| `Ashlar__Mcp__Server__ExposedToolIds__N` | Tools to expose (`repo.fs.read`, `repo.fs.list`). |
| `Ashlar__Mcp__Server__RepoRoot` | Root the repo tools read from (default: working directory). |
| `Ashlar__Audit__File__Directory` | Audit directory. |
| `Ashlar__Audit__Sinks__0` | `File` (default here), `StructuredLog`, or `NoOp`. |

Logs go to stderr; stdout carries only MCP JSON-RPC.

## Known limits

- **Host-registered bricks are not certificate-checked.** The built-in bricks are admitted because this host
  registers them, like any `RegisterBrick<T>()` host. Use the certification gate for bricks you did not build.
- **No record, no result.** A call returns only after its record is written and flushed. If the record cannot be
  written (a full disk, an unwritable directory), the call comes back as a tool error saying so, with its `auditId`
  and without the brick's output, even when the brick ran. Flushed means handed to the operating system, not
  `fsync`ed: a power loss can still take the last records.
- **A token passed as `githubToken` travels in the tool arguments**, so the MCP client sees it. The audit record
  holds only its hash. The GitHub bricks send through Ashlar's egress guard (`EG-HTTP-08`), and the server refuses
  to start under the `AirGapped` profile.
- **The caller identity is not in the audit record.** Over stdio there is one local caller; the record holds
  what was asked and answered, not who asked.
- **No local GGUF inference in this package.** The `local` model provider's native backend is left out of the
  tool package to keep the download small; build the host from source to get it.
