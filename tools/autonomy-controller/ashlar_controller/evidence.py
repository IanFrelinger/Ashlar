"""Evidence retention: a backup of the live ledger, a restore that refuses what it does not know, and a
per-run archive that keeps the run's files, its transcript and its slice of the ledger together.

Everything here is LLM-free. No adapter method is ever called; the only adapter symbol used is
`adapters.claude.locate_transcript`, a pure path helper, imported lazily inside `archive_run`.

Standard library only: sqlite3 (the online backup API), tarfile, hashlib, json, os, re, tempfile.
"""
from __future__ import annotations

import hashlib
import io
import json
import os
import re
import sqlite3
import tarfile
import tempfile
import time
from pathlib import Path
from typing import Any, Dict, Iterator, List, Optional, Tuple

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


# What an archive leaves out. An archive may be published (the storage branch is on a public repository), so
# the account identifiers and the account's email address are removed from the ARCHIVED copy; the transcript
# and the run directory on disk are left as they are. Three layers, in this order:
#
# 1. Transcript records, by record type. Observed (phase F pre-flight, finding C7): the first line of every Codex
#    session file is a `session_meta` record whose payload names the ChatGPT account (`creator_user_id`,
#    `creator_account_id`); the keys are removed.
TRANSCRIPT_ACCOUNT_KEYS = {"session_meta": ("creator_user_id", "creator_account_id")}
#    Observed (PR 5 live run, 2026-10-13): every Claude transcript holds an `attachment` record of type
#    `session_context` whose `attachment.context.userEmail` is a sentence naming the account's email address,
#    and whose `rendered[*].content` (a key of the record itself in CLI 2.1.263; `attachment.rendered` is read
#    too) repeats it. The key is removed, and every address in the rendered text becomes `<redacted>`. Named
#    `attachment.<type>.<key>` and `attachment.<type>.rendered`.
TRANSCRIPT_ATTACHMENT_ACCOUNT_KEYS = {"session_context": ("userEmail",)}
# 2. Run-directory files, by file name: top-level string keys in which every address becomes `<redacted>`.
#    Observed in the same run: the Cursor manifest's `status_text` is the `cursor-agent status` text,
#    `Logged in as <account email>`. Named `run/<file>:<key>`.
RUN_FILE_ACCOUNT_KEYS = {"manifest.json": ("status_text",)}
# 3. A safety net over every text member the archive writes (the transcript, each `run/` file and `ledger.json`;
#    a member that is not UTF-8 is copied unchanged): any address left after layers 1 and 2 becomes `<redacted>`,
#    except the commit-trailer address `noreply@anthropic.com` and the reserved test domain `example.invalid`.
#    Named `<member>:email`.
KEPT_ADDRESSES = {("noreply", "anthropic.com")}
KEPT_DOMAINS = ("example.invalid",)
REDACTED = "<redacted>"

# A conservative address shape, found from its `@`: the domain is one or more labels and an alphabetic top
# label; the local part is the run of local-part characters before the `@`. Neither part can contain a quote,
# a backslash or white space, so a replacement never breaks the JSON string it sits in.
_EMAIL_DOMAIN = re.compile(r"@(?:[A-Za-z0-9-]+\.)+[A-Za-z]{2,63}")
_LOCAL_PART_CHARS = frozenset("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789._%+-")
_HEX = frozenset("0123456789abcdefABCDEF")


def _address_spans(text: str, json_escapes: bool) -> Iterator[Tuple[int, int, int]]:
    """(start, at, end) of each address in `text`. With `json_escapes`, `text` is raw JSON: a local part that
    begins right after an escaping backslash starts after the escape (`\\n`, `\\t`, `\\uXXXX`...), which stays."""
    floor = 0
    for m in _EMAIL_DOMAIN.finditer(text):
        at = m.start()
        start = at
        while start > floor and text[start - 1] in _LOCAL_PART_CHARS:
            start -= 1
        if json_escapes and start < at:
            slashes = 0
            while start - slashes - 1 >= 0 and text[start - slashes - 1] == "\\":
                slashes += 1
            if slashes % 2 == 1:
                hex4 = text[start + 1:start + 5]
                start += 5 if text[start] == "u" and len(hex4) == 4 and set(hex4) <= _HEX else 1
        if start < at:
            yield start, at, m.end()
        floor = m.end()


def _is_kept(local: str, domain: str) -> bool:
    domain = domain.lower()
    return (local.lower(), domain) in KEPT_ADDRESSES or domain in KEPT_DOMAINS


def mask_emails(text: str, *, keep_allowed: bool = True, json_escapes: bool = False) -> Tuple[str, int]:
    """Return (`text` with each address replaced by `<redacted>`, the number replaced). With `keep_allowed`,
    the addresses of KEPT_ADDRESSES and KEPT_DOMAINS stay. Everything outside an address is kept as it is."""
    out: List[str] = []
    last = count = 0
    for start, at, end in _address_spans(text, json_escapes):
        if keep_allowed and _is_kept(text[start:at], text[at + 1:end]):
            continue
        out.append(text[last:start])
        out.append(REDACTED)
        last = end
        count += 1
    if not count:
        return text, 0
    out.append(text[last:])
    return "".join(out), count


def _redact_attachment(obj: Dict[str, Any]) -> List[str]:
    """Layer 1 for an `attachment` record of TRANSCRIPT_ATTACHMENT_ACCOUNT_KEYS, in place. Returns the names."""
    attachment = obj.get("attachment")
    kind = attachment.get("type") if isinstance(attachment, dict) else None
    keys = TRANSCRIPT_ATTACHMENT_ACCOUNT_KEYS.get(kind) if isinstance(kind, str) else None
    if not keys:
        return []
    removed: List[str] = []
    context = attachment.get("context")
    if isinstance(context, dict):
        for k in keys:
            if k in context:
                del context[k]
                removed.append(f"attachment.{kind}.{k}")
    masked = 0
    for holder in (obj, attachment):
        rendered = holder.get("rendered")
        if not isinstance(rendered, list):
            continue
        for i, item in enumerate(rendered):
            if isinstance(item, dict) and isinstance(item.get("content"), str):
                item["content"], n = mask_emails(item["content"], keep_allowed=False)
                masked += n
            elif isinstance(item, str):
                rendered[i], n = mask_emails(item, keep_allowed=False)
                masked += n
    if masked:
        removed.append(f"attachment.{kind}.rendered")
    return removed


def redact_transcript(data: bytes) -> tuple:
    """Return (the transcript without the account identifiers of TRANSCRIPT_ACCOUNT_KEYS and
    TRANSCRIPT_ATTACHMENT_ACCOUNT_KEYS, the dotted names removed). Only a line that changes is re-serialized; any
    other line, a line that is not a JSON object included, is kept byte for byte, and so is its line ending."""
    removed: List[str] = []
    out: List[bytes] = []
    for line in data.split(b"\n"):
        body, cr = (line[:-1], b"\r") if line.endswith(b"\r") else (line, b"")
        try:
            obj = json.loads(body.decode("utf-8")) if body.strip() else None
        except (UnicodeDecodeError, ValueError):
            obj = None
        names: List[str] = []
        if isinstance(obj, dict):
            payload = obj.get("payload")
            keys = TRANSCRIPT_ACCOUNT_KEYS.get(obj.get("type")) if isinstance(obj.get("type"), str) else None
            if keys and isinstance(payload, dict):
                for k in keys:
                    if k in payload:
                        del payload[k]
                        names.append(f"{obj['type']}.{k}")
            if obj.get("type") == "attachment":
                names += _redact_attachment(obj)
        if names:
            removed += names
            out.append(json.dumps(obj, ensure_ascii=False).encode("utf-8") + cr)
        else:
            out.append(line)
    return b"\n".join(out), removed


def redact_run_file(name: str, data: bytes) -> Tuple[bytes, List[str]]:
    """Layer 2: in the run file `name`, replace every address inside the string value of each key that
    RUN_FILE_ACCOUNT_KEYS names for it. Returns (the bytes, the keys changed). A file that is not UTF-8 is
    returned unchanged, and every byte outside those values is kept."""
    keys = RUN_FILE_ACCOUNT_KEYS.get(name)
    if not keys:
        return data, []
    try:
        text = data.decode("utf-8")
    except UnicodeDecodeError:
        return data, []
    changed: List[str] = []
    for key in keys:
        pattern = re.compile(r'("' + re.escape(key) + r'"\s*:\s*")((?:[^"\\]|\\.)*)(")')

        def sub(m) -> str:
            value, n = mask_emails(m.group(2), keep_allowed=False, json_escapes=True)
            if n and key not in changed:
                changed.append(key)
            return m.group(1) + value + m.group(3)

        text = pattern.sub(sub, text)
    return (text.encode("utf-8"), changed) if changed else (data, [])


def mask_member(name: str, data: bytes) -> Tuple[bytes, bool]:
    """Layer 3, the safety net: (the member's bytes with every remaining address masked, whether any was).
    Bytes that are not UTF-8 are returned unchanged. A `.json` or `.jsonl` member is masked as raw JSON."""
    try:
        text = data.decode("utf-8")
    except UnicodeDecodeError:
        return data, False
    masked, n = mask_emails(text, json_escapes=name.endswith((".json", ".jsonl")))
    return (masked.encode("utf-8"), True) if n else (data, False)


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
    only). No adapter method is called. The members are redacted copies (the three layers above); the files on
    disk are only read, and `MANIFEST.txt`, the result and the journal row name what was redacted."""
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
    removed: Dict[str, List[str]] = {}      # member -> what layers 1 and 2 removed from it
    masked: List[str] = []                  # members in which the safety net masked an address

    def add(tar: tarfile.TarFile, arcname: str, data: bytes, mtime: float) -> None:
        # Every member is written from the bytes read once (a run still in flight cannot slip unredacted bytes in
        # between a check and a copy), after the safety net.
        data, hit = mask_member(arcname, data)
        if hit:
            masked.append(arcname)
        _add_bytes(tar, arcname, data, mtime)
        members.append(arcname)
        sizes.append((arcname, len(data)))

    with tarfile.open(out_path, "w:gz") as tar:
        for name in run_files:
            src = os.path.join(run_dir, name)
            with open(src, "rb") as f:
                data, keys = redact_run_file(name, f.read())
            if keys:
                removed[f"run/{name}"] = keys
            add(tar, f"run/{name}", data, os.path.getmtime(src))
        if transcript:
            with open(transcript, "rb") as f:
                data, names = redact_transcript(f.read())
            if names:
                removed["transcript.jsonl"] = names
            add(tar, "transcript.jsonl", data, os.path.getmtime(transcript))
        ledger_bytes = json.dumps(ledger, indent=2, sort_keys=True, default=str).encode("utf-8")
        add(tar, "ledger.json", ledger_bytes, t)
        lines = [f"run_id {run_id}", f"archived_at {ledger['archived_at']}"]
        lines += [f"{size} {name}" for name, size in sizes]
        for member in sorted(removed):
            lines.append(f"redacted from {member}: " + ", ".join(sorted(set(removed[member]))))
        if masked:
            lines.append("email addresses masked in: " + ", ".join(sorted(masked)))
        lines.append("MANIFEST.txt")
        manifest_bytes = ("\n".join(lines) + "\n").encode("utf-8")
        _add_bytes(tar, "MANIFEST.txt", manifest_bytes, t)
        members.append("MANIFEST.txt")

    digest = _sha256_file(out_path)
    pr_number = ledger["pr"]["pr_number"] if ledger["pr"] else None
    redacted = _redacted_names(removed, masked)
    store.journal("evidence-archived", task_id=run["task_id"], run_id=run_id,
                  detail={"run_id": run_id, "path": os.path.abspath(out_path), "sha256": digest, "members": members,
                          "redacted": redacted},
                  now=now)
    return {"path": out_path, "sha256": digest, "run_id": run_id, "members": members,
            "transcript_included": transcript is not None, "pr_number": pr_number, "redacted": redacted}


def _redacted_names(removed: Dict[str, List[str]], masked: List[str]) -> List[str]:
    """The sorted `redacted` list: a transcript record path as it is (`session_meta.creator_user_id`,
    `attachment.session_context.userEmail`), `<member>:<key>` for a run file's key (`run/manifest.json:status_text`),
    and `<member>:email` for each member the safety net changed."""
    names = []
    for member, whats in removed.items():
        names += [w if member == "transcript.jsonl" else f"{member}:{w}" for w in whats]
    names += [f"{member}:email" for member in masked]
    return sorted(set(names))


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
