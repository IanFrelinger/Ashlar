# Agent Sandbox Architecture (Host + Project-Scoped Tools)

This guide describes how to run Ashlar agents in a constrained sandbox while still
allowing host-bound tools and dependency downloads.

## Goals

1. Prevent agents from writing outside approved paths.
2. Keep third-party SDKs/tools and caches in project-scoped directories.
3. Support host-installed apps that cannot be containerized by default.

## Policy enforcement in this repo

Two layers decide whether a cycle may write a path, and only one of them is configurable.

**The floor (not configurable).** Every write tool (`repo.fs.write`, `repo.fs.search_replace`,
`repo.fs.ensure_file`, `docs.update`, `repo.git.commit`) resolves its target through
`ToolSandbox.TryResolveWritePath`, which refuses — whatever policy list the host composed —
anything that escapes the repo root, runs through a symlink or junction, or is a governance
path: `.ashlar/` (the admission ledger and runtime state), `.git*/`, `scripts/`, the root
`ashlar.yaml` / `ashlar.policy.yaml`, and at any depth `.editorconfig`, `.globalconfig`,
`global.json`, `nuget.config`, `Makefile`, `dotnet-tools.json`, `.pre-commit-config.yaml`,
`*.props` and `*.targets`. Project and solution files are writable. `GovernanceFloorPolicy`
turns the same refusal into a policy denial, so the cycle's record counts it.

**The allowlist (configurable).** `PathAllowlist` bounds WHERE a write may land:

- Relative allowlisted prefixes (defaults): `src/`, `tests/`, `docs/`, `application/`
- Optional extra prefixes via env: `ASHLAR_PATH_ALLOWLIST_EXTRA`
- Absolute paths only when inside sandbox root (`WorldSnapshot["SandboxRoot"]` or `ASHLAR_SANDBOX_ROOT`)

When set, `ASHLAR_PATH_ALLOWLIST_EXTRA` extends relative write prefixes (comma-separated):

```bash
export ASHLAR_PATH_ALLOWLIST_EXTRA="agent-sandbox/host_apps/,agent-sandbox/agents/workspaces/"
```

The variable can widen the allowlist but can no longer grant a governance prefix: an entry
such as `.ashlar/host_apps/` is dropped at construction and reported on
`PathAllowlist.RejectedExtras`. An earlier version of this guide prescribed exactly that
value. The floor refuses every write beneath it regardless, so the allowlist now refuses it
too rather than advertising a prefix nothing can write.

The policy still blocks:

- absolute paths outside the sandbox root
- path traversal (`..`)
- writes outside allowlisted prefixes

## Recommended project layout

The agent sandbox is a SIBLING of `.ashlar/`, never inside it. `.ashlar/` is the governance
directory — the admission ledger (`gates/`), the forge queue, runtime state — and the write
floor refuses it on every path. A sandbox rooted there would either be unwritable or would
hand a cycle the NuGet package cache and the host-app project root, which is build-time code
execution on the next `dotnet build`.

Create a per-project sandbox tree under repo root:

```text
agent-sandbox/
  agents/
    workspaces/    # agent-created artifacts and generated work trees
  tools/
    bin/           # third-party tools/SDKs installed for this project
    cache/         # npm/nuget/pip package caches
  host_apps/
    projects/      # host-app project data staged for agent workflows
    cache/         # host-app package/import caches
    runtimes/      # optional host-app runtime payloads
  logs/
  tmp/
```

Bootstrap helper:

```bash
bash scripts/sandbox/init-agent-sandbox.sh
```

`agent-sandbox/` is gitignored, like `.ashlar/`.

## Host-bound applications (generic)

Some tools cannot be containerized economically or by license.
For these, use a split model:

1. Keep editor/runtime host-installed by a human operator.
2. Keep project-specific dependencies/caches under `agent-sandbox/`.
3. Restrict agent-generated files to sandbox + approved code folders.
4. Trigger host apps through wrapper scripts that accept only sandboxed paths.

## Operating modes

### Mode A: Fully containerized workers

- Use container execution for generic build/test tasks.
- Mount only sandbox directories into container.

### Mode B: Hybrid host workers (host-app capable)

- Use host workers for tool/app operations that cannot be containerized by default.
- Keep strict path allowlist + sandbox-rooted caches.
- Prefer dedicated OS user account for agent processes.

## Minimal hardening checklist

1. Run agent daemons as non-admin user.
2. Set `ASHLAR_SANDBOX_ROOT` in daemon environment, to `<repo>/agent-sandbox` — a sibling of
   `.ashlar/`, never inside it.
3. Optionally set `ASHLAR_PATH_ALLOWLIST_EXTRA` for additional project-local prefixes. A
   governance prefix (`.ashlar/`, `scripts/`, a build import) is dropped, not honoured.
4. Route all temp/cache dirs (`TMPDIR`, package caches) to `agent-sandbox/tools/cache` and
   `agent-sandbox/host_apps/cache`.
5. Keep network egress rules narrow for worker nodes where possible.
6. Audit tool calls and denied writes; log `PathAllowlist.RejectedExtras` at startup so a
   dropped prefix is visible rather than silently narrowed.

## Example daemon env

```bash
export ASHLAR_SANDBOX_ROOT="$PWD/agent-sandbox"
export ASHLAR_PATH_ALLOWLIST_EXTRA="agent-sandbox/host_apps/,agent-sandbox/agents/workspaces/"
export TMPDIR="$PWD/agent-sandbox/tools/cache/tmp"
export NUGET_PACKAGES="$PWD/agent-sandbox/tools/cache/nuget"
export npm_config_cache="$PWD/agent-sandbox/tools/cache/npm"
```

Then run:

```bash
dotnet run --project application/src/Ashlar.CLI -- background-agent daemon --duration 2h
```
