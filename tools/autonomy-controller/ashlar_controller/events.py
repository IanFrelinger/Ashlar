"""Event deduplication. An event's id is its idempotency key; a repeated or out-of-order delivery
of the same id is a no-op. The controller acts only on events `ingest` reports as new."""
from __future__ import annotations

import json
from typing import Any, Dict, Optional

from .store import Store, utcnow_iso


def ingest(store: Store, event_id: str, source: str, kind: str, task_id: Optional[str],
           payload: Optional[Dict[str, Any]] = None, now: Optional[float] = None) -> bool:
    with store.tx():
        cur = store.conn.execute(
            "INSERT OR IGNORE INTO events(event_id, source, kind, task_id, received_at, payload) VALUES (?,?,?,?,?,?)",
            (event_id, source, kind, task_id, utcnow_iso(now), json.dumps(payload or {}, sort_keys=True)))
        new = cur.rowcount == 1
        if new:
            store.conn.execute("UPDATE tasks SET last_event_at=?, last_event=?, updated_at=? WHERE task_id=?",
                               (utcnow_iso(now), f"{kind}:{event_id}", utcnow_iso(now), task_id))
    return new


def mark_handled(store: Store, event_id: str, now: Optional[float] = None) -> None:
    with store.tx():
        store.conn.execute("UPDATE events SET handled_at=? WHERE event_id=? AND handled_at IS NULL",
                           (utcnow_iso(now), event_id))


def unhandled(store: Store):
    return store.rows("SELECT * FROM events WHERE handled_at IS NULL ORDER BY received_at, event_id")
