#!/usr/bin/env python3
"""Check a sweep's retained record citation; the .NET archive owns signature verification.

This checks JSON shape and current file bytes, not the truth of the record or sidecar. A
writer able to replace both can forge this local receipt; it is not an external anchor.
"""

import hashlib
import json
from pathlib import Path
import re
import sys


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("duplicate JSON field")
        result[key] = value
    return result


def inspect(directory):
    if not directory:
        raise ValueError("no campaign directory supplied")
    root = Path(directory).resolve()
    files = sorted(root.rglob("*.evidence.json")) if root.is_dir() else []
    if len(files) != 1:
        raise ValueError(f"{len(files)} *.evidence.json files: expected exactly one retained verdict sidecar")
    sidecar = files[0]
    if sidecar.stat().st_size > 128 * 1024:
        raise ValueError("evidence sidecar exceeds 128 KiB")
    data = json.loads(sidecar.read_text(encoding="utf-8"), object_pairs_hook=unique_object,
                      parse_constant=lambda _: (_ for _ in ()).throw(ValueError("invalid JSON constant")))
    if not isinstance(data, dict):
        raise ValueError("evidence sidecar must be a JSON object")
    if data.get("verified") is not True:
        code = data.get("failureCode")
        reason = data.get("failureReason")
        raise ValueError(f"{code}: {reason}" if isinstance(code, str) and code else "sidecar does not report verified: true")
    if data.get("failureCode") or data.get("failureReason"):
        raise ValueError("sidecar reports verified: true alongside a failure")
    fingerprint = data.get("signerFingerprint")
    if not isinstance(fingerprint, str) or not re.fullmatch(r"ed25519:[0-9a-f]{16}", fingerprint):
        raise ValueError("sidecar names no valid signer fingerprint")
    for field in ("pinningEnabled", "usesDevHmacKey"):
        if type(data.get(field)) is not bool:
            raise ValueError(f"sidecar must disclose {field} as a boolean")
    record_path = data.get("recordPath")
    if not isinstance(record_path, str) or not record_path or any(ord(c) < 32 for c in record_path):
        raise ValueError("sidecar names no valid record path")
    record = Path(record_path)
    if not record.is_absolute():
        raise ValueError("record path must be absolute")
    record = record.resolve()
    if root not in record.parents:
        raise ValueError("cited record is outside this campaign directory")
    expected = data.get("recordSha256")
    if not isinstance(expected, str) or not re.fullmatch(r"[0-9a-f]{64}", expected):
        raise ValueError("sidecar names no valid record SHA-256")
    digest = hashlib.sha256()
    with record.open("rb") as stream:
        for block in iter(lambda: stream.read(65536), b""):
            digest.update(block)
    if digest.hexdigest() != expected:
        raise ValueError("cited record SHA-256 differs from the bytes re-verified by the archive")
    return data


def main():
    mode, directory = sys.argv[1:]
    try:
        data = inspect(directory)
    except (OSError, ValueError, UnicodeError, RecursionError) as error:
        if mode == "reason":
            # Failure text becomes one ledger cell, never executable workflow syntax.
            print(" ".join(str(error).split())[:400], end="")
        return 3 if mode == "classify" else 0
    if mode == "citation":
        print(f"record={data['recordPath']} sha256={data['recordSha256']} "
              f"signer={data['signerFingerprint']} "
              f"pinned={str(data['pinningEnabled']).lower()} "
              f"dev-hmac={str(data['usesDevHmacKey']).lower()}", end="")
    return 0


if __name__ == "__main__":
    sys.exit(main())
