#!/usr/bin/env bash
# Verify all PackageReference entries have corresponding PackageVersion entries in Directory.Packages.props.
# Reports missing package versions with clear diagnostics to help developers fix Central Package Management issues.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
PACKAGES_PROPS="${ROOT}/Directory.Packages.props"
EXIT_CODE=0

# Color output (if terminal supports it)
if [[ -t 1 ]]; then
  RED='\033[0;31m'
  YELLOW='\033[1;33m'
  GREEN='\033[0;32m'
  BLUE='\033[0;34m'
  NC='\033[0m' # No Color
else
  RED=''
  YELLOW=''
  GREEN=''
  BLUE=''
  NC=''
fi

echo "Verifying package versions against Central Package Management..."
echo

# Extract all package names from Directory.Packages.props
declare -A DEFINED_PACKAGES
while IFS= read -r line; do
  if [[ "$line" =~ \<PackageVersion[[:space:]]+Include=\"([^\"]+)\" ]]; then
    pkg="${BASH_REMATCH[1]}"
    DEFINED_PACKAGES["$pkg"]=1
  fi
done < "$PACKAGES_PROPS"

echo "Found ${#DEFINED_PACKAGES[@]} package versions defined in Directory.Packages.props"
echo

# Track missing packages and which projects reference them
declare -A MISSING_PACKAGES
declare -A PROJECT_REFS

# Scan all .csproj files for PackageReference entries
while IFS= read -r csproj; do
  # Skip sample/test template projects
  if [[ "$csproj" =~ samples/templates/ ]]; then
    continue
  fi
  
  # Extract package references from this project
  while IFS= read -r line; do
    if [[ "$line" =~ \<PackageReference[[:space:]]+Include=\"([^\"]+)\" ]]; then
      pkg="${BASH_REMATCH[1]}"
      
      # Skip if this reference has an inline Version attribute (e.g. Version="$(PackageVersion)")
      # These are using MSBuild properties, not Central Package Management
      if [[ "$line" =~ Version= ]]; then
        continue
      fi
      
      # Check if this package has a version defined in Directory.Packages.props
      if [[ -z "${DEFINED_PACKAGES[$pkg]:-}" ]]; then
        MISSING_PACKAGES["$pkg"]=1
        # Track which project references this package
        if [[ -z "${PROJECT_REFS[$pkg]:-}" ]]; then
          PROJECT_REFS["$pkg"]="$csproj"
        else
          PROJECT_REFS["$pkg"]="${PROJECT_REFS[$pkg]}|$csproj"
        fi
      fi
    fi
  done < "$csproj"
done < <(find "$ROOT" -name "*.csproj" -type f)

# Report results
MISSING_COUNT=0
if [[ -n "${!MISSING_PACKAGES[@]:-}" ]]; then
  MISSING_COUNT=${#MISSING_PACKAGES[@]}
fi

if [[ $MISSING_COUNT -eq 0 ]]; then
  echo -e "${GREEN}✓ All package references have versions defined in Directory.Packages.props${NC}"
  echo
  echo "verify-package-versions: OK"
  exit 0
fi

# Report missing packages with clear diagnostics
echo -e "${RED}✗ Found ${MISSING_COUNT} package(s) missing from Directory.Packages.props${NC}"
echo
echo "The following packages are referenced in .csproj files but do not have"
echo "versions defined in Directory.Packages.props:"
echo

for pkg in "${!MISSING_PACKAGES[@]}"; do
  echo -e "${YELLOW}Package: ${pkg}${NC}"
  echo "  Referenced in:"
  IFS='|' read -ra PROJECTS <<< "${PROJECT_REFS[$pkg]}"
  for proj in "${PROJECTS[@]}"; do
    rel_path="${proj#$ROOT/}"
    echo "    - $rel_path"
  done
  echo
done

echo -e "${BLUE}To fix this issue:${NC}"
echo
echo "1. Determine the correct version for each missing package"
echo "2. Add PackageVersion entries to Directory.Packages.props:"
echo
for pkg in "${!MISSING_PACKAGES[@]}"; do
  echo "   <PackageVersion Include=\"${pkg}\" Version=\"X.Y.Z\" />"
done
echo
echo "3. Re-run this script to verify"
echo
echo -e "${YELLOW}Note: With Central Package Management, all PackageReference items must${NC}"
echo -e "${YELLOW}have their versions declared in Directory.Packages.props, not in .csproj files.${NC}"
echo

EXIT_CODE=1
exit $EXIT_CODE
