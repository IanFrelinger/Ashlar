export const meta = {
  name: 'verify-lane',
  description: 'Adversarially verify one pushed SPEC-007 PR 4 lane branch through four lenses, then synthesize a repair list',
  phases: [
    { title: 'Verify', detail: 'records, design, write-down, mutation evidence' },
    { title: 'Synthesize', detail: 'dedupe into one repair list' },
  ],
}

// args: { lane, branch, head, body, attack, records, design_sections, extra }
const SP = '/tmp/claude-0/-home-user-Ashlar/a81481a5-34f6-5b00-a61f-74b03c502c7b/scratchpad'
const L = args.lane
const BR = args.branch
const HEAD = args.head

const COMMON = `
You verify SPEC-007 PR ${L} on branch ${BR} at head ${HEAD} (Ashlar repo). Your job is to REFUTE the lane's claims; a claim counts only if your refutation fails.
Setup: clone fresh from GitHub into your OWN directory: \`git clone -q https://github.com/IanFrelinger/Ashlar ${SP}/v-${L}-<lens> && git -C ${SP}/v-${L}-<lens> checkout -q ${HEAD}\` (verify \`git rev-parse HEAD\` == ${HEAD}; if the branch moved, say so and verify ${HEAD} anyway). Never edit /home/user/Ashlar or any c-* clone. Never run dotnet on the host: builds and tests only through \`bash scripts/test-in-container.sh --repo ${SP}/v-${L}-<lens> ...\` (committed state; never pipe through head; save logs under ${SP}/vlogs-${L}/). Repo gates run on the host: \`PATH=${SP}/sc-py/bin:$PATH bash scripts/ci/run-repo-gates.sh\`.
Inputs: the PR body draft ${args.body}; the lane's attack list ${args.attack} and records checklist ${args.records}; the design ${SP}/ws/DESIGN-4-final.md (${args.design_sections}; owner answers at the end override §3.A); the phase B handoff ${SP}/ws/handoff.md §3 (decisions, 4.4 obligation (d)); ${SP}/ws/pr-4.4-body.md and pr-4.6-body.md for what 4.4/4.6 merged. Phase B superseded: D9 also lists Ashlar.Orchestration; D31 amended (no record for the ns2.0 refusal); Detach() is RunDetached(Action). Owner decision 2026-10-06: an open, unreported read scope decides at SystemHigh for every tool. ${args.extra || ''}
Every finding cites file:line at ${HEAD} and quotes the evidence (a diff hunk, a test name and count, a grep). Severity: blocker (a write-down, a false claim in the body, a missing required record, a red check), major (a design deviation not recorded, a twin that does not test what it claims), minor (wording, nits). Default to reporting; do not soften. Return the structured result; also write your full notes to ${SP}/vlogs-${L}/<lens>.md.`

const SCHEMA = {
  type: 'object',
  properties: {
    lens: { type: 'string' },
    verdict: { type: 'string', enum: ['pass', 'repair'] },
    findings: {
      type: 'array',
      items: {
        type: 'object',
        properties: {
          severity: { type: 'string', enum: ['blocker', 'major', 'minor'] },
          title: { type: 'string' },
          file: { type: 'string' },
          line: { type: 'number' },
          evidence: { type: 'string' },
          fix: { type: 'string' },
        },
        required: ['severity', 'title', 'file', 'evidence', 'fix'],
      },
    },
    verified_claims: { type: 'array', items: { type: 'string' }, description: 'claims from the body you tried to refute and could not, with how' },
    not_checked: { type: 'array', items: { type: 'string' } },
    runs: { type: 'array', items: { type: 'string' }, description: 'each container/host run with its verbatim summary line' },
  },
  required: ['lens', 'verdict', 'findings', 'verified_claims', 'not_checked', 'runs'],
}

phase('Verify')

const records = () => agent(`${COMMON}
LENS: records and evidence (lens dir suffix "records"). Do NOT run containers (the other lenses do); you may run the repo gates on the host once. Check: (1) every number in the PR body (test counts, cert-gate total, TSV rows/occurrences, floors, Certification file/entry counts) against the tree at ${HEAD}: recompute each with git ls-files / awk / grep and show the command and result; (2) every item in ${args.records}: present, correct, and the text now true; (3) ci/cert-gate-assertions.md: a new row per new blocking convention test, and the count paragraph; (4) docs/knowledge-graph.{json,md} regenerate byte-identical (run python scripts/knowledge-graph/build-knowledge-graph.py in your clone after git add -A and check git status --porcelain is empty — report the exact diff if not); (5) SPEC-007 status line has "(this PR)" placeholders only where intended and the 4.4 line carries de41a8ac if this is 4.3; the decisions-log row if this is 4.5; (6) CHANGELOG Unreleased entry; (7) .github/workflows/full-platform-readiness-gate.yml READINESS_PATHS vs on.push.paths still equal as sequences if touched; (8) [coordinated-integration] in the body iff non-test code under application/ changed (list the files); (9) the mutation table: every summary line is verbatim from a mutation-check.sh run (cross-check against ${SP}/mut*/ logs and SUMMARY files if present) and says KILLED with real counts; INVALID lines are explained; (10) "what was not observed failing" is stated. Return verdict repair on any false number or missing record.`, { label: `records:${L}`, phase: 'Verify', schema: SCHEMA })

const design = () => agent(`${COMMON}
LENS: design conformance and scope (lens dir suffix "design"). Do NOT run containers. Diff ${HEAD} against its merge-base with origin/master (\`git diff $(git merge-base ${HEAD} origin/master) ${HEAD}\`), file by file. Check: (1) the §4 row for this PR is implemented exactly — list each design bullet as implemented / deviated (recorded in the body?) / missing; (2) nothing outside the row (scope creep) except the carry-ins the handoff names; (3) every owner decision and §3.B default the row relies on is respected (cite); (4) the 4.4 frame rule and obligation (d) for any EgressSubject.Enter/BeginRead/RunDetached touched or added (using on the entering flow, disposed in order, no yield return span, no async helper whose frame outlives it, work created and started inside); (5) public API changes are in PublicAPI.Unshipped.txt with XML docs; Abstractions remarks updated where semantics changed; (6) fail-closed defaults: any new branch that could allow (return Host, skip a decision, swallow an exception, default to report) is justified; (7) tests: each twin asserts the behaviour the body claims (read the test bodies; a twin that passes trivially or asserts a weaker property is a major finding); red-first evidence exists for behaviour changes; (8) commit message trailer (Claude Fable 5.1 + Claude-Session) on every commit of the branch; no model identifier anywhere in the tree. Return verdict repair on any unrecorded deviation or weak twin.`, { label: `design:${L}`, phase: 'Verify', schema: SCHEMA })

const writeDown = () => agent(`${COMMON}
LENS: code / write-down (lens dir suffix "code"). You MAY run containers: at most ONE container run at a time, and keep it to targeted filters (the full cert-gate is the lane's job; three other lanes share 4 CPUs). Take ${args.attack} as your attack list. For each item: trace the call path at ${HEAD}, decide refuted (the PR closes it — cite the code and the twin) / open (still a path — show the sequence) / out of scope (recorded as a known limit or deferred to a named later PR in the body or SPEC-007). Then go beyond the list: look for any way labelled data (or, for 4.10, any AG/SW network or inbound path) still escapes a correct decision after this PR — new branches the implementation added, exception paths, concurrency (Task.Run, timers, async iterators, ConfigureAwait), partial failures (dispose order, exceptions in Dispose), configuration edge cases (null sections, empty strings, case), and ns2.0 vs net8.0 differences. Where a twin would settle it cheaply, write a probe test in your clone, commit it locally (never push), and run it in the container with a filter; report counts. Any open item that is not recorded as a known limit is a blocker. Return verdict repair on any open item.`, { label: `code:${L}`, phase: 'Verify', schema: SCHEMA, effort: 'high' })

const mutation = (prev) => agent(`${COMMON}
LENS: mutation evidence (lens dir suffix "mut"). The code lens has finished its container runs (its verdict: ${prev ? prev.verdict : 'unknown'}; its blockers: ${prev ? JSON.stringify(prev.findings.filter(f => f.severity === 'blocker').map(f => f.title)) : '[]'}). You MAY run containers, one at a time. (1) Pick the TWO mutations from the PR body's table that guard the most important behaviour and re-run them yourself with \`bash scripts/mutation-check.sh --repo ${SP}/v-${L}-mut --ref ${HEAD} --file ... --old ... --new ... --filter ... --id ...\` (read the script's usage first; reproduce the lane's --old/--new exactly from the body or ${SP}/mut*/ files); quote the summary lines verbatim and compare with the body's lines (same KILLED, same counts); a mismatch is a blocker. (2) Design ONE new mutation the body does not list that would break the PR's central guarantee silently (e.g. the handler skips evaluation on hop 2; the read frame is not pushed; the AG validator returns success), run it, and report whether the suite catches it; an uncaught one is a blocker. (3) Check that every behaviour change in the diff has at least one mutation or red-first twin in the body; list those without. Return verdict repair on any mismatch or uncaught mutation.`, { label: `mutation:${L}`, phase: 'Verify', schema: SCHEMA, effort: 'high' })

// Read-only lenses run in parallel with the container-using chain; the container users are serialized.
const results = await parallel([
  records,
  design,
  () => pipeline([null], writeDown, mutation).then(r => r[0]),
])
const codeChain = results[2]
// pipeline returns only the last stage's result; recover the code lens from its notes file through the synthesizer.
const lenses = [results[0], results[1], codeChain].filter(Boolean)
log(`${lenses.length}/3 lens results; blockers: ${lenses.reduce((n, r) => n + r.findings.filter(f => f.severity === 'blocker').length, 0)}`)

phase('Synthesize')
const synth = await agent(`You synthesize the adversarial verification of SPEC-007 PR ${L} (branch ${BR}, head ${HEAD}).
Lens results (structured): ${JSON.stringify(lenses)}. The code lens's full notes are at ${SP}/vlogs-${L}/code.md, the mutation lens's at ${SP}/vlogs-${L}/mut.md, records at ${SP}/vlogs-${L}/records.md, design at ${SP}/vlogs-${L}/design.md — read all four (the code lens's structured result is not in the list above; take it from code.md).
Produce ${SP}/vlogs-${L}/REPAIR-LIST.md: (1) deduplicated findings, blockers first, each with file:line, evidence, the fix, and which lens found it; drop findings that another lens refuted with evidence (say which); (2) "Verified claims" — what the body says that the lenses could not refute, with how; (3) "Not checked"; (4) a one-paragraph integrator verdict: READY TO OPEN PR / REPAIR FIRST, and whether anything needs the OWNER (policy, security posture, compatibility, an accepted default) — list those separately and only if genuinely undecided (check SPEC-007's decisions log, the design owner answers, handoff §3 and the 2026-10-06 open-read-scope decision first). Do not run anything. Return the structured result with lens = "synthesis".`, { phase: 'Synthesize', schema: SCHEMA, effort: 'high' })

return { lane: L, head: HEAD, lenses, synthesis: synth }
