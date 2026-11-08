# Job Search Studio: development and release

Releases are gated by QA agents that each operate their own screen (owner decision 2026-11-08, [STUDIO.md](STUDIO.md)
§5). The workflow is [`.github/workflows/job-search-studio.yml`](../../../.github/workflows/job-search-studio.yml).
The agents and how to run them are in [qa/README.md](../qa/README.md); the server is in
[deploy/README.md](../deploy/README.md).

## The lanes

| When | What runs | What stops it |
|---|---|---|
| Pull request touching `apps/job-search/**` (and pushes to `master`) | 1. The .NET tests, with a guard that fails if none ran. 2. The image is built. 3. The QA harness self-test. 4. The UI suite against the image. 5. The scripted QA fleet against a fresh copy of the image. Steps 4 and 5 run the image hardened as in production, on synthetic data. | Any failure; any `blocker` or `major` finding. The report, with screens and videos, is the `qa-scripted` artifact. |
| Nightly, only once the owner sets the repository variable `STUDIO_QA_AGENTS` to `on` | The same, then the **Claude QA fleet**: every charter, each agent on its own screen, against the same image. | `blocker` or `major` findings turn the run red; the report is the `qa-claude` artifact. |
| Manual run (*Run workflow*, with *claude_agents* ticked) | The same as nightly. | The same. |
| Tag `studio-v*` | Everything above. The Claude fleet always runs on a tag. | Any failure, or any `blocker`/`major` finding, and the release job does not start. |
| Release (same tag run) | A reviewer approves the `studio-production` environment. The image the agents tested goes to ECR under the tag, and the server rolls to it. | The server keeps the new image only if `/health` and `/health/model` answer; otherwise it returns to the previous image and the job fails. |

Two properties hold at every stage.

- **The tested bits ship.** QA runs against the image built in the same workflow run. The release pushes that saved
  image; it never rebuilds.
- **QA never sees real data.** Every QA target is a synthetic studio started from the image. The agents refuse any
  target that does not report `synthetic: true`.

## Cutting a release

```bash
git tag studio-v1.0.0 && git push origin studio-v1.0.0
```

1. Watch the run. The Claude fleet takes about 10–30 minutes, depending on findings.
2. Read the `qa-claude` report, including the `minor` findings and `note`s that do not block. Approve the
   `studio-production` deployment if the release is wanted.
3. The job prints the server's rollout log. Afterwards, set `image_tag = "studio-v1.0.0"` in `terraform.tfvars`, so a
   replaced server comes back on this release.

**Rolling back.** Run `deploy/deploy.sh studio-v0.9.0` with operator credentials, or push a new tag on the old commit.
ECR tags are immutable, and ECR keeps the newest 30 images.

## One-time setup

**Repository variables** (*Settings → Secrets and variables → Actions*):

| Variable | Value |
|---|---|
| `STUDIO_AWS_REGION` | `terraform output -raw region` |
| `STUDIO_AWS_DEPLOY_ROLE_ARN` | `terraform output -raw deploy_role_arn`; needs `github_repository` set in Terraform |
| `STUDIO_ECR_REPOSITORY_URL` | `terraform output -raw ecr_repository_url` |
| `STUDIO_INSTANCE_ID` | `terraform output -raw instance_id` |
| `STUDIO_ROLLOUT_DOCUMENT` | `terraform output -raw rollout_document` |
| `STUDIO_QA_AGENTS` | `on` to run the Claude fleet nightly; leave unset to run it only on tags and manual runs |

**Repository secret.** `ANTHROPIC_API_KEY` is the key the QA agents use. Give it its own Anthropic workspace and
spending limit, separate from the studio's production key: QA spend should never eat the users' step allowance.

**Environment.** Create `studio-production` with required reviewers. The approval is the release decision, and the
deploy role trusts only jobs that run in this environment.

No AWS keys are stored in GitHub. The release job exchanges the run's OIDC token for the deploy role. That role can
push to the studio's repository and run the rollout document, and nothing else.

## Cost of the QA agents

Each Claude agent works for at most `maxSteps` turns (30 to 40 per charter, six charters). Each turn sends a
screenshot. Old screenshots are cleared server-side, so a turn does not resend the whole history. Expect a full fleet
run to cost a few dollars at `claude-opus-5-5` with effort `high`. `QA_EFFORT=medium` lowers it.

Measure a run before turning nightly on: `report.json` records the input and output tokens of every agent.

## When a finding blocks a release

1. Open the report. Every finding has steps to reproduce, the expected and actual result, and the agent's screens
   and video.
2. Reproduce it on a synthetic studio (`qa/serve-image.sh`), fix it, and add a scripted step or a UI test that pins
   it. The next pull request then catches it without tokens.
3. If the finding is wrong (the agent misread the screen), sharpen the charter's goal or checks. Do not lower
   `QA_FAIL_ON` to get a release through.
