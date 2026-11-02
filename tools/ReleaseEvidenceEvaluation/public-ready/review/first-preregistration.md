# First independent public-ready review registration

This is an AI-only blinded review of three real public GitHub PR captures, across two repositories. It is not human review, customer evidence, or release approval. Independence means a separate reviewer context; shared-model error remains possible.

## Frozen inputs and permitted access

The source-author manifest byte hash was verified before detailed case labeling: `84191b5f1fa6f5acc7743c8ed0ce8852ed6e6a1f2df0c97513880f31e599fc2d`. The input-manifest byte hash is `17e01f26dff07b58d004b43ae12c5c3959e6a58b36b0a22ef56b0aa107919035`. Baseline candidate identity is `86c6e9e18da496264d2e138efcbd18fabc1558bc`; the identity is recorded, not inspected or executed.

Before registration I read repository `CLAUDE.md`, the original `review/ground-truth-protocol.md`, `SCORING.md`, the two permitted manifests, and raw-file top-level shapes and byte hashes. The source manifest includes operational-clean selection summaries; those are selection metadata, not ground truth. Detailed response bodies have not yet been read in this reviewer context. I will inspect only the three assigned cases' `capture.json`, `routes.json`, and `sha256.json`, plus read-only official GitHub documentation if needed. I will not read production code, tests or their expected answers, existing label files, replay outputs, candidate results, or another reviewer's labels. I will not run .NET or perform network writes.

## Fixed decision rules

I adopt the original blinded protocol without changing its outcomes: conservative readiness is ready only when identity and policy discovery are sufficient and every applicable mandatory requirement is observably satisfied. Scoped assurance passes only on that fully observed bounded evidence. A trustworthy applicable blocker yields blocked/fail and is retained alongside any unknowns. Missing applicable policy, denied policy access, unresolved identity conflicts, or unsupported mandatory evidence yields unknown/unknown when no confirmed blocker exists. A clean mergeability summary alone cannot remove these unknowns.

I will first establish PR, repository, base/head identity, open/draft state, collection interval, and initial/final observations. Then I will distinguish observed applicable rules from possible hidden requirements. A required check/review is inferred only from raw effective policy or an authoritative effective summary. Failed optional checks or raw review counts alone do not establish mandatory blockers. Divergent PR-base versus branch-tip identity is recorded as an identity limitation, rather than invented as either a strict-policy failure or proof of freshness. Endpoint denials are not empty policies. Empty rulesets alone do not establish absence of classic branch protection.

Every conclusion will retain JSON pointers into immutable raw captures, applicable source routes, input hashes, and a rationale. Legacy compatibility is a separate allowable-vocabulary annotation derived from observed GitHub summary facts, not anticipated collector behavior. The review will preserve operational GitHub clean/mergeable observations separately from fully observed readiness and assurance.

## Freeze and adjudication

I will write `first-ground-truth.json` as the original independent result before cross-review or output inspection. Its review-process status will remain unadjudicated. A second independent review and any later reconciliation must retain this first version and explain disagreements. The original 36-case benchmark is outside this review and will remain untouched. Only the two assigned first-review files are written.
