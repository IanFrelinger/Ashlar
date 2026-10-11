"""Durable controller state in one SQLite file.

Everything the controller must remember across a restart, a replaced session, a convergence cycle or
a provider change lives here: tasks, budget pools, runs, events, usage observations, the writer
lease, the outbox and the operator controls. The file is the budget: if it is missing or fails an
integrity check, dispatch stops (StateMissing / StateCorrupt). The controller never recreates an
empty database on its own; `init` is an explicit operator step.
"""
from __future__ import annotations

import json
import os
import socket
import sqlite3
import time
from contextlib import contextmanager
from pathlib import Path
from typing import Any, Dict, Iterator, List, Optional

from .models import SCHEMA_VERSION, LeaseError, StateCorrupt, StateMissing

SCHEMA = """
CREATE TABLE IF NOT EXISTS meta (
  key TEXT PRIMARY KEY,
  value TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS policy_versions (
  id INTEGER PRIMARY KEY,
  version TEXT NOT NULL,
  sha256 TEXT NOT NULL UNIQUE,
  source_path TEXT NOT NULL,
  loaded_at TEXT NOT NULL,
  json TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS worker_checkouts (
  path TEXT PRIMARY KEY,
  registered_at TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS budget_pools (
  pool_id TEXT PRIMARY KEY,
  unit TEXT NOT NULL,
  task_ceiling REAL NOT NULL,
  invocation_ceiling REAL NOT NULL,
  attempts_max INTEGER NOT NULL,
  consumed REAL NOT NULL DEFAULT 0,
  reserved REAL NOT NULL DEFAULT 0,
  overshoot REAL NOT NULL DEFAULT 0,
  attempts_used INTEGER NOT NULL DEFAULT 0,
  policy_version_id INTEGER REFERENCES policy_versions(id),
  created_at TEXT NOT NULL,
  CHECK (consumed >= 0 AND reserved >= 0 AND attempts_used >= 0)
);
CREATE TABLE IF NOT EXISTS failure_retries (
  pool_id TEXT NOT NULL REFERENCES budget_pools(pool_id),
  failure_identity TEXT NOT NULL,
  count INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (pool_id, failure_identity)
);
CREATE TABLE IF NOT EXISTS tasks (
  task_id TEXT PRIMARY KEY,
  card_number INTEGER,
  workstream TEXT NOT NULL,
  title TEXT NOT NULL DEFAULT '',
  handoff_ref TEXT,
  decision_refs TEXT NOT NULL DEFAULT '[]',
  owner_agent TEXT NOT NULL DEFAULT 'claude',
  provider TEXT NOT NULL,
  parent_task_id TEXT REFERENCES tasks(task_id),
  pool_id TEXT NOT NULL REFERENCES budget_pools(pool_id),
  state TEXT NOT NULL,
  stop_reason TEXT,
  blocker TEXT,
  next_eligible_at TEXT,
  priority INTEGER NOT NULL DEFAULT 100,
  base_sha TEXT,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL,
  last_event_at TEXT,
  last_event TEXT
);
CREATE TABLE IF NOT EXISTS phases (
  phase_id TEXT PRIMARY KEY,
  task_id TEXT NOT NULL REFERENCES tasks(task_id),
  name TEXT NOT NULL,
  state TEXT NOT NULL,
  handoff_ref TEXT,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS prs (
  pr_number INTEGER PRIMARY KEY,
  task_id TEXT NOT NULL REFERENCES tasks(task_id),
  base_sha TEXT,
  head_sha TEXT,
  merge_sha TEXT,
  state TEXT NOT NULL,
  outcome TEXT,
  repair_rounds_used INTEGER NOT NULL DEFAULT 0,
  premium_escalations_used INTEGER NOT NULL DEFAULT 0,
  provider_transfers_used INTEGER NOT NULL DEFAULT 0,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS runs (
  run_id TEXT PRIMARY KEY,
  task_id TEXT NOT NULL REFERENCES tasks(task_id),
  pool_id TEXT NOT NULL REFERENCES budget_pools(pool_id),
  attempt_no INTEGER NOT NULL,
  role TEXT NOT NULL DEFAULT 'implementation',
  provider TEXT NOT NULL,
  model TEXT NOT NULL,
  effort TEXT NOT NULL,
  speed TEXT NOT NULL,
  funding_pool TEXT NOT NULL,
  policy_version_id INTEGER REFERENCES policy_versions(id),
  limits_json TEXT NOT NULL,
  native_session_id TEXT,
  native_run_handle TEXT,
  workspace TEXT,
  state TEXT NOT NULL,
  reservation REAL NOT NULL,
  consumed REAL,
  stop_reason TEXT,
  failure_identity TEXT,
  result_json TEXT,
  owner_holder TEXT,
  reserved_at TEXT NOT NULL,
  launched_at TEXT,
  finished_at TEXT,
  settled_at TEXT
);
-- One active implementation writer across every provider. Partial unique index: the database
-- itself refuses a second active writer, whatever the controller code believes.
CREATE UNIQUE INDEX IF NOT EXISTS one_active_writer
  ON runs(role) WHERE role = 'implementation' AND state IN ('reserved', 'launching', 'running');
CREATE TABLE IF NOT EXISTS usage_observations (
  id INTEGER PRIMARY KEY,
  run_id TEXT REFERENCES runs(run_id),
  provider TEXT NOT NULL,
  native_session_id TEXT,
  kind TEXT NOT NULL,
  unit TEXT NOT NULL,
  freshness TEXT NOT NULL,
  value REAL,
  total REAL,
  delta REAL,
  observed_at TEXT NOT NULL,
  raw TEXT NOT NULL DEFAULT '{}'
);
CREATE TABLE IF NOT EXISTS events (
  event_id TEXT PRIMARY KEY,
  source TEXT NOT NULL,
  kind TEXT NOT NULL,
  task_id TEXT,
  received_at TEXT NOT NULL,
  payload TEXT NOT NULL DEFAULT '{}',
  handled_at TEXT
);
CREATE TABLE IF NOT EXISTS outbox (
  id INTEGER PRIMARY KEY,
  dedup_key TEXT NOT NULL UNIQUE,
  kind TEXT NOT NULL,
  target TEXT NOT NULL,
  slug TEXT NOT NULL,
  body TEXT NOT NULL,
  state TEXT NOT NULL,
  attempts INTEGER NOT NULL DEFAULT 0,
  remote_id TEXT,
  last_error TEXT,
  created_at TEXT NOT NULL,
  sent_at TEXT
);
CREATE TABLE IF NOT EXISTS lease (
  name TEXT PRIMARY KEY,
  holder_id TEXT NOT NULL,
  host TEXT NOT NULL,
  pid INTEGER NOT NULL,
  acquired_at TEXT NOT NULL,
  heartbeat_at TEXT NOT NULL,
  expires_at REAL NOT NULL,
  released_at TEXT,
  released_by TEXT
);
CREATE TABLE IF NOT EXISTS controls (
  key TEXT PRIMARY KEY,
  value TEXT NOT NULL,
  updated_at TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS journal (
  id INTEGER PRIMARY KEY,
  at TEXT NOT NULL,
  kind TEXT NOT NULL,
  task_id TEXT,
  run_id TEXT,
  detail TEXT NOT NULL DEFAULT '{}'
);
"""

REQUIRED_TABLES = ("meta", "policy_versions", "budget_pools", "failure_retries", "tasks", "phases", "prs",
                   "runs", "usage_observations", "events", "outbox", "lease", "controls", "journal",
                   "worker_checkouts")


def utcnow_iso(now: Optional[float] = None) -> str:
    t = time.time() if now is None else now
    return time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime(t))


def _pid_alive(pid: int) -> bool:
    if pid <= 0:
        return False
    try:
        os.kill(pid, 0)
    except ProcessLookupError:
        return False
    except PermissionError:
        return True
    except OSError:
        return False
    return True


class Store:
    """One connection to the controller database. Not thread-safe; one Store per process."""

    def __init__(self, path: str, conn: sqlite3.Connection, read_only: bool = False):
        self.path = path
        self.conn = conn
        self.read_only = read_only
        self.conn.row_factory = sqlite3.Row

    # ----- open / init -----------------------------------------------------------------------

    @classmethod
    def init(cls, path: str, controller_id: str, now: Optional[float] = None) -> "Store":
        p = Path(path)
        if p.exists():
            raise StateCorrupt(f"refusing to init: {path} already exists (delete it deliberately first)")
        p.parent.mkdir(parents=True, exist_ok=True)
        conn = sqlite3.connect(path, isolation_level=None)
        conn.execute("PRAGMA journal_mode=WAL")
        conn.execute("PRAGMA foreign_keys=ON")
        conn.executescript(SCHEMA)
        at = utcnow_iso(now)
        conn.execute("INSERT INTO meta(key, value) VALUES ('schema_version', ?)", (str(SCHEMA_VERSION),))
        conn.execute("INSERT INTO meta(key, value) VALUES ('controller_id', ?)", (controller_id,))
        conn.execute("INSERT INTO meta(key, value) VALUES ('created_at', ?)", (at,))
        store = cls(path, conn)
        store.journal("init", detail={"controller_id": controller_id}, now=now)
        return store

    @classmethod
    def open(cls, path: str, read_only: bool = False) -> "Store":
        p = Path(path)
        if not p.is_file():
            raise StateMissing(
                f"controller state {path} is missing. Dispatch stops: a missing database would mean "
                f"recreating an empty budget. Run `init` deliberately, or point --state at the real file."
            )
        uri = f"file:{p.resolve().as_posix()}?mode={'ro' if read_only else 'rw'}"
        try:
            conn = sqlite3.connect(uri, uri=True, isolation_level=None)
            conn.execute("PRAGMA foreign_keys=ON")
            store = cls(path, conn, read_only=read_only)
            store.check_integrity()
        except sqlite3.DatabaseError as e:
            raise StateCorrupt(f"controller state {path} is not a readable database: {e}")
        return store

    def check_integrity(self) -> None:
        try:
            row = self.conn.execute("PRAGMA integrity_check").fetchone()
        except sqlite3.DatabaseError as e:
            raise StateCorrupt(f"controller state {self.path}: integrity_check failed: {e}")
        if row is None or row[0] != "ok":
            raise StateCorrupt(f"controller state {self.path}: integrity_check reported {row[0] if row else 'nothing'}")
        tables = {r[0] for r in self.conn.execute("SELECT name FROM sqlite_master WHERE type='table'")}
        missing = [t for t in REQUIRED_TABLES if t not in tables]
        if missing:
            raise StateCorrupt(f"controller state {self.path}: tables missing {missing}; dispatch stops")
        version = self.meta("schema_version")
        if version != str(SCHEMA_VERSION):
            raise StateCorrupt(f"controller state {self.path}: schema_version {version!r} != {SCHEMA_VERSION}")

    def close(self) -> None:
        self.conn.close()

    # ----- primitives ------------------------------------------------------------------------

    @contextmanager
    def tx(self) -> Iterator[sqlite3.Connection]:
        """BEGIN IMMEDIATE ... COMMIT, rolled back on any exception. Writers serialize here."""
        if self.read_only:
            raise StateCorrupt("write attempted on a read-only store (dry-run)")
        self.conn.execute("BEGIN IMMEDIATE")
        try:
            yield self.conn
        except BaseException:
            self.conn.execute("ROLLBACK")
            raise
        else:
            self.conn.execute("COMMIT")

    def meta(self, key: str) -> Optional[str]:
        row = self.conn.execute("SELECT value FROM meta WHERE key=?", (key,)).fetchone()
        return None if row is None else row["value"]

    def journal(self, kind: str, task_id: Optional[str] = None, run_id: Optional[str] = None,
                detail: Optional[Dict[str, Any]] = None, now: Optional[float] = None) -> None:
        self.conn.execute(
            "INSERT INTO journal(at, kind, task_id, run_id, detail) VALUES (?,?,?,?,?)",
            (utcnow_iso(now), kind, task_id, run_id, json.dumps(detail or {}, sort_keys=True)),
        )

    def control(self, key: str) -> Optional[str]:
        row = self.conn.execute("SELECT value FROM controls WHERE key=?", (key,)).fetchone()
        return None if row is None else row["value"]

    def set_control(self, key: str, value: str, now: Optional[float] = None) -> None:
        with self.tx():
            self.conn.execute(
                "INSERT INTO controls(key, value, updated_at) VALUES (?,?,?) "
                "ON CONFLICT(key) DO UPDATE SET value=excluded.value, updated_at=excluded.updated_at",
                (key, value, utcnow_iso(now)),
            )

    def clear_control(self, key: str) -> None:
        with self.tx():
            self.conn.execute("DELETE FROM controls WHERE key=?", (key,))

    def rows(self, sql: str, params: tuple = ()) -> List[sqlite3.Row]:
        return self.conn.execute(sql, params).fetchall()

    def row(self, sql: str, params: tuple = ()) -> Optional[sqlite3.Row]:
        return self.conn.execute(sql, params).fetchone()

    # ----- worker checkouts -------------------------------------------------------------------

    def register_worker_checkout(self, path: str, now: Optional[float] = None) -> None:
        with self.tx():
            self.conn.execute(
                "INSERT OR IGNORE INTO worker_checkouts(path, registered_at) VALUES (?, ?)",
                (str(Path(path).resolve()), utcnow_iso(now)),
            )

    def worker_checkouts(self) -> List[str]:
        return [r["path"] for r in self.rows("SELECT path FROM worker_checkouts ORDER BY path")]

    # ----- policy versions --------------------------------------------------------------------

    def record_policy(self, version: str, sha256: str, source_path: str, policy_json: Dict[str, Any],
                      now: Optional[float] = None) -> int:
        with self.tx():
            self.conn.execute(
                "INSERT OR IGNORE INTO policy_versions(version, sha256, source_path, loaded_at, json) "
                "VALUES (?,?,?,?,?)",
                (version, sha256, source_path, utcnow_iso(now), json.dumps(policy_json, sort_keys=True)),
            )
            row = self.conn.execute("SELECT id FROM policy_versions WHERE sha256=?", (sha256,)).fetchone()
        return int(row["id"])

    # ----- lease (single writer) --------------------------------------------------------------

    def lease_row(self) -> Optional[sqlite3.Row]:
        return self.row("SELECT * FROM lease WHERE name='writer'")

    def acquire_lease(self, holder_id: str, ttl_seconds: int, now: Optional[float] = None,
                      host: Optional[str] = None, pid: Optional[int] = None) -> None:
        """Take or renew the writer lease.

        An EXPIRED lease is not free. It is taken over only when the previous holder is confirmed
        stopped: it released the lease, or it ran on this host and its pid is gone, or an operator
        confirmed it with `lease break`. Anything else is refused: an expired heartbeat can be a
        paused process that will wake up and write.
        """
        t = time.time() if now is None else now
        host = host or socket.gethostname()
        pid = os.getpid() if pid is None else pid
        at = utcnow_iso(t)
        with self.tx():
            row = self.conn.execute("SELECT * FROM lease WHERE name='writer'").fetchone()
            if row is None:
                self.conn.execute(
                    "INSERT INTO lease(name, holder_id, host, pid, acquired_at, heartbeat_at, expires_at) "
                    "VALUES ('writer', ?, ?, ?, ?, ?, ?)", (holder_id, host, pid, at, at, t + ttl_seconds))
                self.journal("lease-acquired", detail={"holder": holder_id, "reason": "free"}, now=t)
                return
            if row["holder_id"] == holder_id and row["released_at"] is None:
                self.conn.execute("UPDATE lease SET heartbeat_at=?, expires_at=? WHERE name='writer'",
                                  (at, t + ttl_seconds))
                return
            if row["released_at"] is None:
                if row["expires_at"] > t:
                    raise LeaseError(
                        f"writer lease held by {row['holder_id']} on {row['host']} (pid {row['pid']}) "
                        f"until {utcnow_iso(row['expires_at'])}; this controller does not dispatch")
                if row["host"] == host and not _pid_alive(int(row["pid"])):
                    reason = "pid-dead"
                else:
                    raise LeaseError(
                        f"writer lease of {row['holder_id']} on {row['host']} (pid {row['pid']}) expired at "
                        f"{utcnow_iso(row['expires_at'])} but that worker is not confirmed stopped. Confirm it "
                        f"is gone, then run: lease break --holder {row['holder_id']} --confirm-stopped")
            else:
                reason = f"released-by:{row['released_by']}"
            self.conn.execute(
                "UPDATE lease SET holder_id=?, host=?, pid=?, acquired_at=?, heartbeat_at=?, expires_at=?, "
                "released_at=NULL, released_by=NULL WHERE name='writer'",
                (holder_id, host, pid, at, at, t + ttl_seconds))
            self.journal("lease-acquired", detail={"holder": holder_id, "reason": reason,
                                                   "previous": row["holder_id"]}, now=t)

    def holds_lease(self, holder_id: str, now: Optional[float] = None) -> bool:
        t = time.time() if now is None else now
        row = self.lease_row()
        return bool(row and row["holder_id"] == holder_id and row["released_at"] is None and row["expires_at"] > t)

    def release_lease(self, holder_id: str, now: Optional[float] = None) -> None:
        with self.tx():
            row = self.conn.execute("SELECT * FROM lease WHERE name='writer'").fetchone()
            if row and row["holder_id"] == holder_id and row["released_at"] is None:
                self.conn.execute("UPDATE lease SET released_at=?, released_by=? WHERE name='writer'",
                                  (utcnow_iso(now), holder_id))
                self.journal("lease-released", detail={"holder": holder_id}, now=now)

    def break_lease(self, holder_id: str, confirmed_by: str, now: Optional[float] = None) -> None:
        """Operator confirmation that `holder_id` is stopped. Recorded; the next acquire may take over."""
        t = time.time() if now is None else now
        with self.tx():
            row = self.conn.execute("SELECT * FROM lease WHERE name='writer'").fetchone()
            if row is None or row["holder_id"] != holder_id:
                raise LeaseError(f"no writer lease held by {holder_id}")
            if row["released_at"] is not None:
                return
            if row["expires_at"] > t:
                raise LeaseError(
                    f"lease of {holder_id} has not expired (until {utcnow_iso(row['expires_at'])}); "
                    f"stop that controller and wait for expiry before breaking its lease")
            self.conn.execute("UPDATE lease SET released_at=?, released_by=? WHERE name='writer'",
                              (utcnow_iso(t), f"operator:{confirmed_by}"))
            self.journal("lease-broken", detail={"holder": holder_id, "confirmed_by": confirmed_by}, now=t)
