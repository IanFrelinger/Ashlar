#!/usr/bin/env bash
# Tests that the post-push verifier survives nuget.org index lag, against a local HTTP server.
#
# WHY THIS EXISTS. scripts/verify-nuget-published-sha256-matches-manifest.sh runs AFTER
# `dotnet nuget push`. It downloaded each package with a bare urllib.request.urlretrieve - no
# timeout, no retry, no HTTPError handling. nuget.org indexes packages one at a time after a push,
# and the visibility poll before this step waits for only four ids, so the other ~18 can still 404
# on the first request. That raised HTTPError and ended the step with a traceback, with every
# package already public - and `github-release`, then gated on success(), skipped attaching the
# release artifacts. It is the same index lag that failed the v0.1.2 release. (github-release is
# now gated on publication having begun, so the artifacts survive a failure here.)
#
# This drives the REAL script, not a copy of its logic, against a server that answers 404 for a
# while and then serves the package. It asserts four things, and a retry loop that satisfied only
# the first would still be broken:
#   1. a package that 404s and then appears is verified          (lag is ridden out)
#   2. a package that never appears is a named failure            (not a traceback)
#   3. the loop CONTINUES past it and verifies what follows       (failures are reported together)
#   4. a non-retryable status fails fast                          (403 is not index lag)
#
# Run:  bash tests/scripts/nuget-verify-retry.test.sh
# Pure python + bash: no network beyond 127.0.0.1, no dotnet, no container.

set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
VERIFIER="${ROOT}/scripts/verify-nuget-published-sha256-matches-manifest.sh"

PASS=0
FAIL=0
EXPECTED_ASSERTIONS=8

ok()  { PASS=$((PASS + 1)); echo "  ok   — $1"; }
bad() { FAIL=$((FAIL + 1)); echo "  FAIL — $1"; echo "         $2"; }

# Probe by RUNNING each candidate. On Windows `command -v python3` finds an App Execution Alias that
# exits 49 without executing anything; every assertion below would then pass having run nothing.
PY_BIN=""
for cand in python3 python py; do
  if "${cand}" -c 'print(1)' >/dev/null 2>&1; then PY_BIN="${cand}"; break; fi
done
[[ -n "${PY_BIN}" ]] || { echo "FAIL - no working python interpreter"; exit 1; }

# The verifier itself invokes `python3`. Where that name is the Windows stub, put a shim first on
# PATH so the script under test runs the interpreter that works. CI is Linux and never needs it.
WORK="$(mktemp -d)"
trap 'rm -rf "${WORK}"' EXIT
if ! python3 -c 'print(1)' >/dev/null 2>&1; then
  mkdir -p "${WORK}/bin"
  printf '#!/bin/sh\nexec %s "$@"\n' "${PY_BIN}" > "${WORK}/bin/python3"
  chmod +x "${WORK}/bin/python3"
  export PATH="${WORK}/bin:${PATH}"
fi

# Hand python the bash that is running THIS script. A bare "bash" from Windows python resolves to
# System32ash.exe - the WSL launcher - which fails before the verifier starts, so the server
# receives nothing and every assertion about its requests is vacuous.
RESULT="$("${PY_BIN}" - "${ROOT}" "${VERIFIER}" "${WORK}" "${BASH}" <<'PY'
import http.server, json, os, subprocess, sys, threading, zipfile

root, verifier, work, bash_bin = sys.argv[1], sys.argv[2], sys.argv[3], sys.argv[4]
sys.path.insert(0, os.path.join(root, "scripts", "lib"))
from nupkg_content_digest import content_digest

VER = "9.9.9"
pkgs = {}
for pid in ("Pkg.Lagging", "Pkg.Missing", "Pkg.Present", "Pkg.Forbidden"):
    path = os.path.join(work, f"{pid}.{VER}.nupkg")
    with zipfile.ZipFile(path, "w") as z:
        z.writestr(f"{pid}.nuspec", f"<package><metadata><id>{pid}</id></metadata></package>")
        z.writestr("lib/net8.0/x.dll", f"payload-{pid}")
        z.writestr(".signature.p7s", b"repository-signature")
    pkgs[pid] = path

# Behaviour per package, by lowercase id as the flat container spells it.
lag_left = {"pkg.lagging": 2}      # 404 twice, then serve
hits = {}

class Handler(http.server.BaseHTTPRequestHandler):
    def log_message(self, *a): pass
    def do_GET(self):
        pid_lc = self.path.strip("/").split("/")[0]
        hits[pid_lc] = hits.get(pid_lc, 0) + 1
        if pid_lc == "pkg.missing":
            self.send_response(404); self.end_headers(); return
        if pid_lc == "pkg.forbidden":
            self.send_response(403); self.end_headers(); return
        if lag_left.get(pid_lc, 0) > 0:
            lag_left[pid_lc] -= 1
            self.send_response(404); self.end_headers(); return
        real = next(p for k, p in pkgs.items() if k.lower() == pid_lc)
        data = open(real, "rb").read()
        self.send_response(200); self.send_header("Content-Length", str(len(data))); self.end_headers()
        self.wfile.write(data)

srv = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Handler)
threading.Thread(target=srv.serve_forever, daemon=True).start()

manifest = os.path.join(work, "manifest.json")
json.dump([{"id": pid, "version": VER, "fileName": os.path.basename(p),
            "sha256": "unused", "contentSha256": content_digest(p)} for pid, p in pkgs.items()],
          open(manifest, "w"))

env = dict(os.environ,
           ASHLAR_MANIFEST_JSON=manifest,
           ASHLAR_NUGET_VERIFY_VERSION=VER,
           ASHLAR_NUGET_FLAT_CONTAINER=f"http://127.0.0.1:{srv.server_address[1]}",
           ASHLAR_NUGET_VERIFY_ATTEMPTS="4",
           ASHLAR_NUGET_VERIFY_SLEEP_SEC="0")
proc = subprocess.run([bash_bin, verifier], env=env, capture_output=True, text=True)
srv.shutdown()

out = proc.stdout + proc.stderr
def emit(name, cond, detail=""):
    print(f"{name}\t{'PASS' if cond else 'FAIL'}\t{detail}")

emit("the run fails overall, because one package never appears", proc.returncode != 0, f"exit {proc.returncode}")
emit("a lagging package is retried and then verified", "Pkg.Lagging" not in proc.stderr.split("could not download")[-1] and hits.get("pkg.lagging") == 3,
     f"hits={hits.get('pkg.lagging')}")
emit("a package that never appears is a NAMED failure", "could not download Pkg.Missing" in out, "")
emit("and not an uncaught traceback", "Traceback" not in out, out[-400:] if "Traceback" in out else "")
emit("the missing package is retried the configured number of times", hits.get("pkg.missing") == 4, f"hits={hits.get('pkg.missing')}")
emit("the loop continues past the failure and verifies what follows", hits.get("pkg.present", 0) >= 1 and "could not download Pkg.Present" not in out, f"hits={hits.get('pkg.present')}")
emit("a non-retryable status fails fast", hits.get("pkg.forbidden") == 1, f"hits={hits.get('pkg.forbidden')}")
emit("and says it was not retryable", "not retryable" in out, "")
PY
)"

if [[ -z "${RESULT}" ]]; then
  echo "FAIL - the python harness produced no results"
  exit 1
fi

echo "== post-push verifier under index lag =="
while IFS=$'\t' read -r name verdict detail; do
  [[ -z "${name}" ]] && continue
  if [[ "${verdict}" == "PASS" ]]; then ok "${name}"; else bad "${name}" "${detail}"; fi
done <<< "${RESULT}"

echo
echo "passed: ${PASS}   failed: ${FAIL}"

RAN=$((PASS + FAIL))
if [[ "${RAN}" -ne "${EXPECTED_ASSERTIONS}" ]]; then
  echo "FAIL - ran ${RAN} assertions, expected ${EXPECTED_ASSERTIONS}. A partial run is not a pass."
  exit 1
fi
[[ "${FAIL}" -eq 0 ]] || exit 1
