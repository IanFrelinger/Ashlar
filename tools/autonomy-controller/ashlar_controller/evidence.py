"""Evidence retention: a backup of the live ledger, a restore that refuses what it does not know, and a
per-run archive that keeps the run's files, its transcript and its slice of the ledger together.

Everything here is LLM-free. No adapter method is ever called; the only adapter symbol used is
`adapters.claude.locate_transcript`, a pure path helper, imported lazily inside `archive_run`.

Standard library only: sqlite3 (the online backup API), tarfile, hashlib, json, os, tempfile.
"""
from __future__ import annotations

import hashlib
import io
import json
import os
import sqlite3
import tarfile
import tempfile
import time
from pathlib import Path
from typing import Any, Dict, List, Optional

from .models import PolicyError, StateCorrupt
from .store import REQUIRED_TABLES, Store, utcnow_iso

# Schema 1 is migrated in place when the restored file is opened; anything outside this tuple is a
# ledger this controller cannot vouch for, so `restore` refuses it before writing a byte.
KNOWN_SCHEMA_VERSIONS = ("1", "2")

_SIDECAR_SUFFIXES = ("-wal", "-shm")


def _sha256_file(path: str) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def _row_counts(conn: sqlite3.Connection) -> Dict[str, int]:
    return {table: int(conn.execute(f"SELECT COUNT(*) FROM {table}").fetchone()[0]) for table in REQUIRED_TABLES}


def _meta(conn: sqlite3.Connection, key: str) -> Optional[str]:
    row = conn.execute("SELECT value FROM meta WHERE key=?", (key,)).fetchone()
    return None if row is None else str(row[0])


def _remove_sidecars(db_path: str) -> List[str]:
    removed = []
    for suffix in _SIDECAR_SUFFIXES:
        p = db_path + suffix
        if os.path.exists(p):
            os.remove(p)
            removed.append(p)
    return removed


# ----- backup ----------------------------------------------------------------------------------


def backup(store: Store, dest_path: str, now: Optional[float] = None) -> Dict[str, Any]:
    """Online backup of the OPEN store into `dest_path` (sqlite3.Connection.backup). Refuses to overwrite.
    The `backup` journal row is written to the SOURCE after the copy, so the backup file does not carry
    a row about itself."""
    if os.path.exists(dest_path):
        raise StateCorrupt(f"refusing to back up onto {dest_path}: it already exists")
    Path(dest_path).parent.mkdir(parents=True, exist_ok=True)
    dest = sqlite3.connect(dest_path)
    try:
        store.conn.backup(dest)
        rows = _row_counts(dest)
        schema_version = _meta(dest, "schema_version")
    finally:
        dest.close()
    digest = _sha256_file(dest_path)
    store.journal("backup", detail={"path": os.path.abspath(dest_path), "sha256": digest}, now=now)
    return {"path": dest_path, "sha256": digest, "schema_version": schema_version, "rows": rows}


# ----- restore ---------------------------------------------------------------------------------


def _inspect_source(src_path: str) -> str:
    """Open `src_path` read-only and prove it is a ledger this controller knows. Returns its schema version."""
    if not os.path.isfile(src_path):
        raise StateCorrupt(f"refusing to restore: {src_path} does not exist")
    uri = f"file:{Path(src_path).resolve().as_posix()}?mode=ro"
    try:
        conn = sqlite3.connect(uri, uri=True, isolation_level=None)
    except sqlite3.DatabaseError as e:
        raise StateCorrupt(f"refusing to restore {src_path}: not a readable database: {e}")
    try:
        try:
            row = conn.execute("PRAGMA integrity_check").fetchone()
        except sqlite3.DatabaseError as e:
            raise StateCorrupt(f"refusing to restore {src_path}: integrity_check failed: {e}")
        if row is None or row[0] != "ok":
            raise StateCorrupt(f"refusing to restore {src_path}: integrity_check reported "
                               f"{row[0] if row else 'nothing'}")
        tables = {r[0] for r in conn.execute("SELECT name FROM sqlite_master WHERE type='table'")}
        missing = [t for t in REQUIRED_TABLES if t not in tables]
        if missing:
            raise StateCorrupt(f"refusing to restore {src_path}: tables missing {missing}")
        version = _meta(conn, "schema_version")
        if version not in KNOWN_SCHEMA_VERSIONS:
            raise StateCorrupt(f"refusing to restore {src_path}: schema_version {version!r} is not one this "
                               f"controller knows ({', '.join(KNOWN_SCHEMA_VERSIONS)})")
        return version
    finally:
        conn.close()


def restore(src_path: str, dest_path: str, *, replace: bool = False, now: Optional[float] = None) -> Dict[str, Any]:
    """Put the ledger at `src_path` in place at `dest_path`. Refuses an unknown or damaged source and an
    existing destination without `replace`; nothing is written on refusal. The copy goes to a temp file
    beside the destination and is moved onto it with `os.replace` after the stale `-wal` / `-shm`
    sidecars are removed, with every connection closed first (Windows cannot replace an open file)."""
    version_before = _inspect_source(src_path)
    dest_exists = os.path.exists(dest_path)
    if dest_exists and not replace:
        raise StateCorrupt(f"refusing to overwrite live state {dest_path}; pass replace to replace it deliberately")
    digest = _sha256_file(src_path)

    parent = Path(dest_path).resolve().parent
    parent.mkdir(parents=True, exist_ok=True)
    fd, tmp_path = tempfile.mkstemp(prefix=".restore-", suffix=".tmp", dir=str(parent))
    os.close(fd)
    try:
        src = sqlite3.connect(f"file:{Path(src_path).resolve().as_posix()}?mode=ro", uri=True)
        try:
            tmp = sqlite3.connect(tmp_path)
            try:
                src.backup(tmp)
            finally:
                tmp.close()
        finally:
            src.close()
        _remove_sidecars(tmp_path)
        _remove_sidecars(dest_path)
        os.replace(tmp_path, dest_path)
    except BaseException:
        for p in (tmp_path, tmp_path + "-wal", tmp_path + "-shm"):
            if os.path.exists(p):
                os.remove(p)
        raise

    restored = Store.open(dest_path)      # migrates schema 1 to 2 and runs the integrity check
    try:
        version_after = restored.meta("schema_version")
        restored.journal("restored", detail={"source": os.path.abspath(src_path), "sha256": digest,
                                             "replaced": bool(dest_exists)}, now=now)
    finally:
        restored.close()
    return {"path": dest_path, "source": src_path, "sha256": digest, "schema_version_before": version_before,
            "schema_version_after": version_after, "replaced": bool(dest_exists)}


# ----- per-run archives ------------------------------------------------------------------------


def _dict(row: Optional[sqlite3.Row]) -> Optional[Dict[str, Any]]:
    return None if row is None else dict(row)


def _ledger_slice(store: Store, run: sqlite3.Row, now: Optional[float]) -> Dict[str, Any]:
    run_id, task_id = run["run_id"], run["task_id"]
    pr = None
    if run["pr_number"] is not None:
        pr = store.row("SELECT * FROM prs WHERE pr_number=?", (run["pr_number"],))
    if pr is None:
        pr = store.row("SELECT * FROM prs WHERE task_id=? ORDER BY rowid DESC LIMIT 1", (task_id,))
    policy_version = None
    if run["policy_version_id"] is not None:
        policy_version = store.row("SELECT * FROM policy_versions WHERE id=?", (run["policy_version_id"],))
    return {
        "run": dict(run),
        "task": _dict(store.row("SELECT * FROM tasks WHERE task_id=?", (task_id,))),
        "pool": _dict(store.row("SELECT * FROM budget_pools WHERE pool_id=?", (run["pool_id"],))),
        "pr": _dict(pr),
        "journal": [dict(r) for r in store.rows("SELECT * FROM journal WHERE run_id=? OR task_id=? ORDER BY id",
                                                (run_id, task_id))],
        "usage": [dict(r) for r in store.rows("SELECT * FROM usage_observations WHERE run_id=? ORDER BY id", (run_id,))],
        "policy_version": _dict(policy_version),
        "archived_at": utcnow_iso(now),
    }


def _add_bytes(tar: tarfile.TarFile, name: str, data: bytes, mtime: float) -> None:
    info = tarfile.TarInfo(name)
    info.size = len(data)
    info.mtime = int(mtime)
    info.mode = 0o644
    tar.addfile(info, io.BytesIO(data))


def archive_run(store: Store, run_id: str, out_dir: str, runs_dir: str, now: Optional[float] = None) -> Dict[str, Any]:
    """Write `<out_dir>/<run_id>.tar.gz`: the run directory's files under `run/`, the transcript when the
    manifest lets `locate_transcript` find one, the run's slice of the ledger as `ledger.json`, and a
    `MANIFEST.txt` of member sizes. A missing run directory is allowed (the archive then holds the ledger
    only). No adapter method is called."""
    run = store.row("SELECT * FROM runs WHERE run_id=?", (run_id,))
    if run is None:
        raise PolicyError(f"no run {run_id} in {store.path}")
    os.makedirs(out_dir, exist_ok=True)
    out_path = os.path.join(out_dir, f"{run_id}.tar.gz")
    if os.path.exists(out_path):
        raise StateCorrupt(f"refusing to archive onto {out_path}: it already exists")

    t = time.time() if now is None else now
    ledger = _ledger_slice(store, run, now)
    run_dir = os.path.join(runs_dir, run_id)
    run_files: List[str] = []
    if os.path.isdir(run_dir):
        run_files = sorted(f for f in os.listdir(run_dir) if os.path.isfile(os.path.join(run_dir, f)))

    manifest: Dict[str, Any] = {}
    manifest_path = os.path.join(run_dir, "manifest.json")
    if "manifest.json" in run_files:
        try:
            with open(manifest_path, "r", encoding="utf-8") as f:
                loaded = json.load(f)
            if isinstance(loaded, dict):
                manifest = loaded
        except (OSError, ValueError):
            manifest = {}
    transcript: Optional[str] = None
    if manifest:
        from .adapters import locate_run_transcript   # a pure path helper; no adapter method is called
        transcript = locate_run_transcript(manifest, run["native_session_id"])

    members: List[str] = []
    sizes: List[tuple] = []
    with tarfile.open(out_path, "w:gz") as tar:
        for name in run_files:
            src = os.path.join(run_dir, name)
            arcname = f"run/{name}"
            tar.add(src, arcname=arcname, recursive=False)
            members.append(arcname)
            sizes.append((arcname, os.path.getsize(src)))
        if transcript:
            tar.add(transcript, arcname="transcript.jsonl", recursive=False)
            members.append("transcript.jsonl")
            sizes.append(("transcript.jsonl", os.path.getsize(transcript)))
        ledger_bytes = json.dumps(ledger, indent=2, sort_keys=True, default=str).encode("utf-8")
        _add_bytes(tar, "ledger.json", ledger_bytes, t)
        members.append("ledger.json")
        sizes.append(("ledger.json", len(ledger_bytes)))
        lines = [f"run_id {run_id}", f"archived_at {ledger['archived_at']}"]
        lines += [f"{size} {name}" for name, size in sizes]
        lines.append("MANIFEST.txt")
        manifest_bytes = ("\n".join(lines) + "\n").encode("utf-8")
        _add_bytes(tar, "MANIFEST.txt", manifest_bytes, t)
        members.append("MANIFEST.txt")

    digest = _sha256_file(out_path)
    pr_number = ledger["pr"]["pr_number"] if ledger["pr"] else None
    store.journal("evidence-archived", task_id=run["task_id"], run_id=run_id,
                  detail={"run_id": run_id, "path": os.path.abspath(out_path), "sha256": digest, "members": members},
                  now=now)
    return {"path": out_path, "sha256": digest, "run_id": run_id, "members": members,
            "transcript_included": transcript is not None, "pr_number": pr_number}


def list_archives(out_dir: str) -> List[Dict[str, Any]]:
    """Every `*.tar.gz` in `out_dir`, sorted by run id."""
    if not os.path.isdir(out_dir):
        return []
    found = []
    for name in os.listdir(out_dir):
        if not name.endswith(".tar.gz"):
            continue
        path = os.path.join(out_dir, name)
        if not os.path.isfile(path):
            continue
        st = os.stat(path)
        found.append({"run_id": name[: -len(".tar.gz")], "path": path, "bytes": int(st.st_size),
                      "modified_at": utcnow_iso(st.st_mtime)})
    return sorted(found, key=lambda a: a["run_id"])
