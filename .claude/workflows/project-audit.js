export const meta = {
  name: 'project-audit',
  description: 'Phase-end audit of the whole project: area readers score Ashlar against its own definitions of done, a critic refutes, a synthesis writes the handoff status',
  whenToUse: 'Run by /handoff at the end of every phase, after scripts/phase-end/validate.py and before the handoff is written (.claude/commands/handoff.md). Read-only. args: {workstream, phase, date (YYYY-MM-DD), masterSha, validation (the validate.py JSON), previous (the previous phase\'s project-status.json, or null)}. Returns {status, critique, reports, inputs}; status.markdown is the "Project status" subsection of the handoff\'s section 1.',
  phases: [
    { title: 'Read', detail: 'one read-only reader per area, scored against the repo\'s own definition of done, with evidence' },
    { title: 'Critique', detail: 'refute the load-bearing claims, find gaps and cross-area conflicts' },
    { title: 'Synthesize', detail: 'the structured status and the handoff section, with the change since the previous phase' },
  ],
}

const A = args || {}
const WORKSTREAM = A.workstream || null
const PHASE = A.phase || '?'
const DATE = A.date || 'unknown date'
const MASTER = A.masterSha || 'origin/master'
const VALIDATION = A.validation || null
const PREVIOUS = A.previous || null

function validationLines(val) {
  if (!val || !Array.isArray(val.checks)) return 'No validation results were passed. Establish master\'s state yourself.'
  const s = val.summary || {}
  const head = `Phase-end validation (scripts/phase-end/validate.py) on master ${String(val.master_sha || '').slice(0, 12)}, ${val.generated_at || ''}: ${s.pass || 0} pass, ${s.warn || 0} warn, ${s.fail || 0} fail, ${s.error || 0} error.`
  return head + '\n' + val.checks.map(c => `- [${c.status}] ${c.title}: ${c.detail}`).join('\n')
}

const CONTEXT = `
Repository: the Ashlar checkout in the current working directory (a clone; GitHub repo IanFrelinger/Ashlar). Assess master at ${MASTER}, as of ${DATE}. Run 'git fetch origin' and read from origin/master if the working tree differs.
This audit runs at the end of phase ${PHASE}${WORKSTREAM ? ` of the workstream "${WORKSTREAM}"` : ''}, before its handoff is written. It covers the WHOLE project, not only that workstream.

ESTABLISHED FACTS. A deterministic validation already ran; treat these as given, and report a conflict if you find one wrong:
${validationLines(VALIDATION)}

STRICT RULES: read-only. Do not edit files, commit, push, post comments, open or close issues or PRs, dispatch workflows, or run dotnet. You may use git, gh (read commands only: issue view/list, pr view/list/diff/checks, run list/view, workflow list, api GET), grep and file reads, and the repo's read-only scripts (for example scripts/release/readiness-verdict-for-sha.sh). Cite every claim with a file path (and line where useful), an issue or PR number, a commit SHA, or a command's output. If a doc's claim conflicts with the code or the GitHub state, report the conflict rather than trusting the doc. Distinguish "done on master" from "claimed done in a doc". Say plainly what you could not verify.`

const AREA = {
  type: 'object',
  properties: {
    area: { type: 'string' },
    definition_of_done: { type: 'string', description: 'what "complete" means for this area according to the repo docs, with source paths; say so if no definition exists' },
    status_summary: { type: 'string' },
    done_items: { type: 'array', items: { type: 'object', properties: { item: { type: 'string' }, evidence: { type: 'string' } }, required: ['item', 'evidence'] } },
    remaining_items: { type: 'array', items: { type: 'object', properties: {
      item: { type: 'string' }, size: { type: 'string', enum: ['small', 'medium', 'large', 'unknown'] },
      blocker: { type: 'string', description: 'what or who blocks it: owner decision, owner action, external, none' },
      evidence: { type: 'string' } }, required: ['item', 'size', 'blocker', 'evidence'] } },
    owner_only_steps: { type: 'array', items: { type: 'string' } },
    completion_estimate: { type: 'object', properties: {
      label: { type: 'string', description: 'for example "4.5 of 8 PRs", or "no defined end state"' },
      basis: { type: 'string', description: 'how it was counted: items done against items listed, criteria met against criteria, and so on' },
      units: { type: 'object', description: 'the counted units, when the area has them', properties: {
        done: { type: 'number' }, partial: { type: 'number' }, open: { type: 'number' }, unit: { type: 'string' } } } },
      required: ['label', 'basis'] },
    confidence: { type: 'string', enum: ['high', 'medium', 'low'] },
    stale_or_conflicting_docs: { type: 'array', items: { type: 'object', properties: { path: { type: 'string' }, claim: { type: 'string' }, reality: { type: 'string' } }, required: ['path', 'claim', 'reality'] } },
    hard_dates: { type: 'array', items: { type: 'object', properties: { date: { type: 'string' }, what: { type: 'string' } }, required: ['date', 'what'] } },
  },
  required: ['area', 'definition_of_done', 'status_summary', 'done_items', 'remaining_items', 'owner_only_steps', 'completion_estimate', 'confidence', 'stale_or_conflicting_docs'],
}

const READERS = [
  { key: 'roadmap', name: 'Product roadmap', prompt: `Area: PRODUCT ROADMAP AND VISION. What is Ashlar meant to be when "done", and how much of that roadmap is built on master? Read CONTINUITY.md (what it says is done, the roadmap, the open threads), README.md, docs/audits/2026-09-completion-roadmap.md (its milestone exit checkboxes), docs/ProductFleetImplementationRoadmap.md, docs/ProductsAndBusinessPlan.md, docs/DocsIndex.md (what it marks live versus roadmap or historical), docs/marketing/positioning.md and docs/marketing/product-boundary.md (what may be claimed), and every open epic or roadmap issue ('gh issue list --state open --search "Epic OR Roadmap in:title"'). For each roadmap item or milestone, decide built, partly built or not started by checking the code (src/, application/, commercial/) rather than trusting the doc. Count the milestone exit checkboxes ticked against the total.` },
  { key: 'release', name: 'Next release', prompt: `Area: RELEASE AND PRODUCTION READINESS. How close is the next release? Read docs/RELEASE_RUNBOOK.md (its pre-tag steps), docs/ProductionReadinessGate-v1.md (its exit checklist), docs/production-readiness/ (the evidence rule, ReleaseSignOff-v1.md, RollbackDrill-v1.md), docs/release/RELEASE-READINESS.md, CHANGELOG.md [Unreleased], VERSION, ci/published-version, and every PublicAPI.Unshipped.txt. Use the validation's readiness verdicts. Report which runbook steps are done on a release commit (none, if no release commit exists), which gate criteria are met with evidence, which gates are dormant or dispatch-only and when they last ran, and what the next version must contain per the docs and the unreleased changes. 'gh release list' gives the published releases.` },
  { key: 'security', name: 'Security specs', prompt: `Area: THE SPECS (docs/specs/). For each spec, read its status line, its PR or implementation plan, its "done when" per PR, its decisions log and its open questions. Determine which planned PRs are merged and actually wired into production code (a library with no production caller does not meet a "refused" or "enforced" done-when: grep for callers outside tests), which remain, and which wait on owner decisions. Include the agent-task cards that belong to each spec ('gh issue list --state open --label agent-task' and read them) and say which are still real work. Read the agent-bus issue #695's last 40 comments for spec status posts and unanswered 'drift-*' asks.` },
  { key: 'autonomy', name: 'Autonomy unlock', prompt: `Area: THE PRODUCT'S AUTONOMY LOOP AND ITS MARKETING UNLOCK. Read docs/dogfood-scorecard.md (the unlock criteria and thresholds), docs/dogfood-ledger.md (the landed rows), docs/marketing/claims-cheat-sheet.md, docs/LearningHarness.md and samples/autonomy-objectives/README.md. Determine each unlock criterion's status on LANDED evidence only (rows on master, not CI artifacts), and how far each threshold is met. Check the dogfood workflow's recent runs ('gh run list --workflow dogfood-continuous-proof.yml --limit 20') and when its evidence artifacts expire ('gh api repos/IanFrelinger/Ashlar/actions/runs/<id>/artifacts'). A criterion with an unmeasured threshold is partly met, not met. tools/autonomy-controller is a development tool, not this loop, unless a doc says otherwise.` },
  { key: 'bugs', name: 'Bug backlog', prompt: `Area: OPEN BUG BACKLOG. List every open issue labelled bug, plus any other open issue that reports a defect ('gh issue list --state open --limit 200' and read the titles). For each, decide by reading the cited code path on master (do not run dotnet): still present, already fixed but left open (search 'git log origin/master' for the issue number and the fix described, and check the code), partly fixed (say which half remains), or addressed by an open PR (read its diff with 'gh pr diff' and its checks). Classify severity from the issue text: security, data-loss, crash, contract, cosmetic. Count each group. Check docs/exceptions.yaml: an empty registry means untriaged, not clean.` },
  { key: 'ci', name: 'CI health', prompt: `Area: ENGINEERING AND CI HEALTH. Start from the validation's facts (required checks, the newest master run of every workflow, open PRs, stranded local branches) and go deeper: for each workflow whose newest master run is red, say why it is red and whether anything tracks it; for each open PR, why it is not merged and who owns it ('gh pr view <n> --json mergeStateStatus,statusCheckRollup,updatedAt,author'); for the stranded local branches, what work they hold and whether any of it is a fix master still lacks; the CI blind spots documented in CONTRIBUTING.md, docs/CiGateInventory.md and open issues. Report what is green, what is red, and what nobody owns.` },
]
if (WORKSTREAM) {
  READERS.push({ key: 'workstream', name: `Workstream ${WORKSTREAM}`, prompt: `Area: THE WORKSTREAM ENDING THIS PHASE, "${WORKSTREAM}". Read its newest handoff ('bash scripts/handoff-fetch.sh --workstream ${WORKSTREAM}', or 'git show origin/claude/${WORKSTREAM}-workspace:_handoff/${WORKSTREAM}/handoff.md'), its phase map and exit criteria, its task cards and their acceptance criteria (the cards it names; 'gh issue view <n>'), and its bus posts on #695. Check each acceptance criterion and each phase exit criterion against the code, tests and merged PRs on master. Report what is done with evidence, what remains, what only the owner can do, and anything the workstream's original brief asked for that no PR delivered. The handoff for phase ${PHASE} is being written now, so the newest stored handoff may be the previous phase's.` })
}

phase('Read')
const reports = (await parallel(READERS.map(r => () =>
  agent(`${CONTEXT}\n\n${r.prompt}\n\nReturn the structured assessment. Be concrete: name items, not categories. If the docs define no end state for this area, say so in definition_of_done and estimate against the nearest stated goal. Put dated deadlines (expiring artifacts, end-of-support dates, scheduled changes) in hard_dates.`,
    { label: `read:${r.key}`, phase: 'Read', schema: AREA })
    .then(rep => rep ? Object.assign({ key: r.key, name: r.name }, rep) : null)))).filter(Boolean)
const missing = READERS.filter(r => !reports.some(x => x.key === r.key)).map(r => r.key)
log(`${reports.length}/${READERS.length} area reports returned${missing.length ? `; missing: ${missing.join(', ')}` : ''}`)

phase('Critique')
const CRITIQUE = {
  type: 'object',
  properties: {
    checked_claims: { type: 'array', items: { type: 'object', properties: {
      area: { type: 'string' }, claim: { type: 'string' }, verdict: { type: 'string', enum: ['confirmed', 'wrong', 'overstated', 'understated', 'unverifiable'] },
      evidence: { type: 'string' }, correction: { type: 'string' } }, required: ['area', 'claim', 'verdict', 'evidence'] } },
    gaps: { type: 'array', items: { type: 'object', properties: { what_is_missing: { type: 'string' }, finding: { type: 'string' }, evidence: { type: 'string' } }, required: ['what_is_missing', 'finding'] } },
    cross_area_conflicts: { type: 'array', items: { type: 'string' } },
  },
  required: ['checked_claims', 'gaps', 'cross_area_conflicts'],
}
const critique = await agent(`${CONTEXT}

You are the critic of a phase-end audit. Area readers each assessed one area. Their reports (JSON):
${JSON.stringify(reports)}

Do three things, reading the sources yourself:
1. Pick the 10 to 15 most load-bearing claims (completion estimates, "done" items that drive the answer, release blockers, "fixed but left open" calls, "met" criteria) and try to REFUTE each by checking the cited evidence and the code or GitHub state. Default to 'unverifiable' when you cannot check.
2. Find gaps: parts of Ashlar no reader covered (look at the top-level layout of src/, application/, commercial/, extensions/, infra/, docs/ and .github/workflows/, and CONTINUITY.md's open threads), security settings, hard dates nobody listed, and owner decisions or owner-only steps nobody listed.
3. List conflicts between readers.`, { label: 'critic', phase: 'Critique', schema: CRITIQUE })

phase('Synthesize')
const STATUS = {
  type: 'object',
  properties: {
    headline: { type: 'string', description: 'two to four plain sentences: how close the project is, in the senses that matter, and what moved this phase' },
    areas: { type: 'array', items: { type: 'object', properties: {
      key: { type: 'string', description: 'the reader key: roadmap, release, security, autonomy, bugs, ci or workstream' },
      name: { type: 'string' },
      distance: { type: 'string', enum: ['near', 'partway', 'far'] },
      done_means: { type: 'string' }, status: { type: 'string' },
      estimate: { type: 'string' }, basis: { type: 'string' },
      units: { type: 'object', properties: { done: { type: 'number' }, partial: { type: 'number' }, open: { type: 'number' }, unit: { type: 'string' } } },
      blocker: { type: 'string' },
      change_since_previous: { type: 'string', description: 'what changed in this area since the previous status; "first audit" when there is none' } },
      required: ['key', 'name', 'distance', 'done_means', 'status', 'estimate', 'basis', 'blocker', 'change_since_previous'] } },
    resolved_since_previous: { type: 'array', items: { type: 'string' } },
    new_since_previous: { type: 'array', items: { type: 'string' } },
    critical_path: { type: 'array', items: { type: 'object', properties: { step: { type: 'string' }, owner_only: { type: 'boolean' } }, required: ['step', 'owner_only'] } },
    next_completion_point: { type: 'string' },
    owner_decisions: { type: 'array', items: { type: 'string' } },
    hard_dates: { type: 'array', items: { type: 'object', properties: { date: { type: 'string' }, what: { type: 'string' } }, required: ['date', 'what'] } },
    stale_docs: { type: 'array', items: { type: 'object', properties: { path: { type: 'string' }, claim: { type: 'string' }, reality: { type: 'string' } }, required: ['path', 'claim', 'reality'] } },
    workstream_findings: { type: 'array', items: { type: 'string' }, description: 'findings about the ending workstream itself, for its handoff queue' },
    unverified: { type: 'array', items: { type: 'string' } },
    markdown: { type: 'string', description: 'the "Project status" subsection for the handoff, Markdown, at most about 650 words' },
  },
  required: ['headline', 'areas', 'resolved_since_previous', 'new_since_previous', 'critical_path', 'next_completion_point', 'owner_decisions', 'hard_dates', 'stale_docs', 'workstream_findings', 'unverified', 'markdown'],
}
const status = await agent(`${CONTEXT}

Write the phase-end project status. Inputs: the area reports and the critic's verdicts (JSON below), and the previous phase's status (or null). Apply the critic's corrections: where a claim was 'wrong' or 'overstated', use the corrected version; drop claims the critic refuted.

AREA REPORTS: ${JSON.stringify(reports)}
CRITIC: ${JSON.stringify(critique)}
PREVIOUS STATUS: ${PREVIOUS ? JSON.stringify(PREVIOUS) : 'null (this is the first audit for this workstream)'}

Rules:
- One areas[] row per area report, keyed by its 'key'. distance: near (the area's own definition of done is mostly met), partway, or far. Every estimate states its basis; give units when the area has countable units.
- change_since_previous compares with the previous status's row of the same key: what was resolved, what is new, what moved. With no previous status, write "first audit".
- resolved_since_previous and new_since_previous list the concrete items (issues, gates, PRs, criteria, dates) that changed across the whole project.
- next_completion_point is the next meaningful milestone (usually the next release); critical_path is the ordered steps to it, owner-only steps marked.
- hard_dates: only dates within the next 60 days of ${DATE}, plus any already passed that still matter.
- markdown is what goes verbatim into the handoff's section 1 under "### Project status at the end of phase ${PHASE}". Use this shape, plain sentences, no em-dashes, no hype:
  1. The headline paragraph.
  2. One line with the validation summary (pass, warn, fail, error counts on master's short SHA), then one bullet per validation check that is fail, error or warn.
  3. A table: | Area | Distance | Estimate (basis) | Since last phase | Blocks the rest |
  4. "Moved since the last phase" with resolved and new, or "First audit." when there is no previous status.
  5. "Critical path to <next completion point>" as a numbered list, owner-only steps starting with "You:".
  6. "Hard dates" as a short list.
  7. "Not verified" as a short list, if anything was not.`, { label: 'synthesis', phase: 'Synthesize', schema: STATUS })

return {
  status,
  critique,
  reports,
  inputs: { workstream: WORKSTREAM, phase: PHASE, date: DATE, masterSha: MASTER, previous: PREVIOUS ? true : false,
            validation_summary: VALIDATION ? VALIDATION.summary : null, missing_readers: missing },
}
