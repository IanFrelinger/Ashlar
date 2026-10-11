from __future__ import annotations

from dataclasses import dataclass
from typing import Optional, Protocol

from ..models import LaunchRequest, LaunchResult, ReconcileResult, RunStatus, UsageObservation


@dataclass(frozen=True)
class AdapterCapabilities:
    """What an adapter has actually been verified to do. Nothing here is assumed."""
    exact_resume: bool = False
    cancel: bool = False
    structured_output: bool = False
    usage_observation: bool = False
    transcript_retention: bool = False


class ProviderAdapter(Protocol):
    name: str
    capabilities: AdapterCapabilities

    def launch(self, request: LaunchRequest) -> LaunchResult:
        """Start a bounded run. Raises LaunchOutcomeUnknown when it cannot tell whether it started."""

    def poll(self, handle: str) -> RunStatus: ...

    def cancel(self, handle: str) -> None: ...

    def reconcile(self, run_id: str, handle: Optional[str]) -> ReconcileResult:
        """Decide what became of an uncertain launch. Must return a definite answer or found=False."""

    def observe_usage(self) -> UsageObservation: ...
