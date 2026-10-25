#!/usr/bin/env bash
# Packs Ashlar.Mcp.Server.Host as the tool package a release pushes, runs it with `dnx` from a temp
# feed (nothing else on the source list, empty package cache), and drives a real MCP stdio session:
# initialize -> tools/list -> tools/call on the built-in github.pr-status brick. Then reads the
# audit record the call wrote.
#
# WHAT THIS PROVES
# ----------------
# A .NET 10 SDK can run the packed host with `dnx Ashlar.Mcp.Server.Host@<version>`; the package is
# an McpServer tool package whose .mcp/server.json carries its own version and no placeholder; the
# local GGUF natives are not in it; with one brick allowlisted the server lists exactly that brick,
# a call reaches it and returns the auditId, and one MCP_BRICK_INVOKED record with that id and the
# hashes of the exchange reaches disk (within 10 s; the file sink drains asynchronously).
#
# WHAT THIS DOES NOT PROVE
# ------------------------
# Anything about GitHub: the call uses a malformed PR URL, which the brick rejects before sending a
# request, so the run is offline and token-free. Nor that a nuget.org restore works: the feed is local.
#
# Usage:  bash scripts/verify-mcp-brick-host-dnx.sh
# Env:    ASHLAR_MCP_DNX_VERIFY_VERSION (default 9.9.9-local)
#         ASHLAR_MCP_DNX_VERIFY_WORK    (default: mktemp -d; kept when set)
# Needs the .NET 10 SDK: scripts/test-in-container.sh -- 'bash scripts/verify-mcp-brick-host-dnx.sh'
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
VERSION="${ASHLAR_MCP_DNX_VERIFY_VERSION:-9.9.9-local}"
WORK="${ASHLAR_MCP_DNX_VERIFY_WORK:-$(mktemp -d)}"
FEED="${WORK}/feed"
AUDIT_DIR="${WORK}/audit"
PACKAGE_ID="Ashlar.Mcp.Server.Host"

mkdir -p "${FEED}" "${AUDIT_DIR}" "${WORK}/packages" "${WORK}/cli-home"

echo "==> dotnet pack ${PACKAGE_ID} ${VERSION} (tool package, no local-model natives)"
dotnet pack "${ROOT}/src/Ashlar.Mcp.Server.Host/Ashlar.Mcp.Server.Host.csproj" \
  -c Release -o "${FEED}" \
  -p:PackageVersion="${VERSION}" \
  -p:AshlarMcpHostLocalModels=false \
  -v minimal

NUPKG="${FEED}/${PACKAGE_ID}.${VERSION}.nupkg"
[[ -f "${NUPKG}" ]] || { echo "FAIL - ${NUPKG} was not produced" >&2; exit 1; }

echo "==> Inspecting package"
fail() { echo "FAIL - $1" >&2; exit 1; }
NUSPEC="$(unzip -p "${NUPKG}" "${PACKAGE_ID}.nuspec")"
grep -q '<packageType name="DotnetTool"' <<<"${NUSPEC}" || fail "nuspec is not a DotnetTool package"
grep -q '<packageType name="McpServer"' <<<"${NUSPEC}" || fail "nuspec is not an McpServer package"
unzip -p "${NUPKG}" .mcp/server.json >"${WORK}/server.json" || fail "package has no .mcp/server.json"
python3 - "${VERSION}" "${PACKAGE_ID}" "${WORK}/server.json" <<'PY' || fail ".mcp/server.json does not describe this package"
import json, sys
version, package_id, path = sys.argv[1:4]
doc = json.load(open(path, encoding="utf-8"))
nuget = [p for p in doc["packages"] if p.get("registryType") == "nuget"][0]
assert doc["version"] == version, doc["version"]
assert nuget["version"] == version, nuget["version"]
assert nuget["identifier"] == package_id, nuget["identifier"]
assert nuget["transport"]["type"] == "stdio"
assert "__PACKAGE_VERSION__" not in json.dumps(doc), "unsubstituted placeholder"
PY
if unzip -l "${NUPKG}" | grep -qi 'libllama'; then
  fail "tool package carries the local GGUF natives (libllama*)"
fi
unzip -p "${NUPKG}" README.md | head -1 | grep -q "^# ${PACKAGE_ID}" || fail "packed README is not the host's own"

echo "==> dnx ${PACKAGE_ID}@${VERSION} over stdio"
export NUGET_PACKAGES="${WORK}/packages"
export DOTNET_CLI_HOME="${WORK}/cli-home"
export DOTNET_NOLOGO=1
export Ashlar__Mcp__Bricks__ExposedBrickIds__0="github.pr-status"
export Ashlar__Audit__File__Directory="${AUDIT_DIR}"

python3 - "${PACKAGE_ID}@${VERSION}" "${FEED}" "${AUDIT_DIR}" <<'PY'
import glob, json, os, subprocess, sys, threading, time

package, feed, audit_dir = sys.argv[1:4]
proc = subprocess.Popen(
    ["dnx", package, "--yes", "--source", feed],
    stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, bufsize=1)

stderr_lines = []
threading.Thread(target=lambda: stderr_lines.extend(proc.stderr), daemon=True).start()
responses = {}
def read_stdout():
    for line in proc.stdout:
        line = line.strip()
        if not line:
            continue
        try:
            msg = json.loads(line)
        except json.JSONDecodeError:
            responses.setdefault("non-json", []).append(line)
            continue
        if "id" in msg:
            responses[msg["id"]] = msg
threading.Thread(target=read_stdout, daemon=True).start()

def send(msg):
    proc.stdin.write(json.dumps(msg) + "\n")
    proc.stdin.flush()

def wait(i, timeout=240):
    end = time.time() + timeout
    while time.time() < end:
        if i in responses:
            return responses[i]
        if proc.poll() is not None:
            break
        time.sleep(0.2)
    sys.stderr.write("".join(stderr_lines[-40:]))
    sys.stderr.write("\n".join(responses.get("non-json", [])[-20:]) + "\n")
    raise SystemExit(f"FAIL - no response to request {i} (exit={proc.poll()})")

try:
    send({"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {
        "protocolVersion": "2025-06-18", "capabilities": {},
        "clientInfo": {"name": "verify-mcp-brick-host-dnx", "version": "1"}}})
    init = wait(1)
    print("  ok   - initialize:", init["result"]["serverInfo"])
    send({"jsonrpc": "2.0", "method": "notifications/initialized"})

    send({"jsonrpc": "2.0", "id": 2, "method": "tools/list"})
    names = [t["name"] for t in wait(2)["result"]["tools"]]
    assert names == ["github_pr-status"], f"FAIL - tools/list returned {names}"
    print("  ok   - tools/list lists exactly the allowlisted brick:", names)

    send({"jsonrpc": "2.0", "id": 3, "method": "tools/call", "params": {
        "name": "github_pr-status", "arguments": {"prUrl": "https://example.invalid/not-a-pull-request"}}})
    result = wait(3)["result"]
    assert not result.get("isError"), f"FAIL - tools/call isError: {result}"
    text = result["content"][0]["text"]
    payload = json.loads(text)
    assert payload["brickId"] == "github.pr-status", payload
    assert payload["outputs"]["state"] == "error", payload
    audit_id = payload["auditId"]
    print("  ok   - tools/call reached the brick; auditId", audit_id)

    # The file sink drains a queue on its own task and flushes per event, so the line lands
    # moments after the response; allow it a few seconds rather than racing it.
    deadline = time.time() + 10
    while True:
        records = []
        for path in glob.glob(os.path.join(audit_dir, "*")):
            with open(path, encoding="utf-8") as f:
                records += [json.loads(l) for l in f if l.strip()]
        mine = [r for r in records if r.get("correlationId") == audit_id]
        if mine or time.time() > deadline:
            break
        time.sleep(0.2)
    assert len(mine) == 1, f"FAIL - {len(mine)} audit records for {audit_id} in {audit_dir}: {records}"
    record = mine[0]
    assert record["eventType"] == "MCP_BRICK_INVOKED", record
    detail = json.loads(record["detail"])
    assert detail["outcome"] == "succeeded" and detail["brickId"] == "github.pr-status", detail
    import hashlib
    assert detail["resultSha256"] == hashlib.sha256(text.encode()).hexdigest(), "FAIL - result hash mismatch"
    assert "example.invalid" not in record["detail"], "FAIL - argument value written to the audit record"
    print("  ok   - one MCP_BRICK_INVOKED record on disk, hash matches the returned text")
    assert "non-json" not in responses, f"FAIL - non-JSON on stdout: {responses['non-json'][:3]}"
    print("  ok   - stdout carried only JSON-RPC")
finally:
    proc.stdin.close()
    try:
        proc.wait(timeout=30)
    except subprocess.TimeoutExpired:
        proc.kill()
PY

if [[ -z "${ASHLAR_MCP_DNX_VERIFY_WORK:-}" ]]; then
  rm -rf "${WORK}"
fi
echo "verify-mcp-brick-host-dnx: OK (${PACKAGE_ID} ${VERSION})"
