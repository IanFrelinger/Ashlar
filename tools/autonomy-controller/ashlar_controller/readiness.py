"""Bounded readiness: parse the three gate commands' outputs into stable failure identities, run one gate
without retrying, build one fixer launch, and tell a refuted fix from a landed one.

Everything here is a pure function over text except `run_gate` (one subprocess call, injected) and
`launch_fixer` (one adapter call, plus exactly one general-purpose fallback when the named agent role
does not exist). The controller's retry counter keys on `GateFailure.identity`, so an identity must be
equal across two cycles whose outputs differ only in noise (timestamps, durations, absolute paths, temp
dirs, hex ids) and different when the failing test set or the failing gate differs. `normalise` is the
only place that noise is defined.
"""
from __future__ import annotations

import hashlib
import json
import os
import re
import subprocess
import tempfile
from dataclasses import dataclass, field
from typing import Any, Callable, Dict, Iterable, List, Optional

from .models import LaunchRequest, LaunchResult


@dataclass
class GateFailure:
    gate: str
    identity: str      # "gate:<name>:<12 hex>"; stable across runs and cycles, see `failure_identity`
    evidence: str      # the raw (un-normalised) lines the identity was computed from


@dataclass
class GateReport:
    source: str                              # "readiness-local" | "repo-gates" | "container-tests"
    commit: Optional[str]
    failures: List[GateFailure] = field(default_factory=list)
    passed: List[str] = field(default_factory=list)
    raw: Dict[str, Any] = field(default_factory=dict)

    @property
    def ok(self) -> bool:
        return not self.failures


class GateInfrastructureError(Exception):
    """The gate script itself did not produce a verdict (crashed, timed out, printed nothing parsable).
    Not a gate failure: nothing here is attributable to the code under test, so no fixer is launched."""

    def __init__(self, evidence: str):
        super().__init__(evidence)
        self.evidence = evidence


# ---------------------------------------------------------------------------------------------------
# Normalisation and identities
# ---------------------------------------------------------------------------------------------------

# Order matters: temp dirs before generic paths (a temp path would otherwise keep a pid-bearing
# basename); uuids before plain hex (a uuid's 4-char segments are below the hex floor); ISO stamps
# before bare HH:MM:SS (the time part of a stamp is a bare clock as well).
_UUID = re.compile(r"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b")
_ISO = re.compile(r"\b\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}:\d{2}(?:[.,]\d+)?(?:Z|[+-]\d{2}:?\d{2})?\b")
_CLOCK = re.compile(r"\b\d{1,2}:\d{2}:\d{2}(?:[.,]\d+)?\b")
_BRACKET_DUR = re.compile(r"\[\s*[<>]?\s*\d+(?:[.,]\d+)?\s*(?:ms|s|m|min|h)\s*\]")
_KV_DUR = re.compile(r"\b(dur|duration|elapsed|took)(\s*[=:]\s*)\d+(?:[.,]\d+)?\s*(?:ms|s|m|min|h)?\b", re.I)
_UNIT_DUR = re.compile(r"\b\d+(?:[.,]\d+)?\s?(?:ms|s)\b")
_TMP = re.compile(
    r"(?:/tmp|/var/tmp|/private/tmp|/var/folders|/dev/shm"
    r"|[A-Za-z]:[\\/](?:[^\\/\s'\"`]+[\\/])*?(?:Temp|tmp|TEMP))"
    r"[\\/][^\s'\"`]*")
_POSIX_PATH = re.compile(r"(?<![\w.:\\/])/(?:[\w.@+~-]+/)+[\w.@+~-]*")
_WIN_PATH = re.compile(r"\b[A-Za-z]:[\\/](?:[^\\/\s'\"`<>|]+[\\/])*[^\\/\s'\"`<>|]*")
_HEX = re.compile(r"\b[0-9a-fA-F]{7,}\b")
_WS = re.compile(r"[ \t]+")


def _basename(m: "re.Match[str]") -> str:
    p = m.group(0).rstrip("\\/")
    base = re.split(r"[\\/]", p)[-1]
    return base or "<path>"


def normalise(line: str) -> str:
    """Strip everything that legitimately differs between two runs of the same failure.

    Removed, in this order: CR and trailing whitespace; temp dirs (whole path -> <tmp>); uuids and
    ISO timestamps; bare HH:MM:SS clocks; bracketed durations ("[12 ms]"), key durations ("dur=3s",
    "Duration: 1 s"), bare unit durations ("2.1s", "350 ms"); absolute POSIX and Windows paths
    (kept: the basename only); hex runs of 7 or more characters (short shas, ids); whitespace runs."""
    s = line.replace("\r", "").rstrip()
    s = _TMP.sub("<tmp>", s)
    s = _UUID.sub("<id>", s)
    s = _ISO.sub("<ts>", s)
    s = _CLOCK.sub("<time>", s)
    s = _BRACKET_DUR.sub("[<dur>]", s)
    s = _KV_DUR.sub(lambda m: m.group(1) + m.group(2) + "<dur>", s)
    s = _UNIT_DUR.sub("<dur>", s)
    s = _WIN_PATH.sub(_basename, s)
    s = _POSIX_PATH.sub(_basename, s)
    s = _HEX.sub("<hex>", s)
    s = _WS.sub(" ", s).strip()
    return s


def failure_identity(gate: str, evidence_lines: Iterable[str]) -> str:
    """"gate:<name>:" + the first 12 hex of sha256 over the normalised, de-duplicated, sorted evidence.

    Sorted so that the same failing set reported in another order (parallel test runners) keeps its
    identity; de-duplicated so a repeated line adds nothing. Blank lines after normalisation are not
    evidence."""
    lines = sorted({normalise(l) for l in evidence_lines if normalise(l)})
    digest = hashlib.sha256("\n".join(lines).encode("utf-8")).hexdigest()
    return f"gate:{gate}:{digest[:12]}"


def _failure(gate: str, evidence_lines: List[str]) -> GateFailure:
    return GateFailure(gate=gate, identity=failure_identity(gate, evidence_lines), evidence="\n".join(evidence_lines))


def _lines(text: str) -> List[str]:
    return text.replace("\r\n", "\n").replace("\r", "\n").split("\n")


# ---------------------------------------------------------------------------------------------------
# Parsers: one per gate command, each over the exact shape the script prints today
# ---------------------------------------------------------------------------------------------------

def parse_readiness_json(text: str) -> GateReport:
    """scripts/readiness-gate-local.sh --json <out>:
    {layer, commit, started_at, failed, gates:[{gate, status, exit_code, duration_s, log, log_tail}]}.
    `log_tail` is non-empty only for a FAIL and is the evidence; `duration_s`, `log` and `started_at`
    are noise and stay in `raw` only."""
    try:
        obj = json.loads(text)
    except ValueError as e:
        raise GateInfrastructureError(f"readiness-local: --json output is not JSON ({e}): {text[-2000:]!r}")
    if not isinstance(obj, dict) or not isinstance(obj.get("gates"), list):
        raise GateInfrastructureError(f"readiness-local: --json object has no gates list: {text[-2000:]!r}")
    commit = obj.get("commit")
    if commit in ("", "unknown"):
        commit = None
    report = GateReport(source="readiness-local", commit=commit, raw=obj)
    for entry in obj["gates"]:
        name = str(entry.get("gate", "?"))
        if str(entry.get("status", "")).upper() == "PASS":
            report.passed.append(name)
            continue
        tail = str(entry.get("log_tail") or "")
        evidence = [l for l in _lines(tail) if l.strip()]
        if not evidence:
            evidence = [f"{name} failed (exit {entry.get('exit_code')})"]
        report.failures.append(_failure(name, evidence))
    return report


_GROUP = re.compile(r"^::group::(.+?)\s*$")
_ENDGROUP = re.compile(r"^::endgroup::\s*$")
_GATE_ERROR = re.compile(r"^::error::(.+?) failed \(exit (\d+)\)\s*$")
_ALL_PASSED = re.compile(r"^repo-gates: all (\d+) gate\(s\) passed\s*$")
_SOME_FAILED = re.compile(r"^repo-gates: (\d+) of (\d+) gate\(s\) failed:\s*$")
_FAILED_ITEM = re.compile(r"^  (\S.*?)\s*$")
_REPO_GATE_TAIL = 40


def parse_repo_gates(text: str) -> GateReport:
    """scripts/ci/run-repo-gates.sh (stdout and stderr merged): "::group::<path>" ... "::endgroup::",
    "::error::<path> failed (exit N)" after a failing group, then either
    "repo-gates: all N gate(s) passed" or "repo-gates: K of N gate(s) failed:" + "  <path>" lines.
    Without one of the two summary lines the runner itself stopped (discovery floor, missing python):
    that is infrastructure, not a gate failure."""
    lines = _lines(text)
    bodies: Dict[str, List[str]] = {}
    order: List[str] = []
    errors: Dict[str, List[str]] = {}
    current: Optional[str] = None
    summary: Optional[str] = None
    listed: List[str] = []
    in_list = False
    total: Optional[int] = None
    for line in lines:
        m = _GROUP.match(line)
        if m:
            current = m.group(1)
            if current not in bodies:
                bodies[current] = []
                order.append(current)
            continue
        if _ENDGROUP.match(line):
            current = None
            continue
        m = _GATE_ERROR.match(line)
        if m:
            errors.setdefault(m.group(1), []).append(line.rstrip())
            continue
        m = _ALL_PASSED.match(line)
        if m:
            summary, total, in_list = "passed", int(m.group(1)), False
            continue
        m = _SOME_FAILED.match(line)
        if m:
            summary, total, in_list = "failed", int(m.group(2)), True
            continue
        if in_list:
            m = _FAILED_ITEM.match(line)
            if m:
                listed.append(m.group(1))
                continue
            in_list = False
        if current is not None:
            bodies[current].append(line.rstrip())
    if summary is None:
        raise GateInfrastructureError("repo-gates: no 'repo-gates: ... passed/failed' summary line in the output: "
                                      + "\n".join(l for l in lines[-20:] if l.strip()))
    report = GateReport(source="repo-gates", commit=None,
                        raw={"summary": summary, "total": total, "listed": listed, "groups": order})
    failed_names = list(order)
    for name in listed + list(errors):
        if name not in failed_names:
            failed_names.append(name)
    for name in failed_names:
        if name in errors or name in listed:
            body = [l for l in bodies.get(name, []) if l.strip()]
            evidence = errors.get(name, [f"::error::{name} failed"]) + body[-_REPO_GATE_TAIL:]
            report.failures.append(_failure(name, evidence))
        elif name in order:
            report.passed.append(name)
    return report


_TEST_RUN_HEADER = re.compile(r"^Test run for (.+?\.dll) \((.*?)\)\s*$")
_DOTNET_SUMMARY = re.compile(
    r"^(\w+)!\s+-\s+Failed:\s*(\d+),\s*Passed:\s*(\d+),\s*Skipped:\s*(\d+),\s*Total:\s*(\d+),"
    r"\s*Duration:\s*(.+?)\s+-\s+(\S+?\.dll) \((.*?)\)\s*$")
_FAILED_TEST = re.compile(r"^\s+Failed (\S.*?)\s*(\[[^\]]*\])?\s*$")
_BUILD_ERROR = re.compile(r"\berror (?:CS|MSB|NU|NETSDK)\d+\b")


def parse_container_test(text: str) -> GateReport:
    """scripts/test-in-container.sh carrying `dotnet test` output: "Test run for <path>/<name>.dll (<tfm>)"
    headers, "  Failed <FullyQualifiedName> [<n> ms]" lines per failing test, and one
    "<Word>!  - Failed: F, Passed: P, Skipped: S, Total: T, Duration: D - <name>.dll (<tfm>)" summary
    per assembly and tfm (the last one counts, as scripts/mutation-check.sh counts it).
    One gate per "<name>.dll (<tfm>)"; the evidence of a failing one is its failing test names, so
    the identity follows the failing SET and not the counts, which move when tests are added.
    No summary at all (a build error, a crashed container) is infrastructure."""
    lines = _lines(text)
    headers: List[str] = []
    pending: List[str] = []           # "  Failed ..." names since the last summary
    gates: Dict[str, Dict[str, Any]] = {}
    order: List[str] = []
    build_errors: List[str] = []
    for line in lines:
        m = _TEST_RUN_HEADER.match(line)
        if m:
            headers.append(line.rstrip())
            continue
        m = _DOTNET_SUMMARY.match(line)
        if m:
            name = f"{m.group(7)} ({m.group(8)})"
            entry = {"failed": int(m.group(2)), "passed": int(m.group(3)), "skipped": int(m.group(4)),
                     "total": int(m.group(5)), "summary": line.rstrip(), "tests": sorted(set(pending))}
            if name not in gates:
                order.append(name)
            gates[name] = entry
            pending = []
            continue
        m = _FAILED_TEST.match(line)
        if m:
            pending.append(m.group(1))
            continue
        if _BUILD_ERROR.search(line):
            build_errors.append(line.rstrip())
    if not gates:
        tail = build_errors or [l for l in lines if l.strip()][-20:]
        raise GateInfrastructureError("container-tests: no dotnet test summary line in the output: " + "\n".join(tail))
    report = GateReport(source="container-tests", commit=None,
                        raw={"headers": headers, "assemblies": gates, "build_errors": build_errors})
    for name in order:
        entry = gates[name]
        if entry["failed"] == 0:
            report.passed.append(name)
            continue
        evidence = [f"Failed {t}" for t in entry["tests"]] or [entry["summary"]]
        report.failures.append(_failure(name, evidence))
    return report


# ---------------------------------------------------------------------------------------------------
# Running a gate: one call, no retry
# ---------------------------------------------------------------------------------------------------

GATE_COMMANDS: Dict[str, List[str]] = {
    "repo-gates": ["bash", "scripts/ci/run-repo-gates.sh"],
    "readiness-local": ["bash", "scripts/readiness-gate-local.sh", "--layer", "application", "--json", "<out>"],
    "container-tests": ["bash", "scripts/test-in-container.sh", "--repo", "<workspace>"],
}

_PARSERS: Dict[str, Callable[[str], GateReport]] = {
    "repo-gates": parse_repo_gates,
    "readiness-local": parse_readiness_json,
    "container-tests": parse_container_test,
}


def gate_argv(name: str, workspace: str, out_path: Optional[str] = None) -> List[str]:
    if name not in GATE_COMMANDS:
        raise ValueError(f"unknown gate {name!r}; known: {', '.join(sorted(GATE_COMMANDS))}")
    argv = []
    for part in GATE_COMMANDS[name]:
        if part == "<workspace>":
            part = workspace
        elif part == "<out>":
            if out_path is None:
                raise ValueError(f"gate {name!r} needs an --json output path")
            part = out_path
        argv.append(part)
    return argv


def run_gate(name: str, workspace: str, runner: Callable[..., Any] = subprocess.run,
             timeout: int = 3600) -> GateReport:
    """Run one gate command in `workspace` exactly once and parse its output. Never retries: a flaky
    gate is a finding, and a second run would hide it. stderr is merged into stdout because
    run-repo-gates.sh prints its failed list to stderr. A runner that raises, a script that produces
    nothing parsable, or a missing --json file raises GateInfrastructureError with the evidence."""
    out_path: Optional[str] = None
    if "<out>" in GATE_COMMANDS.get(name, []):
        fd, out_path = tempfile.mkstemp(prefix="readiness-", suffix=".json")
        os.close(fd)
    argv = gate_argv(name, workspace, out_path)
    try:
        try:
            completed = runner(argv, cwd=workspace, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                               text=True, encoding="utf-8", errors="replace", timeout=timeout)
        except subprocess.TimeoutExpired as e:
            raise GateInfrastructureError(f"{name}: timed out after {timeout}s: {' '.join(argv)}") from e
        except OSError as e:
            raise GateInfrastructureError(f"{name}: could not run {' '.join(argv)}: {e}") from e
        output = completed.stdout or ""
        rc = completed.returncode
        if name == "readiness-local":
            try:
                with open(out_path, encoding="utf-8") as fh:  # type: ignore[arg-type]
                    text = fh.read()
            except OSError as e:
                raise GateInfrastructureError(f"{name}: no --json output (exit {rc}; {e}): {output[-2000:]}") from e
            if not text.strip():
                raise GateInfrastructureError(f"{name}: empty --json output (exit {rc}): {output[-2000:]}")
        else:
            text = output
        report = _PARSERS[name](text)
    finally:
        if out_path:
            try:
                os.unlink(out_path)
            except OSError:
                pass
    report.raw["argv"] = argv
    report.raw["exit_code"] = rc
    report.raw["workspace"] = workspace
    return report


# ---------------------------------------------------------------------------------------------------
# One bounded fixer launch
# ---------------------------------------------------------------------------------------------------

MISSING_ROLE_PATTERNS = (r"unknown agent", r"agent .* not found", r"no agent named", r"agent not found")
_MISSING_ROLE = tuple(re.compile(p, re.I) for p in MISSING_ROLE_PATTERNS)
FIXER_ROLE = "readiness-fixer"


def fixer_prompt(failure: GateFailure, workspace: str) -> str:
    return (
        f"Readiness gate `{failure.gate}` failed in {workspace}.\n"
        f"Failure identity: {failure.identity}\n"
        "Fix the root cause with a minimal change and prove it with the test that was red. "
        "Do not weaken, skip or delete the failing check. Do not touch unrelated files.\n\n"
        "Evidence from the gate runner:\n"
        f"{failure.evidence}\n"
    )


def fixer_request(failure: GateFailure, task_id: str, run_id: str, provider_spec: Dict[str, Any],
                  invocation_ceiling: int, workspace: str, agent: Optional[str] = FIXER_ROLE) -> LaunchRequest:
    """A launch for one failure. `provider_spec` is the policy's provider entry (model, effort, speed)
    plus the provider name under "provider" (or "name"); the controller passes
    `dict(policy.provider(p), provider=p)`. `allowed_tools` stays None: the fixer needs the CLI default."""
    provider = provider_spec.get("provider") or provider_spec.get("name")
    if not provider:
        raise ValueError("provider_spec needs the provider name under 'provider'")
    return LaunchRequest(run_id=run_id, task_id=task_id, provider=str(provider),
                         model=str(provider_spec["model"]), effort=str(provider_spec.get("effort", "medium")),
                         speed=str(provider_spec.get("speed", "standard")),
                         invocation_ceiling=int(invocation_ceiling), workspace=workspace,
                         prompt=fixer_prompt(failure, workspace), role=FIXER_ROLE, agent=agent, allowed_tools=None)


def is_missing_role_error(error: BaseException) -> bool:
    text = str(error)
    return any(p.search(text) for p in _MISSING_ROLE)


def launch_fixer(adapter: Any, request: LaunchRequest) -> LaunchResult:
    """Launch the fixer. If the adapter refuses because the named agent role does not exist (its error
    text matches MISSING_ROLE_PATTERNS) and the request named one, retry ONCE as a general-purpose
    launch (agent=None) and record `fallback="general-purpose"` on the request. Any other error, and
    any error from the fallback launch, propagates unchanged. There is no second fallback."""
    try:
        return adapter.launch(request)
    except Exception as e:  # noqa: BLE001 - the pattern match decides, not the type
        if request.agent is None or not is_missing_role_error(e):
            raise
        request.agent = None
        request.fallback = "general-purpose"  # type: ignore[attr-defined]
        return adapter.launch(request)


# ---------------------------------------------------------------------------------------------------
# Did the fix land?
# ---------------------------------------------------------------------------------------------------

def refuted(before: GateReport, after: GateReport) -> List[GateFailure]:
    """Failures whose identity is present in both reports: the fix was refuted for these. A failure
    that appears only in `after` is new, not refuted; one only in `before` was fixed."""
    earlier = {f.identity for f in before.failures}
    return [f for f in after.failures if f.identity in earlier]
