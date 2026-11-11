#!/usr/bin/env bash
# Invoked by run-reported-gates.py so failures, stalls and partial logs are retained.
set -euo pipefail
: "${READINESS_DOCKERFILE:?container Dockerfile is required}"
: "${READINESS_BUILD_ARG:?container build argument is required}"
: "${READINESS_IMAGE:?container image name is required}"
: "${READINESS_OS:?expected container OS is required}"
printf 'readiness-build: setup only; product tests have not started\n'
printf 'reproduce: READINESS_DOCKERFILE=%q READINESS_BUILD_ARG=%q READINESS_IMAGE=%q READINESS_OS=%q READINESS_OS_VERSION=%q bash scripts/ci/build-readiness-container.sh\n' \
    "$READINESS_DOCKERFILE" "$READINESS_BUILD_ARG" "$READINESS_IMAGE" "$READINESS_OS" "${READINESS_OS_VERSION:-}"
docker build --progress=plain \
    -f "$READINESS_DOCKERFILE" \
    --build-arg "$READINESS_BUILD_ARG" \
    -t "$READINESS_IMAGE" .
docker run --rm "$READINESS_IMAGE" \
    sh scripts/ci/check-readiness-container.sh "$READINESS_OS" "${READINESS_OS_VERSION:-}"
