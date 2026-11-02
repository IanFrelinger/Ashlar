#!/usr/bin/env bash
set -uo pipefail
while [[ ! -f /provider-evidence/stop ]]; do
 if [[ -f /provider-evidence/request ]]; then
  step="$(cat /provider-evidence/request)"
  rm /provider-evidence/request
  if bash "/provider-evidence/steps/${step}.sh" > "/provider-evidence/${step}.log" 2>&1; then status=0; else status=$?; fi
  printf '%s\n' "$status" > "/provider-evidence/${step}.status"
  chown -R 1000:1000 /provider-evidence
 fi
 sleep 1
done
