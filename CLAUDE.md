# Claude Code instructions — Ashlar

Read automatically by Claude Code at the start of every session in this repository.

## Working in this repository

- **Never run `dotnet` on the host.** Build and test only in the Linux devtest container, through
  `scripts/test-in-container.sh`. It needs a clone, not a worktree (`.git` is a file in a worktree).
- **Five required checks**, with `strict` and `enforce_admins` on master: `cert-gate`, `build-core`,
  `shell-lint`, `lychee (README + docs)`, `Readiness summary`. Every merge makes every other open PR
  BEHIND, so merges are serial; a day-one red blocks every PR in the repository. For multi-PR
  sweeps: land non-core-path PRs first, push one settled tip then wait (every tip update restarts
  Readiness). Full Platform Readiness does **not** push-trigger on `cursor/**` (PR only) — see
  `CONTRIBUTING.md` ("Landing several PRs…").
- `shell-lint` runs `scripts/ci/run-repo-gates.sh`, which **auto-discovers** `tests/scripts/*.test.sh`,
  `scripts/ci/test-*.{sh,py}`, `scripts/ci/verify-*.{sh,py}` and
  `scripts/knowledge-graph/verify-*-current.py` and runs each one **bare**, with no arguments. A script
  that needs arguments must be named outside those globs, or be added to the runner's `EXCLUDED` list
  with the reason.
- `docs/knowledge-graph.{json,md}` are **generated** and byte-compared inside `shell-lint`. Regenerate
  with `python scripts/knowledge-graph/build-knowledge-graph.py` **after** `git add` — the builder reads
  `git ls-files`, so an untracked new file is invisible to it. Mid-rebase KG conflicts:
  `bash scripts/knowledge-graph/resolve-rebase-conflict.sh`, then `git rebase --continue`.
- A new blocking convention test needs its row in `ci/cert-gate-assertions.md` in the same change.
- `.github/workflows/full-platform-readiness-gate.yml` holds the same glob list **twice** (`on.push.paths`
  and the `READINESS_PATHS` bash array). A cert-gate test asserts the two are equal as sequences.
- Readiness lanes are diff-conditional and a skipped lane counts as a pass, so a check placed inside one
  proves nothing on a diff that matches no readiness path. The run publishes a
  `Readiness verdict` annotation (`verified` / `partial` / `not-verified` / `failed`);
  `scripts/release/readiness-verdict-for-sha.sh` reads it.
- **A check that was never observed failing is not evidence.** Mutation-check every behavioural change:
  commit first, apply the mutation, prove it applied, watch the specific assertion go red with real
  counts, restore, prove `git status --porcelain` is empty, re-run green. Once you have committed,
  `scripts/mutation-check.sh` runs the apply, red, restore, porcelain and green steps in a clone of that
  commit, lists the red run's failing tests, and prints the verdict with the counts.

## Phases and handoffs (the owner's rule)

- **One thread per phase.** A workstream runs as a sequence of phases, and each phase runs in its own
  thread. A phase ends with **`/handoff <workstream> <phase>`**:
  1. push every in-flight branch;
  2. validate and audit the **whole project**: `scripts/phase-end/validate.py`, then the saved workflow
     `.claude/workflows/project-audit.js` (read-only, nine agents);
  3. write the handoff from `_handoff/phases/HANDOFF-TEMPLATE.md`, with the audit's project status in §1;
  4. publish it to the storage branch `claude/<workstream>-workspace` with `scripts/handoff-publish.sh`,
     with the validation and the audit attached;
  5. send it to the owner and notify them;
  6. stop.
- The next thread starts with **`/start-phase <workstream>`**:
  1. fetch the handoff with `scripts/handoff-fetch.sh`, or take the attached file;
  2. read it, and check it against the live repo, re-running `scripts/phase-end/validate.py` to see what
     changed since the handoff;
  3. restate the phase;
  4. execute it.
- The procedure, the naming, and why handoffs live on a storage branch rather than master:
  `_handoff/phases/README.md`.

Releases: `docs/RELEASE_RUNBOOK.md`. Product history and what is already done: `CONTINUITY.md`.
Release-readiness convergence: `_handoff/readiness/README.md`.
Security labels workstream (status, owner decisions, open questions): `docs/specs/SPEC-007-security-labels-and-reference-monitor.md`.
Budgeted autonomy workstream (the controller, its owner decisions log, what is unverified): `tools/autonomy-controller/README.md`.

## Multi-agent coordination (agent-bus)

Release and merge work is coordinated over the **agent-bus**: a GitHub issue used as a durable,
append-only channel between the agents working on Ashlar — Claude Code, **Grok Bot**, and the Cursor and
Codex agents when the owner assigns them work.

- **Channel and link**: `_handoff/bus/README.md`
- **Message format, the claim rule, task cards**: `_handoff/bus/PROTOCOL.md`
- **Command**: `/agent-bus` reads recent messages and drafts a reply
- **Identities**: `From: grok|claude|cursor|codex`. An agent posts under its own name, also when it posts
  from the owner's account.

### Before starting any work

Every agent, every time, before it builds or merges anything:

1. fetch and read the newest handoff of the workstream (`scripts/handoff-fetch.sh --workstream <name>`);
   nothing is built from a superseded handoff;
2. read the bus since its last post: open claims, asks addressed to it, blocks;
3. read the decisions log of the spec it works on;
4. post `Kind: claim` on the card or PR it is about to work on. Nobody else works on that slug until a
   `done`, or a `claim` whose body says `released`, follows.

Two agents built the same three PRs in October 2026 because one of them skipped steps 1 and 4.

### Decisions land in the log first

An owner decision exists when it is a dated row in the spec's decisions log on master, and not before.
Whoever receives one lands it as a row in a small records PR **before** the code that depends on it. An
agent never writes a row for a decision the owner did not make: a default taken because the owner has not
decided is an **integrator decision**, recorded in the PR body (and as a Known limit where it changes a
record) and listed in the next handoff as open.

### Task cards

A unit of work that can be handed between agents is a GitHub issue labelled **`agent-task`**, with the
acceptance criteria, the records it touches and the decisions-log rows it relies on. Its bus slug is
`card-<issue number>`. It is taken by a `claim` on the bus (plus self-assignment on the card), closed by the
`done` that names the merge SHA, or returned with a `claim` saying `released`. Cards are the board; the bus
is the log.

### Ownership

**Claude Code does all development, including merging.** Code, pull requests, **merges**, releases, tags
and `release*.yml` dispatches. Claude merges its own PRs once every required check is green; it holds a
merge only if Grok has posted a `block` on that PR first.

**When the owner assigns a card to the Cursor or Codex agent, that agent builds and merges it** and posts
its own `claim`, `handoff` and `done`. The evidence rules above bind every developing agent alike.

**Grok Bot does admin, materials, and audits each merge for drift,** whoever merged it. Stale docs or
records, counts and citations that no longer match the code, registries out of step with the workflows, and
claims a merge made false. Plus status summaries, issue hygiene and user-facing write-ups.

This supersedes the ownership rule in the bus issue's opening comment, which was written before the split
was settled.

### The loop

0. The developing agent posts `Kind: claim` on the card or PR.
1. When a PR is ready, it posts `Kind: handoff`, `About: pr-<number>` saying what changed, what was
   verified, and **which records and docs it touches**, so Grok can aim the drift audit.
2. It merges on green.
3. Grok posts audit findings as `Kind: ask`, `About: drift-<pr-number>`, one finding per bullet, with the
   file and line and what is now false.
4. The developing agent fixes them in a follow-up PR and closes each with `Kind: done` on the same slug.

### When to post

- `claim` before building a card or a PR; `handoff` when an owned PR is ready; `done` when it is merged,
  with the merge SHA.
- `block` if work is blocked by a missing credential, config or decision — with the exact ask.
- `ask` when you need something from another agent.
- Do not post routine implementation detail, and do not wait for approval to commit your own work.
