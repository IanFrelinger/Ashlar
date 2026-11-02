#!/usr/bin/env bash
# Run only in a disposable devtest clone with dependencies restored and network disconnected.
set -euo pipefail
if [[ "${ASHLAR_CERT_DISPOSABLE:-0}" != 1 || ! -f /.dockerenv || ! -d /src-mirror/.git ]]; then
  echo 'Refusing: run in the disposable devtest clone with ASHLAR_CERT_DISPOSABLE=1.' >&2
  exit 64
fi
for interface in /sys/class/net/*; do
  if [[ "$(basename "$interface")" != lo ]]; then
    echo 'Refusing: disconnect container networking before adversarial reproduction.' >&2
    exit 64
  fi
done
cd "${ASHLAR_CERT_REPO:-/repo}"
if [[ "$(git rev-parse --show-toplevel)" == /src-mirror ]]; then
  echo 'Refusing to modify the mounted source repository.' >&2
  exit 64
fi
aggregate_only=0
if [[ "${1:-}" == --aggregate-only ]]; then aggregate_only=1; shift; fi
record_dir="${1:-/tmp/ashlar-cert-reproduction}"
mkdir -p "$record_dir"
rm -f "$record_dir/baseline-red.trx" "$record_dir/fixed-green.trx" "$record_dir/cert-gate.trx" "$record_dir/aggregate-red.trx"
base_sha=5e0beeae3b037508f23b475d98d13cc7e64c25de
loader=src/Ashlar.Infrastructure/Certification/BrickCertificationProjectLoader.cs
files=(src/Ashlar.Infrastructure/Certification/WitnessValueComparer.cs src/Ashlar.Infrastructure/Certification/WitnessRunner.cs)
fixed_copy="$(mktemp -d)"
restore_fixed() {
  for file in "${files[@]}"; do cp "$fixed_copy/$(basename "$file")" "$file"; done
  cp "$fixed_copy/$(basename "$loader")" "$loader"
  rm -rf "$fixed_copy"
}
for file in "${files[@]}"; do cp "$file" "$fixed_copy/$(basename "$file")"; done
cp "$loader" "$fixed_copy/$(basename "$loader")"
trap restore_fixed EXIT
filter='FullyQualifiedName~WitnessKindRegressionTests|FullyQualifiedName~CompiledSourceSetRegressionTests|FullyQualifiedName~WitnessValueComparerTests'
if [[ "$aggregate_only" == 0 ]]; then
for file in "${files[@]}"; do git show "$base_sha:$file" > "$file"; done
set +e
dotnet test src/Ashlar.Tests.Infrastructure/Ashlar.Tests.Infrastructure.csproj --framework net10.0 --filter "$filter" --logger "trx;LogFileName=baseline-red.trx" --results-directory "$record_dir" -p:NuGetAudit=false --no-restore --nologo -v minimal -m:2 > "$record_dir/baseline-red.log" 2>&1
baseline_rc=$?
set -e
bash scripts/ci/zero-test-guard.sh "$record_dir/baseline-red.trx"
tail -n 45 "$record_dir/baseline-red.log"
if [[ "$baseline_rc" == 0 ]] || ! grep -Eq '^Failed!.*Failed: +[1-9]' "$record_dir/baseline-red.log"; then
  echo "Expected baseline assertion failures, got exit $baseline_rc; this is not a confirmed reproduction" >&2
  exit 2
fi
for file in "${files[@]}"; do cp "$fixed_copy/$(basename "$file")" "$file"; done
fi
# Isolate the loader gap with the fixed comparer in place: raw JSON text must not
# satisfy an aggregate witness. A separate red run attributes this failure clearly.
git show "$base_sha:$loader" > "$loader"
set +e
dotnet test src/Ashlar.Tests.Infrastructure/Ashlar.Tests.Infrastructure.csproj --framework net10.0 --filter 'FullyQualifiedName~AggregateWitnessLoaderRegressionTests' --logger "trx;LogFileName=aggregate-red.trx" --results-directory "$record_dir" -p:NuGetAudit=false --no-restore --nologo -v minimal -m:2 > "$record_dir/aggregate-red.log" 2>&1
aggregate_rc=$?
set -e
bash scripts/ci/zero-test-guard.sh "$record_dir/aggregate-red.trx"
tail -n 30 "$record_dir/aggregate-red.log"
if [[ "$aggregate_rc" == 0 ]] || ! grep -Eq '^Failed!.*Failed: +[1-9]' "$record_dir/aggregate-red.log"; then
  echo "Expected aggregate assertion failures, got exit $aggregate_rc; this is not a confirmed reproduction" >&2
  exit 2
fi
cp "$fixed_copy/$(basename "$loader")" "$loader"
if [[ "$aggregate_only" == 1 ]]; then
  filter='FullyQualifiedName~AggregateWitnessLoaderRegressionTests'
else
  filter="$filter|FullyQualifiedName~AggregateWitnessLoaderRegressionTests"
fi
dotnet test src/Ashlar.Tests.Infrastructure/Ashlar.Tests.Infrastructure.csproj --framework net10.0 --filter "$filter" --logger "trx;LogFileName=fixed-green.trx" --results-directory "$record_dir" -p:NuGetAudit=false --no-restore --nologo -v minimal -m:2 > "$record_dir/fixed-green.log" 2>&1
bash scripts/ci/zero-test-guard.sh "$record_dir/fixed-green.trx"
tail -n 12 "$record_dir/fixed-green.log"
if [[ "$aggregate_only" == 1 ]]; then exit 0; fi
# Required cert-gate's full configured namespace filter on this working snapshot.
source scripts/cert-gate-config.sh
dotnet test src/Ashlar.Tests.Infrastructure/Ashlar.Tests.Infrastructure.csproj --framework net10.0 --filter "$CERT_GATE_FILTER" --logger "trx;LogFileName=cert-gate.trx" --results-directory "$record_dir" -p:NuGetAudit=false --no-restore --nologo -v minimal -m:2 > "$record_dir/cert-gate.log" 2>&1
bash scripts/ci/zero-test-guard.sh "$record_dir/cert-gate.trx"
tail -n 16 "$record_dir/cert-gate.log"
