#!/usr/bin/env bash
# Directory.Build.props injects versionless PackageReferences for the two audit overrides
# (System.Text.Encodings.Web, System.Text.RegularExpressions). A project that sets
# ManagePackageVersionsCentrally to false has no central version for them, and restore
# fails NU1015. The props file is imported before the project body, so the skip lives in
# Directory.Build.targets, which runs after that property is set: drop only the versionless
# copies of those two packages. A project that leaves central management on still receives
# them. A project that pins its own Version keeps that pin.
#
# Run:  bash tests/scripts/central-management-opt-out.test.sh
# Pure bash + python3: no network, no dotnet, no container.

set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
PASS=0
FAIL=0
ok()  { PASS=$((PASS + 1)); echo "  ok   — $1"; }
bad() { FAIL=$((FAIL + 1)); echo "  FAIL — $1"; echo "         $2"; }

python3 - "${ROOT}/Directory.Build.props" "${ROOT}/Directory.Build.targets" << 'PY'
import sys
import xml.etree.ElementTree as ET

NS = {"m": "http://schemas.microsoft.com/developer/msbuild/2003"}
AUDIT = ("System.Text.Encodings.Web", "System.Text.RegularExpressions")

def load(path):
    # The repo files omit the MSBuild namespace. Keep the parser strict about that.
    text = open(path, encoding="utf-8").read()
    if "schemas.microsoft.com/developer/msbuild/2003" in text:
        raise SystemExit(f"unexpected default namespace in {path}")
    return ET.parse(path).getroot()

props = load(sys.argv[1])
targets = load(sys.argv[2])

injected = []
for group in props.findall("ItemGroup"):
    for ref in group.findall("PackageReference"):
        name = ref.get("Include")
        if name in AUDIT:
            injected.append((name, ref.get("Version"), group.get("Condition")))

if [name for name, _, _ in injected] != list(AUDIT):
    raise SystemExit(f"props no longer injects exactly the two audit overrides: {injected}")
if any(version for _, version, _ in injected):
    raise SystemExit(f"audit overrides must stay versionless in props: {injected}")
print("ok props injects the two versionless audit overrides")

drops = [
    t for t in targets.findall("Target")
    if t.get("Name") == "AshlarDropVersionlessAuditOverrides"
]
if len(drops) != 1:
    raise SystemExit(f"expected one AshlarDropVersionlessAuditOverrides target, found {len(drops)}")
target = drops[0]
before = target.get("BeforeTargets") or ""
if "CollectPackageReferences" not in before:
    raise SystemExit(f"drop target must run before NuGet collects references: {before}")
removes = []
for group in target.findall("ItemGroup"):
    cond = group.get("Condition") or ""
    for ref in group.findall("PackageReference"):
        removes.append((cond, ref.get("Remove"), ref.get("Condition") or ""))
if not removes or "'$(ManagePackageVersionsCentrally)' == 'false'" not in removes[0][0]:
    raise SystemExit(f"Remove group is not gated on central management off: {removes}")
by_name = {name: cond for _, name, cond in removes}
if set(by_name) != set(AUDIT):
    raise SystemExit(f"Remove must name exactly the two audit overrides: {removes}")
for name, item_cond in by_name.items():
    if item_cond != "'%(PackageReference.Version)' == ''":
        raise SystemExit(f"{name} remove must keep a project that pins Version: {item_cond}")
print("ok targets drops only the versionless audit overrides when central management is off")
PY
PY_RC=$?
if [[ "${PY_RC}" -eq 0 ]]; then
  ok "versionless audit overrides are skipped when central management is off"
else
  bad "central-management opt-out" "checker exited ${PY_RC}"
fi

echo "central-management-opt-out: ${PASS} passed, ${FAIL} failed"
[[ "${FAIL}" -eq 0 ]]
