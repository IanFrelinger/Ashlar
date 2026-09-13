# Commercial CI coverage

Native readiness already builds and executes all three commercial test projects through
`ci verify` → root `validate` → `ValidationServiceAdapter`. That reaches all five commercial
production projects through project references. The sweep runs Fleet and MeshDirector on
net8.0, and Fleet.Host on net10.0, in Debug. It excludes Stress and DockerOptional categories.
The commercial path, receipt registry, checker and file controls are included in readiness's automatic change selection. The manual
composition gate supplements this route.

This corrects the earlier claim that no automatic workflow compiled commercial code. At master
`a0a386169e5270834aa55a53b10110ef6c448fcd`, readiness run
[34721291876](https://github.com/IanFrelinger/Ashlar/actions/runs/34721291876) recorded:

- Fleet: 196 passed on net8.0.
- Fleet.Host: 7 passed on net10.0.
- MeshDirector: 4 passed on net8.0.

Each suite has a test-host path, completed run and parsed TRX receipt in the
[Linux](https://github.com/IanFrelinger/Ashlar/actions/runs/34721291876/job/103627541043),
[macOS](https://github.com/IanFrelinger/Ashlar/actions/runs/34721291876/job/103627541038) and
[Windows](https://github.com/IanFrelinger/Ashlar/actions/runs/34721291876/job/103627541034) jobs.
These counts describe that run, not permanent test floors. The quiet build output does not
list each production DLL; validation awaits a successful project build before its no-build test.
The tests include fakes, in-process ASP.NET hosting and real LiteDB cases. They do not establish
a deployed, distributed fleet. Original Linux mutation measurements remain Linux measurements.

`build-core` alone does not build commercial code. `Ashlar.sln` reaches five of eight commercial
projects; Fleet.Api, Fleet.Host and Fleet.Host tests are outside the solution. A YAML search or
a solution-only inventory therefore misses the validation route.

Two checks protect distinct evidence:

- `CommercialCoverageConventionTests` calls the real validation discovery and framework selectors
  for the registry in [commercial-test-suites.json](../ci/commercial-test-suites.json). It checks
  that all eight commercial projects are reachable from the three test roots through literal,
  unconditional commercial project references. External references must exist, but their graphs
  are outside this static lens. Conditional or dynamic commercial edges require revisiting it.
- Native readiness runs [verify-commercial-receipts.py](../scripts/ci/verify-commercial-receipts.py)
  after `ci verify`, then uploads the TRXs and a receipt summary. The fresh checkout must contain
  exactly one TRX under each project's `TestResults`. The checker requires successful nonempty
  execution, consistent counters and result/definition/entry identities, and matching
  project/Debug/framework/assembly paths. It handles the flat xUnit/VSTest layout these suites
  currently emit and fails clearly on unsupported nested layouts. It does **not** prove every
  discoverable test was selected. File-based controls run in the required `shell-lint` check. `CommercialReceiptRoutingTests` pins
  the three receipt inputs in both path lists and the direct verification/control commands,
  including their job-level failure propagation.

To observe this wiring after changing it, inspect all three native `Verify commercial validation
receipts` steps and the corresponding `readiness-*` artifacts. Each should report all three suite
paths/frameworks and contain their TRXs plus `commercial-receipts/receipts.json`. A successful
workflow that skipped the native lanes does not exercise this check.
