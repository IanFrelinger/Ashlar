#!/bin/sh
# Only for the Ubuntu/Debian readiness image build. Never continue with partial indexes.
set -eu
if [ "$#" -eq 0 ]; then
    echo "apt-setup: at least one package is required" >&2
    exit 2
fi

attempt=1
while :; do
    echo "apt-setup: refreshing package indexes (attempt ${attempt}/3; limit 90s)" >&2
    if timeout --kill-after=5s 90s apt-get \
        -o APT::Update::Error-Mode=any \
        -o Acquire::Retries=1 \
        -o Acquire::http::Timeout=20 \
        -o Acquire::https::Timeout=20 update; then
        break
    else
        status=$?
    fi
    echo "apt-setup: package index refresh failed (exit ${status})" >&2
    if [ "$status" -eq 124 ] || [ "$status" -eq 137 ]; then
        echo "apt-setup: package index refresh stalled; refusing installation" >&2
        exit "$status"
    fi
    if [ "$attempt" -eq 3 ]; then
        echo "apt-setup: retries exhausted; refusing installation with missing or stale indexes" >&2
        exit "$status"
    fi
    sleep "$((attempt * 2))"
    attempt=$((attempt + 1))
done

echo "apt-setup: installing required packages (limit 180s)" >&2
if timeout --kill-after=5s 180s apt-get \
    -o Acquire::Retries=1 -o Acquire::http::Timeout=20 -o Acquire::https::Timeout=20 \
    install -y --no-install-recommends "$@"; then
    rm -rf /var/lib/apt/lists/*
else
    status=$?
    echo "apt-setup: package installation failed or stalled (exit ${status})" >&2
    exit "$status"
fi
