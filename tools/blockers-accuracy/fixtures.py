"""Recorded GitHub REST responses: written by capture.py, replayed to the native arm by run.py."""
from __future__ import annotations

import datetime
import hashlib
import json
import pathlib
import re
import urllib.error
import urllib.parse
import urllib.request


def slug(url: str) -> str:
    return url.removeprefix("https://github.com/").replace("/", "_")


def _name(path: str) -> str:
    base = re.sub(r"[^A-Za-z0-9._-]+", "_", path.strip("/"))[:150]
    return base + "-" + hashlib.sha256(path.encode()).hexdigest()[:8] + ".json"


class GitHubFixtures:
    def __init__(self, root: pathlib.Path, token: str = "", allow_live: bool = True):
        self.root = root
        self.root.mkdir(parents=True, exist_ok=True)
        self.token = token
        self.allow_live = allow_live
        self.live_paths: list[str] = []

    def _fetch(self, path: str) -> dict:
        req = urllib.request.Request("https://api.github.com" + path, headers={
            "Accept": "application/vnd.github+json", "User-Agent": "ashlar-blockers-accuracy",
            **({"Authorization": f"Bearer {self.token}"} if self.token else {})})
        try:
            with urllib.request.urlopen(req, timeout=60) as r:
                rec = {"path": path, "status": r.status, "body": r.read().decode("utf-8", "replace")}
        except urllib.error.HTTPError as e:
            rec = {"path": path, "status": e.code, "body": e.read().decode("utf-8", "replace")}
        rec["recorded_at"] = datetime.datetime.now(datetime.timezone.utc).isoformat()
        return rec

    def record(self, path: str, refresh: bool = False) -> str:
        f = self.root / _name(path)
        if refresh or not f.exists():
            f.write_text(json.dumps(self._fetch(path)))
        rec = json.loads(f.read_text())
        return rec["body"]

    def get(self, path: str) -> tuple[str, bool]:
        """Return (tool result text, served_live). Tries the exact path, then without its query string."""
        if not path.startswith("/"):
            path = "/" + path
        if path.startswith("https://api.github.com"):
            path = path.removeprefix("https://api.github.com")
        candidates = [path]
        bare = urllib.parse.urlsplit(path).path
        if bare != path:
            candidates.append(bare)
        for p in candidates:
            f = self.root / _name(p)
            if f.exists():
                rec = json.loads(f.read_text())
                return (rec["body"] if rec["status"] < 400 else f"HTTP {rec['status']}\n{rec['body']}"), False
        if not self.allow_live:
            return "HTTP 404\n{\"message\": \"Not Found\"}", False
        rec = self._fetch(path)
        (self.root / _name(path)).write_text(json.dumps(rec))
        self.live_paths.append(path)
        return (rec["body"] if rec["status"] < 400 else f"HTTP {rec['status']}\n{rec['body']}"), True
