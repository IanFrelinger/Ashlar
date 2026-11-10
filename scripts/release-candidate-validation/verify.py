#!/usr/bin/env python3
"""Fail-closed checks for the nonpublishing RC rehearsal evidence."""
import hashlib
import json
from pathlib import Path
import sys
import xml.etree.ElementTree as ET
import zipfile


def require(condition, message):
    if not condition:
        raise ValueError(message)


def assets(path, version, package_root):
    data = json.loads(Path(path).read_text())
    libraries = data["libraries"]
    require(not any(v.get("type") == "project" for v in libraries.values()),
            "ProjectReference detected in package-only consumer")
    ashlar = {k: v for k, v in libraries.items() if k.lower().startswith("ashlar.")}
    require(len(ashlar) >= 10, f"Incomplete Ashlar dependency closure: {len(ashlar)}")
    require(f"Ashlar.Hosting.Bundle/{version}" in ashlar, "Expected bundle is absent")
    for name, value in ashlar.items():
        require(name.rsplit("/", 1)[1].lower() == version.lower(), f"Wrong version: {name}")
        require(value.get("type") == "package", f"Not a package: {name}")
    roots = {str(Path(p).resolve()) for p in data["packageFolders"]}
    require(roots == {str(Path(package_root).resolve())}, f"Unexpected package caches: {roots}")
    print(f"PASS assets: {len(ashlar)} Ashlar packages at {version}; no project references; isolated cache")


def trx(path, count):
    root = ET.parse(path)
    counters = root.find(".//{*}Counters")
    require(counters is not None, "Missing TRX counters")
    expected = {"total": count, "executed": count, "passed": count, "failed": 0}
    actual = {k: int(counters.get(k, "-1")) for k in expected}
    require(actual == expected, f"Wrong test counts: {actual}, expected {expected}")
    results = root.findall(".//{*}UnitTestResult")
    require(len(results) == count and all(r.get("outcome") == "Passed" for r in results),
            "TRX results disagree with counters")
    print(f"PASS TRX: {count}/{count} executed and passed; none skipped")


def packages(directory, version, source_sha):
    rows = []
    for path in sorted(Path(directory).glob("*.nupkg")):
        with zipfile.ZipFile(path) as archive:
            spec = [n for n in archive.namelist() if n.endswith(".nuspec")]
            require(len(spec) == 1, f"Expected one nuspec: {path}")
            root = ET.fromstring(archive.read(spec[0]))
            metadata = root.find("{*}metadata")
            package_id = metadata.findtext("{*}id")
            package_version = metadata.findtext("{*}version")
            require(package_version == version, f"Wrong package version: {path}")
        rows.append({"file": path.name, "id": package_id, "version": package_version,
                     "sha256": hashlib.sha256(path.read_bytes()).hexdigest(), "bytes": path.stat().st_size})
    expected = {"Ashlar." + name for name in (
        "Abstractions", "AI.Pipeline", "Analyzers", "Authoring", "BackgroundAgents",
        "Brick.Contracts", "Bricks.GitHub", "Certification.Contracts", "CLI", "Client",
        "Contracts", "Core.Application", "Core.Domain", "Hosting", "Hosting.Bundle",
        "Infrastructure", "Mcp.Server.Host", "Orchestration", "Policies", "Runtime",
        "Sdk", "Tools.Assembly", "Tools.Dev", "Transport.Grpc")}
    observed = {row["id"] for row in rows}
    require(len(rows) == 24 and observed == expected,
            f"Release package set mismatch: missing={expected - observed}, extra={observed - expected}, count={len(rows)}")
    for row in rows:
        (Path(directory) / (row["file"] + ".sha256.txt")).write_text(f'{row["sha256"]}  {row["file"]}\n')
    output = Path(directory).parent / "package-manifest.json"
    output.write_text(json.dumps({"source_sha": source_sha, "validation_version": version,
                                  "published": False, "packages": rows}, indent=2) + "\n")
    print(f"PASS package manifest: {len(rows)} candidate packages; {output}")


if __name__ == "__main__":
    command, *args = sys.argv[1:]
    if command == "assets":
        assets(*args)
    elif command == "trx":
        trx(args[0], int(args[1]))
    elif command == "packages":
        packages(*args)
    else:
        raise SystemExit(f"Unknown command: {command}")
