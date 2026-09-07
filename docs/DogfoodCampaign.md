# Automated dogfood campaign

Ashlar is a developer tool. The automated dogfood campaign is how the tree
proves that to itself: a **release manager** dispatches specialist **sub-agents**,
each specialist **must report back**, and silence is a blocker.

This is Ashlar on Ashlar. The campaign is a real `BackgroundAgents:Agents`
document (`docs/background-agents/examples/dogfood-campaign.json`) loaded by
the same config loader the daemon uses. The specialists are deterministic
runners in `Ashlar.BackgroundAgents.Campaign`. Cursor personas under
`.cursor/agents/` can drive the same lanes when a human (or parent agent)
wants a second pair of eyes.

## Command

The campaign (and every `make dogfood-*` target) runs **inside the repo's
dev/test container**. The .NET SDK is the container's SDK — do not install
one on the host. `scripts/run-in-devcontainer.sh` is a no-op when you are
already inside that container (Cursor / VS Code Dev Containers, or a
previous `devbox.sh` session).

```bash
# Fast: docs-drift + dev-tool surface + cheap regression slice
make dogfood-campaign
# same thing:
bash scripts/run-dogfood-campaign.sh

# Full: also runs scripts/run-cert-gate.sh --fast (still in the container)
make dogfood-campaign-full

# Already inside the container, you can call the CLI directly:
dotnet run --project application/src/Ashlar.CLI -- dogfood campaign --lane DocsDrift
```

Requires Docker. Image: `.docker/Dockerfile.devtest` (devcontainer
`dotnet:10.0-noble` plus the ASP.NET Core 8 runtime). Built by
`scripts/ensure-devtest-image.sh` on first use.

Exit `0` only when every specialist reported `Pass` to the release manager.

## Specialists

| Agent id | Role | Lane | What it reports |
|----------|------|------|-----------------|
| `docs-drift` | `docs-auditor` | DocsDrift | Stale `apps/release-manager/` claims, unpublished VERSION pins, leftover `<!-- verify:` markers, missing campaign docs |
| `regression` | `tester` | Regression | Cert-gate / dogfood surfaces still exist; fast slice runs `CampaignAgentSetConventionTests`; `--full` runs `scripts/run-cert-gate.sh --fast` |
| `dev-tool` | `dev-tool-auditor` | DevTool | CLI campaign + `ashlar new brick`, authoring docs, packable tool, consumer template |

The release manager (`Role: release-manager`) does not run a lane of its own.
It aggregates reports, publishes one observation per specialist plus its own
verdict, and writes:

- `.ashlar/dogfood-campaign/report.json`
- `.ashlar/dogfood-campaign/report.md`
- `.ashlar/dogfood-campaign/observations.jsonl`

## Fail-closed rules

- A specialist that does not report is `Error`, not a skip.
- A crashed specialist is recorded as `Error`.
- Missing `ci/published-version` is a docs-drift blocker. That file is the
  nuget.org pin (currently `0.1.2`). Repo `VERSION` may read ahead of it;
  docs must not tell consumers to install the unpublished number.
- Historical mentions of `apps/release-manager/` are allowed when the line
  records the extraction, or when the path is listed in
  `docs/background-agents/dogfood-campaign-doc-exceptions.tsv`.

## Cursor sub-agents

Parent agents should delegate — do not audit every lane in one brain:

- `.cursor/agents/release-manager.md` — coordinator
- `.cursor/agents/docs-drift-auditor.md`
- `.cursor/agents/regression-auditor.md`
- `.cursor/agents/dev-tool-auditor.md`

Playbook: `.cursor/skills/dogfood-campaign/SKILL.md`.

Each Cursor specialist runs `ashlar dogfood campaign --lane <Lane>` (or the
equivalent `dotnet run` form), then returns the JSON report plus a short
blocker list to the release manager. The manager reconciles and, if needed,
asks for fixes and a re-run.

## Tests

| Test | What it pins |
|------|----------------|
| `ReleaseManagerCoordinatorTests` | Pass / fail / silence / crash aggregation + observations |
| `DocsDriftLaneRunnerTests` | Stale path and unpublished pin detection |
| `DevToolLaneRunnerTests` | Developer-tool surface |
| `RegressionLaneRunnerTests` | Fast vs full commands |
| `CampaignAgentSetConventionTests` | Shipped JSON is a real agent set with `ParentId` links |
| `DogfoodCampaignTests` | Campaign runs against this repository and all three specialists report |
| `DogfoodCampaignCommandTests` | CLI registers `dogfood campaign` |
