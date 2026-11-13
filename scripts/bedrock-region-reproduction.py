#!/usr/bin/env python3
"""Controlled application-level reproduction; authoring this file does not authorize execution.

Run only through test-in-container.sh, passing the exact committed 40-character SHA.
No AWS clients/requests, real profile reads, cache resets, or isolation harnesses.
"""
import argparse
import base64
import hashlib
import json
import os
from pathlib import Path
import re
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
    with (directory / "console.log").open("w") as log:
        result = subprocess.run(command, cwd=ROOT, env=environment, stdout=log, stderr=subprocess.STDOUT, timeout=timeout)
    (directory / "exit-code.txt").write_text(str(result.returncode) + "\n")
    return result.returncode


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
            require(message.strip() == f'Expected string to be <null>, but found "{region}".' and ORIGINAL_ID in stack and "EgressRedirectHardeningTests.cs:line 124" in stack, "Failure is not the expected region assertion")
        results.append({"name": row.get("testName"), "id": row.get("testId"), "outcome": row.get("outcome"), "message": message, "stack": stack})
    return {"total": len(rows), "failed": len(failed), "results": results, "trx_sha256": digest(path)}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source_sha")
    args = parser.parse_args()
    require(re.fullmatch(r"[0-9a-f]{40}", args.source_sha), "Exact lowercase 40-character SHA required")
    require(Path.cwd().resolve() == ROOT and Path(__file__).resolve().parent == ROOT / "scripts" and Path("/.dockerenv").is_file() and Path("/src-mirror/.git").is_dir(), "Use the Linux devtest clone through test-in-container.sh")
    require(git("rev-parse", "HEAD") == args.source_sha and not git("status", "--porcelain=v1", "--untracked-files=all"), "Source SHA mismatch or dirty source")
    require(not (ROOT / INJECTED).exists(), "Injection path already exists")
    out = Path(tempfile.mkdtemp(prefix="ashlar-bedrock-region-", dir="/tmp"))
    before = {path: digest(ROOT / path) for path in (FACTORY, ORIGINAL, PROBE, PROJECT, "Directory.Packages.props")}
    summary = {"source_sha": args.source_sha, "source_tree": git("rev-parse", "HEAD^{tree}"), "verdict": "ERROR", "source_before": before, "cases": [], "scope": "Configuration-only reproduction; no attribution of the historical CI provider, AWS clients, or OS isolation proof."}
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
                    evidence = read_trx(case / "results.trx", kind, None if mode == "none" else mode, code)
                    if kind == "probe":
                        receipt = json.loads((case / "probe.json").read_text())
                        require(receipt["source_sha"] == args.source_sha and receipt["injected_source_sha256"] == before[PROBE], "Probe source receipt mismatch")
                        require(receipt["runtime"]["version"].split(".")[0] == tfm[3:].split(".")[0] and receipt["runtime"]["compiled_target"] == tfm, "Wrong actual test runtime or compiled target")
                        loaded = {item["name"]: item for item in receipt["loaded_assemblies"]}
                        require(set(loaded) == {"AWSSDK.Core", "AWSSDK.BedrockRuntime"} and all(loaded[name]["file_version"] and loaded[name]["assembly_version"] for name in loaded), "Missing loaded AWS version evidence")
                    summary["cases"].append(dict(framework=tfm, mode=mode, kind=kind, exit_code=code, **evidence))
        require(len(summary["cases"]) == 12, "Incomplete process inventory")
        summary["verdict"] = "REPRODUCED"
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
