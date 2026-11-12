#!/usr/bin/env bash
# Run one pinned source batch; caller enforces ordering between batches.
set -euo pipefail
SOURCE_REPO=$(realpath "${1:?source clone required}")
MANIFEST=${2:?manifest required}
IDS=${3:?comma-separated IDs required}
EVIDENCE=$(realpath "${4:?existing evidence parent required}")
EXPECTED_SHA=792446d09b59f98b47e08279907cb3d4df314caa
[[ "$MANIFEST" =~ ^scripts/mutations/spec007-[a-z-]+\.json$ ]]
[[ "$IDS" =~ ^[A-Za-z0-9.,_-]+$ ]]
[[ -d "$SOURCE_REPO/.git" ]]
test "$(git -C "$SOURCE_REPO" rev-parse HEAD)" = "$EXPECTED_SHA"
test -z "$(git -C "$SOURCE_REPO" status --porcelain)"
printf '%s\n' "$EXPECTED_SHA" > "$EVIDENCE/source-sha.txt"
python3 "$SOURCE_REPO/scripts/ci/verify-mutation-manifests-current.py" --root "$SOURCE_REPO" > "$EVIDENCE/currency.log"
IMAGE=$(bash "$SOURCE_REPO/scripts/ensure-devtest-image.sh")
docker image inspect "$IMAGE" > "$EVIDENCE/image.json"
# A single container runs at a time. No --dirty or --no-build is used.
set +e
bash "$SOURCE_REPO/scripts/test-in-container.sh" --repo "$SOURCE_REPO" --ref "$EXPECTED_SHA" -- \
  "python3 scripts/run-spec007-mutation-batch.py --manifest '$MANIFEST' --ids '$IDS' --archive-stdout" \
  > "$EVIDENCE/container.log" 2>&1
rc=$?
set -e
printf '%s\n' "$rc" > "$EVIDENCE/exit-code.txt"
python3 "$SOURCE_REPO/scripts/decode-spec007-mutation-evidence.py" \
  --repo "$SOURCE_REPO" --log "$EVIDENCE/container.log" --output "$EVIDENCE/decoded"
python3 - "$EVIDENCE/decoded/summary.json" <<'PY'
import json,sys
s=json.load(open(sys.argv[1]))
for r in s['records']:
 red,green=r.get('red',{}),r.get('green',{})
 print(f"mutation {r['id']}: {r['verdict']} red={red.get('failed','?')}/{red.get('total','?')} green={green.get('passed','?')}/{green.get('total','?')} ref={r['ref']}")
 print('FAILED TESTS',json.dumps(red.get('failures',[])))
print('BATCH',s['verdict'])
PY
# A failure leaves decoded evidence and a failed job. No subsequent batch starts.
exit "$rc"
