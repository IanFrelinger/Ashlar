#!/bin/sh
# Verify the image actually provides the distro and real runtime families the lane names.
set -eu
expected_os="${1:?expected OS id is required}"
expected_version="${2:-}"
release_file="${3:-/etc/os-release}"
. "$release_file"
echo "readiness-image: os=${ID:-missing} version=${VERSION_ID:-missing} expected=${expected_os}:${expected_version}"
if [ "${ID:-}" != "$expected_os" ] || { [ -n "$expected_version" ] && [ "${VERSION_ID:-}" != "$expected_version" ]; }; then
    echo "readiness-image: OS identity mismatch; this lane cannot certify the requested distro" >&2
    exit 3
fi
sdk="$(dotnet --version)"
echo "readiness-image: sdk=${sdk}"
case "$sdk" in
    10.*) ;;
    *) echo "readiness-image: SDK 10 is required" >&2; exit 3 ;;
esac
runtimes="$(dotnet --list-runtimes)"
printf '%s\n' "$runtimes"
for framework in Microsoft.NETCore.App Microsoft.AspNetCore.App; do
    for major in 8 10; do
        if ! printf '%s\n' "$runtimes" | grep -F "${framework} ${major}." >/dev/null; then
            echo "readiness-image: missing real ${framework} ${major}.x runtime" >&2
            exit 3
        fi
    done
done
