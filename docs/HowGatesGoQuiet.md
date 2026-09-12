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
file — was written entirely against attempt 2.

**Do not over-claim the remedy.** A second sighting carries the same signature
(run 34374416444, job 102544517994, ~1912 results discarded), and it is *not* an example of this
section: that run is `run_attempt: 1`, `conclusion: failure`, and its failing macOS job is listed
by `gh run view` and by `/runs/<id>/jobs` like any other. Enumerating attempts finds the first
sighting; it would not have found the second. Why the sweep over "the last 60 gate runs" behind the
refutation missed a plainly failed run is still unexplained — two blind spots were in play and only
one of them is described here, so a reader who adopts only the attempts remedy still has the other.

**And do not read the ordering.** The natural next move is to use the log lines to tell a
Blame-killed hang from a fast crash, with the inactivity line before the crash line as the
discriminator. That does not hold. The two lines come from streams GitHub interleaves: in job
103182128289 the crash line is stamped 07:38:14.7839330Z and the inactivity line 07:38:15.1106820Z
— crash first, in a run that was unambiguously a hang — and job 102544517994 is the same way
round. What discriminates is that Blame printed an inactivity line **at all**, together with the
`Sequence_*.xml`; where it sits relative to the crash line means nothing.

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

## 12. The convention test's lens is narrower than the rule it claims to enforce

Section 7 is about an inventory with only one of its two facts. This is the next one along: the
inventory has both facts, is honestly maintained, goes red when broken — and covers a fraction of
what its name says. It is the most comfortable failure on this page, because mutating it *does*
make it fail. You just mutated something inside the lens.

Three of these shipped together, in one change, in the same week:

- **A reflection scoped to the wrong container.** `Every_per_test_timeout_fits_inside_the_broad_sweep_blame_window`
  enumerated `typeof(TestTimeouts).GetFields(...).Where(f => f.IsLiteral)`. Real per-test deadlines
  are not all `TestTimeouts` fields: `[Fact(Timeout = 300_000)]` and `[Fact(Timeout = 600_000)]`
  are literals written at the call site, and the widest deadline in the suite was 600s while the
  check believed it was 480s. Every conclusion drawn from it — including the size of the window
  the same change chose — was off by that.
- **A scan scoped to one directory.** `No_other_file_in_this_project_builds_its_own_reference_set`
  used `SearchOption.TopDirectoryOnly` and two literal needles. A copy of the forbidden set one
  folder down passed, and so did the same set built through `AssemblyMetadata` rather than
  `MetadataReference`.
- **An inventory of exactly the files that were edited.** `The_broad_sweep_lanes_pass_the_checked_window_to_dotnet_test`
  listed `ValidationServiceAdapter.cs` and `CiCommand.cs` — which is where the fix had been
  applied. `make test-prod-style` runs byte-for-byte the same filter as the `ci verify` step, at
  the window the fix had just called a defect. Twenty-two lanes were outside the list.

The shape: **the fix defined the check's scope, so the check could only ever confirm the fix.**

**The rule:** a convention test states a rule about a population. Derive the population from the
thing itself — reflect over the real attributes, walk the whole tree, parse every invocation — and
give the derivation a positive control with a floor, so a population that collapses to nothing
fails instead of passing. Where you genuinely cannot derive it, say what the lens excludes *in the
test*, next to the assertion, so the next reader is not told a narrow fact in wide words.

**Open, in this shape, right now.** Deriving the lane population turned up three lanes whose
`--filter` selects **zero** tests in the project they run, so each passes having executed nothing:
`scripts/security-gate-tier-b.sh:11` and `scripts/application-gate-tier-c.sh:11` both run
`-f net8.0` while naming classes under `Tests/API` and `Tests/VirtualProduction`, which the csproj
compiles out on net8.0; and `scripts/compat-gate-tier-a.sh:11` names
`MeshTaskExecutionServiceTests`, which exists in no project — the real tests are
`MeshTaskExecutionServiceGapCoverageTests` in `Ashlar.Commercial.Tests.Fleet`. Not fixed here:
each needs a decision about which framework or project the step should have been running, and that
is a separate change from the window work these were found by.

## 13. The guard is written, it compiles, and it cannot fire

Sections 4, 7 and 12 are about a check whose lens is too small. This one is about a guard whose
lens is empty: the code is there, a reader finds it, the comment above it says what it protects
against — and there is no input that reaches it. Nothing goes red, because nothing was ever
measured. It is the hardest of these to see by reading, and the easiest to settle by running.

Both instances below came out of one review, in one file, in the certifier:

- **A `catch` for an exception the call does not throw.** `TryCreateReference` wrapped
  `MetadataReference.CreateFromFile` in `catch (BadImageFormatException)`, documented as "skips
  native libraries that share the managed extension on Windows". That factory is **lazy**: measured
  on Windows it returns a reference for a native `.dll`, and for a text file renamed `.dll`, without
  throwing anything — and `AssemblyMetadata.CreateFromFile` behaves the same. So nothing is skipped.
  The bad entry surfaces one layer down as `CS0009: PE image doesn't contain managed metadata`,
  once per compilation, **attributed to the source being compiled** — which in a certifier means a
  fault in the verifier recorded as a fact about someone's change (section 5, one level deeper). The
  eager form that does work is `new PEReader(stream).HasMetadata`, which throws on a non-PE file and
  reports `false` for a native one. What kept the dead `catch` from mattering was an unrelated,
  undocumented property of the input: the platform list it happened to read names no native DLLs.
- **An emptiness check standing in for a floor.** The same type refused when the framework half
  came back with **zero** entries, and its remarks said it "refuses rather than returning a partial
  set". Measured: two framework paths composed to three references and proceeded; one composed to
  two. Zero is not the interesting case — a trimmed deployment leaves a *subset* on disk, which
  passes an emptiness check and then judges every non-trivial proposal to be a compile error. The
  fix is a floor named as **assemblies**, checked against the composed result rather than against
  the input count, so it also catches an entry that was present and unreadable.

The shape: **a guard is a claim about an input, and an unexercised guard is an unverified claim.**

**The rule:** every guard gets an input that reaches it, from a test, with the guard's own condition
driven directly — and then **mutate the guard so it cannot fire and watch the test go red**. If you
cannot construct an input that reaches it, either the guard is unnecessary or the real precondition
is somewhere else and belongs written down. A framework-boundary API is worth one probe before you
trust its exception contract: lazy factories, `TryParse`-shaped methods that swallow, and readers
that defer I/O to first use are all places where the exception you catch is thrown somewhere your
`try` no longer covers.
## 11. The positive control cannot fail

Section 4 says to include a positive control wherever the interesting assertions are all refusals.
A control that is **arithmetically always true** satisfies that instruction and measures nothing,
and it is harder to spot than a missing control because the failure message reads exactly like a
real one.

`MeshTaskWriteRaceTests.The_sweep_leaves_a_renewed_lease_alone` raced the lease sweep against a
worker renewing the leases it was walking, asserted that no task was told BOTH "renewed" and
"invalid token", and carried:

```csharp
(reclaimed + renewed).Should().Be(
    SweepKeys,
    "the positive control: every seeded task must have been decided one way or the other.");
```

`reclaimed` and `renewed` are incremented in an `if`/`else` exactly once per element of a list
seeded with exactly `SweepKeys` entries, so that assertion is `tokens.Count == SweepKeys`. It
cannot fail. Both degenerate ends of the race then produced an empty contradiction list and a
green test: `SweepEnabled = false` — **the shipped default** — and an `ExtendLeaseAsync` that
refuses everything.

**Closed by** two deterministic uncontended phases that run before the race: one expired lease the
sweep must actually reclaim, and one live lease an extension must actually be granted on with its
token intact. Both were shown red under exactly those two mutations. The vacuous assertion is kept,
relabelled as the sanity check it is, so nobody re-promotes it.

**The habit this needs:** a control is only a control if you can name the state that makes it fail
and then produce that state. If the answer is "nothing could make this assertion fail", it is
decoration. Derive a control from the thing under test, never from the test's own arrangement.

---

## 12. The gate freezes the spelling of the fix, not the thing the fix does

An inventory test can be complete, have both its facts, drive its own classifier, and still be
checking a word rather than a property.

#602 wrapped every store-level read-modify-write in `LiteDbAtomic.Mutate`, and
`Every_inventoried_read_modify_write_opens_a_transaction` asked whether each inventoried member's
text **contained** `LiteDbAtomic.Mutate`. Hoisting the read and the transform ABOVE the call and
leaving only the write inside it satisfies that question exactly, and restores the lost update.
Measured in the container with that mutation applied to `LiteDbMeshTaskRegistry.UpdateAsync`: the
spelling-only version was **11 of 11 green**, and three of the four contended fleet facts went red —
so the window is real, and the guard could not see it.

The same PR did it again one layer up. #604's ports removed
`IMeshTaskRegistry.UpdateAsync(MeshTaskState)` and its sibling, and the durability claim was "no
unconditional overload is left, so the compiler finds any attempt to write the old shape". That is
the old **signature**, not the old shape: `UpdateAsync(task.TaskId, _ => task)` writes a
caller-held snapshot through the new port, compiles, and restores the measured loss of 313 updates
in 400. The repository's own test arrange shim is that exact expression.

**Closed by** checking the property instead of the word. Every read and every write on a collection
local must fall inside the *argument list* of one of the member's `Mutate` calls — bracket-matched,
with string literals and comments excluded, and the region finder driven by its own fact on five
fragments including a decoy empty transaction and the helper's name inside a comment. And every
transform handed to those ports must name its parameter and read it.

**The habit this needs:** after writing a convention test, write down the sentence it actually
asserts. If that sentence contains the name of the fix rather than the behaviour of the fix, a
refactor that keeps the name and drops the behaviour is green. Then mutate in exactly that
direction — keep the call, move the work — and watch.

---

## Before you trust a gate

1. Has it ever produced a run **with jobs in it**? (Section 1.)
2. Did it run on the last commit that should have triggered it? (Section 3.)
3. Does it assert on output, with a positive control? (Section 4.) Can you name a state in which
   that control FAILS, and produce it? (Section 11.)
4. Does a deliberate break make it fail? **Mutate and measure** — revert the fix, keep the test, and
   watch it go red. This repository has shipped a fix that did not fix the thing twice.
5. If it is not required, is it green on `master` right now? (Section 2.)
6. If you are refuting a report from CI history, did you enumerate **attempts**, not just
   runs? (Section 11.)
7. Does the check's lens cover the population its name claims, or only the files you just
   edited? **Mutate outside the fix, not inside it.** (Section 12.)
8. For each guard inside it: is there a test whose input reaches that guard, and does disabling
   the guard turn that test red? (Section 13.)
5. Write down the sentence the check asserts. Does it name the fix, or the behaviour of the fix?
   Then mutate in the direction that keeps the name and drops the behaviour. (Section 12.)
6. If it is not required, is it green on `master` right now? (Section 2.)

## Before you record a premise

A note in a queue or a design doc saying "X is broken" ages badly. Check it before you build on it.
An example from this very file's research: an open item read *"today, tagging a test `Category=Stress`
is the same as disabling it."* Nothing in the repository filters on that trait in either direction,
and `kernel-coverage` runs both `Ashlar.Tests.Infrastructure` and `Ashlar.Tests.Application` with
filters that do not exclude it — so the Stress-tagged classes do run. What is actually true is
narrower: **no lane runs them as a distinguishable lane**, so a Stress failure is not attributable
and nothing reports on stress as a category. That is still worth fixing. It is not what the note
said.
