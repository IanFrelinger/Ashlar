#!/usr/bin/env bash
# Ashlar UAT — Tier 9 (the release path).
#
# v0.1.0 is "production-usable core, experimental autonomy" (D5), and the mechanism that makes that
# more than a slogan is the public-API contract: stable-tier packages carry PublicAPI baselines, and
# what sits in PublicAPI.Shipped.txt at tag time is the promise. This tier checks the parts of the
# runbook that can be checked before anyone tags anything.
#
# It does not tag, pack or publish. `NuGet local pack -> StableSdkHostSample` already packs on every
# PR, and a release rehearsal that pushes artefacts is not something a gate should do by itself.
set -uo pipefail

SRC="${UAT_REPO_DIR:-$PWD}"
OUT="${UAT_OUT:-${WORK:-/work}/out}"
mkdir -p "$OUT"
: >"$OUT/results-tier9.tsv"

PASS=0; FAIL=0
result() {
  local tier="$1" id="$2" verdict="$3"; shift 3
  printf 'RESULT\t%s\t%s\t%s\t%s\n' "$tier" "$id" "$verdict" "$*" | tee -a "$OUT/results-tier9.tsv"
  if [ "$verdict" = PASS ]; then PASS=$((PASS+1)); else FAIL=$((FAIL+1)); fi
}
say() { printf '\n=== %s ===\n' "$*"; }
cd "$SRC" || exit 1

say "9.1 every project with a public-API baseline carries BOTH halves of it"
# Shipped without Unshipped (or the reverse) means the analyzer has nothing coherent to compare against,
# and the promote-at-tag step in RELEASE_RUNBOOK has nothing to move.
HALF=""; BASELINED=0
for shipped in $(find src application -name 'PublicAPI.Shipped.txt' 2>/dev/null | sort); do
  BASELINED=$((BASELINED+1))
  [ -f "$(dirname "$shipped")/PublicAPI.Unshipped.txt" ] || HALF="$HALF $(basename "$(dirname "$shipped")")"
done
for unshipped in $(find src application -name 'PublicAPI.Unshipped.txt' 2>/dev/null | sort); do
  [ -f "$(dirname "$unshipped")/PublicAPI.Shipped.txt" ] || HALF="$HALF $(basename "$(dirname "$unshipped")"):no-shipped"
done
if [ -z "$HALF" ] && [ "$BASELINED" -gt 0 ]; then
  result 9 api-baselines-paired PASS "$BASELINED baselined projects each carry Shipped and Unshipped"
else
  result 9 api-baselines-paired FAIL "unpaired baselines:${HALF:- none} (baselined=$BASELINED)"
fi

say "9.2 the experimental surface is not in the stable PROMISE"
# D5's whole point: v0.1.0 promises a production-usable core while the autonomy surface stays
# experimental. PublicAPI.Shipped.txt is what a tag turns into a promise, so an [Experimental] type
# appearing there would promise exactly what the release says it does not.
# Only declaration lines (optional modifiers) and PascalCase names. Prompt strings such as
# "the class name" / "Declared class name" inside [Experimental] files must not count as a
# type called `name` (that matched constructor parameters in Shipped.txt after the v0.1.2
# PublicAPI promotion).
# Product code only. A TEST file can legitimately contain the text "[Experimental(" - a convention test
# that polices the attribute does - and declare fixture classes beside it, and harvesting those names
# reported the real, [Obsolete] AshlarSdkBuilder as an experimental type leaked into Ashlar.Sdk's
# Shipped.txt. Test projects are every directory whose name contains "Tests".
EXP_TYPES=$(grep -rl "\[Experimental(" --include=*.cs src/ 2>/dev/null \
            | grep -vE '(^|/)[^/]*Tests[^/]*/' \
            | xargs -r grep -hE '^[[:space:]]*(public|internal|private|protected)?[[:space:]]*(sealed|abstract|static|partial|readonly|ref)*[[:space:]]*(class|record|interface|enum|struct)[[:space:]]+[A-Z][A-Za-z0-9_]*' 2>/dev/null \
            | grep -oE '(class|record|interface|enum|struct)[[:space:]]+[A-Z][A-Za-z0-9_]*' \
            | awk '{print $2}' | sort -u)
LEAKED=""
for t in $EXP_TYPES; do
  # Match the type as a whole word in a promised signature, not as a substring of a longer name.
  if grep -rhqE "(^|[ .<(])$t([ .<>(,]|$)" $(find src application -name 'PublicAPI.Shipped.txt') 2>/dev/null; then
    LEAKED="$LEAKED $t"
  fi
done
EXP_COUNT=$(printf '%s\n' "$EXP_TYPES" | grep -c .)
# 82 [Experimental] types were declared in product code on 2026-10-01. A scan that finds far fewer has
# lost its lens (a moved tree, a broken pattern), and "none leaked" from an empty list proves nothing.
EXP_FLOOR=60
if [ "$EXP_COUNT" -lt "$EXP_FLOOR" ]; then
  result 9 experimental-not-promised FAIL "the scan found only $EXP_COUNT [Experimental] types (floor $EXP_FLOOR), so its PASS would mean nothing"
elif [ -z "$LEAKED" ]; then
  result 9 experimental-not-promised PASS "none of the $EXP_COUNT [Experimental] types appear in any PublicAPI.Shipped.txt"
else
  result 9 experimental-not-promised FAIL "experimental types in the stable promise:$LEAKED"
fi

say "9.3 every path the release runbook names exists"
MISSING=""; CHECKED=0
for p in $(grep -oE '`(scripts|docs|\.github|application|src)/[A-Za-z0-9._/-]+`' docs/RELEASE_RUNBOOK.md 2>/dev/null \
           | tr -d '`' | sort -u); do
  CHECKED=$((CHECKED+1)); [ -e "$p" ] || MISSING="$MISSING $p"
done
if [ -z "$MISSING" ] && [ "$CHECKED" -gt 0 ]; then
  result 9 runbook-paths-exist PASS "$CHECKED paths named in RELEASE_RUNBOOK.md resolve"
else
  result 9 runbook-paths-exist FAIL "named but absent:${MISSING:- none} (checked=$CHECKED)"
fi

say "9.4 the runbook's own commands exist on the CLI"
# The fastest path in the runbook is a CLI verb. If it has been renamed, the page sends an operator
# into a dead end on the one day they cannot afford one.
if timeout 600 dotnet run --project application/src/Ashlar.CLI -- release --help >"$OUT/release-help.log" 2>&1; then
  MISSING_VERB=""
  for verb in preflight gate; do
    grep -qiE "(^|[^a-z])$verb([^a-z]|$)" "$OUT/release-help.log" || MISSING_VERB="$MISSING_VERB $verb"
  done
  if [ -z "$MISSING_VERB" ]; then
    result 9 release-cli-verbs PASS "'release preflight' and 'release gate' are present on the CLI"
  else
    result 9 release-cli-verbs FAIL "runbook names verbs the CLI does not offer:$MISSING_VERB"
  fi
else
  result 9 release-cli-verbs FAIL "'release --help' failed: $(tail -3 "$OUT/release-help.log" | tr '\n' ' ')"
fi

say "9.5 the version the repo would ship has ONE source of truth"
# The repo reads its version out of the root VERSION file in both Directory.Build.props and
# Directory.Build.targets. That is the right shape -- a tag and a package cannot disagree about which
# number they are -- so this pins it rather than accepting a literal creeping into either file.
VER=$(tr -d ' \t\r\n' <VERSION 2>/dev/null)
if printf '%s' "$VER" | grep -qE '^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$'; then
  DERIVED=0
  for f in Directory.Build.props Directory.Build.targets; do
    grep -q "ReadAllText.*VERSION" "$f" 2>/dev/null && DERIVED=$((DERIVED+1))
  done
  if [ "$DERIVED" -eq 2 ]; then
    result 9 single-version PASS "VERSION=$VER, and both Directory.Build.props and .targets derive from that file"
  else
    result 9 single-version FAIL "VERSION=$VER but only $DERIVED of 2 build files derive from it; a literal has crept in"
  fi
else
  result 9 single-version FAIL "root VERSION is not a semver value: '${VER:-<empty or missing>}'"
fi

say "summary"
printf 'PASS=%d FAIL=%d\n' "$PASS" "$FAIL" | tee "$OUT/summary-tier9.txt"
[ "$FAIL" -eq 0 ]
