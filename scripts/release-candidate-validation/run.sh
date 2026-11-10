#!/usr/bin/env bash
# Run ONLY inside scripts/test-in-container.sh against an untouched pinned product clone.
# /rc-harness is the read-only rehearsal harness; /rc-evidence is a separate output mount.
set -euo pipefail
SHA="${1:?full source SHA required}"
OUT="${2:?evidence output directory required}"
BASELINE="0.4.2"
VERSION="9.9.9-rcvalidation.g${SHA:0:12}"
HARNESS="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(pwd)"
[[ "$SHA" =~ ^[0-9a-f]{40}$ ]] || { echo 'Full SHA required' >&2; exit 2; }
[[ "$(git rev-parse HEAD)" == "$SHA" ]] || { echo 'Source SHA mismatch' >&2; exit 2; }
[[ -z "$(git status --porcelain)" ]] || { echo 'Source is dirty' >&2; exit 2; }
[[ -f /.dockerenv ]] || { echo 'Container execution required' >&2; exit 2; }
mkdir -p "$OUT/packages" "$OUT/logs" "$OUT/consumer-evidence" "$OUT/tests"
printf '%s\n' "$SHA" > "$OUT/source-sha.txt"
printf '%s\n' "$VERSION" > "$OUT/validation-version.txt"
dotnet --info > "$OUT/dotnet-info.txt"

run_logged() {
  local name="$1"; shift
  "$@" 2>&1 | tee "$OUT/logs/$name.log"
}

run_logged pack-alignment python3 scripts/verify-pack-ashlar-hosting-graph-alignment.py
run_logged pack bash scripts/pack-ashlar-hosting-graph.sh "$VERSION" "$OUT/packages"
# Match the additional package IDs in reusable-release-nuget.yml without invoking
# that publishing workflow or reading any publication credentials.
for project in src/Ashlar.Client/Ashlar.Client.csproj src/Ashlar.Sdk/Ashlar.Sdk.csproj \
  src/Ashlar.Authoring/Ashlar.Authoring.csproj src/Ashlar.Bricks.GitHub/Ashlar.Bricks.GitHub.csproj \
  application/src/Ashlar.CLI/Ashlar.CLI.csproj src/Ashlar.Mcp.Server.Host/Ashlar.Mcp.Server.Host.csproj; do
  name="$(basename "$project" .csproj)"
  run_logged "pack-$name" dotnet pack "$project" -c Release -o "$OUT/packages" \
    "-p:PackageVersion=$VERSION" -p:IncludeTestProjectReferences=false -p:AshlarMcpHostLocalModels=false -v minimal
done
python3 "$HARNESS/verify.py" packages "$OUT/packages" "$VERSION" "$SHA"

# Copy the public sample outside the repository. No ancestor Directory.Build.props,
# shared NuGet cache, or ProjectReference can accidentally make these consumers pass.
WORK="$(mktemp -d /tmp/ashlar-rc-consumers-XXXXXX)"
trap 'rm -rf "$WORK"' EXIT
cat > "$WORK/published.config" <<'XML'
<configuration><packageSources><clear /><add key="nuget.org" value="https://api.nuget.org/v3/index.json" /></packageSources></configuration>
XML
cat > "$WORK/candidate.config" <<XML
<configuration>
 <packageSources><clear /><add key="candidate" value="$OUT/packages" /><add key="nuget.org" value="https://api.nuget.org/v3/index.json" /></packageSources>
 <packageSourceMapping><packageSource key="candidate"><package pattern="Ashlar.*" /></packageSource><packageSource key="nuget.org"><package pattern="*" /></packageSource></packageSourceMapping>
</configuration>
XML

new_consumer() {
  local dir="$1"
  mkdir -p "$dir/package-consumer"
  cp "$ROOT/docs/samples/StableSdkHostSample/Program.cs" "$dir/Program.cs"
  cp "$ROOT/docs/samples/StableSdkHostSample/package-consumer/"*.props "$dir/package-consumer/"
  cp "$ROOT/docs/samples/StableSdkHostSample/package-consumer/StableSdkHostSample.Package.csproj" "$dir/package-consumer/"
}

consumer_run() {
  local name="$1" dir="$2" version="$3" framework="$4" config="$5" cache="$6"
  local project="$dir/package-consumer/StableSdkHostSample.Package.csproj"
  mkdir -p "$cache/packages" "$cache/cli-home" "$cache/http"
  export NUGET_PACKAGES="$cache/packages" DOTNET_CLI_HOME="$cache/cli-home" NUGET_HTTP_CACHE_PATH="$cache/http"
  local properties=("-p:AshlarSdkPackageVersion=$version" "-p:TargetFramework=$framework")
  run_logged "$name-restore" dotnet restore "$project" --configfile "$config" --force-evaluate --no-http-cache "${properties[@]}" -v minimal
  python3 "$HARNESS/verify.py" assets "$dir/package-consumer/obj/project.assets.json" "$version" "$NUGET_PACKAGES"
  cp "$dir/package-consumer/obj/project.assets.json" "$OUT/consumer-evidence/$name.assets.json"
  run_logged "$name-build" dotnet build "$project" -c Release --no-restore "${properties[@]}" -v minimal
  run_logged "$name-run" dotnet run --project "$project" -c Release --no-build --no-restore "${properties[@]}"
  grep -Fq 'Stable SDK host sample bootstrapped successfully.' "$OUT/logs/$name-run.log"
}

for framework in net8.0 net10.0; do
  clean="$WORK/clean-$framework"
  new_consumer "$clean"
  consumer_run "clean-$framework" "$clean" "$VERSION" "$framework" "$WORK/candidate.config" "$WORK/clean-cache-$framework"

  upgrade="$WORK/upgrade-$framework"
  new_consumer "$upgrade"
  before="$(sha256sum "$upgrade/Program.cs" | cut -d ' ' -f1)"
  consumer_run "baseline-$framework" "$upgrade" "$BASELINE" "$framework" "$WORK/published.config" "$WORK/upgrade-cache-$framework"
  # Keep baseline packages and source; force re-evaluation on the explicit candidate pin.
  consumer_run "upgrade-$framework" "$upgrade" "$VERSION" "$framework" "$WORK/candidate.config" "$WORK/upgrade-cache-$framework"
  [[ "$(sha256sum "$upgrade/Program.cs" | cut -d ' ' -f1)" == "$before" ]]
  printf 'PASS unchanged consumer source: %s %s\n' "$framework" "$before"
done
unset NUGET_PACKAGES DOTNET_CLI_HOME NUGET_HTTP_CACHE_PATH

# Exercise the developer-experience changes through the packaged CLI, not dotnet run
# from source: a clean tool install, scaffold, all three starter contracts, then edit/test.
export NUGET_PACKAGES="$WORK/authoring-cache/packages" DOTNET_CLI_HOME="$WORK/authoring-cache/cli-home" NUGET_HTTP_CACHE_PATH="$WORK/authoring-cache/http"
mkdir -p "$NUGET_PACKAGES" "$DOTNET_CLI_HOME" "$NUGET_HTTP_CACHE_PATH" "$WORK/standalone"
run_logged cli-install dotnet tool install --tool-path "$WORK/tools" Ashlar.CLI \
  --version "$VERSION" --configfile "$WORK/candidate.config" --no-cache
run_logged cli-inventory dotnet tool list --tool-path "$WORK/tools"
run_logged scaffold "$WORK/tools/ashlar" new brick SampleThing --output "$WORK/standalone" --ashlar-version "$VERSION" --json
run_logged scaffold-paths bash "$ROOT/scripts/verify-consumer-repo-paths.sh" "$WORK/standalone"
project="$WORK/standalone/SampleThingBrick.Tests/SampleThingBrick.Tests.csproj"
run_logged scaffold-restore dotnet restore "$project" --configfile "$WORK/candidate.config" --force-evaluate --no-http-cache
run_logged scaffold-tests dotnet test "$project" --no-restore -f net8.0 \
  --logger 'trx;LogFileName=scaffold.trx' --results-directory "$OUT/tests" \
  --blame-hang-timeout 120s --blame-hang-dump-type none
python3 "$HARNESS/verify.py" trx "$OUT/tests/scaffold.trx" 3
cp "$ROOT/tools/ci-agents/fixtures/SampleThingBrick.cs" "$WORK/standalone/SampleThingBrick/SampleThingBrick.cs"
cp "$ROOT/tools/ci-agents/fixtures/SampleThingBrickTests.cs" "$WORK/standalone/SampleThingBrick.Tests/SampleThingBrickTests.cs"
run_logged developer-edit-tests dotnet test "$project" --no-restore -f net8.0 \
  --logger 'trx;LogFileName=developer-edit.trx' --results-directory "$OUT/tests" \
  --blame-hang-timeout 120s --blame-hang-dump-type none
python3 "$HARNESS/verify.py" trx "$OUT/tests/developer-edit.trx" 4
unset NUGET_PACKAGES DOTNET_CLI_HOME NUGET_HTTP_CACHE_PATH

run_logged packed-trust env ASHLAR_CONFORMANCE_FEED="$OUT/packages" ASHLAR_CONFORMANCE_VERSION="$VERSION" \
  bash scripts/verify-packed-certification-trust-behavior.sh

# This exercises the real API/auth pipeline with a recording platform, not Docker operations.
# Its 8 net10-only cases include null/empty mount acceptance, every nonempty mount refusal,
# configured legacy-root rejection, symlink refusal, and dark-by-default/auth behavior.
run_logged remote-bind-migration dotnet test src/Ashlar.Tests.Infrastructure/Ashlar.Tests.Infrastructure.csproj \
  --framework net10.0 --filter 'FullyQualifiedName~RemoteExecutionOptInProdStyleTests' \
  --blame-hang-timeout 120s --blame-hang-dump-type none \
  --logger 'trx;LogFileName=remote-bind-migration.trx' --results-directory "$OUT/tests" --nologo -v minimal
python3 "$HARNESS/verify.py" trx "$OUT/tests/remote-bind-migration.trx" 8
git status --porcelain > "$OUT/final-source-status.txt"
[[ ! -s "$OUT/final-source-status.txt" ]]
printf 'PASS source=%s version=%s packages=24 clean-installs=2 upgrade-pairs=2 scaffold-tests=3 developer-edit-tests=4 remote-api-tests=8 published=false\n' "$SHA" "$VERSION" | tee "$OUT/result.txt"
