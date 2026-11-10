#!/usr/bin/env bash
# The public starter must restore without editing a placeholder feed or mixing CPM and inline pins.
# This is a source/configuration contract, not a package-restore or C# execution test.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
python3 - "${ROOT}" <<'PY'
from pathlib import Path
import re
import sys
import xml.etree.ElementTree as ET

root = Path(sys.argv[1])
config = ET.parse(root / "consumer-template/nuget.config").getroot()
sources = config.findall("./packageSources/add")
assert [(s.get("key"), s.get("value")) for s in sources] == [
    ("nuget.org", "https://api.nuget.org/v3/index.json")
], "the default consumer config must enable only nuget.org, not a placeholder staging feed"
assert config.find("./packageSourceCredentials") is None, "do not enable placeholder credentials"
print("PASS default sources require no private feed or token")

pins = ET.parse(root / "consumer-template/Directory.Packages.props").getroot()
pin = (root / "ci/published-version").read_text().strip()
assert pins.findtext("./PropertyGroup/ManagePackageVersionsCentrally") == "true"
assert pins.findtext("./PropertyGroup/AshlarConsumerPackageVersion") == pin
references = pins.findall("./ItemGroup/PackageVersion")
assert len(references) >= 5
assert all(p.get("Version") == "$(AshlarConsumerPackageVersion)" for p in references)
print("PASS optional central pins match the published-version pin")

text = (root / "consumer-template/CONSUMING.md").read_text()
examples = [ET.fromstring(s) for s in re.findall(r"```xml\n(.*?)\n```", text.split("## Target frameworks", 1)[0], re.S)]
projects = [e for e in examples if e.tag == "Project" and e.findall("./ItemGroup/PackageReference")]
assert projects, "a complete quick-start project is required"
for project in projects:
    assert project.findtext("./PropertyGroup/ManagePackageVersionsCentrally") == "false", (
        "inline-version starter must opt out even under a copied central props file")
    assert all(p.get("Version") == pin for p in project.findall("./ItemGroup/PackageReference"))
assert any(e.tag == "ItemGroup" and e.find("PackageReference") is not None
           and all(p.get("Version") is None for p in e.findall("PackageReference")) for e in examples), (
    "the optional central-management instructions need a versionless PackageReference example")
print("PASS documented inline and central examples have compatible version shapes")
PY
