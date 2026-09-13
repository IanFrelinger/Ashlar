# Tester quickstart

One lane, one command sequence, about fifteen minutes. You will build Ashlar from source, run the API on loopback, submit one task and read its output **and** the audit trail it left, then watch the certification gate admit correct code and reject buggy code. No API keys, no model server, no Docker.

Every command below was last run end to end on 2026-08-17 against a clean clone of `master` inside `mcr.microsoft.com/dotnet/sdk:10.0` with a cold package cache -- clone to an audited job in under three minutes -- and the same commands work in bash on Linux/macOS and in PowerShell on Windows. That run is what caught the missing `-f net10.0` in section 3: the page had been verified on SDK 9 before `Ashlar.API` began multi-targeting, and a command that used to work had rotted. `Ashlar.API` targets `net10.0` alone again, so the flag is no longer required — the command works with or without it. If a command on this page does not do what it says, that is a bug worth reporting (section 5).

## 0. Prerequisites

- Git and the **.NET SDK 10.x** (`global.json` pins the 10.0 band; `dotnet --version` should print `10.0.x`). The CLI and API target `net10.0`; libraries multi-target `net8.0;net10.0`, and executables roll forward (`RollForward=Major`, set in `Directory.Build.targets`), so you do **not** need any other runtime.
- **Docker is optional.** Nothing on this page needs it. It is required only for the experimental autonomy loop (section 6), which builds and runs model-proposed code inside attested containers.
- No provider credentials. The walk-through uses the mock provider, which the runtime refuses to use unless you set `ASHLAR_ALLOW_MOCK=1` explicitly (a fail-closed default; see `src/Ashlar.Infrastructure/Execution/ProviderFactory.cs`).

## 1. Clone and build the kernel

```bash
git clone https://github.com/IanFrelinger/Ashlar.git && cd Ashlar
dotnet build Ashlar.Kernel.sln
```

`Ashlar.Kernel.sln` is the kernel spine plus its test projects; it also builds `Ashlar.API`, because `src/Ashlar.Tests.Infrastructure/Ashlar.Tests.Infrastructure.csproj` hosts the API in-process. Expect a few minutes cold. `Ashlar.CLI` is not in this solution and builds on its first `dotnet run` in the next step.

## 2. Ask the doctor

Run from the repository root (the doctor's CLI smoke check re-invokes `dotnet run --project application/src/Ashlar.CLI -- --help`, so the relative path must resolve):

```bash
dotnet run --project application/src/Ashlar.CLI -- doctor
```

Look for the `overall: PASS` line (it is followed by a few "recommended next steps"). `container smoke: warn` is normal when Docker is absent or the daemon does not answer within eight seconds; the container lane is optional. Add `--json` for machine-readable output.

`doctor` probes the Docker **daemon** with a timed `docker info` (eight seconds, then the process tree is killed). It does **not** pull `ghcr.io/ianfrelinger/nexo-cli:latest` as a blocking smoke. If Docker Desktop is installed but wedged, expect `container smoke: warn` and `docker daemon did not answer within 8s` rather than a hang. The recommended next step still names `docker run --rm ghcr.io/ianfrelinger/nexo-cli:latest --help` for an operator who wants the published image.

## 3. The hero: submit a task, read the audit trail

Start the API in one terminal. It listens on `http://localhost:5000` (loopback), **HTTP only, no authentication** - the shipped defaults are `Ashlar:Security:ExposureProfile=Localhost` and `AuthorizationMode=None` (`application/src/Ashlar.API/appsettings.json`). The exposure rule is fail-closed: declaring `Lan`, `Tailnet` or `Public` without a built-in auth mode makes the API **refuse to start** (`application/src/Ashlar.API/Program.cs`, "Security: exposure profile"). Do not put this process on a network as-is; see `SECURITY.md`.

```bash
# bash
ASHLAR_ALLOW_MOCK=1 dotnet run --project application/src/Ashlar.API -f net10.0
```

```powershell
# PowerShell
$env:ASHLAR_ALLOW_MOCK = '1'
dotnet run --project application/src/Ashlar.API -f net10.0
```

`-f net10.0` is optional here — `Ashlar.API` targets `net10.0` only — but harmless, and it keeps the command identical to the one in `docs/GettingStarted.md`.

Wait for `Now listening on: http://localhost:5000`. In a second terminal:

```bash
curl -s http://localhost:5000/health
# {"status":"healthy","timestamp":"..."}

curl -s http://localhost:5000/api/copilot/task \
  -H "Content-Type: application/json" \
  -d '{"task": "Summarize what this repository does", "auditCount": 5}'
```

```powershell
Invoke-RestMethod http://localhost:5000/health

$r = Invoke-RestMethod -Method Post -Uri http://localhost:5000/api/copilot/task `
  -ContentType 'application/json' `
  -Body (@{ task = 'Summarize what this repository does'; auditCount = 5 } | ConvertTo-Json)
$r | Select-Object taskId, success, summary
```

The route is `POST /api/copilot/task` (`application/src/Ashlar.API/Endpoints/AshlarEndpoints.cs`); the body is `CopilotTaskRequest(string Task, int AuditCount = 25)` (`application/src/Ashlar.API/Endpoints/CopilotTaskRequest.cs`). The response is `CopilotTaskResponse`: `taskId`, `tenantId` (`default` unless you send `X-Ashlar-Tenant`), `success`, `summary` (for example `1 agent(s) executed`), `output` (per-agent results), `isTrustPaused`, and `recentAudit`. Two things to know when reading it:

- With the mock provider the `output` text is deterministic scaffolding (a "fallback decomposition"), not model output. The point of this page is the **record**, not the prose. To route to a real model, set `ASHLAR_OLLAMA_BASE_URL` / `OPENAI_API_KEY` instead of `ASHLAR_ALLOW_MOCK` (see `docs/Configuration.md`).
- `recentAudit` holds the entries recorded **before** this task (the handler snapshots the log, then records the task), so it is `[]` on your first call and shows the previous task on the second.

Now read the trail the task left:

```bash
TASK_ID=<taskId from the response>
curl -s http://localhost:5000/api/copilot/tasks/$TASK_ID     # the stored record: task, submittedAt, completedAt, success, summary
curl -s http://localhost:5000/api/copilot/tasks              # history, newest first
curl -s http://localhost:5000/api/trust/dashboard            # recentAudit: eventType=CopilotTask, disposition=Success, sourceId=<taskId>
curl -s http://localhost:5000/api/activity/feed              # the same event as an activity entry
curl -s http://localhost:5000/api/trust/status               # isPaused, active policy pack
```

```powershell
Invoke-RestMethod "http://localhost:5000/api/copilot/tasks/$($r.taskId)"
(Invoke-RestMethod http://localhost:5000/api/trust/dashboard).recentAudit |
  Select-Object eventType, disposition, sourceId
```

That is the whole claim in one screen: the task ran, its record is stored under a task id, and the trust log carries a `CopilotTask` entry whose `sourceId` is that id. The portal at `http://localhost:5000` shows the same data (for Swagger at `/swagger`, restart the API with `Ashlar__Api__EnableSwagger=true` — it is off outside Development). Stop the API with Ctrl+C when done.

## 4. See it certify

The certification gate is what makes "certified" a checkable claim rather than a label: analyzer fence, then witness (correctness), then mutation testing (does the witness have teeth), then determinism. It runs in CI as `cert-gate`; reproduce it locally with the same filter (`scripts/cert-gate-config.sh` is the single source of truth for that filter):

```bash
bash scripts/run-cert-gate.sh
```

If you want the smallest slice (the two gate-teeth classes, 16 tests, under two minutes on a laptop), run them directly:

```bash
dotnet test src/Ashlar.Tests.Infrastructure/Ashlar.Tests.Infrastructure.csproj -f net10.0 \
  --filter "FullyQualifiedName~CertificationGateTeethTests"
```

Watch for the pair that defines the gate: `GoodBrick_StrongWitness_Admits_WithZeroEscapeRate` (ADMIT, `escape_rate=0`) and `WeakWitness_AllowsMutantEscapes_RejectsWithTeeth` (REJECT on `mutation`). The ledger row for each, with the CI run that proved it, is in `docs/certification-evidence.md`.

To author something the gate can judge, start from the reference brick: `samples/hello-brick/README.md` (`dotnet test samples/hello-brick/HelloBrick.Tests/HelloBrick.Tests.csproj`), then `docs/AuthoringBricks.md`. The sample uses a `ProjectReference` into `src/` (no feed required). Packages on nuget.org pin `ci/published-version`.

## 5. What to test, what to report

Worth your time:

- **Time to first audited job.** Fresh clone to a `CopilotTask` entry in `/api/trust/dashboard`. Note the wall-clock and every place you had to guess.
- **Does the doctor tell the truth?** Break something (rename `dotnet`, unset the SDK) and see whether `doctor` names it.
- **Does the API fail closed?** Set `Ashlar__Security__ExposureProfile=Lan` with no auth mode and confirm the refusal to start; then set `Ashlar__Security__AuthorizationMode=ApiKey` and `Ashlar__Security__ApiKey=...` and confirm `POST /api/copilot/task` returns 401 without the `X-Ashlar-Api-Key` header.
- **Does the gate have teeth?** Weaken a witness in `src/Ashlar.Tests.Infrastructure/Tests/Certification/` and confirm the mutation leg rejects.
- **Do these docs match the code?** Every path and command on this page is meant to be exact.

Report through the issue templates: `.github/ISSUE_TEMPLATE/bug_report.md`, `.github/ISSUE_TEMPLATE/feature_request.md`, `.github/ISSUE_TEMPLATE/integrator_feedback.md`. Include OS, `dotnet --version`, the exact command, and the output. Security findings go through `SECURITY.md`, not a public issue.

## 6. Known limitations (read before judging)

- The certification gate's own limits are listed under **Known v0 limitations** in `docs/certification-evidence.md`, now nine rows (row 9 closed in part on 2026-09-13 - an explicit composition signing key is honoured; compositions still have no operator path to a real key): a development HMAC signer rather than PKI, a type-level (not semantic) composition seam check, runtime-derived expected test counts, the exact boundary of session containment — and, added 2026-08-27, two ways a forged record can be made to verify (a signature downgrade, limitation 7, and a schema downgrade that additionally frees the gate name and the list of gates passed from the signed bytes, limitation 8) plus limitation 9, which as of 2026-09-13 is closed in part: the composition signer now honours an explicitly supplied key, but compositions still have no operator path to a real key. Read 7–9 before judging any certificate.
- The **autonomy loop is experimental and ships in hold mode**: it certifies fully and admits nothing (`HoldAdmission=true` by default, `src/Ashlar.Infrastructure/Autonomy/AshlarAutonomyOptions.cs`). It needs a container engine and a local model server, and its evidence so far is spike-grade (`docs/certification-evidence.md`, rows P2 through S5). Start from `samples/autonomy-objectives/README.md`; the flight runner `spikes/autonomy-first-flight/run-first-flight.ps1` is a spike, not a supported entry point (`spikes/README.md`).
- Local defaults are HTTP-only with no auth. That is intentional for this page and wrong for any network.
- Packages are published on nuget.org (0.1.1 and 0.1.2). You can also consume by `ProjectReference` (`samples/hello-brick/`) or from a feed you supply (`consumer-template/CONSUMING.md`). [corrected 2026-09-13: this line previously said no packages were published.]
- The mock provider proves the plumbing and the record, not model quality.

Next: `docs/GettingStarted.md` for the pipeline and CLI tour, `docs/CopilotMvpWalkthrough.md` for the portal and trust-control commands, `docs/IntegratorGuide.md` to embed Ashlar in your own host, `docs/DocsIndex.md` for everything else.
