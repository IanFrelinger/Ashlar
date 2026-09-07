---
name: dogfood-campaign
description: Run the automated Ashlar dogfood campaign. Specialist sub-agents report to the release manager to catch documentation drift, regressions, and developer-tool breakage. Use when asked to dogfood Ashlar, audit docs vs code, or verify the framework still works as a dev tool.
---

# Dogfood campaign

Ashlar is a developer tool. Validate it by running the in-tree campaign, not
by improvising a parallel checklist.

## Invariants

- The release manager is the only aggregator. Specialists report; they do not
  declare the campaign green on their own.
- Missing, crashed, or silent specialists are blockers.
- Do not weaken `docs/background-agents/examples/dogfood-campaign.json` or
  `ci/published-version` to get a green verdict.
- Do not publish, tag, or push packages without an explicit user instruction
  for that exact action.

## Workflow

1. Bind the run to one commit SHA (`git rev-parse HEAD`).
2. Launch these specialists in parallel via the Task tool (or run the CLI
   lanes if Task is unavailable):
   - `docs-drift-auditor`
   - `regression-auditor`
   - `dev-tool-auditor`
3. Each specialist must return a structured report (lane, verdict, findings,
   evidence). Prefer:

   ```bash
   bash scripts/run-in-devcontainer.sh \
     dotnet run --project application/src/Ashlar.CLI -- dogfood campaign --lane <Lane> --format-json
   ```

   Do not install a host SDK. The container image is the toolchain.

4. Reconcile as the release manager. Duplicate findings merge; disagreements
   are called out; silence is `Error`.
5. Fix verified in-repo blockers. Re-run only the affected lane, then the
   full campaign:

   ```bash
   make dogfood-campaign
   ```

6. Read `.ashlar/dogfood-campaign/report.json` and `observations.jsonl`.
   READY means every specialist reported `Pass` and the manager observation
   is `Info`.
