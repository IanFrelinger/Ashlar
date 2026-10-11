"""Outbox for milestone posts (bus claim / handoff / done, card comments).

A post is first written here with a dedup key, then sent. If the controller dies between sending and
marking sent, the row is left `sending`; reconcile asks the poster whether a comment carrying the
dedup marker already exists before anything is re-sent. That is duplicate-safe recovery, not
exactly-once: a poster that cannot search cannot promise more than "at most one extra".
"""
from __future__ import annotations

import json
import subprocess
from typing import Dict, List, Optional, Protocol

from .store import Store, utcnow_iso

MARKER = "<!-- ashlar-controller:dedup:{key} -->"
MAX_ATTEMPTS = 5


class Poster(Protocol):
    def post(self, target: str, body: str) -> str: ...
    def find(self, target: str, dedup_key: str) -> Optional[str]: ...


def marker(dedup_key: str) -> str:
    return MARKER.format(key=dedup_key)


def enqueue(store: Store, *, dedup_key: str, kind: str, target: str, slug: str, body: str,
            now: Optional[float] = None) -> bool:
    with store.tx():
        cur = store.conn.execute(
            "INSERT OR IGNORE INTO outbox(dedup_key, kind, target, slug, body, state, created_at) "
            "VALUES (?,?,?,?,?,'pending',?)", (dedup_key, kind, target, slug, body, utcnow_iso(now)))
        return cur.rowcount == 1


def pending(store: Store) -> List:
    return store.rows("SELECT * FROM outbox WHERE state IN ('pending','sending') ORDER BY id")


def reconcile(store: Store, poster: Poster, now: Optional[float] = None) -> Dict[str, int]:
    """Rows stuck in `sending` were interrupted mid-post. Ask the poster before re-sending."""
    counts = {"recovered_sent": 0, "requeued": 0}
    for row in store.rows("SELECT * FROM outbox WHERE state='sending' ORDER BY id"):
        remote = poster.find(row["target"], row["dedup_key"])
        with store.tx():
            if remote is not None:
                store.conn.execute("UPDATE outbox SET state='sent', remote_id=?, sent_at=? WHERE id=?",
                                   (remote, utcnow_iso(now), row["id"]))
                counts["recovered_sent"] += 1
            else:
                store.conn.execute("UPDATE outbox SET state='pending' WHERE id=?", (row["id"],))
                counts["requeued"] += 1
    return counts


def flush(store: Store, poster: Poster, now: Optional[float] = None) -> Dict[str, int]:
    counts = {"sent": 0, "failed": 0, "deferred": 0}
    for row in store.rows("SELECT * FROM outbox WHERE state='pending' ORDER BY id"):
        if int(row["attempts"]) >= MAX_ATTEMPTS:
            with store.tx():
                store.conn.execute("UPDATE outbox SET state='failed' WHERE id=?", (row["id"],))
            counts["failed"] += 1
            continue
        with store.tx():
            store.conn.execute("UPDATE outbox SET state='sending', attempts = attempts + 1 WHERE id=?", (row["id"],))
        body = row["body"].rstrip("\n") + "\n\n" + marker(row["dedup_key"]) + "\n"
        try:
            remote = poster.post(row["target"], body)
        except Exception as e:  # the poster failed; the row goes back to pending for a later flush
            with store.tx():
                store.conn.execute("UPDATE outbox SET state='pending', last_error=? WHERE id=?", (str(e)[:500], row["id"]))
            counts["deferred"] += 1
            continue
        with store.tx():
            store.conn.execute("UPDATE outbox SET state='sent', remote_id=?, sent_at=? WHERE id=?",
                               (remote, utcnow_iso(now), row["id"]))
        counts["sent"] += 1
    return counts


class FakePoster:
    """Records posts; can be told to fail, and can 'lose' the ack so reconcile has work to do."""

    def __init__(self, fail_times: int = 0):
        self.posts: List[Dict[str, str]] = []
        self.fail_times = fail_times
        self.next_id = 1

    def post(self, target: str, body: str) -> str:
        if self.fail_times > 0:
            self.fail_times -= 1
            raise RuntimeError("poster unavailable")
        rid = f"c{self.next_id}"
        self.next_id += 1
        self.posts.append({"target": target, "body": body, "id": rid})
        return rid

    def find(self, target: str, dedup_key: str) -> Optional[str]:
        m = marker(dedup_key)
        for p in self.posts:
            if p["target"] == target and m in p["body"]:
                return p["id"]
        return None


class GhIssuePoster:
    """Posts through the `gh` CLI. Target is `issue:<number>`. Not exercised live in PR 1; it is here
    so the outbox has a real destination shape. `find` lists the issue's comments and looks for the
    dedup marker; it needs an authenticated gh with issue read/write."""

    def __init__(self, repo: str, gh: str = "gh"):
        self.repo = repo
        self.gh = gh

    @staticmethod
    def _number(target: str) -> str:
        kind, _, number = target.partition(":")
        if kind != "issue" or not number.isdigit():
            raise ValueError(f"GhIssuePoster target must be issue:<number>, got {target!r}")
        return number

    def post(self, target: str, body: str) -> str:
        number = self._number(target)
        out = subprocess.run([self.gh, "api", f"repos/{self.repo}/issues/{number}/comments", "-f", f"body={body}"],
                             check=True, capture_output=True, text=True, timeout=60).stdout
        return str(json.loads(out)["id"])

    def find(self, target: str, dedup_key: str) -> Optional[str]:
        number = self._number(target)
        out = subprocess.run([self.gh, "api", "--paginate", f"repos/{self.repo}/issues/{number}/comments"],
                             check=True, capture_output=True, text=True, timeout=120).stdout
        m = marker(dedup_key)
        for comment in json.loads(out):
            if m in (comment.get("body") or ""):
                return str(comment["id"])
        return None
