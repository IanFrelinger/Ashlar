# User-acceptance tests

These scripts test **claims**, not code. A unit test asks whether a function does what the author
meant; these ask whether the product does what the documentation promises a user. That distinction is
the whole point: every defect this suite found on 2026-08-17 was a claim that used to be true, and in
each case the code was fine.

| tier | file | asks |
|------|------|------|
| 0 | `tier0-2.sh` | Can a tester clone, build and run `doctor` from `docs/TesterQuickstart.md` verbatim? |
| 1 | `tier0-2.sh` | Does a submitted task leave a retrievable record and a trust-log entry under its own id? |
| 2 | `tier0-2.sh` | Does the certification gate admit correct code and reject a weak witness? |
| 4 | `tier4.sh` | Does the reference brick build, and does the API actually fail closed? |
| 5 | `tier5.sh` | Do the golden paths in `docs/DEPLOYMENT.md` describe the compose files an operator would run? |
| 6 | `tier6.sh` | Turning MCP/A2A on does not open a door: protocol ingress stays credentialed on every verb |
| 7 | `tier7.sh` | The security negatives `SECURITY.md` claims: what is unmapped, what refuses, what is credentialed |
| 8 | `tier8.sh` | Do the docs still describe this repo — do the paths they name exist, and can the commands they print run? |
| 9 | `tier9.sh` | The release path: public-API baselines, and that the experimental surface is not in the stable promise |
| 10 | `tier10.sh` | Under concurrent submissions, is every task still on the record exactly once? |
| 11 | *(no script)* | The `cross-platform` job: the deterministic tiers, on Windows |

`tier6.sh` covers MCP ingress but **not** A2A ingress or agent-card anonymity: A2A refuses to start until
an exposed agent is actually registered, which an out-of-process run of the shipped host cannot do
(naming an unregistered id is still refused — verified). `McpA2AProtocolIngressProdStyleTests` covers
that surface in-repo with a test agent. Written down rather than quietly dropped, because a tier that
appears to cover something it does not is worse than one that says so.

`tier7.sh` tests what `SECURITY.md` **claims**, citing the claim in each check. One thing it deliberately
does *not* assert is tenant isolation: that page says tenant/org/user headers are "client-asserted" and
must be trusted only behind auth or an authenticating proxy, so a green on tenant isolation would be a
green on a guarantee the project explicitly declines to make — worse than no check at all. It starts the
API once per configuration, so it refuses to run at all if something already holds `:5000`, rather than
reporting "API did not start" for every phase and blaming the product for a dirty environment.

`tier8.sh` check 8.2 is the general form of the defect in #350: a documented `dotnet run --project X`
cannot run if `X` multi-targets and the command omits `-f`, and the command still *looks* correct on
the page, which is why rereading never catches it. Check 8.1 is deliberately scoped to pages a reader
is told to follow — swept across every page it produced ~85 hits and zero real defects, because
documentation legitimately names paths that do not exist (plans name paths they intend to create,
pages name files the reader creates, and `CONTRIBUTING` names one path in the negative to warn you off
it). A check that cries wolf gets muted, so it asks the narrower question that actually matters.

`tier5.sh` needs the docker CLI and so runs on the CI runner rather than inside the SDK container the
other tiers use. It only calls `docker compose config`, which resolves a stack without building or
starting anything; booting the portal stack and curling `/health` is a deliberate run, noted at the
foot of that script.

Tier 3 (the autonomy loop) is not here. It needs a container engine and a local model server, and a
gate that depends on that infrastructure teaches people to ignore it. Run it deliberately:
`spikes/autonomy-first-flight/run-first-flight.ps1 -SweepLive -Models <model> -MaxObjectives 1`.

## Running

Locally — clones fresh, which is the point of tier 0 (a cold first fifteen minutes):

```bash
docker run --rm -v "$PWD/tests/uat:/uat:ro" -w /work mcr.microsoft.com/dotnet/sdk:10.0 bash /uat/tier0-2.sh
```

Against a checkout you already have (this is how CI runs it, so the gate tests the commit under
review rather than whatever `master` happens to be):

```bash
UAT_REPO_DIR="$PWD" UAT_OUT=/tmp/uat bash tests/uat/tier0-2.sh
```

Each check prints one `RESULT<tab>tier<tab>id<tab>PASS|FAIL<tab>detail` line and appends it to
`$UAT_OUT/results.tsv`. A failing check never aborts the run — one bad claim must not hide the rest —
and the script exits non-zero at the end if anything failed.

## Writing a check

Assert a machine-verifiable outcome, and prefer running the documented thing over re-implementing it.
The strongest check in the suite greps the API command **out of** `docs/TesterQuickstart.md` and runs
it; that is what catches the page going stale, which no amount of testing the API itself would.

Six rules, each of which this suite violated at least once before it stopped lying:

1. **Never hardcode a verdict.** `time-to-first-audited-job` was written as an unconditional PASS and
   cheerfully reported "167s to a `CopilotTask` entry" during a run where no such entry existed. A
   stopwatch reading is not the claim; it now only passes if the audit entry is really there.
2. **Don't scrape a run log for evidence it doesn't contain.** Two gate tests were scored "did not
   run" because `dotnet test` prints test names only for *failures* at default verbosity. Establish
   presence with `--list-tests`, not by grepping output that was never going to say it.
3. **Anchor every parse.** A bare "first `http://` in the log" grep pointed `curl` at the Ollama probe
   URL that precedes the listen line, so three *security* checks reported connection failures as
   `000` — false negatives on the checks where a false negative is least acceptable. Anchor on
   `Now listening on:`.
4. **Separate the environment from the product.** The SDK image sets `ASPNETCORE_HTTP_PORTS=8080`,
   which made the documented `:5000` look like a defect. The scripts unset it so the container behaves
   like a tester's machine; when a check fails, rule out your own environment before filing anything.
5. **Never `wait` with no arguments.** These tiers run the API as a background job of the same shell,
   so a bare `wait` waits for a server designed never to exit. It hung a CI job for twenty minutes and
   read as "tier 10 hangs under concurrency" — the API's own log showed it idle and healthy throughout.
   Collect the PIDs you actually care about and wait on those.
6. **Rule 4 applies to throwaway scripts too.** The one-off reproduction written to *diagnose* rule 5's
   hang omitted the `unset`, so the API listened on 8080 while `curl` called 5000. Every request
   returned `000` instantly, and the evidence briefly appeared to say the host became unreachable the
   moment a second request arrived. Read the listen line out of the log; never assume the port.

And pair every negative with its positive control. "POST without an API key returns 401" proves
nothing on its own — an endpoint that is simply broken also fails to return 200. The paired check
("...and the same POST *with* the key returns 200") is what makes the first one mean *authentication*.

`tier10.sh` is a correctness check, not a benchmark. Timings are recorded for information and nothing
passes or fails on them, because a shared runner cannot support a latency claim. What it does assert is
the thing a load test usually misses: that a task which *ran* is on the record, once, under its own id.
It was written that way on purpose and found a defect on its first run — the store took an exclusive
file lock, so a second concurrent submission threw **after** the task had already executed, losing the
record while returning nothing a status-code check would flag.

Tier 11 is not a script. It is the `cross-platform` job in `uat-gate.yml`, which runs the deterministic
tiers on `windows-latest`: `TesterQuickstart` promises its commands work on Windows *and* in bash on
Linux/macOS, and the page was written on Windows, so a green on Linux alone is not evidence for half its
audience. The API-starting tiers stay on Linux — process and port handling differ per platform, and a
flaky cross-platform job would teach people to ignore the whole gate.

On Windows locally, the API-starting tiers can leave a process holding `:5000` if a run is interrupted;

Git Bash's `pkill` does not reliably kill it. `Get-NetTCPConnection -LocalPort 5000 | Stop-Process` does.
The tiers abort rather than run against a dirty port, so the symptom is a clear refusal, not a false
failure.

## Concurrency, and why tier 10 gates what it gates

Tier 10 found three defects stacked behind one another, each with the same user-visible symptom —
"concurrent submissions don't work" — and each only reachable once the one in front of it was gone:

1. the task store held an **exclusive file lock**, so the second concurrent write threw *after* the
   task had already run: the work happened and the record was lost;
2. `LifecycleManager._activeAgents` was a plain `Dictionary` shared across orchestrations, so one run
   mutated it while another enumerated and a null id reached `TryGetValue`;
3. concurrent runs registered different containers under the **same decomposition id** (the parser's
   fallback emits `fallback-1` every time), so a run executed the other run's container
   (`cannot execute from state Executing`) and `ShutdownAllAsync` tore down both.

The third was fixed with an **ambient run scope**: `LifecycleManager` keeps its agent map in an
`AsyncLocal`, and `OrchestrateAsync` opens one scope per run. Two runs may then reuse an id safely, and
because the scope flows with the async context, `IAgentTransport` — which resolves agents from the same
manager — sees the right run without a signature change. Serialising orchestration would have capped
throughput at one; a pool would have kept the shared state and added lifecycle; renaming ids would have
meant remapping dependency edges, execution order, outputs and the integrator consistently.

Fixing any one of the three alone looks like success to a check that counts HTTP 200s. Tier 10 asks
whether the **work reached the record**, which is why it kept failing until all three were gone.
The mock fallback still performs no work, so a finished run is `success: false` with the no-work
summary. Tier 10 counts those receipts. It still fails a body that says `cannot execute from state
Executing`, which is the shared-agent crash and not the placeholder.
