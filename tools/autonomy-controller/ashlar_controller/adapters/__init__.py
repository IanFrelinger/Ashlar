"""Provider adapters. PR 1 shipped the protocol and a fake; PR 2 adds the Claude CLI adapter; PR 5 adds the
Codex CLI and Cursor agent CLI adapters, and `locate_run_transcript`, the one place a run's transcript is found
from its manifest (by the provider that wrote it), for `status` and `evidence archive`."""
from typing import Any, Dict, Optional

from .base import AdapterCapabilities, ProviderAdapter
from .claude import ClaudeAdapter
from .codex import CodexAdapter
from .cursor import CursorAdapter
from .fake import FakeAdapter

__all__ = ["AdapterCapabilities", "ProviderAdapter", "FakeAdapter", "ClaudeAdapter", "CodexAdapter", "CursorAdapter",
           "locate_run_transcript"]


def locate_run_transcript(manifest: Dict[str, Any], native_session_id: Optional[str] = None) -> Optional[str]:
    """Where the CLI kept the run's transcript, or None, from the run's manifest alone (no adapter method is
    called). A Codex manifest names `sessions_dir` and `thread_id` (the session file is
    `rollout-*-<thread_id>.jsonl` under it); a Claude manifest names `projects_dir` and `session_id`; a Cursor
    run keeps no transcript the controller can locate (none was found for the one logged-in run, run-bdc63ec68d56)."""
    if manifest.get("provider") == "codex" or manifest.get("sessions_dir"):
        from .codex import locate_session_file
        return locate_session_file(manifest.get("sessions_dir"), manifest.get("thread_id") or native_session_id,
                                   manifest.get("transcript_path"))
    if manifest.get("transcript_path") or manifest.get("projects_dir"):
        from .claude import locate_transcript
        return locate_transcript(manifest.get("projects_dir"), manifest.get("session_id") or native_session_id,
                                 manifest.get("transcript_path"))
    return None
