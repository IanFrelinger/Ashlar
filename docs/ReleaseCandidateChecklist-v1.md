# Release Candidate Checklist v1

Use this checklist to move from "locally passing" to "release-ready with evidence."

## 1) CI gate (mandatory)

- [ ] Trigger `production-readiness-gate-v1` in GitHub Actions.
- [ ] Trigger `environment-setup-gate-v1` in GitHub Actions.
- [ ] Trigger `runtime-release-gate` in GitHub Actions (core + visual required, chaos non-gating).
- [ ] Trigger `runtime-release-promotion` in GitHub Actions (strict thresholds).
- [ ] Trigger `installer-bruteforce-gate` in GitHub Actions.
- [ ] Trigger `container-image-gate` in GitHub Actions.
- [ ] Trigger `container-image-publish` in GitHub Actions (or verify latest successful publish on `master`).
- [ ] Trigger `onboarding-docs-guard` in GitHub Actions.
- [ ] Confirm ephemeral setup container jobs pass for each distro in matrix.
- [ ] Confirm matrix jobs pass on:
  - [ ] ubuntu-latest
  - [ ] windows-latest
  - [ ] macos-latest

  These matrices (`production-readiness-gate-v1`, `environment-setup-gate-v1`) still use floating
  `-latest` labels, so the label alone does not say what was tested: record the `Image:` line of each
  job's "Set up job" step next to its tick. On 2026-09-30 they resolved to `ubuntu-24.04`,
  `windows-2025-vs2026` and `macos-26-arm64`; from 2026-10-19 GitHub starts moving `ubuntu-latest` to
  Ubuntu 26.04 (actions/runner-images#14748).
- [ ] Confirm uploaded artifacts include:
  - [ ] test TRX files
  - [ ] `gate-validate.log`
  - [ ] `gate-run-success.log`
  - [ ] `gate-run-fallback.log`
  - [ ] `gate-diagnostics.log`
  - [ ] `gate-resume-source.log`
  - [ ] `gate-resume-target.log`
  - [ ] `setup-gate-summary-<os>.txt`
  - [ ] `setup-gate-ephemeral-summary-*.txt`
  - [ ] container image gate summary + smoke logs
  - [ ] published image smoke log (`docker run ... --help` in publish workflow)
  - [ ] runtime release lane logs (core, visual, chaos)
  - [ ] runtime SLO evidence JSON (`.ashlar/runtime/release-gate/last-run/evidence.json`)
  - [ ] runtime SLO evidence markdown (`.ashlar/runtime/release-gate/last-run/evidence.md`)
  - [ ] installer brute-force matrix + summary logs
  - [ ] native installer package artifacts (linux/macos/windows)

## 2) Runtime correctness review

- [ ] Verify `gate-run-fallback.log` shows `hybrid` stage worker type as `Agentic`.
- [ ] Verify `gate-resume-source.log` has run state `Failed` (intentional source failure).
- [ ] Verify `gate-resume-target.log` has run state `Completed`.
- [ ] Verify `gate-diagnostics.log` reports known persistence provider and resolved adapter keys.

## 3) Exceptions policy (mandatory)

For each open High/Critical exception:

- [ ] Owner assigned
- [ ] Expiration date set
- [ ] Mitigation plan documented
- [ ] Explicit sign-off recorded

If any item is missing for any High/Critical exception, release is blocked.

## 4) Rollback readiness (mandatory)

- [ ] Rollback command/procedure documented.
- [ ] Rollback tested in staging or equivalent environment.
- [ ] Responsible operator/team identified.

## 5) Release decision

- [ ] All sections above complete.
- [ ] Product/engineering sign-off recorded.
- [ ] Release candidate promoted.
