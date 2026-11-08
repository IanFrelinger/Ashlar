#!/usr/bin/env bash
# Starts a studio image as a fresh SYNTHETIC studio for the QA suites, hardened as in production (read-only root,
# no capabilities, non-root, memory and process caps), and waits until its starter steps are certified.
#
#   qa/serve-image.sh IMAGE [PORT]      then: STUDIO_URL=http://127.0.0.1:PORT npm run ui | npm run agents ...
#
# Each call replaces the previous container and its data, so every suite starts from the same synthetic state.
set -euo pipefail
image=${1:?usage: serve-image.sh IMAGE [PORT]}
port=${2:-8080}
name=studio-qa-$port

docker rm -f "$name" >/dev/null 2>&1 || true
docker run -d --name "$name" -p "127.0.0.1:$port:8080" \
  --read-only --tmpfs /tmp:size=512m --cap-drop ALL --security-opt no-new-privileges:true \
  --memory 4g --pids-limit 512 \
  "$image" serve --synthetic --public-url "http://127.0.0.1:$port" >/dev/null

for _ in $(seq 1 90); do
  if curl -sf "http://127.0.0.1:$port/api/meta" 2>/dev/null | grep -q '"libraryReady":true'; then
    echo "synthetic studio from $image on http://127.0.0.1:$port"
    exit 0
  fi
  sleep 2
done
echo "the studio did not become ready" >&2
docker logs --tail 60 "$name" >&2 || true
exit 1
