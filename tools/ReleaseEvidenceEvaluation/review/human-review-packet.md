# Human review packet — accuracy follow-up

Status: **e29 reviewed by the conversation user: agreed; e20/e26 pending**. The response is
retained separately in `user-label-review-2026-11-02.json`. These are selected ground-truth judgments, not a request
to approve code, a release, a pilot or a change to the hold. Existing labels remain frozen.
Any human response will be retained separately as post-output label review, not represented
as a blinded independent review or as review of all 36 cases.

| Case | Raw observed facts | Frozen judgment to review |
|---|---|---|
| [e29](../cases/e29-app-bound-and-deployment-rule.json) | Rules require `ci / unit` specifically from app 44444 and a successful production deployment. App 44444 succeeds; the same name from app 55555 fails. No deployment observation is available. | The required CI is satisfied; the optional app's failure is not a confirmed blocker. Deployment is unknown, so overall assurance is unknown. |
| [e20](../cases/e20-review-transport-timeout.json) | The same PR/head/base remains open, non-draft and clean. Policy requires one approval. GitHub's head-matching review decision is `APPROVED`, but the review inventory times out. | GitHub's operational summary supports ready; the incomplete evidence inventory keeps scoped assurance unknown. This was the original AI reviewers' one assurance disagreement. |
| [e26](../cases/e26-facts-change-same-head.json) | Head/base remain unchanged. Initial CI/review observations pass/approve. Later responses show required CI failure, changes requested and a blocked merge summary. | A matching commit alone does not establish freshness. Later failed CI and changes requested are blockers; an earlier passing snapshot cannot authorize the current state. |

Review the linked frozen response sequences, including their `routes`, not just this summary.
For each reviewed case, record agree/change/unable to decide and a short reason. A change is
an erratum to evaluate separately; it does not rewrite the original benchmark to improve scores.
The source files and original adjudication are synthetic/public development evidence, not
customer data or a compliance determination.
