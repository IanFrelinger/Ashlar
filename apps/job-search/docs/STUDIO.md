# Job Search Studio: the hosted product

**Status:** Phase 1 built (2026-11-08) on branch `claude/job-search-tracker-system-afvtol`; deployment files in
[`deploy/`](../deploy/README.md). Not deployed to AWS yet.

Job Search Studio is the job-search app as a hosted web product. Each user signs in, connects their own Google Drive,
and designs their own search pipeline in a visual designer. When no step does what they want, they describe it in
plain words; the studio writes the step once, proves it safe with Ashlar's certification gate, and adds it to a
library every user shares. The next user who wants the same step gets it without spending a token.

## 1. Owner decisions

| Date | Decision | Source |
|---|---|---|
| 2026-11-08 | The product is the **job-search service** as a web app, built on reusable Ashlar pieces; a general studio may follow. | Owner, answering "What should the hosted product be?" |
| 2026-11-08 | New steps are written by **Claude through one platform API key** held by the operator; **each user has a monthly limit** on new steps. | Owner, answering "Which model ... who pays for its tokens?" |
| 2026-11-08 | First hosting is **one EC2 server**: the app container under Docker Compose, HTTPS by Caddy, data on the instance disk, nightly backups to S3. | Owner, answering "How should it run on AWS first?" |
| 2026-11-08 | Generated steps are **shared between users** "so that tokens don't go to waste", and the product should **use its privacy advantage where possible**. | Owner's request |
| 2026-11-08 | The product must **adhere to data-protection regulations for PII**, and **PII is routed only to server-hosted models**. | Owner's request |
| 2026-11-08 | The server-hosted model runs on **the same server, on CPU**: one m7i.xlarge (4 vCPU, 16 GB) runs the app and an ~8B open model under Ollama, reached over loopback. This replaces the small single server. | Owner, answering "Which server should run it?" |
| 2026-11-08 | Development and release are built around **automated QA test agents, each with its own screen**, testing the UI. | Owner's request |
| 2026-11-08 | QA agents are driven by **Claude computer use**, only against a staging copy filled with made-up personas; the runner refuses any target that is not flagged synthetic. | Owner, answering "Which model should drive them?" |
| 2026-11-09 | The **step library is shared across customers**, and it comes next, ahead of fleet operations: it is the advantage that compounds. | Owner, after the scaling discussion: "bump up 3) A step library shared across customers" |

## 2. Integrator decisions (defaults taken; the owner may change any of them)

| # | Decision | Why |
|---|---|---|
| I1 | Two model tiers. The **private model** (Ollama on 127.0.0.1, default `llama3.1:8b`, `JOBSEARCH_PRIVATE_MODEL`) handles every prompt that may hold personal data: what a user types to describe a step, and cover-letter wording. **Claude** (`claude-opus-5-5`, effort `high`, `JOBSEARCH_FORGE_MODEL`) only writes step code from a design that passed the PII gate. | The owner's PII rule, with Claude kept for the one task that needs a strong coder and holds no personal data. |
| I2 | Requests opt into server-side refusal fallbacks (`fallbacks: "default"`, beta `server-side-fallback-2026-07-01`). | A declined request is re-served instead of failing; recommended for Opus 5.5. |
| I3 | Default limit: **10 new steps per user per calendar month**, plus an operator-wide daily token budget. Reusing a library step does not count. | Bounded spend with room to customise. `JOBSEARCH_FORGE_MONTHLY_LIMIT`, `JOBSEARCH_FORGE_DAILY_TOKENS`. |
| I4 | Two kinds of step in Phase 1: **check** (pass / flag / exclude a posting, with a note) and **column** (a value shown in the tracker). Each has up to three text settings. | Fixed contracts make a generated step reusable by anyone, testable on made-up postings, and safe to wire automatically. |
| I5 | A step is **generic code plus per-user settings**. The user's own values (company names, keywords, limits) are settings, stored with the user's workflow, never in shared code. | Sharing code without sharing anyone's data. |
| I6 | Shared by default. A user can mark a new step **private** when asking for it; then only they see it. | The owner asked for sharing; a private escape hatch costs nothing. |
| I7 | The library never shows who wrote a step. | Which steps a person asked for says something about their search. |
| I8 | LLM-written code never runs in the web server's process. Certification runs in a child process with a hard timeout; certified steps run in a separate step-host process with a per-call timeout and a memory cap. | The web process holds every user's tokens. |
| I9 | The platform signs every step it certifies with its own Ed25519 key (`JOBSEARCH_SIGNING_KEY`), and loads only steps signed by a pinned key. | The same trust model as the shipped certified bricks. |
| I10 | Sign-in is Google (OpenID Connect, code flow) and asks for Drive access in the same consent. A persona sign-in (`/auth/dev`) exists only in synthetic mode, which runs only with fake Google. | Users already connect Drive; one consent. |
| I11 | The store stays SQLite on the instance disk; OAuth refresh tokens are encrypted at rest with AES-GCM under `JOBSEARCH_MASTER_KEY`. | One server (owner decision); backups hold only ciphertext for secrets. |
| I12 | The web UI is self-contained: no CDN, no fonts or scripts fetched from anyone else, no analytics, strict Content-Security-Policy. The one font (Inter, OFL) is served from the studio's own origin (`studio-ui/fonts/README.md`). | A privacy product should not tell third parties who uses it; a self-hosted font tells no one. (Reworded 2026-11-09 from "no web fonts" for the redesign; the rule it protects is unchanged.) |
| I13 | Everything a user types in free text is treated as personal data. | A detector cannot prove free text holds no one's name; routing it privately needs no proof. |
| I14 | Data-subject rights in the product: export everything (JSON), delete the account and every row about the user, consent records with versions, an age confirmation (16+), retention limits (archived jobs 365 days, runs 180 days, configurable). | GDPR Arts. 7, 8, 15, 17, 20 and CCPA/CPRA rights to know and delete; technical support for them, not legal advice. |
| I15 | QA runs use the app's synthetic mode: fake Google, stub job boards, seeded personas, and deterministic stand-ins for both model tiers, so a QA run needs no Ollama and no Claude key for the app itself. | Reproducible QA and no personal data on any screen an agent sees. |
| I16 | A finding of severity `blocker` or `major` from the agent fleet blocks a release candidate. | A gate that can fail; the owner can tune the threshold. |
| I17 | The server runs Canonical's **Ubuntu 24.04 LTS** image, not Amazon Linux 2023. | Docker and Compose come from signed distribution packages; Amazon Linux has no Compose package, and a downloaded binary would need a checksum this project could not verify. |
| I18 | The studio and Ollama share one network namespace held by a small `pod` container; Caddy reaches only the studio's port. The data lives on its own EBS volume, which outlives the instance. | The model stays on loopback (I1) without host networking, so containers still cannot reach the instance role (IMDSv2, hop limit 1); a studio restart rejoins the namespace; the server can be replaced without touching the data. |
| I19 | Backups run **hourly** (the owner chose nightly; hourly includes it) and carry an erasure ledger; a restore re-applies the erasures made after the backup was taken. Backups expire after 35 days. | An erased account must not come back from a backup (GDPR Art. 17); the ledger holds keyed hashes, not personal data. Set the timer to `daily` to return to the owner's original choice. |
| I20 | The release gate: the scripted QA fleet and the UI suite run on every studio pull request against the built image; the Claude fleet runs on every `studio-v*` tag, on dispatch, and nightly only when the owner sets `STUDIO_QA_AGENTS=on`. The release pushes **the very image the agents tested**, after approval in the `studio-production` environment, and the server rolls back if `/health` or `/health/model` does not answer. | QA that gates the bits that ship; no token spend until the owner opts in; the repository's manual-first CI policy. |
| I21 | Caddy writes no access log and strips `X-Forwarded-For` and `X-Real-IP`; the studio never learns a visitor's IP address. | Data minimisation: nothing in the studio needs it. |
| I22 | Third-party images are pinned by exact tag (`busybox:1.37`, `caddy:2.11.9`, `ollama/ollama:0.40.12`); the studio's own images are immutable tags in ECR. | A deploy runs what was tested; upgrades are deliberate. |

## 3. The privacy model

Personal data (PII) is anything about a user: the résumé, profile fields, preferences, the jobs they look at, their
tracker, and anything they type. Job postings themselves are public.

| Claim | Enforced by |
|---|---|
| PII reaches only models on this server. | Reading PII observes the `Confidential` label in the current egress subject frame. Every model client goes through Ashlar's egress guard in **enforce** mode. Only loopback counts as inside the host, so the private model (Ollama on 127.0.0.1) is reachable from a frame that read PII and Claude is not: the guard refuses the call before a byte leaves (`ForgeTests`). A model call made outside any frame is refused too, because no frame reads as `SystemHigh`. The private model client refuses a base URL that is not loopback. |
| Claude never receives a user's résumé, profile, tracker or typed words. It receives a generic step design and made-up examples. | The design is produced by the private model; the PII gate (emails, phone numbers, ID-like numbers, links to profiles, and the user's own profile strings and setting values) must pass before the design leaves the host, inside a frame entered at `Public` (`ForgeTests`). |
| Shared step code cannot send your data anywhere. | Ashlar's certification gate: the IL import fence refuses network, files, processes, reflection, environment and threads; witness cases, mutation testing (every mutant killed) and a determinism check. The step then runs in a separate process with no secrets in its environment. |
| A shared step carries no one's personal values. | The design step separates settings from code (I5). Before publishing, a scan refuses source, witness, name or description that contain the author's setting values or strings from their profile; the step is kept private instead. |
| Your documents stay in your Drive. | Unchanged from the app: drafts, PDFs and the tracker live in the user's own Drive; the server keeps metadata and an index. |
| Optional model wording of cover letters stays on this server. | It runs on the private model (the résumé is PII) and only with the user's opt-in (`materials.modelCoverLetter`). |
| A user can see, take and erase their data. | `GET /api/me/export`, `POST /api/me/delete` (every row with the user's id; shared steps they wrote stay, with no author), consent records, retention sweeper. Documents in the user's own Drive stay theirs. |

What this does not claim: it is not a proof that nothing can leak. The guard checks egress at the boundary it mediates;
code outside the guard (for example a library with its own sockets) is outside the claim. See SPEC-010 §10 for how
Ashlar phrases privacy claims.

Regulatory scope: the controls above support obligations under GDPR / UK GDPR and CCPA/CPRA (records of processing,
data minimisation, purpose limitation, security of processing, the rights of access, portability and erasure, and
limits on sensitive personal information such as citizenship or immigration status). They are not legal advice and do
not make the operator compliant by themselves: the operator still needs a privacy notice with their own details, a
legal basis, processor agreements (AWS, Google, Anthropic), and a breach procedure. `PRIVACY.md` is the record of
processing to start from.

## 4. Architecture

```
 browser (designer SPA, same origin, no third parties)
    │  cookie session (HttpOnly, SameSite=Lax, __Host- prefix on HTTPS) + X-Studio-Request header on writes
    ▼
 jobsearch serve  (ASP.NET Core, JobSearch.Host)
    ├─ auth: Google OIDC (code flow) │ dev sign-in (fake mode only)
    ├─ API: profile, preferences, workflow, library, forge jobs, runs, tracker, applications
    ├─ workers: dispatcher (per-user schedule), forge queue
    ├─ App (existing): Db, Accounts, Profiles, Applications, Tracker, RunEngine
    ├─ Ashlar runtime: registry overlay = app bricks + certified bricks + library steps (proxies)
    ├─ Forge ─┬─ private model (Ollama, 127.0.0.1): reads the user's words, matches or designs a step   [PII]
    │         ├─ PII gate ─► Claude (Anthropic C# SDK over EgressHttp, enforce mode): writes code from the design [no PII]
    │         └─ certify-step child process (Ashlar certification gate, timeout) ──► signed record + DLL
    └─ Step host child process(es): verified library steps, per-call timeout, memory cap, no secrets
 ollama (same server, in the studio's network namespace via the `pod` container, listens on 127.0.0.1:11434 only)
 caddy (TLS, no access log) ──► pod:8080        host: hourly `jobsearch backup` ──► S3 (KMS)
```

Deployment: [`deploy/README.md`](../deploy/README.md) (Docker image, Compose stack, Terraform, rollouts, backups and
restore). The UI's design rules and test contract: [UI-DESIGN.md](UI-DESIGN.md).

### Step contracts

| Kind | Inputs (all text) | Outputs |
|---|---|---|
| `check` | `title`, `employer`, `location`, `arrangement`, `description`, `payMin`, `payMax`, `skillsRequired`, `skillsMissing`, `setting1`–`setting3` | `verdict` (`pass` / `flag` / `exclude`), `note` |
| `column` | the same | `value` |

The model writes one static method (`Evaluate` for a check, `Value` for a column) and the witness cases. The studio
wraps it in a fixed brick template, so the metadata, the interface and the input handling are never model-written.

### The workflow document

Each user has one workflow document: the stages of the per-posting pipeline, their settings, and the user's steps in
order. The compiler turns it into an Ashlar `WorkflowDefinition`; the run engine executes it with Ashlar's
`WorkflowExecutor`. Built-in stages (read, screen, decide, draft, validate) can be configured but not removed; reading
the posting and validating materials cannot be turned off.

### Forge flow

1. **Match or design** (private model, on this server): the request and the descriptions of the closest library steps
   go to the private model, which either picks an existing step and fills its settings from the request, or designs a
   generic step (kind, name, description, settings) and says which settings the request implies.
2. **Reuse:** no Claude tokens at all. The step is added to the user's workflow with their settings.
3. **PII gate** on the design. If it fails, the private model rewrites the design once; if it still fails, the private
   model writes the step itself and the step stays private.
4. **Write** (Claude): Claude writes the method and witness cases from the design only.
5. **Certify** in a child process: compile, IL fence, witness, mutation testing, determinism, dependency check,
   signed with the platform key. A refusal goes back to the writer with the gate's reasons; at most three attempts.
6. **Scan** for personal values, then **publish** to the library (or keep private), load into the step host, and add it
   to the user's workflow.

## 5. QA agents and the release strategy

Every change to the studio is tested twice in the browser: by scripted UI tests, and by QA agents that each operate
their own screen (a Chromium window at a fixed resolution, recorded to video) and are asked to use the product the way
a job seeker would.

| Stage | Runs | Gate |
|---|---|---|
| Pull request (studio paths) | .NET tests; the image; the harness self-test; the deterministic UI suite and the scripted QA fleet (no tokens), each against the built image running hardened on synthetic data. | Any failure. |
| Nightly (only with `STUDIO_QA_AGENTS=on`) and on dispatch | All of the above, then the Claude QA fleet, every charter, against the same image. | `blocker`/`major` findings turn the run red. |
| Release candidate (`studio-v*` tag) | All of the above; the Claude fleet always runs. | `blocker` or `major` findings stop the release (I16). |
| Release | After approval in the `studio-production` environment, the image the agents tested goes to ECR and the server rolls to it. | The server rolls back to the previous image unless `/health` and `/health/model` answer. |

QA agents never see production. Production holds real PII, and agents send screenshots to Claude; the runner reads
`/api/meta` and refuses any target that does not report `synthetic: true`. Details: `RELEASE.md` and `qa/README.md`.

## 6. Phases

| Phase | Scope |
|---|---|
| 1 (built) | Web host, Google sign-in, workflow documents and compiler, designer, step library, forge with two model tiers, certification and sharing, isolated step host, PII controls and data-subject rights, encrypted secrets, one-server AWS deployment with the private model, QA agents and release gates, tests. |
| 2 (next) | **Steps shared across customers** (owner decision 2026-11-09): every customer's server draws on one reviewed, signed library, so a step written for one customer serves all of them. Design: [LIBRARY-SHARING.md](LIBRARY-SHARING.md). |
| 3 | A chat assistant that edits the workflow ("only remote jobs in Colorado, and flag anything with on-call") as a reviewable diff; branches; more step kinds (score adjustments, digests the user explicitly asks for). |
| 4 | Operations across customers: measured seats per server, one-command customer setup, staged rollouts across servers, central health, metering and billing, quotas per plan, a coach view, KMS-wrapped keys; Postgres only if one customer outgrows a server. |

## 7. Known limits (Phase 1)

- A step that loops forever on a real posting (never on its witness cases) is stopped by the step host's timeout,
  quarantined for everyone and reported; until the host is restarted, that host process is replaced, not the server.
- The certifier's mutation testing runs model-written code; it runs in a child process with a timeout and a memory cap,
  not in a container sandbox.
- One server: no failover. Backups are hourly. An erasure made in the hour before a total loss of the data volume is
  in neither the newest backup's ledger nor anywhere else, so the restore brings that account back; the operator must
  erase it again on the user's request.
- The master key (`JOBSEARCH_MASTER_KEY`) cannot be rotated yet: there is no re-encryption of stored tokens.
- ASP.NET's cookie-protection keys are stored unencrypted on the data volume (an encrypted EBS volume, root-only);
  they are left out of backups, so a restore signs everyone out.
- The private model runs on CPU: a step design or a cover letter takes tens of seconds, and an 8B model designs less
  well than Claude. A GPU instance also needs the NVIDIA container toolkit and a Compose override for Ollama, which
  Phase 1 does not include.
- The deployment has been verified in containers here (deploy/README.md lists what), not yet on a real AWS account.
- The PII gate is a detector. It catches the user's own profile strings and common identifier shapes; it cannot prove
  a design holds no one's personal data. That is why free text never goes to Claude in the first place (I13).
- The Google OAuth client must be a **Web application** client with the studio's callback URL; the Desktop client used
  by the CLI does not work for the web.
