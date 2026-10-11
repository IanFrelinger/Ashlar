"""Load and validate the operator's budget policy.

THE TRUST BOUNDARY. A worker runs with write access to its own checkout. If the policy it runs under
could be read from that checkout, the worker could raise its own limits by editing a file. So the
active policy is read from an operator-controlled path, and this loader REFUSES a path that lies
inside any registered worker checkout or inside the repository that holds the controller itself
(the committed policy.example.json is an example, never the active policy). A policy that is
group- or world-writable is refused for the same reason.

Limits are also frozen onto each run at reservation time (see budget.py), so a later policy change
only affects later reservations.
"""
from __future__ import annotations

import hashlib
import json
import os
import stat
import subprocess
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Dict, Iterable, List, Optional

from .models import PolicyError

LIMIT_KEYS: Dict[str, Any] = {
    # key: (type, minimum, maximum)
    "active_writers": (int, 1, 1),
    "verifiers_per_task": (int, 0, 1),
    "readiness_fixers": (int, 0, 1),
    "same_failure_retries": (int, 0, 1),
    "repair_rounds": (int, 0, 2),
    "premium_escalations_per_pr": (int, 0, 1),
    "provider_transfers_per_pr": (int, 0, 1),
    "invocation_turn_ceiling": (int, 1, 200),
    "task_turn_ceiling": (int, 1, 10000),
    "max_attempts_per_task": (int, 1, 20),
}

FUNDING_KEYS: Dict[str, Any] = {
    "paid_inference_enabled": bool,
    "new_paid_infrastructure": bool,
    "interactive_reserve_fraction": float,
    "unobservable_allowance": str,  # "task-ceiling-only" | "wait"
}

UNOBSERVABLE_CHOICES = ("task-ceiling-only", "wait")

# PR 3: the PR lifecycle section. Optional; every key has a default. `required_checks` are the five
# required checks on master (CLAUDE.md); a merge needs every one of them green ON THE CURRENT HEAD.
PR_DEFAULTS: Dict[str, Any] = {
    "repo": "IanFrelinger/Ashlar",
    "remote_url": "https://github.com/IanFrelinger/Ashlar.git",
    "base_branch": "master",
    "branch_prefix": "auto/",
    "required_checks": ["cert-gate", "build-core", "shell-lint", "lychee (README + docs)", "Readiness summary"],
    "reference_checkout": None,        # a local clone whose objects speed up the worker clone (git --reference)
}
POOL_KINDS = ("subscription_allowance", "api_grant_balance", "paid_spend")
PROVIDER_KEYS = ("enabled", "model", "effort", "speed", "funding_pool", "pool_kind", "unit")


@dataclass(frozen=True)
class Policy:
    version: str
    limits: Dict[str, int]
    funding: Dict[str, Any]
    providers: Dict[str, Dict[str, Any]]
    idle: Dict[str, Any]
    sha256: str
    source_path: str
    pr: Dict[str, Any] = None  # type: ignore[assignment]  # PR 3 section; see PR_DEFAULTS

    def provider(self, name: str) -> Dict[str, Any]:
        try:
            return self.providers[name]
        except KeyError:
            raise PolicyError(f"policy {self.version} names no provider '{name}'")

    def to_json(self) -> Dict[str, Any]:
        return {
            "policy_version": self.version,
            "limits": dict(self.limits),
            "funding": dict(self.funding),
            "providers": {k: dict(v) for k, v in self.providers.items()},
            "idle": dict(self.idle),
            "pr": dict(self.pr or PR_DEFAULTS),
        }


def _is_within(path: Path, root: Path) -> bool:
    try:
        path.relative_to(root)
        return True
    except ValueError:
        return False


def repository_root_of(path: Path) -> Optional[Path]:
    """The git top level containing `path`, or None. Used to forbid the controller's own checkout."""
    try:
        out = subprocess.run(
            ["git", "-C", str(path if path.is_dir() else path.parent), "rev-parse", "--show-toplevel"],
            check=True, capture_output=True, text=True, timeout=20,
        ).stdout.strip()
    except (OSError, subprocess.SubprocessError):
        return None
    return Path(out).resolve() if out else None


def controller_checkout_root() -> Optional[Path]:
    return repository_root_of(Path(__file__).resolve().parent)


def forbidden_roots(worker_checkouts: Iterable[str]) -> List[Path]:
    roots: List[Path] = []
    own = controller_checkout_root()
    if own is not None:
        roots.append(own)
    for c in worker_checkouts:
        roots.append(Path(c).resolve())
    return roots


def _check_location(path: Path, roots: Iterable[Path]) -> None:
    resolved = path.resolve()
    for root in roots:
        if _is_within(resolved, root):
            raise PolicyError(
                f"refusing policy {path}: it lies inside a checkout ({root}). The active policy must "
                f"live at an operator-controlled path no worker can write to."
            )
    if os.name == "posix":
        mode = resolved.stat().st_mode
        if mode & (stat.S_IWGRP | stat.S_IWOTH):
            raise PolicyError(
                f"refusing policy {path}: mode {stat.filemode(mode)} is group- or world-writable"
            )


def _validate(raw: Dict[str, Any], source: str) -> Dict[str, Any]:
    if not isinstance(raw, dict):
        raise PolicyError(f"{source}: policy must be a JSON object")
    version = raw.get("policy_version")
    if not isinstance(version, str) or not version.strip():
        raise PolicyError(f"{source}: policy_version must be a non-empty string")

    limits = raw.get("limits")
    if not isinstance(limits, dict):
        raise PolicyError(f"{source}: limits must be an object")
    for key, (typ, lo, hi) in LIMIT_KEYS.items():
        if key not in limits:
            raise PolicyError(f"{source}: limits.{key} is required")
        value = limits[key]
        if isinstance(value, bool) or not isinstance(value, typ):
            raise PolicyError(f"{source}: limits.{key} must be an integer")
        if value < lo or value > hi:
            raise PolicyError(f"{source}: limits.{key}={value} is outside the permitted range [{lo}, {hi}]")
    unknown = sorted(set(limits) - set(LIMIT_KEYS))
    if unknown:
        raise PolicyError(f"{source}: unknown limits {unknown}")
    if limits["invocation_turn_ceiling"] > limits["task_turn_ceiling"]:
        raise PolicyError(f"{source}: invocation_turn_ceiling exceeds task_turn_ceiling")

    funding = raw.get("funding")
    if not isinstance(funding, dict):
        raise PolicyError(f"{source}: funding must be an object")
    for key, typ in FUNDING_KEYS.items():
        if key not in funding:
            raise PolicyError(f"{source}: funding.{key} is required")
        value = funding[key]
        if typ is float:
            if isinstance(value, bool) or not isinstance(value, (int, float)):
                raise PolicyError(f"{source}: funding.{key} must be a number")
        elif not isinstance(value, typ):
            raise PolicyError(f"{source}: funding.{key} must be {typ.__name__}")
    frac = float(funding["interactive_reserve_fraction"])
    if not 0.0 <= frac < 1.0:
        raise PolicyError(f"{source}: funding.interactive_reserve_fraction must be in [0, 1)")
    if funding["unobservable_allowance"] not in UNOBSERVABLE_CHOICES:
        raise PolicyError(f"{source}: funding.unobservable_allowance must be one of {UNOBSERVABLE_CHOICES}")

    providers = raw.get("providers")
    if not isinstance(providers, dict) or not providers:
        raise PolicyError(f"{source}: providers must be a non-empty object")
    for name, spec in providers.items():
        if not isinstance(spec, dict):
            raise PolicyError(f"{source}: providers.{name} must be an object")
        for key in PROVIDER_KEYS:
            if key not in spec:
                raise PolicyError(f"{source}: providers.{name}.{key} is required")
        if not isinstance(spec["enabled"], bool):
            raise PolicyError(f"{source}: providers.{name}.enabled must be a boolean")
        if spec["pool_kind"] not in POOL_KINDS:
            raise PolicyError(f"{source}: providers.{name}.pool_kind must be one of {POOL_KINDS}")
        if spec["pool_kind"] == "paid_spend" and spec["enabled"] and not funding["paid_inference_enabled"]:
            raise PolicyError(
                f"{source}: providers.{name} is enabled on a paid_spend pool while "
                f"funding.paid_inference_enabled is false"
            )

    idle = raw.get("idle", {})
    if not isinstance(idle, dict):
        raise PolicyError(f"{source}: idle must be an object")
    idle = {
        "empty_queue_poll_seconds": int(idle.get("empty_queue_poll_seconds", 900)),
        "quota_wait_seconds": int(idle.get("quota_wait_seconds", 3600)),
        "rate_limit_backoff_seconds": [int(x) for x in idle.get("rate_limit_backoff_seconds", [60, 300, 900])],
        "lease_ttl_seconds": int(idle.get("lease_ttl_seconds", 300)),
    }
    for k, v in idle.items():
        if isinstance(v, int) and v <= 0:
            raise PolicyError(f"{source}: idle.{k} must be positive")
    if not idle["rate_limit_backoff_seconds"] or any(x <= 0 for x in idle["rate_limit_backoff_seconds"]):
        raise PolicyError(f"{source}: idle.rate_limit_backoff_seconds must be positive integers")

    pr_raw = raw.get("pr", {})
    if not isinstance(pr_raw, dict):
        raise PolicyError(f"{source}: pr must be an object")
    unknown = sorted(set(pr_raw) - set(PR_DEFAULTS))
    if unknown:
        raise PolicyError(f"{source}: unknown pr keys {unknown}")
    pr = dict(PR_DEFAULTS)
    pr["required_checks"] = list(PR_DEFAULTS["required_checks"])
    pr.update(pr_raw)
    for key in ("repo", "remote_url", "base_branch", "branch_prefix"):
        if not isinstance(pr[key], str) or not pr[key].strip():
            raise PolicyError(f"{source}: pr.{key} must be a non-empty string")
    if pr["reference_checkout"] is not None and not isinstance(pr["reference_checkout"], str):
        raise PolicyError(f"{source}: pr.reference_checkout must be a string or null")
    checks = pr["required_checks"]
    if not isinstance(checks, list) or not checks or not all(isinstance(c, str) and c.strip() for c in checks):
        raise PolicyError(f"{source}: pr.required_checks must be a non-empty list of check names")
    if len(set(checks)) != len(checks):
        raise PolicyError(f"{source}: pr.required_checks has duplicates")

    return {"version": version, "limits": dict(limits), "funding": dict(funding),
            "providers": {k: dict(v) for k, v in providers.items()}, "idle": idle, "pr": pr}


def parse_policy(text: str, source: str = "<text>") -> Policy:
    """Validate policy text. Location checks are the caller's business (load_policy does both)."""
    try:
        raw = json.loads(text)
    except json.JSONDecodeError as e:
        raise PolicyError(f"{source}: not valid JSON: {e}")
    v = _validate(raw, source)
    digest = hashlib.sha256(text.encode("utf-8")).hexdigest()
    return Policy(version=v["version"], limits=v["limits"], funding=v["funding"], providers=v["providers"],
                  idle=v["idle"], sha256=digest, source_path=source, pr=v["pr"])


def load_policy(path: str, worker_checkouts: Iterable[str] = (), *, roots: Optional[List[Path]] = None) -> Policy:
    """Read the active policy from an operator-controlled path.

    `roots` overrides the forbidden-root computation (tests use it to avoid shelling out to git).
    """
    p = Path(path)
    if not p.is_file():
        raise PolicyError(f"policy file not found: {path}")
    _check_location(p, roots if roots is not None else forbidden_roots(worker_checkouts))
    text = p.read_text(encoding="utf-8")
    policy = parse_policy(text, source=str(p))
    return Policy(version=policy.version, limits=policy.limits, funding=policy.funding,
                  providers=policy.providers, idle=policy.idle, sha256=policy.sha256,
                  source_path=str(p.resolve()), pr=policy.pr)
