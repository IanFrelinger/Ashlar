#!/usr/bin/env python3
"""Sign an explicit release-evidence snapshot, or verify it against a retained anchor.

Python 3.10+ and OpenSSL 3 with Ed25519 are required. No network calls are made.
This verifies bytes and the pinned signer, not the truth or freshness of observations.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import re
import shutil
import subprocess
import sys
import tempfile
from datetime import datetime, timezone
from pathlib import Path

SCHEMA = "ashlar.evidence-export/1"
MAX_FILE_BYTES = 64 * 1024 * 1024
MAX_TOTAL_BYTES = 256 * 1024 * 1024
MAX_FILES = 128
ED25519_SPKI_PREFIX = bytes.fromhex("302a300506032b6570032100")


class InvalidExport(ValueError):
    pass


def openssl(*arguments: str) -> bytes:
    result = subprocess.run(["openssl", *arguments], stdin=subprocess.DEVNULL,
                            capture_output=True, timeout=30, check=False)
    if result.returncode:
        # OpenSSL diagnostics may include operator-specific paths; keep them out of evidence/logs.
        raise InvalidExport("OpenSSL rejected the key or signature")
    return result.stdout


def check_key(path: Path, public: bool) -> None:
    arguments = ["pkey"] + (["-pubin"] if public else [])
    der = openssl(*arguments, "-in", str(path), "-pubout", "-outform", "DER")
    if len(der) != 44 or not der.startswith(ED25519_SPKI_PREFIX):
        raise InvalidExport("an Ed25519 PEM key is required")


def read_file(path: Path, limit: int = MAX_FILE_BYTES) -> bytes:
    if path.is_symlink() or not path.is_file():
        raise InvalidExport("a regular, non-symlink file is required")
    if path.stat().st_size > limit:
        raise InvalidExport("file exceeds its export size limit")
    with path.open("rb") as stream:
        data = stream.read(limit + 1)
    if len(data) > limit:
        raise InvalidExport("file exceeds its export size limit")
    return data


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def parse_json(data: bytes) -> object:
    def pairs(items):
        result = {}
        for name, value in items:
            if name in result:
                raise InvalidExport("duplicate JSON property")
            result[name] = value
        return result

    def invalid_constant(_value):
        raise InvalidExport("non-finite JSON value")

    return json.loads(data, object_pairs_hook=pairs, parse_constant=invalid_constant)


def export_snapshot(evidence: Path, report: Path, audits: list[Path], key: Path, output: Path) -> str:
    if output.exists():
        raise InvalidExport("output must not already exist")
    if len(audits) + 2 > MAX_FILES:
        raise InvalidExport("too many export files")
    check_key(key, public=False)
    evidence_bytes = read_file(evidence)
    try:
        pack = parse_json(evidence_bytes)
    except (json.JSONDecodeError, UnicodeDecodeError, RecursionError) as error:
        raise InvalidExport("evidence must be a JSON object") from error
    if not isinstance(pack, dict):
        raise InvalidExport("evidence must be a JSON object")
    files = [("evidence.json", evidence_bytes)]
    total = len(evidence_bytes)
    for name, path in [("evidence.md", report)] + [
            (f"audit-{i:04d}.ndjson", path) for i, path in enumerate(audits)]:
        data = read_file(path, limit=min(MAX_FILE_BYTES, MAX_TOTAL_BYTES - total))
        total += len(data)
        files.append((name, data))
    manifest = {
        "schemaVersion": SCHEMA,
        "signatureAlgorithm": "Ed25519",
        "exportedAt": datetime.now(timezone.utc).isoformat(),
        "actor": {"authenticatedIdentity": None, "status": "unknown"},
        "coverage": "Exact exported bytes only; supplied audit files may be incomplete before export.",
        "files": [{"path": name, "size": len(data), "sha256": sha256(data)} for name, data in files],
    }
    manifest_bytes = (json.dumps(manifest, sort_keys=True, separators=(",", ":")) + "\n").encode()
    output.parent.mkdir(parents=True, exist_ok=True)
    # Assemble alongside the destination and rename only after signing succeeds.
    staging = Path(tempfile.mkdtemp(prefix=".ashlar-evidence-", dir=output.parent))
    try:
        for name, data in files:
            (staging / name).write_bytes(data)
        (staging / "manifest.json").write_bytes(manifest_bytes)
        signature = openssl("pkeyutl", "-sign", "-rawin", "-inkey", str(key),
                            "-in", str(staging / "manifest.json"))
        if len(signature) != 64:
            raise InvalidExport("invalid Ed25519 signature length")
        (staging / "manifest.sig").write_bytes(signature)
        staging.rename(output)
    finally:
        if staging.exists():
            shutil.rmtree(staging)
    return sha256(manifest_bytes)


def verify_snapshot(bundle: Path, public_key: Path, expected_sha256: str) -> None:
    if not re.fullmatch(r"[0-9a-fA-F]{64}", expected_sha256):
        raise InvalidExport("expected manifest SHA256 must be a retained 64-digit hex value")
    if bundle.is_symlink() or not bundle.is_dir():
        raise InvalidExport("bundle must be a non-symlink directory")
    manifest_bytes = read_file(bundle / "manifest.json", limit=128 * 1024)
    if sha256(manifest_bytes) != expected_sha256.lower():
        raise InvalidExport("manifest does not match the retained anchor")
    signature_bytes = read_file(bundle / "manifest.sig", limit=64)
    if len(signature_bytes) != 64:
        raise InvalidExport("invalid Ed25519 signature length")
    public_key_bytes = read_file(public_key, limit=64 * 1024)
    # Authenticate precisely the bytes whose anchor was checked, with one captured trusted key.
    # OpenSSL must not reopen mutable caller paths after those bytes have been inspected.
    with tempfile.TemporaryDirectory(prefix="ashlar-evidence-verify-") as temporary:
        captured = Path(temporary)
        manifest_path, signature_path, key_path = (
            captured / "manifest.json", captured / "manifest.sig", captured / "public.pem")
        manifest_path.write_bytes(manifest_bytes)
        signature_path.write_bytes(signature_bytes)
        key_path.write_bytes(public_key_bytes)
        check_key(key_path, public=True)
        openssl("pkeyutl", "-verify", "-rawin", "-pubin", "-inkey", str(key_path),
                "-in", str(manifest_path), "-sigfile", str(signature_path))
    try:
        manifest = parse_json(manifest_bytes)
    except (json.JSONDecodeError, UnicodeDecodeError, RecursionError) as error:
        raise InvalidExport("invalid manifest JSON") from error
    if not isinstance(manifest, dict) or manifest.get("schemaVersion") != SCHEMA:
        raise InvalidExport("unsupported manifest schema")
    if manifest.get("signatureAlgorithm") != "Ed25519":
        raise InvalidExport("unsupported signature algorithm")
    files = manifest.get("files")
    if not isinstance(files, list) or not 2 <= len(files) <= MAX_FILES:
        raise InvalidExport("invalid manifest file inventory")
    names = set()
    total = 0
    for item in files:
        if not isinstance(item, dict):
            raise InvalidExport("invalid manifest file entry")
        name = item.get("path")
        if not isinstance(name, str) or not re.fullmatch(r"evidence\.(json|md)|audit-[0-9]{4}\.ndjson", name):
            raise InvalidExport("invalid manifest path")
        if name in names:
            raise InvalidExport("duplicate manifest path")
        names.add(name)
        data = read_file(bundle / name)
        total += len(data)
        if total > MAX_TOTAL_BYTES:
            raise InvalidExport("export exceeds the 256 MiB total size limit")
        if type(item.get("size")) is not int or len(data) != item["size"] or sha256(data) != item.get("sha256"):
            raise InvalidExport("exported file bytes differ from the signed manifest")
    if not {"evidence.json", "evidence.md"}.issubset(names):
        raise InvalidExport("manifest is missing the evidence or report")
    if set(path.name for path in bundle.iterdir()) != names | {"manifest.json", "manifest.sig"}:
        raise InvalidExport("bundle inventory differs from the signed manifest")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    export = commands.add_parser("export")
    export.add_argument("--evidence", type=Path, required=True)
    export.add_argument("--report", type=Path, required=True)
    export.add_argument("--audit", type=Path, action="append", default=[])
    export.add_argument("--private-key", type=Path, required=True)
    export.add_argument("--output", type=Path, required=True)
    verify = commands.add_parser("verify")
    verify.add_argument("--bundle", type=Path, required=True)
    verify.add_argument("--trusted-public-key", type=Path, required=True)
    verify.add_argument("--expected-manifest-sha256", required=True)
    args = parser.parse_args()
    try:
        if args.command == "export":
            print(export_snapshot(args.evidence, args.report, args.audit, args.private_key, args.output))
        else:
            verify_snapshot(args.bundle, args.trusted_public_key, args.expected_manifest_sha256)
            print("Verified: anchored manifest, pinned Ed25519 signer and exact exported bytes.")
    except (InvalidExport, OSError, subprocess.TimeoutExpired) as error:
        print(f"Evidence export refused: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
