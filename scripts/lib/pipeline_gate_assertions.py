#!/usr/bin/env python3
"""Assertions over `ashlar pipeline run --format-json` output, shared by the pipeline CLI gates.

Used by scripts/kernel-gate-tier-b.sh (which compat-gate Tier B runs) and scripts/ship-gate-tier-a.sh.
The contract is docs/ProductionReadinessGate-v1.md section 3.C and 3.D.

WHY TWO KINDS OF RUN. Since #363 the registered default pipeline adapters are placeholders that FAIL a
stage instead of reporting success for work they never did. So a gate has two different things to prove,
and a run proves only one of them:

  unconfigured  ASHLAR_ALLOW_MOCK unset. The run must fail closed: ok=false, state Failed, and the
                ingest stage's error naming the placeholder. This is the safety property.
  configured    ASHLAR_ALLOW_MOCK=1, the CI-only stub mode of the same adapters (#522). The run must
                complete, and the work must be attributed to the stub's worker id. This proves the
                orchestration: scheduling, the Hybrid fallback chain, and LiteDB resume to completion.
                It does not prove a real engine; none is registered.

Before this helper, kernel-gate-tier-b.sh asserted the configured outcome on a run it never configured.
It passed only when its caller exported ASHLAR_ALLOW_MOCK=1 (kernel-gate.yml does, compat-gate.yml does
not), so compat-gate was red from 2026-09-12 on a check that tested its caller's environment.

USAGE (exit 0 and one "pipeline-gate: PASS" line, or exit 1 and one "pipeline-gate: FAIL" line)
  pipeline_gate_assertions.py fail-closed        <log> <exit-code> <label>
  pipeline_gate_assertions.py configured-success <log> <exit-code> <label>
  pipeline_gate_assertions.py fallback           <log> <exit-code> <label>
  pipeline_gate_assertions.py resume <source-log> <source-exit> <target-log> <target-exit> <completed|failed> <label>

<log> is the CLI's captured stdout; the payload is its last line that is a JSON object.
"""
import json
import sys

PLACEHOLDER_ERROR = "No deterministic pipeline adapter is configured"
MOCK_DETERMINISTIC_WORKER = "deterministic-default-mock"
MOCK_AGENTIC_WORKER = "agentic-default-mock"
MISSING_RESUME_SOURCE = "no prior run was found"


class GateFailure(Exception):
    pass


def require(condition, message):
    if not condition:
        raise GateFailure(message)


def read_text(path):
    try:
        with open(path, encoding="utf-8") as fh:
            return fh.read()
    except OSError as exc:
        raise GateFailure(f"cannot read {path}: {exc}")


def payload_of(path):
    lines = [line.strip() for line in read_text(path).splitlines()]
    candidates = [line for line in lines if line.startswith("{") and line.endswith("}")]
    require(candidates, f"no JSON payload in {path}")
    try:
        payload = json.loads(candidates[-1])
    except ValueError as exc:
        raise GateFailure(f"last JSON line of {path} does not parse: {exc}")
    require(isinstance(payload, dict), f"payload of {path} is not an object")
    return payload


def exit_code(text):
    try:
        return int(text)
    except ValueError:
        raise GateFailure(f"exit code {text!r} is not an integer")


def stages_of(payload):
    data = payload.get("data")
    require(isinstance(data, dict), "payload has no data object")
    stages = data.get("stages")
    require(isinstance(stages, list) and stages, "payload lists no stages")
    return data, stages


def stage(stages, stage_id):
    found = [s for s in stages if isinstance(s, dict) and s.get("stageId") == stage_id]
    require(len(found) == 1, f"expected exactly one '{stage_id}' stage, found {len(found)}")
    return found[0]


def check_fail_closed(path, rc):
    require(rc != 0, f"exit code 0: a run that did no work exited as a success ({path})")
    payload = payload_of(path)
    require(payload.get("ok") is False, f"ok={payload.get('ok')!r}, expected false")
    data, stages = stages_of(payload)
    require(data.get("state") == "Failed", f"state={data.get('state')!r}, expected 'Failed'")
    ingest = stage(stages, "ingest")
    require(ingest.get("state") == "Failed", f"ingest state={ingest.get('state')!r}, expected 'Failed'")
    error = ingest.get("error") or ""
    require(PLACEHOLDER_ERROR in error,
            f"ingest error does not name the unconfigured placeholder: {error!r}")
    ran = [s.get("stageId") for s in stages if s.get("state") == "Completed"]
    require(not ran, f"stages reported Completed with no adapter configured: {ran}")
    return f"exit={rc} ok=false state=Failed ingest.error names the placeholder; no stage Completed"


def check_configured_success(path, rc):
    require(rc == 0, f"exit code {rc}, expected 0")
    payload = payload_of(path)
    require(payload.get("ok") is True, f"ok={payload.get('ok')!r}, expected true")
    data, stages = stages_of(payload)
    require(data.get("state") == "Completed", f"state={data.get('state')!r}, expected 'Completed'")
    pending = [s.get("stageId") for s in stages if s.get("state") != "Completed"]
    require(not pending, f"stages not Completed: {pending}")
    ingest = stage(stages, "ingest")
    require(ingest.get("workerId") == MOCK_DETERMINISTIC_WORKER,
            f"ingest workerId={ingest.get('workerId')!r}, expected {MOCK_DETERMINISTIC_WORKER!r} "
            "(the configured stub); success from any other worker is not what this run configured")
    return f"exit=0 ok=true state=Completed {len(stages)}/{len(stages)} stages; ingest by {MOCK_DETERMINISTIC_WORKER}"


def check_fallback(path, rc):
    require(rc == 0, f"exit code {rc}, expected 0")
    payload = payload_of(path)
    require(payload.get("ok") is True, f"ok={payload.get('ok')!r}, expected true")
    data, stages = stages_of(payload)
    require(data.get("state") == "Completed", f"state={data.get('state')!r}, expected 'Completed'")
    hybrid = stage(stages, "hybrid")
    require(hybrid.get("state") == "Completed", f"hybrid state={hybrid.get('state')!r}, expected 'Completed'")
    require(hybrid.get("workerType") == "Agentic",
            f"hybrid workerType={hybrid.get('workerType')!r}: the fallback chain did not switch to Agentic")
    require(hybrid.get("workerId") == MOCK_AGENTIC_WORKER,
            f"hybrid workerId={hybrid.get('workerId')!r}, expected {MOCK_AGENTIC_WORKER!r}")
    return f"exit=0 ok=true state=Completed hybrid fell back to Agentic ({MOCK_AGENTIC_WORKER}) at attempt {hybrid.get('attempt')}"


def check_resume(source_path, source_rc, target_path, target_rc, mode):
    require(mode in ("completed", "failed"), f"resume mode {mode!r} is not 'completed' or 'failed'")
    require(source_rc != 0, "source run exited 0; the resume scenario needs a failed source")
    source = payload_of(source_path)
    source_data, source_stages = stages_of(source)
    require(source_data.get("state") == "Failed",
            f"source state={source_data.get('state')!r}, expected 'Failed'")
    source_ingest = stage(source_stages, "ingest")
    require(source_ingest.get("state") == "Failed", "source ingest stage did not fail")
    source_attempt = source_ingest.get("attempt")
    require(isinstance(source_attempt, int) and source_attempt >= 1,
            f"source ingest attempt={source_attempt!r}, expected an integer >= 1")

    require(MISSING_RESUME_SOURCE not in read_text(target_path),
            "the resumed run did not find the persisted source run")
    target = payload_of(target_path)
    _, target_stages = stages_of(target)
    target_ingest = stage(target_stages, "ingest")
    target_attempt = target_ingest.get("attempt")
    # Resume carries a failed stage's attempt count forward and re-runs it, so a resumed ingest is
    # numbered after the source's. A fresh run, which is what a lost store produces, starts at 1.
    require(isinstance(target_attempt, int) and target_attempt > source_attempt,
            f"target ingest attempt={target_attempt!r} is not after the source's {source_attempt}: "
            "the target did not resume from the persisted run")

    if mode == "completed":
        detail = check_configured_success(target_path, target_rc)
    else:
        detail = check_fail_closed(target_path, target_rc)
    return f"source Failed at ingest attempt {source_attempt}; target resumed at attempt {target_attempt}; {detail}"


def main(argv):
    if len(argv) < 2:
        print(__doc__, file=sys.stderr)
        return 2
    command, args = argv[1], argv[2:]
    arity = {"fail-closed": 3, "configured-success": 3, "fallback": 3, "resume": 6}
    if command not in arity or len(args) != arity[command]:
        print(f"pipeline-gate: usage error: {command} {args}", file=sys.stderr)
        print(__doc__, file=sys.stderr)
        return 2
    label = args[-1]
    try:
        if command == "fail-closed":
            detail = check_fail_closed(args[0], exit_code(args[1]))
        elif command == "configured-success":
            detail = check_configured_success(args[0], exit_code(args[1]))
        elif command == "fallback":
            detail = check_fallback(args[0], exit_code(args[1]))
        else:
            detail = check_resume(args[0], exit_code(args[1]), args[2], exit_code(args[3]), args[4])
    except GateFailure as exc:
        print(f"pipeline-gate: FAIL {command} [{label}]: {exc}", file=sys.stderr)
        return 1
    print(f"pipeline-gate: PASS {command} [{label}]: {detail}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
