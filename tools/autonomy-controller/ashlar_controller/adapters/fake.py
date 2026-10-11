"""A scripted provider adapter. Tests describe what each launch does; the adapter counts every call,
so a test can assert that no model was 'called' when none should have been."""
from __future__ import annotations

from typing import Any, Dict, List, Optional

from ..models import (LaunchOutcomeUnknown, LaunchRequest, LaunchResult, ReconcileResult, RunState, RunStatus,
                      StopReason, UsageFreshness, UsageKind, UsageObservation)
from .base import AdapterCapabilities


class FakeAdapter:
    name = "fake"
    capabilities = AdapterCapabilities(exact_resume=True, cancel=True, structured_output=True,
                                       usage_observation=True, transcript_retention=False)

    def __init__(self, script: Optional[List[Dict[str, Any]]] = None, usage: Optional[UsageObservation] = None):
        # Each script entry describes one launch:
        #   {"launch": "ok" | "unknown" | "fail",
        #    "status": RunStatus(...),              # what poll returns once running
        #    "reconcile": ReconcileResult(...)}     # what reconcile answers for an unknown launch
        self.script = list(script or [])
        self.default_status = RunStatus(state=RunState.FINISHED, stop_reason=StopReason.COMPLETED, consumed_units=3.0)
        self.usage = usage or UsageObservation(provider="fake", kind=UsageKind.SUBSCRIPTION_ALLOWANCE, unit="turns",
                                               freshness=UsageFreshness.UNAVAILABLE)
        self.launch_count = 0
        self.poll_count = 0
        self.cancel_count = 0
        self.reconcile_count = 0
        self.usage_count = 0
        self.requests: List[LaunchRequest] = []
        self.handles: Dict[str, Dict[str, Any]] = {}
        self.cancelled: List[str] = []

    def _next(self) -> Dict[str, Any]:
        return self.script.pop(0) if self.script else {"launch": "ok"}

    def launch(self, request: LaunchRequest) -> LaunchResult:
        self.launch_count += 1
        self.requests.append(request)
        entry = self._next()
        mode = entry.get("launch", "ok")
        handle = f"fake-handle-{self.launch_count}"
        self.handles[handle] = entry
        if mode == "unknown":
            raise LaunchOutcomeUnknown("the fake lost the launch acknowledgement")
        if mode == "fail":
            raise RuntimeError(entry.get("error", "fake launch failure"))
        return LaunchResult(native_session_id=f"fake-session-{self.launch_count}", native_run_handle=handle,
                            workspace=request.workspace)

    def poll(self, handle: str) -> RunStatus:
        self.poll_count += 1
        entry = self.handles.get(handle, {})
        return entry.get("status") or self.default_status

    def cancel(self, handle: str) -> None:
        self.cancel_count += 1
        self.cancelled.append(handle)

    def reconcile(self, run_id: str, handle: Optional[str]) -> ReconcileResult:
        self.reconcile_count += 1
        # An unknown launch has no handle recorded; the fake answers from the script entry that
        # produced it (the most recent handle without a recorded result).
        for h, entry in reversed(list(self.handles.items())):
            if "reconcile" in entry:
                return entry["reconcile"]
        return ReconcileResult(found=False)

    def observe_usage(self) -> UsageObservation:
        self.usage_count += 1
        return self.usage
