# Dynamic operation preflight

Preflight is optional readiness work. It is not authorization, a dependency installer,
a test result, or a replacement for mandatory configuration and security validation.

## Separate phases

1. The operation selects its dependencies after resolving its options and plan. A
   functional-only operation does not need Docker merely because another capability does.
2. `DependencyPreflight` in `Ashlar.Infrastructure.Preflight` looks up just those probes,
   runs each selected dependency once, and returns a `DependencyPreflightReport`.
3. The caller acts on the report. It can use `DependencyPreflightPolicy.RequireAvailable`
   to raise `DependencyPreflightException`, return an error, warn, skip an optional
   feature, or explicitly choose an alternative. Those decisions do not belong to the
   runner and must not rewrite missing dependencies as available.
4. Execution still performs its mandatory authorization, configuration, egress and
   security checks. Availability may change between preflight and execution.

For example, an operation can assemble these requirements dynamically from its plan:

```csharp
var requirements = new List<DependencyRequirement> { new("source-control") };
if (plan.RequiresVisualChecks)
    requirements.Add(new("visual-service"));
var report = await preflight.RunAsync("generate", requirements,
    enabled: options.Preflight, cancellationToken);

// Caller-owned policy, outside the runner. A different caller can handle the same
// report differently. No missing-dependency exception is raised by RunAsync itself.
DependencyPreflightPolicy.RequireAvailable(report);
```

Register trusted `DependencyProbe` adapters with the runner. Each adapter supplies an ID
and an asynchronous, read-only check returning a `DependencyObservation`. The runner has
no built-in Docker, model-provider or package-manager knowledge. Provider-specific
availability code, bootstrap checks, and existing process/HTTP enforcement should be
reused by adapters rather than duplicated. No arbitrary commands come from a manifest.

## Results and deadlines

Each result preserves the dependency ID, whether this operation required it, and one of:

- `Available`: the selected probe succeeded.
- `Missing`: an adapter established that a necessary executable/dependency is absent.
- `Unavailable`: present but unusable, or a probe could not start. For example, a failed
  service/download is not proof that an executable is missing.
- `Unsupported`: the adapter cannot check this platform or dependency version.
- `TimedOut`: the check exceeded its deadline.
- `NotRegistered`: the operation requested an ID with no adapter. This is not success.
- `NotChecked`: optional preflight was disabled or the operation budget was exhausted.

`Ran`, `Complete`, and `RequiredAvailable` distinguish skipped/incomplete work from a
verified set of required dependencies. Optional failures remain visible but do not block
required dependencies. Repeated IDs run once; a required declaration wins over an optional
one. Results are not cached between operations, so recovery or later failure is observed.
Runtime JSON uses `preflightRan: false` and `preflightOk: null` when optional preflight
was skipped; operation success is separate from preflight evidence.

Each probe has an eight-second default deadline and the operation a thirty-second total
budget, both configurable up to five minutes. An optional `TimeProvider` controls both elapsed
budget and deadline timers; production defaults to `TimeProvider.System`. Caller cancellation
propagates. Probes must
return an asynchronous task promptly and honor cancellation; the runner bounds its wait
for a defective adapter but cannot forcibly stop arbitrary in-process code. I/O adapters
must use cancellable, bounded existing mechanisms. The visual adapters reuse `TimedProcess`,
including its process-tree cleanup and egress enforcement. They retain fixed diagnostics,
not raw stdout/stderr or credentials.

Programming errors and mandatory authorization/security refusals propagate as exceptions.
They are never translated into dependency failures eligible for fallback.

## Current integration and limits

| Entry point | Integration |
|---|---|
| `runtime execute`, visual capability assessment | Shares the generic runner and visual adapters. Checks only when tests, preflight, the aesthetic/visual path and explicit capability degradation are enabled for a strict visual plan. The runtime caller retains its existing fallback decision. |
| `self-extend preflight`, visual QA readiness | Selects visual dependencies only when tests and the aesthetic/visual path are selected. Includes original dependency observations under the visual check's `dependencies` field in JSON. Existing caller policy determines whether missing visual capability blocks the operation. |
| `self-extend run`, visual QA execution readiness | Reuses the same bounded adapters immediately before visual execution. Skipping optional preflight does not remove this execution-time check or any mandatory guard. |
| CI reviewer, coordinator, repository gates, developer dogfood and container profiles | Existing script profiles in PR #904 remain a separate scripting adapter surface. This change does not replace that PR or claim it is C# runtime integration. |
| Other runtime providers, service startup, deployment, release and Make entry points | Existing provider/bootstrap/startup checks remain in place. They have not all been migrated to the generic contract. |

This is a reusable foundation with a visual-operation vertical slice, not universal
coverage of Ashlar. It does not certify model availability, package-feed access, credentials,
all transitive dependencies, restore success, test discovery, or the later operation.
Adding an operation requires explicit dependency selection, adapters reusing existing
mechanisms, a caller-owned outcome policy, and positive/negative tests. Installation or
remediation, if requested separately, must remain outside preflight and obey its own
permissions and confirmation rules.
