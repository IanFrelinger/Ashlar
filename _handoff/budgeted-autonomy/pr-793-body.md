Budgeted-autonomy close-out, after the phase F handoff (storage branch `claude/budgeted-autonomy-workspace` at `bbf7fd4ad`). This is a records PR only: six dated rows in the decisions log of `tools/autonomy-controller/README.md`. No code changes.

## Where the decisions come from

On 2026-10-13 the owner answered the open questions in §3 of the phase F handoff. On the pre-rewrite storage commits, the owner said "I don't care about my email". For the rest, the owner said "I'll go with your judgement". So the rows marked *Accepted recommendation* are the owner accepting Claude's recommendations. Claude made them in the chat just before, and each row states one of them.

## The rows (2026-10-13)

- **Evidence:** the storage branch's four pre-rewrite commits are not purged: `cbc1a0d72929`, `c9eb03d92`, `3ad39c834` and `ec6ac7cfd`. The owner accepts that the account email stays readable there.
- **The six PR 5 evidence archives** stay on the owner's machine and are not published.
- **The outbox post 6149018991**, signed `From: codex`, stands as posted.
- **The owner's Codex `service_tier = "priority"`** stays. The controller keeps refusing Codex launches while it is set.
- **The workstream** closes after one cleanup PR. The PR 5 run sheet is marked historical. No phase G is planned, and the brief's undelivered items get no cards now.
- **Card #790** is a precondition of the next live controller run that launches Claude workers.

## Checked before the evidence row

All four pre-rewrite commits hold the same blob, `a81c1a1c`: the unredacted PR 4 pilot tarball. For each SHA, GitHub's contents API returns that blob, size 3532965. No commit reachable from the branch holds it (`git rev-list --objects`: 0). It was read from the local object store, never downloaded or published, and scanned recursively with nested archives included: 40 members.

The scan found 0 matches for:
- GitHub, Anthropic, OpenAI-style, AWS and Slack token shapes;
- private-key blocks and JWTs;
- bearer headers;
- `access_token`, `refresh_token`, `api_key` and `client_secret` fields;
- `password` and `secret` fields.

The scanner was first observed finding a planted fake `ghp_` token and an `access_token` field, two archives deep. So the email is the only personal data the owner accepts here. The extracted copy was deleted afterwards.

## Not recorded here

- **validate.py fixes.** The recommendation that `scripts/phase-end/validate.py` fixes ride with the next workstream is project tooling, not a decision of this workstream, so it has no row here.
- **Open questions 3 and 9** of the phase F handoff stay open: is the cloud-cost review still the next workstream, and should any phase A to F integrator decision become an owner row. The handoff addendum carries them forward.
- **The reviewers' coverage nits** stay listed, unscheduled, in handoff F §7.

## Review

An adversarial review (three lenses: portability, records accuracy, breakage) found two problems:
- the Evidence row named two pre-rewrite commits, where there are four;
- two rows pointed at the wrong row.

Both are fixed in `30066cf04`, together with the review's wording nits.

## Verified

- `bash scripts/check-docs-phantom-paths.sh`: 0 missing.
- Knowledge graph: current. Rebuilding it changed only line endings in this checkout, so nothing was committed.
- README line endings are unchanged (CRLF, 0 bare LF).

Nothing behavioural changes, so there is nothing to mutation-check.

🤖 Generated with [Claude Code](https://claude.com/claude-code)
