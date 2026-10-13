"""Names shared across the controller. Strings, not enums, so they read the same in SQLite and JSON."""
from __future__ import annotations

from dataclasses import dataclass, field
from typing import Any, Dict, List, Optional

SCHEMA_VERSION = 3   # 2: PR lifecycle columns (prs.branch/url/title, tasks.branch, runs.pr_number)
                     # 3: tasks.brief (card #776)

# A task's brief is the PR title and the commit subject of its controller-opened PR. Card #776 sets the
# limit: at most 72 characters.
BRIEF_MAX_CHARS = 72


class TaskState:
    QUEUED = "queued"
    RESERVED = "reserved"
    RUNNING = "running"
    WAITING_QUOTA = "waiting-quota"
    WAITING_RATE_LIMIT = "waiting-rate-limit"
    WAITING_ALLOWANCE = "waiting-allowance"
    NEEDS_RECONCILE = "needs-reconcile"
    PARKED_BUDGET = "parked-budget"
    PARKED_DECISION = "parked-decision"
    PARKED_FAILURE = "parked-failure"
    BLOCKED_AUTH = "blocked-auth"
    BLOCKED_PERMISSION = "blocked-permission"
    CANCELLED = "cancelled"
    PR_OPEN = "pr-open"              # a PR is open for the task; the controller observes its current head
    DONE = "done"

    # States from which the dispatcher may reserve and launch, once any wait has elapsed.
    DISPATCHABLE = (QUEUED, WAITING_QUOTA, WAITING_RATE_LIMIT, WAITING_ALLOWANCE)


class RunState:
    RESERVED = "reserved"      # budget reserved, launch not yet attempted
    LAUNCHING = "launching"    # launch issued, no handle recorded yet: outcome unknown if we die here
    RUNNING = "running"
    FINISHED = "finished"
    FAILED = "failed"
    CANCELLED = "cancelled"
    UNKNOWN = "unknown"        # reconcile could not decide; dispatch stays blocked

    ACTIVE = (RESERVED, LAUNCHING, RUNNING)
    UNCERTAIN = (LAUNCHING, UNKNOWN)


class StopReason:
    """The exact reason a run or a task stopped. Written verbatim into the record."""
    COMPLETED = "completed"
    QUOTA_EXHAUSTED = "quota-exhausted"
    RATE_LIMITED = "rate-limited"
    TASK_BUDGET_EXHAUSTED = "task-budget-exhausted"
    ATTEMPTS_EXHAUSTED = "attempts-exhausted"
    SAME_FAILURE_RETRY_EXHAUSTED = "same-failure-retry-exhausted"
    AUTH_MISSING = "auth-missing"
    PERMISSION_MISSING = "permission-missing"
    # The operator's own CLI configuration sets something the policy refuses (a Codex `service_tier`, an MCP server
    # that cannot be disabled, a config that cannot be read): an operator step, so the task waits instead of
    # spending an attempt on every tick until the line is changed.
    OPERATOR_CONFIG_REFUSED = "operator-config-refused"
    TRANSIENT = "transient"
    CANCELLED = "cancelled"
    TASK_FAILED = "task-failed"
    NO_FINAL_RESULT = "no-final-result"
    LAUNCH_UNKNOWN = "launch-outcome-unknown"
    LAUNCH_FAILED = "launch-failed"
    DECISION_MISSING = "decision-missing"
    ALLOWANCE_UNOBSERVABLE = "allowance-unobservable"
    ALLOWANCE_RESERVE = "allowance-below-interactive-reserve"
    PAUSED = "paused"
    STATE_MISSING = "state-missing"
    STATE_CORRUPT = "state-corrupt"
    LEASE_NOT_HELD = "lease-not-held"
    PROVIDER_DISABLED = "provider-disabled"
    PAID_ROUTE_DISABLED = "paid-route-disabled"
    EMPTY_QUEUE = "empty-queue"
    WRITER_BUSY = "writer-busy"
    RECONCILE_PENDING = "reconcile-pending"
    DRY_RUN = "dry-run"
    INVOCATION_CEILING = "invocation-ceiling-reached"   # the CLI stopped at the turn limit; resume within the task budget
    MODEL_UNRECOGNIZED = "model-unrecognized"          # the CLI refused the model id before any request
    FUNDING_ROUTE_MISMATCH = "funding-route-mismatch"  # the CLI would bill a route the policy did not select
    # PR lifecycle (PR 3)
    PR_CHECKS_PENDING = "pr-checks-pending"            # the PR's current head has required checks still running
    PR_MERGED = "pr-merged"                            # merged on the required checks; merge_sha recorded
    PR_CLOSED_UNMERGED = "pr-closed-unmerged"          # closed without a merge: never success
    REPAIR_ROUNDS_EXHAUSTED = "repair-rounds-exhausted"  # limits.repair_rounds used; the task parks
    NO_CHANGE_TO_PROPOSE = "no-change-to-propose"      # the worker completed but left no diff to push
    PR_OPEN_BLOCKS_DISPATCH = "pr-open-blocks-dispatch"  # PRs are serial: nothing new launches while one is open
    PROPOSE_FAILED = "propose-failed"                  # a completed run was charged, but its commit, push or PR failed
    PROPOSE_FAILED_BLOCKS_DISPATCH = "propose-failed-blocks-dispatch"  # nothing launches until the operator resolves it
    # Provider transfer (PR 5)
    PROVIDER_TRANSFERRED = "provider-transferred"        # the task moved to another provider; task, PR and pool kept
    PROVIDER_TRANSFERS_EXHAUSTED = "provider-transfers-exhausted"  # limits.provider_transfers_per_pr used
    TRANSFER_WRITER_NOT_STOPPABLE = "previous-writer-not-stoppable"  # a running run whose adapter is not registered
    TRANSFER_NEEDS_PR_LIFECYCLE = "transfer-needs-pr-lifecycle"  # a completed writer would be marked done, not proposed


class UsageFreshness:
    FRESH = "fresh"
    STALE = "stale"
    UNAVAILABLE = "unavailable"


class UsageKind:
    """Four kinds of accounting that must never be summed into one number."""
    SUBSCRIPTION_ALLOWANCE = "subscription_allowance"   # the provider's own units (turns, messages, %)
    API_GRANT_BALANCE = "api_grant_balance"             # verified included grant, in currency
    PAID_SPEND = "paid_spend"                            # incremental paid spend, in currency
    ESTIMATED_API_VALUE = "estimated_api_value"         # a reported estimate: not a charge, not allowance


@dataclass
class UsageObservation:
    provider: str
    kind: str
    unit: str
    freshness: str
    value: Optional[float] = None          # cumulative value as reported (None when unavailable)
    total: Optional[float] = None          # for allowances: the full allowance, when reported
    native_session_id: Optional[str] = None
    observed_at: Optional[str] = None
    raw: Dict[str, Any] = field(default_factory=dict)


@dataclass
class LaunchRequest:
    run_id: str
    task_id: str
    provider: str
    model: str
    effort: str
    speed: str
    invocation_ceiling: int
    workspace: Optional[str]
    prompt: str
    resume_session_id: Optional[str] = None
    role: str = "implementation"              # implementation | readiness-fixer | verifier
    agent: Optional[str] = None               # a named agent role (.claude/agents/<name>.md) to run as, if any
    allowed_tools: Optional[List[str]] = None # restrict the CLI's tool set; None means the CLI default
    # PR lifecycle (PR 3). The controller prepares the workspace as a clone of `base_sha` on `branch`;
    # `handoff` is the compact handoff the prompt carries (facts and identities, never a transcript).
    base_sha: Optional[str] = None
    branch: Optional[str] = None
    handoff: Optional[str] = None
    pr_number: Optional[int] = None           # set on a repair launch: the PR whose head the fixer repairs


@dataclass
class LaunchResult:
    native_session_id: Optional[str]
    native_run_handle: str
    workspace: Optional[str] = None


@dataclass
class RunStatus:
    state: str                              # RunState.RUNNING / FINISHED / FAILED / CANCELLED
    stop_reason: Optional[str] = None
    consumed_units: Optional[float] = None  # units this run consumed, when the adapter can say
    failure_identity: Optional[str] = None  # stable id of a diagnosed failure, for the retry counter
    result: Dict[str, Any] = field(default_factory=dict)
    usage: Optional[UsageObservation] = None            # the observation whose delta may stand in for consumption
    observations: List[UsageObservation] = field(default_factory=list)  # further kinds, recorded, never summed
    transcript_path: Optional[str] = None                # where the provider kept the transcript, when known


@dataclass
class ReconcileResult:
    """What an adapter found when asked about a run whose launch outcome was uncertain."""
    found: bool
    status: Optional[RunStatus] = None      # when found
    native_session_id: Optional[str] = None
    native_run_handle: Optional[str] = None


class ControllerError(Exception):
    exit_code = 1


class StateMissing(ControllerError):
    exit_code = 3


class StateCorrupt(ControllerError):
    exit_code = 3


class PolicyError(ControllerError):
    exit_code = 4


class LeaseError(ControllerError):
    exit_code = 5


class BudgetExhausted(ControllerError):
    exit_code = 6


class LaunchOutcomeUnknown(Exception):
    """Raised by an adapter when it cannot say whether the launch happened."""


class LaunchRefused(Exception):
    """Raised by an adapter that refused to launch for a definite, recorded reason (nothing started).
    `stop_reason` is a StopReason value; `blocker` is the exact text an operator needs."""

    def __init__(self, stop_reason: str, blocker: str):
        super().__init__(f"{stop_reason}: {blocker}")
        self.stop_reason = stop_reason
        self.blocker = blocker
