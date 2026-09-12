#!/usr/bin/env bash
# Assert that the PACKED Ashlar.Certification.Contracts is fail-closed, by running a consumer
# against the package instead of against the source tree.
#
# Why this exists
# ---------------
# Every certification test in this repository compiles src/. A consumer compiles against a
# .nupkg. Those are different artifacts and a release can make them disagree, so "the presets
# are fail-closed" is a claim about the source until something checks the package. This script
# is that something.
#
# It asserts on the consumer's printed TRANSCRIPT, not on its exit code: a harness that trusts
# $? alone reports success for a consumer that never ran.
#
# Modes
#   (default)                       pack src/Ashlar.Certification.Contracts into a temp feed
#   ASHLAR_CONFORMANCE_FEED=<dir>   use a directory of already-packed .nupkg (e.g. a CI artifact)
#   ASHLAR_CONFORMANCE_SOURCE=nuget.org
#                                   restore the version from nuget.org (post-publish verification)
#   ASHLAR_CONFORMANCE_VERSION=<v>  package version to consume; required for the two modes above
#
# Usage
#   bash scripts/verify-packed-certification-trust-behavior.sh
#   ASHLAR_CONFORMANCE_SOURCE=nuget.org ASHLAR_CONFORMANCE_VERSION=1.2.3 bash scripts/verify-packed-certification-trust-behavior.sh
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SAMPLE="${ROOT}/docs/samples/CertificationTrustConsumer/CertificationTrustConsumer.csproj"
VERSION="${ASHLAR_CONFORMANCE_VERSION:-0.0.0-conformance}"
SOURCE_MODE="${ASHLAR_CONFORMANCE_SOURCE:-local}"
FEED="${ASHLAR_CONFORMANCE_FEED:-}"
TFMS="${ASHLAR_CONFORMANCE_TFMS:-net8.0 net10.0}"

# Every assertion the consumer is expected to emit, by name. Listing them here is deliberate
# duplication: it is what makes a DELETED assertion a failure rather than a shorter green run.
EXPECTED_ASSERTIONS=(
  bound-asset-matches-consumer-tfm
  default-requires-ed25519
  strict-requires-ed25519
  default-schema-floor-is-trust-loop
  strict-schema-floor-is-trust-loop
  strict-requires-gate-emitted-artifact
  strict-requires-certifier-identity
  default-reports-itself-strict
  legacy-reports-itself-not-strict
  hmac-fallback-reports-dev-key
  positive-control-legacy-trusts-valid-hmac-record
  default-refuses-hmac-only-record
  strict-refuses-hmac-only-record
  omitted-options-refuses-hmac-only-record
  default-refuses-schema-downgrade
  unknown-schema-version-refused
  content-hash-mismatch-refused
  stripped-hmac-signature-refused
)
EXPECTED_COUNT="${#EXPECTED_ASSERTIONS[@]}"

WORK="$(mktemp -d "${TMPDIR:-/tmp}/ashlar-packed-trust-XXXXXX")"
trap 'rm -rf "${WORK}"' EXIT

# The key must not be inherited from the environment: the consumer asserts that the HMAC fallback
# still reports itself, and an ambient key would make that assertion answer a different question.
unset ASHLAR_CERT_DEV_HMAC_KEY || true

# Isolated restore so a stale entry in the developer's global cache cannot stand in for the
# package under test. This is the whole point of the exercise.
export NUGET_PACKAGES="${WORK}/packages"
export DOTNET_CLI_HOME="${WORK}/cli-home"
mkdir -p "${NUGET_PACKAGES}" "${DOTNET_CLI_HOME}"

case "${SOURCE_MODE}" in
  nuget.org)
    if [[ "${VERSION}" == "0.0.0-conformance" ]]; then
      echo "ASHLAR_CONFORMANCE_SOURCE=nuget.org requires ASHLAR_CONFORMANCE_VERSION" >&2
      exit 2
    fi
    echo "==> Consuming Ashlar.Certification.Contracts ${VERSION} from nuget.org"
    cat > "${WORK}/NuGet.Config" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
  </packageSources>
</configuration>
EOF
    ;;
  local)
    if [[ -n "${FEED}" ]]; then
      if [[ ! -d "${FEED}" ]]; then
        echo "ASHLAR_CONFORMANCE_FEED is not a directory: ${FEED}" >&2
        exit 2
      fi
      FEED="$(cd "${FEED}" && pwd)"
      echo "==> Consuming Ashlar.Certification.Contracts ${VERSION} from pre-packed feed ${FEED}"
    else
      FEED="${WORK}/feed"
      mkdir -p "${FEED}"
      echo "==> Packing Ashlar.Certification.Contracts ${VERSION} from source"
      dotnet pack "${ROOT}/src/Ashlar.Certification.Contracts/Ashlar.Certification.Contracts.csproj" \
        -c Release \
        -o "${FEED}" \
        -p:PackageVersion="${VERSION}" \
        -v minimal
    fi
    cat > "${WORK}/NuGet.Config" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="ashlar-local" value="${FEED}" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
  </packageSources>
</configuration>
EOF
    ;;
  *)
    echo "Unknown ASHLAR_CONFORMANCE_SOURCE: ${SOURCE_MODE} (expected 'local' or 'nuget.org')" >&2
    exit 2
    ;;
esac

FAILED=0

# Restore once, with the config that pins where the package may come from. `dotnet run` has no
# --configfile of its own, so the source selection has to happen here and the run below has to be
# --no-restore, or it would silently re-resolve against the machine's default sources.
echo "==> Restoring the consumer against ${SOURCE_MODE}"
dotnet restore "${SAMPLE}" \
  --configfile "${WORK}/NuGet.Config" \
  -p:AshlarCertificationContractsVersion="${VERSION}" \
  -v minimal

for tfm in ${TFMS}; do
  echo
  echo "=============================================================="
  echo "==> Consumer TFM ${tfm}"
  echo "=============================================================="

  TRANSCRIPT="${WORK}/transcript-${tfm}.txt"

  # `|| true` on purpose: the consumer returns non-zero when an assertion fails, and the
  # transcript it printed on the way out is the evidence we actually want to read.
  set +e
  dotnet run \
    --project "${SAMPLE}" \
    -f "${tfm}" \
    -c Release \
    --no-restore \
    -p:AshlarCertificationContractsVersion="${VERSION}" \
    > "${TRANSCRIPT}" 2>&1
  RUN_STATUS=$?
  set -e

  cat "${TRANSCRIPT}"

  if [[ ${RUN_STATUS} -ne 0 ]] && ! grep -q '^RESULT=' "${TRANSCRIPT}"; then
    echo "::error::consumer did not run to completion on ${tfm} (exit ${RUN_STATUS}, no RESULT line)"
    FAILED=1
    continue
  fi

  # --- assertions on the transcript ------------------------------------------------------------
  tfm_failed=0

  if ! grep -qx 'RESULT=OK' "${TRANSCRIPT}"; then
    echo "::error::${tfm}: RESULT=OK not present"
    tfm_failed=1
  fi

  if ! grep -qx "ASSERTIONS_RUN=${EXPECTED_COUNT}" "${TRANSCRIPT}"; then
    echo "::error::${tfm}: expected ASSERTIONS_RUN=${EXPECTED_COUNT}, transcript says $(grep '^ASSERTIONS_RUN=' "${TRANSCRIPT}" || echo '(nothing)')"
    tfm_failed=1
  fi

  if ! grep -qx 'ASSERTIONS_FAILED=0' "${TRANSCRIPT}"; then
    echo "::error::${tfm}: at least one assertion failed"
    tfm_failed=1
  fi

  for name in "${EXPECTED_ASSERTIONS[@]}"; do
    if ! grep -qx "PASS ${name}" "${TRANSCRIPT}"; then
      echo "::error::${tfm}: missing or failing assertion '${name}'"
      tfm_failed=1
    fi
  done

  if [[ ${tfm_failed} -ne 0 ]]; then
    FAILED=1
    echo "==> ${tfm}: FAILED"
  else
    echo "==> ${tfm}: ${EXPECTED_COUNT}/${EXPECTED_COUNT} assertions passed against the packed artifact"
  fi
done

echo
if [[ ${FAILED} -ne 0 ]]; then
  echo "PACKED_CERTIFICATION_TRUST=FAIL"
  echo
  echo "The packed Ashlar.Certification.Contracts does not behave as the source tree says it does."
  echo "Do not publish this artifact. See docs/samples/CertificationTrustConsumer/README.md."
  exit 1
fi

echo "PACKED_CERTIFICATION_TRUST=OK (version ${VERSION}, source ${SOURCE_MODE})"
