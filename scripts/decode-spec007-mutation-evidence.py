#!/usr/bin/env python3
"""Decode one mutation evidence archive from the captured complete harness log.

Runs on the host. Example: python scripts/decode-spec007-mutation-evidence.py
--log ../artifacts/mutation.log --output ../artifacts/mutation-evidence --repo .
Output must be new, outside --repo. Archive hashes, lengths, members and extraction
limits are checked before any final output directory is created. No links/devices.
"""
import argparse
import base64
import hashlib
import io
import os
from pathlib import Path, PurePosixPath, PureWindowsPath
import re
import shutil
import tarfile
import tempfile


MAX_ARCHIVE = 128 * 1024 * 1024
MAX_EXPANDED = 512 * 1024 * 1024
MAX_MEMBERS = 10000


def read_payload(text):
    pattern = r"(?m)^BEGIN ASHLAR_MUTATION_EVIDENCE sha256=([0-9a-f]{64}) bytes=(\d+)\r?\n([A-Za-z0-9+/=\r\n]+)^END ASHLAR_MUTATION_EVIDENCE\r?$"
    matches = list(re.finditer(pattern, text))
    if len(matches) != 1 or text.count("BEGIN ASHLAR_MUTATION_EVIDENCE") != 1 or text.count("END ASHLAR_MUTATION_EVIDENCE") != 1:
        raise ValueError("expected one complete, unambiguous evidence archive")
    digest, length, encoded = matches[0].groups()
    if int(length) > MAX_ARCHIVE or len(encoded) > MAX_ARCHIVE * 4 // 3 + 4096:
        raise ValueError("archive exceeds extraction limit")
    payload = base64.b64decode(encoded.replace("\r", "").replace("\n", ""), validate=True)
    if len(payload) != int(length) or hashlib.sha256(payload).hexdigest() != digest:
        raise ValueError("evidence length or SHA256 mismatch")
    return payload


def member_path(name):
    path = PurePosixPath(name)
    if not name or "\\" in name or any(ord(c) < 32 for c in name) or path.is_absolute() or PureWindowsPath(name).drive:
        raise ValueError("unsafe archive member path")
    if any(part in ("..", ".git") for part in path.parts) or not path.parts or path.parts[0] != "evidence" or str(path) != name.rstrip("/"):
        raise ValueError("archive member escaped evidence root")
    # Windows ADS and reserved device paths must not be materialised by the host decoder.
    for part in path.parts:
        stem = part.split(".", 1)[0].upper()
        if ":" in part or part.endswith((".", " ")) or stem in {"CON", "PRN", "AUX", "NUL", *(f"COM{i}" for i in range(1, 10)), *(f"LPT{i}" for i in range(1, 10))}:
            raise ValueError("unsafe Windows archive member path")
    return Path(*path.parts[1:])


def decode(text, output, repo):
    output, repo = output.resolve(), repo.resolve()
    if output == repo or output.is_relative_to(repo) or output.exists():
        raise ValueError("output must be new and outside repository")
    payload = read_payload(text)
    with tarfile.open(fileobj=io.BytesIO(payload), mode="r:gz") as archive:
        members = []
        names, total = set(), 0
        for entry in archive:
            if len(members) >= MAX_MEMBERS or not (entry.isfile() or entry.isdir()):
                raise ValueError("too many members or forbidden link/device")
            relative = member_path(entry.name)
            key = str(relative).casefold()
            if key in names:
                raise ValueError("duplicate or case-colliding archive member")
            names.add(key)
            total += entry.size
            if entry.size < 0 or total > MAX_EXPANDED or (entry.isdir() and entry.size):
                raise ValueError("invalid size or expanded archive exceeds limit")
            members.append((entry, relative))
        if not any(str(relative) == "summary.json" and entry.isfile() for entry, relative in members):
            raise ValueError("archive has no batch summary")
        output.parent.mkdir(parents=True, exist_ok=True)
        staging = Path(tempfile.mkdtemp(prefix=".mutation-evidence-", dir=output.parent))
        try:
            for entry, relative in members:
                target = staging / relative
                if not target.resolve().is_relative_to(staging.resolve()):
                    raise ValueError("extraction escaped staging directory")
                if entry.isdir():
                    target.mkdir(parents=True, exist_ok=True)
                else:
                    target.parent.mkdir(parents=True, exist_ok=True)
                    with archive.extractfile(entry) as source, target.open("xb") as destination:
                        shutil.copyfileobj(source, destination)
                    if target.stat().st_size != entry.size:
                        raise ValueError("extracted size mismatch")
            if output.exists():
                raise ValueError("output appeared during extraction")
            os.rename(staging, output)
        finally:
            if staging.exists():
                shutil.rmtree(staging)
    return output


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--log", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--repo", required=True)
    args = parser.parse_args()
    output = decode(Path(args.log).read_text(encoding="utf-8-sig"), Path(args.output), Path(args.repo))
    print("Verified evidence extracted to " + str(output))


if __name__ == "__main__":
    main()
