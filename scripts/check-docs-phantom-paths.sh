#!/usr/bin/env bash
# Check for phantom backtick-wrapped paths in docs before CI runs.
# Mirrors the logic from .github/workflows/onboarding-docs-guard.yml
# "Referenced repo paths must exist" step.
#
# Usage:
#   bash scripts/check-docs-phantom-paths.sh
#
# Exit codes:
#   0 - all referenced paths exist
#   1 - one or more referenced paths are missing

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

# NOTE ON PLACEMENT: this must be defined BEFORE the filter that calls it. When it sat below,
# bash reached the call first, "command not found" inside an `if` condition is not fatal even under
# set -e, and the test simply evaluated FALSE - so nothing was ever skipped and the exemption looked
# like it did not work rather than like it had not loaded.

# A document may declare itself a HISTORICAL RECORD, and then its paths are checked no further.
#
# There is exactly one legitimate case: a file whose job is to say where code USED to live.
# docs/FleetGovernanceExtractionInventory.md lists 63 pre-extraction paths and states that the
# extraction is complete; rewriting them to today's locations would destroy the record it exists to
# be. The marker is required to be IN the document rather than in a list here, so the reason travels
# with the file and a reader meets it before the stale-looking paths.
is_historical_record() {
  grep -q '<!-- phantom-paths: historical-record -->' "$1" 2>/dev/null
}

echo "== Docs Phantom Path Check =="
echo "Scanning README.md, docs/, scripts/, and Makefile for backtick-wrapped repo paths..."

files=(README.md Makefile)
while IFS= read -r f; do files+=("$f"); done < <(find docs -type f -name '*.md' 2>/dev/null | sort)
while IFS= read -r f; do 
  # Exclude this script itself to avoid matching example paths in comments
  if [[ "$f" != "scripts/check-docs-phantom-paths.sh" ]]; then
    files+=("$f")
  fi
done < <(find scripts -maxdepth 1 -type f \( -name '*.sh' -o -name '*.ps1' \) 2>/dev/null | sort)

# Pattern matches backtick-wrapped paths
# Drop declared historical records from the scan, and name them, because a silent exclusion is how a
# document stops being checked without anyone deciding that.
kept=()
for f in "${files[@]}"; do
  if is_historical_record "$f"; then
    echo "  skipping $f (declared a historical record: it documents where code USED to live)"
  else
    kept+=("$f")
  fi
done
files=("${kept[@]}")

pattern='`(scripts|docs|deploy|\.github|src|application|applications|samples)/[A-Za-z0-9._/-]+\.(sh|ps1|md|yml|yaml|json|csproj|sln|slnf|cs)`'
missing=0


while IFS= read -r token; do
  path="${token#\`}"; path="${path%\`}"
  
  # Placeholders are not real paths
  case "$path" in *'<'*|*'{'*) continue ;; esac
  
  # A path git is configured to ignore cannot exist in a clean checkout
  if git check-ignore -q -- "$path" 2>/dev/null; then
    continue
  fi
  
  if [ ! -e "$path" ]; then
    echo "ERROR: referenced path does not exist: $path"
    grep -lF -- "$token" "${files[@]}" 2>/dev/null | sed 's/^/    referenced by: /' || true
    missing=$((missing + 1))
  fi
done < <(grep -ohE -- "$pattern" "${files[@]}" 2>/dev/null | sort -u)

echo ""
if [ "$missing" -eq 0 ]; then
  echo "✓ All referenced paths exist (0 missing)"
  exit 0
else
  echo "✗ Found $missing missing path(s)"
  echo ""
  echo "Fix by either:"
  echo "  1. Creating the missing file/directory"
  echo "  2. Updating the documentation to reference the correct path"
  echo "  3. Adding the path to .gitignore if it's meant to be created by users"
  exit 1
fi
