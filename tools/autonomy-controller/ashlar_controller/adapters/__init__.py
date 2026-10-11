"""Provider adapters. PR 1 shipped the protocol and a fake; PR 2 adds the Claude CLI adapter; Codex and
Cursor arrive in PR 5."""
from .base import AdapterCapabilities, ProviderAdapter
from .claude import ClaudeAdapter
from .fake import FakeAdapter

__all__ = ["AdapterCapabilities", "ProviderAdapter", "FakeAdapter", "ClaudeAdapter"]
