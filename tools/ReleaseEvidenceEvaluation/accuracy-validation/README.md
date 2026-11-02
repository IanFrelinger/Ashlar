# Accuracy follow-up validation

Production candidate: `359edd2a731a43c7f14f31233ca312b96a0a5bb0`. These are local development
checks, separate from the 39 snapshots, hosted CI, customer outcomes or release authorization.

- Required .NET/ASP.NET **8.0.33** certification gate: **3,989/3,989**, zero skipped. Derived-count
  (`expected >= 3,984`) and skip guards pass.
- .NET **10.0.14** GitHub/recollection suites and selector coverage: **235/235**, zero skipped.
- Core solution build: zero errors, two expected local-clone SourceLink warnings. Its first attempt
  failed because a nested restore attempted unreachable NuGet vulnerability data offline. The core-only
  retry applies `NuGetAudit=false` to both restore and build and passes. Both attempts remain; the
  original integrated controller exits **1**, not retroactively a pass. No dependency audit is claimed.
- Retained-study controls: **11 scorer, 5 runner-status, 9 verifier tests** pass, then **4 original
  and 2 supplemental runs** re-score byte-for-byte. Missing studies/reviews fail; old failures remain.

All .NET commands use SDK **10.0.403** through `scripts/test-in-container.sh`. The image is
`ashlar-devtest:slim`, ID `sha256:1d9680d11acd9cf5ff2c6f99b14ed6f4fcacf2f3ce005ba892fbe7ee01457be8`,
Ubuntu 24.04.5 with real 8/10 runtimes. Disposable containers had a read-only source mount,
6 GiB memory limit, `--network none` and `--security-opt no-new-privileges`. NuGet cache was warm;
this is not an installation/latency benchmark. Public CA trust was mounted read-only; TLS verification
was retained. The managed environment reported no configured secrets/runtime credentials. Live
capture's upstream GitHub proxy identity remains unknown. These limits do not establish general
control over agents, other tools or processes outside this test path.

Exact commands, actual-head/empty-porcelain proofs and logs are in `integrated/`. Core retry:

```bash
ASHLAR_DEVTEST_IMAGE=ashlar-devtest:slim bash scripts/test-in-container.sh \
  --ref 359edd2a731a43c7f14f31233ca312b96a0a5bb0 -- \
  'NuGetAudit=false dotnet restore Ashlar.LocalDevCore.slnf && NuGetAudit=false dotnet build Ashlar.LocalDevCore.slnf --no-restore -v minimal'
```

The ordinary helper alone does not set network/memory limits or session CA trust; use the offline
Docker wrapper recipe in the evaluation README to repeat the recorded restriction.

| Committed source mutation | Mutant failures / executed | Restored passes / executed |
|---|---:|---:|
| Disable recollection | 14 / 21 | 21 / 21 |
| Leak earlier read failures | 1 / 21 | 21 / 21 |
| Lose unknown legacy verdict after repeated change | 6 / 21 | 21 / 21 |
| Lose unknown assurance after repeated change | 6 / 21 | 21 / 21 |
| Permit a third collection | 5 / 21 | 21 / 21 |
| Ignore required app binding | 3 / 63 | 63 / 63 |
| Accept malformed provider ID as any app | 6 / 63 | 63 / 63 |
| Let a global flag hide a known failed context | 1 / 63 | 63 / 63 |
| Skip supplemental review declaration | 1 / 9 | 9 / 9 |

Each mutation proves its applied source diff, fails actual assertions, restores exact source with
empty porcelain, then passes the same tests. Compile/import errors, skipped and zero-test runs are
not kills. Recollection source is `a360af5fef0db2d2575ca4a61ee9520d6997ef41`, provider source
`76c8be38be32abe88781ffeb7549a969fd77cad2`, verifier source
`743bbf924bb8f39e4bea4bc04221be72487097f9`. Component proofs precede integration; the integrated
candidate separately passed the checks above. Local refs `codex/evidence-accuracy-recollection`
and `codex/evidence-accuracy-provider` retain component commits; no remote availability is claimed.

Component logs preserve earlier fixture/compile failures and corrections: the recollection fixture
omitted `merged_at`; provider fixtures exposed raw-string syntax and unknown-provider expectations.
Final component suites passed **179** and **211** respectively. Those earlier failures are not passes.

`archive-index.json` maps original paths to exact archived bytes and uncompressed hashes. Logs,
TRXs, patches and whitespace-bearing traces use deterministic gzip. Temporary clones are excluded.
`verified-counts.json` independently parses actual TRXs. Hashes establish local consistency, not
independent custody, signer identity or evidence truth.

Final packaging checks: **48 repository gates pass**, all **149** retained JSON packs validate with
JSON Schema 2020-12 plus format checking, and their Markdown pairs exist. The repository-gate session uses
the identical archived Linux subreaper from the prior phase because this environment's PID 1 does
not reap orphans; this is not a native PID-1 pass or a product isolation claim. Hosted Readiness and
networked lychee were not run. No pushes, releases, deployments or outreach occurred.
