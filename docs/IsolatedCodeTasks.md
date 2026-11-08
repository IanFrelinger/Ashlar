# Isolated code tasks

An embedding host can opt into **one temporary Linux container per code task**. A brick
or workflow task calls `IIsolatedTaskRunner` when it needs this boundary. Ordinary data
classes and method calls retain their existing behavior. This does not automatically
implement the transport hints in [Agent execution isolation](AgentExecutionIsolation.md),
and [generation execution routing](runtime/ExecutionRouting.md) still selects model compute.

## Host opt-in

Reference `Ashlar.Infrastructure` and register the service explicitly:

```csharp
using Ashlar.Core.Application.Execution.Ports;
using Ashlar.Infrastructure.Execution.Sandbox;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();
services.AddAshlarIsolatedTasks();
using var provider = services.BuildServiceProvider();

var runner = provider.GetRequiredService<IIsolatedTaskRunner>();
var result = await runner.RunAsync(new IsolatedTaskRequest(
    "my-parser:reviewed", // Preload a trusted image; pin a digest for repeatability.
    new[] { "/app/parser", "--text", "some input" })
{
    MemoryBytes = 256L * 1024 * 1024,
    Cpus = 1m,
    Pids = 64,
    Timeout = TimeSpan.FromMinutes(1),
});

Console.WriteLine(result.Command.StdOut);
Console.WriteLine(result.Environment.ImageDigest);
```

The example image and program are supplied by the host. The image must be present on
the selected Docker daemon and include `/bin/sh` and `sleep`. Its normal entrypoint is
overridden for the keepalive. The image's configured user, environment and working
directory still apply. Package the program, dependencies and larger static inputs in
that image; building or pulling images is outside this API. Use an immutable image
reference, such as a repository digest, when reproducibility matters.

Inputs cross the boundary as **argv**, without a host shell. A shell is used for the
fixed keepalive only; task arguments are never interpolated into it. Callers can choose
an in-container shell as their program if that is the intended workload. Outputs are
exit code, stdout, stderr and the pre-execution environment inspection. Nonzero exit
codes remain results. CLR objects, host delegates, DI services and ambient execution
context are not transported. A parser can emit JSON for the caller to deserialize.

## Permissions and limits

The task API deliberately exposes a narrow permission set:

| Resource | Policy |
|---|---|
| Network | Disabled; no broader mode in this API |
| Host files, sockets, environment and credentials | No bind mounts or host environment forwarding |
| Root filesystem | Read-only |
| Declared writable scratch | `/tmp`, a temporary 512 MiB-capacity filesystem; pages also count against memory |
| Linux capabilities | All dropped; `no-new-privileges` enabled |
| Memory | 256 MiB by default, at least 6 MiB; Docker's swap policy remains engine-controlled |
| CPU | One CPU quota by default; configurable from 0.01 to 1024, subject to engine capacity |
| Processes/threads | 64 IDs by default, positive; keepalive counts toward the limit |
| Wall clock | One minute by default; positive and at most one day |
| Image fetching | Never; a missing image fails |

The image reference and budgets are **trusted host configuration**. Do not let generated
code select arbitrary images or raise its own limits. Image content is executable code:
image-defined users, environment, volumes and runtime behavior must be reviewed by the
operator. Use images without `VOLUME` declarations: image-defined anonymous volumes can
create additional writable paths, and this runner does not inspect or forbid those declarations.
This API does not provide a policy engine for vetting images or an image builder.

Before the task starts, the runner checks image identity, network isolation, read-only
root, dropped capabilities, privilege escalation prevention and effective memory/CPU/PID
caps. Missing evidence or weaker limits refuse execution. It uses the existing
`ISandboxedSessionRunner`, session inspection and provenance mechanisms; failures never
fall back to running the command on the host. A host-supplied session runner must honor
the same lifecycle and cancellation contracts. Existing registrations are preserved.

## Lifecycle and cleanup

The timeout covers allocation, inspection and task execution together. Expiry throws
`TimeoutException`; caller cancellation remains `OperationCanceledException`. Each path
disposes the owned session. Startup failure or cancellation also attempts removal by the
unique container name, even when Docker did not return a successful start response.
Removal uses an independent ten-second budget, so it can run after caller cancellation
without hanging forever. Cleanup failure is logged and does not replace the task result.

The fixed keepalive uses a finite sleep rounded up to whole seconds. With the trusted
image behaving normally, it exits at the lifetime ceiling and Docker's `--rm` removes the
container, including scratch and anonymous volumes, even if the host dies. Session
deadline labels remain compatible with `DockerSandboxSessionReaper`. The registration
provides the reaper but does **not** schedule it: a long-running host should periodically
call `SweepAsync` and sweep again after restart. A crashed host, unavailable daemon,
late Docker start or failed removal can leave residue until Docker or a later sweep
cleans it up. Neither disposal nor a deadline label guarantees cleanup during outages.

## Limits of this boundary

- Ordinary containers share the host kernel. This is **not a complete security sandbox
  for hostile code**. Use a suitably hardened execution service or VM boundary when the
  threat model requires it. This feature does not make certification claims.
- Docker CLI, a reachable Linux container engine and an appropriate preloaded image are
  required. Windows containers and images without the keepalive tools are unsupported.
- The existing egress guard is unchanged. Docker `exec`, inspection and cleanup are
  classified as `process:docker` network export; restrictive host policies can refuse
  them even for a network-disabled session. Authorize the required operations in the
  host's policy rather than bypassing the guard. See the settled limitation in
  [SPEC-007](specs/SPEC-007-security-labels-and-reference-monitor.md).
- Output capture uses the existing process runner and is buffered without a size cap.
  Use this for bounded-output workloads; container memory limits do not bound the host's
  captured output. Arguments, process listings and configured logs can expose inputs,
  so do not use argv to carry secrets.
- There is no writable host output directory, streaming input/output, dynamic dependency
  installation, per-method interception or transparent class migration. These require
  separately designed contracts. Portability comes from packaging a task in an image,
  and remains subject to image architecture and engine capabilities.

## Verification

Focused tests cover the opt-in composition and task lifecycle, plus session startup and
cleanup failure paths. From a clone with Docker available:

```bash
bash scripts/test-in-container.sh --dirty --filter 'FullyQualifiedName~IsolatedTaskRunnerTests|FullyQualifiedName~DockerSandboxSessionTests'
```

Fake-engine tests establish orchestration behavior; they do not establish kernel-level
containment. Real-engine checks require an explicitly enabled Docker test environment.
The included `Real_Docker_task_has_ephemeral_scratch_readonly_root_and_removes_container`
smoke test requires `ASHLAR_RUN_ISOLATED_TASK_DOCKER=1` and
`ASHLAR_ISOLATED_TASK_TEST_IMAGE` naming a preloaded Linux image with `/bin/sh` and `sleep`.
Set those variables inside a Docker-capable test environment with host policy permitting
the lifecycle operations. The standard devtest wrapper does not forward a Docker socket
or these variables. The smoke test checks scratch separation, read-only root and cleanup;
it does not establish resistance to hostile code or verify every resource constraint.
