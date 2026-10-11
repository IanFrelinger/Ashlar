"""Provider adapters. PR 1 ships the protocol and a fake; live adapters arrive in PR 2 and PR 5."""
from .base import AdapterCapabilities, ProviderAdapter
from .fake import FakeAdapter

__all__ = ["AdapterCapabilities", "ProviderAdapter", "FakeAdapter"]
