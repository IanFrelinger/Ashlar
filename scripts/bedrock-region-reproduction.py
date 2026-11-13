#!/usr/bin/env python3
"""Controlled application-level reproduction; authoring this file does not authorize execution.

Run only through test-in-container.sh, passing the exact committed 40-character SHA.
No AWS clients/requests, real profile reads, cache resets, or isolation harnesses.
"""
import argparse
import base64
import difflib
import hashlib
import json
import os
from pathlib import Path
import re
import runpy
import shutil
import subprocess
import tempfile
import xml.etree.ElementTree as ET
import zipfile

ROOT = Path("/repo")
PROJECT = "src/Ashlar.Tests.Infrastructure/Ashlar.Tests.Infrastructure.csproj"
FACTORY = "src/Ashlar.AI.Pipeline/Clients/AwsBedrockChatClientFactory.cs"
ORIGINAL = "src/Ashlar.Tests.Infrastructure/Tests/Certification/EgressRedirectHardeningTests.cs"
PROBE = "scripts/diagnostics/bedrock-region-probe.cs"
INJECTED = "src/Ashlar.Tests.Infrastructure/Tests/BedrockRegionDiagnosticProbe.cs"
ORIGINAL_ID = "Ashlar.Tests.Infrastructure.Tests.Certification.EgressRedirectHardeningTests.CloudBedrock_TheRuntimeClientsConfigurationNeverFollowsARedirect"
PROBE_ID = "Ashlar.Tests.Infrastructure.Diagnostics.BedrockRegionDiagnosticProbe.Controlled_default_region_and_factory_configuration_are_observed_without_a_client"
NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
MANIFEST = "scripts/mutations/bedrock-region-contracts.json"


def require(ok, message):
    if not ok:
        raise RuntimeError(message)


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def git(*args):
    return subprocess.check_output(["git", *args], cwd=ROOT, text=True).strip()


def record_json(path, value):
    path.write_text(json.dumps(value, indent=2) + "\n")


def run(command, directory, environment, timeout=600):
    directory.mkdir(parents=True, exist_ok=True)
    record_json(directory / "command.json", command)
    timed_out = False
    with (directory / "console.log").open("w") as log:
        process = subprocess.Popen(command, cwd=ROOT, env=environment, stdout=log, stderr=subprocess.STDOUT)
        try:
            process.wait(timeout=timeout)
        except subprocess.TimeoutExpired:
            timed_out = True
            record_json(directory / "process.json", {"exit": None, "timed_out": True})
            # The generic runner handles SIGTERM by stopping its test process and restoring source.
            process.terminate()
            try:
                process.wait(timeout=30)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait(timeout=30)
    (directory / "exit-code.txt").write_text(str(process.returncode) + "\n")
    record_json(directory / "process.json", {"exit": process.returncode, "timed_out": timed_out})
    require(not timed_out, "Command timed out; even a late successful exit is invalid")
    return process.returncode


def read_trx(path, kind, region, exit_code):
    document = ET.parse(path).getroot()
    rows = document.findall("t:Results/t:UnitTestResult", NS)
    expected = {PROBE_ID} if kind == "probe" else {
        ORIGINAL_ID + '(region: "us-west-2", expectedRegion: "us-west-2")',
        ORIGINAL_ID + '(region: " us-west-2 ", expectedRegion: "us-west-2")',
        ORIGINAL_ID + '(region: "", expectedRegion: null)',
        ORIGINAL_ID + '(region: null, expectedRegion: null)'}
    require(len(rows) == len(expected) and {r.get("testName") for r in rows} == expected, "Unexpected TRX test inventory")
    require(len({r.get("testId") for r in rows}) == len(rows), "Duplicate TRX test identities")
    failed = {r.get("testName") for r in rows if r.get("outcome") == "Failed"}
    want_failed = {name for name in expected if name.endswith('expectedRegion: null)')} if kind == "original" and region else set()
    require(failed == want_failed and exit_code == (1 if failed else 0), "Unexpected test failures or process exit")
    require(all(r.get("outcome") in {"Passed", "Failed"} for r in rows), "Skipped or aborted test")
    counters = document.find("t:ResultSummary/t:Counters", NS)
    require(counters is not None and all(int(counters.get(k, "-1")) == v for k, v in {
        "total": len(expected), "executed": len(expected), "passed": len(expected) - len(failed), "failed": len(failed)}.items()), "TRX counter mismatch")
    require(all(int(counters.get(k, "0")) == 0 for k in ("error", "timeout", "aborted", "inconclusive", "notExecuted")), "Non-assertion result")
    require(document.find("t:ResultSummary", NS).get("outcome") == ("Failed" if failed else "Completed"), "Incomplete test run")
    definitions = {x.get("id"): x.find("t:TestMethod", NS) for x in document.findall("t:TestDefinitions/t:UnitTest", NS)}
    results = []
    for row in rows:
        method = definitions.get(row.get("testId"))
        owner, name = (PROBE_ID if kind == "probe" else ORIGINAL_ID).rsplit(".", 1)
        require(method is not None and method.get("name") == name and method.get("className", "").split(",")[0] == owner, "Unexpected test definition")
        message = row.findtext("t:Output/t:ErrorInfo/t:Message", default="", namespaces=NS)
        stack = row.findtext("t:Output/t:ErrorInfo/t:StackTrace", default="", namespaces=NS)
        if row.get("outcome") == "Failed":
            # FluentAssertions can name the source expression when the PDB/source is available.
            expected_messages = {f'Expected {subject} to be <null>, but found "{region}".'
                                 for subject in ("string", "config.RegionEndpoint?.SystemName")}
            require(message.strip() in expected_messages and ORIGINAL_ID in stack and "EgressRedirectHardeningTests.cs:line 124" in stack, "Failure is not the expected region assertion")
        results.append({"name": row.get("testName"), "id": row.get("testId"), "outcome": row.get("outcome"), "message": message, "stack": stack})
    return {"total": len(rows), "failed": len(failed), "results": results, "trx_sha256": digest(path)}


def contract_names():
    return {ORIGINAL_ID + suffix for suffix in (
        '(region: "us-west-2", expectedRegion: "us-west-2")',
        '(region: " us-west-2 ", expectedRegion: "us-west-2")',
        '(region: "", expectedRegion: null)', '(region: null, expectedRegion: null)',
        '(region: "eu-central-1", expectedRegion: "eu-central-1")', '(region: "   ", expectedRegion: null)')}


def read_contract_trx(path, code, console, failed, tfm, strict_reader):
    counts = strict_reader(path, code, console)
    require(counts["total"] == counts["executed"] == 6 and counts["notExecuted"] == 0 and set(counts["failures"]) == set(failed), "Incorrect six-row contract result")
    root = ET.parse(path).getroot()
    rows = root.findall("t:Results/t:UnitTestResult", NS)
    require({r.get("testName") for r in rows} == contract_names(), "Contract theory identity drift")
    identities = {r.get("testName"): r.get("testId") for r in rows}
    require(len(set(identities.values())) == 6, "Duplicate contract test ID")
    assembly = str(ROOT / Path(PROJECT).parent / f"bin/Debug/{tfm}/Ashlar.Tests.Infrastructure.dll")
    definitions = {x.get("id"): x.find("t:TestMethod", NS) for x in root.findall("t:TestDefinitions/t:UnitTest", NS)}
    runtime_versions = []
    for row in rows:
        method = definitions.get(row.get("testId"))
        owner, name = ORIGINAL_ID.rsplit(".", 1)
        require(method is not None and method.get("name") == name and method.get("className", "").split(",")[0] == owner and method.get("codeBase") == assembly, "Wrong contract test assembly")
        stdout = row.findtext("t:Output/t:StdOut", default="", namespaces=NS)
        versions = re.findall(r"(?m)^BEDROCK_RUNTIME ([0-9]+\.[0-9]+\.[0-9]+(?:\.[0-9]+)?)\s*$", stdout)
        require(stdout.count("BEDROCK_RUNTIME") == len(versions) == 1 and versions[0].split(".")[0] == tfm[3:].split(".")[0], "Missing or wrong actual runtime receipt")
        runtime_versions.append(versions[0])
    require(f"Test run for {assembly} (.NETCoreApp,Version=v{tfm[3:]})" in console, "Wrong console test target")
    return {"counts": counts, "identities": identities, "runtime_versions": runtime_versions, "trx_sha256": digest(path)}


def mutation_manifest(checks):
    manifest = json.loads((ROOT / MANIFEST).read_text())
    cases = checks["validate_cases"](ROOT, manifest, None)
    names = contract_names()
    patterns = {
        "redirects-enabled": names,
        "explicit-region-discarded": {n for n in names if 'expectedRegion: "us-west-2"' in n},
        "trim-removed": {n for n in names if '(region: " us-west-2 "' in n},
        "default-overridden": {n for n in names if "expectedRegion: null)" in n}}
    inventory = {f"bedrock-{key}-{tfm}": (tfm, failed) for tfm in ("net8.0", "net10.0") for key, failed in patterns.items()}
    require(len(cases) == 8 and {c["id"] for c in cases} == set(inventory), "Wrong mutation inventory")
    for case in cases:
        tfm, failed = inventory[case["id"]]
        require(case["file"] == FACTORY and case["project"] == PROJECT and case["framework"] == tfm and case["filter"] == "FullyQualifiedName=" + ORIGINAL_ID and set(case["expected_failures"]) == failed, "Wrong mutation selection or failed-row contract")
        require((ROOT / FACTORY).read_bytes().count(case["old"].encode()) == 1, "Mutation does not match original source once")
    return manifest


def audit_mutations(directory, manifest, sha, original, checks):
    batch = json.loads((directory / "summary.json").read_text())
    require(batch.get("verdict") == "KILLED" and batch.get("ref") == sha and len(batch["records"]) == 8, "Incomplete mutation campaign")
    require(json.loads((directory / "manifest.json").read_text()) == manifest, "Retained manifest differs")
    baselines, identities, receipts = {}, {}, []
    original_hash = hashlib.sha256(original).hexdigest()
    for index, (case, record) in enumerate(zip(manifest["mutations"], batch["records"], strict=True)):
        folder = directory / f"{index:03d}-{case['id']}"
        require(record == json.loads((folder / "verdict.json").read_text()) and case == record["case"] == json.loads((folder / "case.json").read_text()), "Case evidence mismatch")
        require(record["id"] == case["id"] and record["ref"] == sha and record["verdict"] == "KILLED" and record.get("restored_clean") is True and record.get("final_clean") is True, "Mutation did not finish restored and green")
        require(record["original_sha256"] == record["restored_sha256"] == original_hash and (folder / "original.bin").read_bytes() == original, "Source-byte restoration mismatch")
        mutated = original.replace(case["old"].encode(), case["new"].encode(), 1)
        diff = (folder / "applied.diff").read_text()
        lines = diff.splitlines(keepends=True)
        require(lines[0] == f"diff --git a/{FACTORY} b/{FACTORY}\n" and lines[2:4] == [f"--- a/{FACTORY}\n", f"+++ b/{FACTORY}\n"], "Unexpected mutation diff paths")
        blob = lambda data: hashlib.sha1(b"blob " + str(len(data)).encode() + b"\0" + data).hexdigest()
        indexes = re.fullmatch(r"index ([0-9a-f]{7,40})\.\.([0-9a-f]{7,40}) 100644\n", lines[1])
        require(indexes is not None and blob(original).startswith(indexes[1]) and blob(mutated).startswith(indexes[2]), "Mutation blob hashes differ")
        expected_diff = "".join(list(difflib.unified_diff(original.decode().splitlines(keepends=True), mutated.decode().splitlines(keepends=True)))[2:])
        actual_diff = re.sub(r"(?m)^(@@ -[0-9,]+ \+[0-9,]+ @@)[^\n]*", r"\1", "".join(lines[4:]))
        require(actual_diff == expected_diff, "Applied mutation bytes differ from manifest")
        tfm = case["framework"]
        phases = ["baseline", "red", "green"] if tfm not in baselines else ["red", "green"]
        for phase in phases:
            path = folder / phase
            command = ["dotnet", "test", PROJECT, "--framework", tfm, "--filter", case["filter"], "--nologo", "-v", "minimal", "--results-directory", str(path), "--logger", "trx;LogFileName=result.trx"]
            require(json.loads((path / "command.json").read_text()) == command, "Mutation command drift")
            process = json.loads((path / "process.json").read_text())
            require(process.get("timed_out") is False and process.get("interrupted") is False, "Mutation process did not finish normally")
            observed = read_contract_trx(path / "result.trx", process["exit"], (path / "console.log").read_text(), case["expected_failures"] if phase == "red" else [], tfm, checks["read_result"])
            require(observed["counts"] == record[phase] == json.loads((path / "counts.json").read_text()), "Retained counters differ from TRX")
            if phase == "baseline":
                baselines[tfm], identities[tfm] = observed["counts"], observed["identities"]
            require(observed["identities"] == identities[tfm], "Mutation test identities changed")
            receipts.append(dict(id=case["id"], phase=phase, framework=tfm, **observed))
        require(record["baseline"] == baselines[tfm], "Reused baseline differs")
    require(len(receipts) == 18 and len(list(directory.rglob("*.trx"))) == 18, "Incorrect mutation TRX inventory")
    return {"verdict": "KILLED", "cases": 8, "trx_count": 18, "phases": receipts}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source_sha")
    parser.add_argument("--regression", action="store_true", help="Require the corrected six-row oracle and eight exact mutations; historical mode is unchanged")
    args = parser.parse_args()
    require(re.fullmatch(r"[0-9a-f]{40}", args.source_sha), "Exact lowercase 40-character SHA required")
    require(Path.cwd().resolve() == ROOT and Path(__file__).resolve().parent == ROOT / "scripts" and Path("/.dockerenv").is_file() and Path("/src-mirror/.git").is_dir(), "Use the Linux devtest clone through test-in-container.sh")
    require(git("rev-parse", "HEAD") == args.source_sha and not git("status", "--porcelain=v1", "--untracked-files=all"), "Source SHA mismatch or dirty source")
    require(not (ROOT / INJECTED).exists(), "Injection path already exists")
    out = Path(tempfile.mkdtemp(prefix="ashlar-bedrock-region-", dir="/tmp"))
    before = {path: digest(ROOT / path) for path in (FACTORY, ORIGINAL, PROBE, PROJECT, "Directory.Packages.props")}
    summary = {"source_sha": args.source_sha, "source_tree": git("rev-parse", "HEAD^{tree}"), "verdict": "ERROR", "source_before": before, "cases": [], "mode": "regression" if args.regression else "historical-reproduction", "scope": "Configuration-only evidence; no attribution of the historical CI provider, AWS clients, or OS isolation proof."}
    # Preserve only named build/runtime settings unchanged; never enumerate allenv or read AWS credentials.
    inherited = ("PATH", "HOME", "TMPDIR", "TMP", "TEMP", "LANG", "LC_ALL", "TZ", "DOTNET_ROOT", "DOTNET_ROOT_X64", "DOTNET_HOST_PATH", "DOTNET_CLI_HOME", "NUGET_PACKAGES", "CI")
    env = {key: os.environ[key] for key in inherited if key in os.environ}
    synthetic = out / "synthetic-config"
    synthetic.mkdir()
    for name in ("credentials", "config"):
        (synthetic / name).write_text("")
    env.update(AWS_SHARED_CREDENTIALS_FILE=str(synthetic / "credentials"), AWS_CONFIG_FILE=str(synthetic / "config"), AWS_EC2_METADATA_DISABLED="true", DOTNET_ROLL_FORWARD="LatestPatch", RollForward="LatestPatch", DOTNET_NOLOGO="1", DOTNET_CLI_TELEMETRY_OPTOUT="1", ASHLAR_BEDROCK_SOURCE_SHA=args.source_sha)
    record_json(out / "controlled-environment.json", {k: v for k, v in env.items() if k.startswith(("AWS_", "ASHLAR_BEDROCK_")) or k in {"DOTNET_ROLL_FORWARD", "RollForward"}})
    error = None
    try:
        # Safe function import only: the unchanged runner's guarded main is not invoked here.
        checks = runpy.run_path(str(ROOT / "scripts/run-spec007-mutation-batch.py")) if args.regression else None
        manifest = mutation_manifest(checks) if args.regression else None
        shutil.copyfile(ROOT / PROBE, ROOT / INJECTED)
        shutil.copyfile(ROOT / INJECTED, out / "injected-probe.cs")
        for original in (FACTORY, ORIGINAL):
            shutil.copyfile(ROOT / original, out / Path(original).name)
        for tfm in ("net8.0", "net10.0"):
            build = out / tfm / "build"
            require(run(["dotnet", "build", PROJECT, "-f", tfm, "-c", "Debug", "-p:RollForward=LatestPatch", "--nologo", "-v", "minimal"], build, env, 1800) == 0, "Infrastructure build failed")
            assets = json.loads((ROOT / Path(PROJECT).parent / "obj/project.assets.json").read_text())
            packages = {k.split("/")[0]: k.split("/")[1] for k in assets["libraries"] if k.startswith("AWSSDK.")}
            require({"AWSSDK.Core", "AWSSDK.BedrockRuntime"} <= packages.keys(), "Missing resolved AWS packages")
            record_json(build / "aws-packages.json", packages)
            runtime_path = ROOT / Path(PROJECT).parent / f"bin/Debug/{tfm}/Ashlar.Tests.Infrastructure.runtimeconfig.json"
            runtime = json.loads(runtime_path.read_text())
            record_json(build / "runtimeconfig.json", runtime)
            require(runtime["runtimeOptions"]["tfm"] == tfm and runtime["runtimeOptions"]["rollForward"] == "LatestPatch", "Runtime target or roll-forward mismatch")
            for mode in ("none", "us-west-2", "eu-central-1"):
                for kind, test_id in (("probe", PROBE_ID), ("original", ORIGINAL_ID)):
                    case = out / tfm / mode / kind
                    case_env = dict(env, ASHLAR_BEDROCK_EXPECT_REGION=mode, ASHLAR_BEDROCK_PROBE_OUTPUT=str(case / "probe.json"))
                    if mode != "none":
                        case_env["AWS_REGION"] = mode
                    command = ["dotnet", "test", PROJECT, "-f", tfm, "-c", "Debug", "--no-build", "--no-restore", "-p:RollForward=LatestPatch", "--filter", "FullyQualifiedName=" + test_id, "--logger", "trx;LogFileName=results.trx", "--logger", "console;verbosity=normal", "--results-directory", str(case), "--nologo"]
                    code = run(command, case, case_env)
                    evidence = (read_contract_trx(case / "results.trx", code, (case / "console.log").read_text(), [], tfm, checks["read_result"])
                                if args.regression and kind == "original" else read_trx(case / "results.trx", kind, None if mode == "none" else mode, code))
                    if kind == "probe":
                        receipt = json.loads((case / "probe.json").read_text())
                        require(receipt["source_sha"] == args.source_sha and receipt["injected_source_sha256"] == before[PROBE], "Probe source receipt mismatch")
                        require(receipt["runtime"]["version"].split(".")[0] == tfm[3:].split(".")[0] and receipt["runtime"]["compiled_target"] == tfm, "Wrong actual test runtime or compiled target")
                        loaded = {item["name"]: item for item in receipt["loaded_assemblies"]}
                        require(set(loaded) == {"AWSSDK.Core", "AWSSDK.BedrockRuntime"} and all(loaded[name]["file_version"] and loaded[name]["assembly_version"] for name in loaded), "Missing loaded AWS version evidence")
                    summary["cases"].append(dict(framework=tfm, mode=mode, kind=kind, exit_code=code, **evidence))
        require(len(summary["cases"]) == 12, "Incomplete process inventory")
        if args.regression:
            (ROOT / INJECTED).unlink()
            require(not git("status", "--porcelain=v1", "--untracked-files=all") and all(digest(ROOT / path) == value for path, value in before.items()), "Source not clean before mutation campaign")
            mutation_env = dict(env, AWS_REGION="eu-central-1")
            record_json(out / "mutation-environment.json", {key: value for key, value in mutation_env.items() if key.startswith("AWS_") or key in {"DOTNET_ROLL_FORWARD", "RollForward"}})
            command = ["python3", "scripts/run-spec007-mutation-batch.py", "--manifest", MANIFEST, "--output", str(out / "mutations"), "--archive-stdout", "--timeout-seconds", "600"]
            require(run(command, out / "mutation-process", mutation_env, 3600) == 0, "Mutation runner did not report all kills")
            summary["mutations"] = audit_mutations(out / "mutations", manifest, args.source_sha, (out / Path(FACTORY).name).read_bytes(), checks)
            for tfm in ("net8.0", "net10.0"):
                runtime_path = ROOT / Path(PROJECT).parent / f"bin/Debug/{tfm}/Ashlar.Tests.Infrastructure.runtimeconfig.json"
                runtime = json.loads(runtime_path.read_text())
                require(runtime["runtimeOptions"]["tfm"] == tfm and runtime["runtimeOptions"]["rollForward"] == "LatestPatch", "Mutation runtime configuration drift")
                record_json(out / f"mutation-runtimeconfig-{tfm}.json", runtime)
        summary["verdict"] = "REGRESSION_VERIFIED" if args.regression else "REPRODUCED"
    except Exception as exc:
        error = exc
        summary["error"] = str(exc)
    finally:
        (ROOT / INJECTED).unlink(missing_ok=True)
        summary["source_after"] = {path: digest(ROOT / path) if (ROOT / path).is_file() else None for path in before}
        summary["git_status_final"] = git("status", "--porcelain=v1", "--untracked-files=all")
        summary["source_restored"] = summary["source_after"] == before and not summary["git_status_final"] and git("rev-parse", "HEAD") == args.source_sha
        if not summary["source_restored"]:
            summary["verdict"] = "ERROR"
        record_json(out / "summary.json", summary)
        archive = out.parent / (out.name + ".zip")
        with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED) as bundle:
            for path in sorted(out.rglob("*")):
                require(not path.is_symlink(), "Unexpected evidence symlink")
                if path.is_file():
                    bundle.write(path, path.relative_to(out))
        require(archive.stat().st_size < 50_000_000, "Evidence exceeds 50 MB")
        print("ASHLAR_BEDROCK_REGION_EVIDENCE_SHA256 " + digest(archive), flush=True)
        print("ASHLAR_BEDROCK_REGION_EVIDENCE_BEGIN", flush=True)
        print(base64.b64encode(archive.read_bytes()).decode("ascii"), flush=True)
        print("ASHLAR_BEDROCK_REGION_EVIDENCE_END", flush=True)
    return 0 if error is None and summary["source_restored"] else 2


if __name__ == "__main__":
    raise SystemExit(main())
