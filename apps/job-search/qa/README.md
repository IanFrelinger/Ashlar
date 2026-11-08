# Studio QA: UI tests and QA agents with their own screens

Two kinds of browser test, both run against a **synthetic** studio. A synthetic studio has made-up personas, fake
Google, stub job boards and stand-ins for both model tiers.

- **The UI suite** (`ui/`) is deterministic Playwright tests of the main flows. They fail on any regression.
- **QA agents** (`agents/`) each get their own screen: a 1280×800 Chromium window of their own, recorded to video.
  Each works through a *charter*, a persona and a goal, the way a job seeker would, and files findings by severity.
  They run in one of two modes:
  - **claude**: Claude drives the screen through the computer toolset (`computer_toolset_20260801`), looking at
    screenshots and clicking and typing like a person. It explores, so it finds what nobody scripted.
  - **scripted**: the same screen, recording and report, but the steps come from `agents/scripts/<charter>.json`. It
    spends no tokens and runs on every pull request.

**QA never touches real data.** Screenshots go to an external model, so every entry point first reads `/api/meta`
and refuses unless the studio reports `synthetic: true` (`lib/studio.cjs`, tested in `lib/studio.test.cjs`). The
browser context is locked to the studio's own origin; requests anywhere else are blocked and counted in the report.

## Run it

```bash
cd apps/job-search/qa
npm ci && npx playwright install chromium          # once

# A synthetic studio: the image, hardened as in production (what CI does)...
./serve-image.sh jobsearch-studio:local 8080
# ...or the app from source: dotnet run --project ../src/JobSearch.Host -- serve --synthetic --urls http://127.0.0.1:8080

export STUDIO_URL=http://127.0.0.1:8080
npm run selftest                                    # the harness itself: no studio, no tokens
npm run ui                                          # the UI suite
npm run agents:scripted                             # every charter, scripted (no tokens)
ANTHROPIC_API_KEY=... npm run agents                # every charter, driven by Claude (spends tokens)
node agents/run.cjs --charters forge-new-step,privacy-rights --parallel 2 --max-steps 30
```

`serve-image.sh` replaces its container and data on every call. Run it again before a second suite, so that every
suite starts from the same state. The `privacy-rights` charter deletes a persona, and the runner schedules charters
marked `destructive` after the others.

| Variable | Default | Meaning |
|---|---|---|
| `STUDIO_URL` | `http://127.0.0.1:5080` | The synthetic studio |
| `QA_MODE` | `claude` | `claude` or `scripted` (or `--mode`) |
| `QA_MODEL` | `claude-opus-5-5` | The model that drives the agents |
| `QA_EFFORT` | `high` | Its effort setting |
| `QA_PARALLEL` | `3` | Agents at once, each with its own browser |
| `QA_MAX_STEPS` | `40` | Turns per agent, unless the charter sets `maxSteps` |
| `QA_FAIL_ON` | `blocker,major` | Severities that make the run exit 1 (the release gate) |
| `QA_HEADED` | unset | Show the browsers |

Exit codes of `agents/run.cjs`:

| Code | Meaning |
|---|---|
| 0 | No blocking findings |
| 1 | Blocking findings |
| 2 | The target is not synthetic |
| 3 | Claude mode without a key |
| 4 | No charter selected |
| 5 | The runner crashed |

## The report

`out/agents/<time>/report.html` (or `--out DIR`), next to `report.json`. It has one section per agent:

- persona, mode and outcome;
- the findings table: severity, title, steps to reproduce, expected, actual;
- every screen the agent saw, and a video of the session;
- the agent's own short progress notes.

`report.json` carries the same data plus `blocking`, the findings at a gating severity.

Severities, as the agents are told:

| Severity | Meaning |
|---|---|
| `blocker` | A user cannot reach the charter's goal, or data is lost or exposed. |
| `major` | The goal needs a workaround, or something is clearly wrong. |
| `minor` | Cosmetic or confusing. |
| `note` | A suggestion. |

## Writing a charter

`agents/charters/<id>.json`:

```json
{
  "persona": "Jordan Rivera, a data engineer (made up)",
  "email": "jordan.rivera@example.com",
  "start": "#/designer",
  "goal": "What the persona is trying to get done, in their words.",
  "checks": ["Specific things to look at along the way."],
  "maxSteps": 40,
  "destructive": false
}
```

`email` must be one of the synthetic personas (`GET /api/meta` lists them). Give every charter a scripted twin in
`agents/scripts/<id>.json`, so that pull requests exercise it without tokens. The step types are listed at the top of
`agents/scripted-agent.cjs`: `click`, `fill`, `check`, `select`, `goto`, `reload`, `waitFor`, `wait`, and the
`expect*` assertions. A failed expectation becomes a finding with the step's severity; a step that cannot be done is a
blocker.

## How it fits the release

[RELEASE.md](../docs/RELEASE.md). In short:

- every studio pull request runs the self-test, the UI suite and the scripted fleet against the image it built;
- every `studio-v*` tag also runs the Claude fleet;
- the release ships the image those agents tested, and only if no finding is blocker or major.
