# How a gate goes quiet

**Read this before you add a gate, and before you believe one.**

`docs/CiGateInventory.md` says what exists. `ci/cert-gate-assertions.md` says what the required
check carries. This file says how a check in this repository has stopped answering while still
looking like it was — because that has happened often enough to be a pattern, and because none of
these failures announce themselves. Every entry below is something that actually happened here, with
the fix that closed it.

The shape is always the same: **a green result, or no result at all, from something that measured
nothing.** A red gate is a good day. These are the other kind.

---

## 1. The workflow file is invalid, so the run has no jobs — and posts no check

The loudest one, and the most invisible.

If GitHub cannot validate a workflow file, it still creates a run. The run is marked failed, it
contains **zero jobs**, and in the API its `name` is the file path rather than the workflow's own
`name:`. **No check run is posted to the pull request.** `gh pr checks` lists nothing, the PR looks
clean, and if the workflow is advisory and path-filtered nobody was looking at it anyway.

`runtime-portability-gate.yml` and `dogfood-continuous-proof.yml` each sat like that from the day
they were written — seven runs apiece — while three CHANGELOG entries described what the portability
lanes *executed*. Two unrelated causes:

- `${{ runner.temp }}` in a **job-level** `env:` block. The `runner` context is available from inside
  a step and nowhere above one.
- A multi-line `git commit -m` inside a `run: |` whose continuation lines start at column 0, which
  terminates the block scalar.

**Closed by** `scripts/ci/verify-workflow-context-availability.sh`, which runs in `shell-lint`
(unfiltered, required). It fails on any `runner.`/`steps.`/`job.`/`env.` reference above a step
(`jobs.<id>.outputs` excepted — that is evaluated after the steps run) and on any workflow file it
cannot parse.

**How to check by hand:**

```bash
gh api "repos/<owner>/<repo>/actions/runs?per_page=100" \
  --jq '[.workflow_runs[] | select(.name | startswith(".github/"))] | group_by(.name) | map({wf: .[0].name, n: length}) | .[]'
```

A workflow whose run `name` is a path has never started.

---

## 2. The gate is not a required check, so it goes red on `master` after the merge

Nothing blocks, so the first red run is on the default branch, where no one is watching a
pull request.

#594 added 120 lines to `src/Ashlar.Core.Application` with its tests in `Ashlar.Tests.Infrastructure`.
`kernel-coverage` measures `Ashlar.Core.Application` against a line floor using **only** the
`Ashlar.Tests.Application` run, so a fully tested type counted as uncovered, the ratio fell from
above 67% to 66.41%, and the gate went red on `master`.

**Closed by** #597: the tests moved to the project
`docs/architecture/TestingStrategyTracking-v1.md` already assigned them to, and
`scripts/ci/pr-testing-strategy-gate.sh` now fails a PR that *adds* a file under
`src/Ashlar.Core.Application/` with no change under `src/Ashlar.Tests.Application/`.

**The habit this needs:** after merging, look at the non-required gates on `master`, not only the
five requireds on the PR.

---

## 3. The `paths:` filter does not include the thing the lane runs

The job exists. It is correct. It never fires, which is indistinguishable from healthy.

The trim/AOT lane referenced `Ashlar.Certification.State` and read
`transition-entry-hashes.golden.json`, and neither was in its filter. The Mono lane's probe lives at
`scripts/ns20-canonical-bytes-probe.sh`, not under `scripts/portability/`, so the filter that
covered its siblings would not have covered it.

**The habit this needs:** when you add a lane, list every file it *reads* — not only the file it
lives in — and put them all in the filter. Then change one of them and watch the workflow fire.

---

## 4. The check asserts on an exit code instead of on output

A consumer that exits 0 having checked nothing is a passing consumer.

The first version of the trim/AOT lane trusted `$?` from a published binary. So did an earlier
consumer verification. Both were green while measuring nothing.

**The rule, now applied throughout:** a script-backed check re-reads what the thing it ran
*printed* — a result line, an exact assertion count, and each assertion by name. The name list is
deliberate duplication: it is what makes a **deleted** assertion a failure rather than a shorter
green run. See `scripts/portability/net9-probe.sh`,
`scripts/verify-packed-certification-trust-behavior.sh`, and
`scripts/ns20-canonical-bytes-probe.sh`.

Include a **positive control** wherever the interesting assertions are all refusals. A component
that refuses everything passes a suite of refusal assertions.

---

## 5. The check points at something that does not exist, and calls that a finding

`dogfood-continuous-proof.yml` read
`src/Ashlar.Infrastructure/Certification/CertificationVerifyOptions.cs`. That path has never
existed; the type is in `Ashlar.Certification.Contracts`. The step always took its not-found branch,
the canary sweep was always skipped, and the workflow wrote a `GAP` row saying `Strict` was not
ready — while `master` had required the signature for weeks.

It was wrong in the direction that looks responsible, which is why it survived: the row read like a
finding rather than a fault.

**The rule:** *a fault in the check is not a fact about the product.* A missing input is a hard
failure of the step, never a recorded verdict. Conflating the two writes false statements into
whatever the check feeds — here, a ledger that gates a marketing claim.

---

## 6. The check skips itself when a dependency is missing

`scripts/rc-gate-tier-e.sh` treats PyYAML as optional and returns `None` when it is absent. For a
report that is a reasonable fallback. For a **gate** it is the same failure as everything else on
this page: an environment without the dependency produces a pass.

**The rule:** a gate exits non-zero when it cannot run, and the workflow installs what it needs.

---

## 7. The convention test has only one of its two facts

An inventory-freezing test needs **both**: a new offender fails, **and** a stale row fails. With only
the first, the inventory rots into a list of things that used to be true and the test still passes.
With only the second, the next offender walks in.

"One store fixed, the rest not" has happened three times in LiteDB alone (#586, #591, #594). The
durable fix each time was a frozen inventory with both facts, not N hand edits.

---

## 8. The verifier's lens cannot see the defect, so it refutes

An adversarial verifier refuted a real flake because its lens demanded *a named concurrent writer*.
A sleep-then-assert defect has no named writer by construction, so it was unrefutable in the wrong
direction. The verifier's own conclusion said so: *"the only remaining failure mode is X, which does
not fit my lens."*

**The rule:** when a verifier's conclusion contains that sentence, the correct output is a **lens
escalation, not a refutation.** Two lens rules that sweep lacked:

- A fixed sleep standing in for a synchronization primitive, before a **positive** assertion, is a
  defect regardless of whether a named concurrent writer exists. (And a `Task.Delay` continuation is
  dispatched on the thread pool's *high-priority* path, so "work queued before the delay has
  finished by the time it completes" is false.)
- A premise validated only on the verifier's own OS is not evidence about the lane reporting the
  flake. **"Measured on one OS" means "not yet measured."** Bitten three times: Windows-only
  refutations in a flake sweep, a macOS `FileSystemWatcher` crash, and LiteDB Direct mode — which
  throws `IOException` on Windows and *silently corrupts* on Linux.

---

## 9. The test compiles the source; the user compiles the package

Every certification test in this repository compiles `src/`. A consumer compiles a `.nupkg`. Nothing
compared them, so "`Default` and `Strict` are fail-closed" was a claim about the source tree,
asserted nowhere about the thing people install.

The release workflow verified a great deal about the packages — that they exist, that their SHA-256
matches the manifest, that a sample restores and builds against them, that nuget.org serves the bytes
that were pushed — and **none of it could tell whether the verifier inside them still refused
anything.**

**Closed by** #595: `docs/samples/CertificationTrustConsumer` takes a `PackageReference`, never a
`ProjectReference`, and runs in `security-gate` plus twice in the release workflow (before every
push, and against nuget.org afterwards).

**The rule:** *auditing the source is not auditing the product.* The same applies to an assembly you
verified and a different build of it that you load — see the consumer-template boot guard, which
hashes the gate-emitted DLL and then executes its own `ProjectReference` build.

---

## 10. The whole gate is switched off at once

This repository's demonstrated response to a red required check is a mute: eight workflows were
deleted or de-triggered after going red (`docs/CiGateInventory.md`, Pruning 2026-08-16). Every
merge-blocking convention lives in `cert-gate`, so **one branch-protection toggle removes all of them
simultaneously.** That concentration is deliberate and it is also the largest single risk here.
`ci/cert-gate-assertions.md` exists so that a returning owner, facing a red gate with the toggle one
click away, can see what they would be turning off.

---

## 11. The failure is in attempt 1, and every default view shows attempt 2

A re-run does not replace a run. It adds an attempt — and `gh run view --log`, `gh run view --json
jobs` and `/runs/<id>/jobs` all return only the LATEST one. A red job that someone re-ran is
therefore invisible to every sweep written the obvious way, and the run itself reports
`conclusion: success`.

`FileSystemEventSourceTests.SubscribeAsync_FileCreated_EmitsEvent` killed the macOS test host in
run 34573927779 **attempt 1** (job 103182128289): the Blame collector's 2-minute inactivity line,
`Test host process crashed`, the test named as the one in flight, a `Sequence_*.xml` emitted, and
~1900 already-recorded results discarded. The run was re-run; attempt 2 passed in 254 ms. #601's
refutation — and the CHANGELOG text shipped with it, which says the report "is not reproduced and
its premise does not hold" and cites the passing job, the 254 ms, and the absence of a sequence
file — was written entirely against attempt 2. The same blind spot hid a second sighting
(run 34374416444 attempt 1, job 102544517994), so the sweep over "the last 60 gate runs" that
backed the refutation could not have found either one.

This is section 8 again — a lens that cannot see the defect refutes it — with a new way to acquire
the lens. `docs/production-readiness/KernelCoverageGate-Findings.md` already records this
repository losing an attempt-1 failure the same way.

**How to check by hand:**

```bash
gh api repos/<owner>/<repo>/actions/runs/<id> --jq .run_attempt
gh api repos/<owner>/<repo>/actions/runs/<id>/attempts/1/jobs \
  --jq '.jobs[] | {name, conclusion}'
```

**The rule:** a sweep of CI history enumerates attempts. A run whose `run_attempt` is greater than
1 has a history the default endpoints will not show you, and a refutation built on what they show
is a statement about the re-run, not about the report.

---

## Before you trust a gate

1. Has it ever produced a run **with jobs in it**? (Section 1.)
2. Did it run on the last commit that should have triggered it? (Section 3.)
3. Does it assert on output, with a positive control? (Section 4.)
4. Does a deliberate break make it fail? **Mutate and measure** — revert the fix, keep the test, and
   watch it go red. This repository has shipped a fix that did not fix the thing twice.
5. If it is not required, is it green on `master` right now? (Section 2.)
6. If you are refuting a report from CI history, did you enumerate **attempts**, not just
   runs? (Section 11.)

## Before you record a premise

A note in a queue or a design doc saying "X is broken" ages badly. Check it before you build on it.
An example from this very file's research: an open item read *"today, tagging a test `Category=Stress`
is the same as disabling it."* Nothing in the repository filters on that trait in either direction,
and `kernel-coverage` runs both `Ashlar.Tests.Infrastructure` and `Ashlar.Tests.Application` with
filters that do not exclude it — so the Stress-tagged classes do run. What is actually true is
narrower: **no lane runs them as a distinguishable lane**, so a Stress failure is not attributable
and nothing reports on stress as a category. That is still worth fixing. It is not what the note
said.
