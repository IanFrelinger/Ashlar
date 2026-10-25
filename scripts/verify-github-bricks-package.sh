#!/usr/bin/env bash
# Packs Ashlar.Bricks.GitHub with the three Ashlar packages it depends on into a temp feed, then builds
# and runs a consumer that restores ONLY from that feed (+ nuget.org) — no repo ProjectReference.
#
# WHAT THIS PROVES
# ----------------
# The package a release pushes is consumable on its own: it restores, its dependency closure is
# Ashlar.Core.Domain (+ Ashlar.Brick.Contracts), Ashlar.Abstractions (the egress guard), Octokit and
# logging abstractions and nothing that
# only exists in this repository, the nuspec carries a description and its own README, the bricks
# are in the `Ashlar.Bricks.GitHub` namespace, and a consumer process can construct both and execute one.
#
# WHAT THIS DOES NOT PROVE
# ------------------------
# Anything about GitHub. The consumer executes each brick with input that fails before a request
# is sent (a malformed PR URL), so the run needs no token and makes no
# GitHub call. A green run says the package ships; not that its answers about a live PR are right.
#
# Usage:  bash scripts/verify-github-bricks-package.sh
# Env:    ASHLAR_GITHUB_BRICKS_VERIFY_VERSION (default 9.9.9-local)
#         ASHLAR_GITHUB_BRICKS_VERIFY_WORK    (default: mktemp -d; kept on failure)
# Needs dotnet: run it in the devtest container, e.g.
#   scripts/test-in-container.sh -- 'bash scripts/verify-github-bricks-package.sh'
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
VERSION="${ASHLAR_GITHUB_BRICKS_VERIFY_VERSION:-9.9.9-local}"
WORK="${ASHLAR_GITHUB_BRICKS_VERIFY_WORK:-$(mktemp -d)}"
FEED="${WORK}/feed"
CONSUMER="${WORK}/consumer"
CFG="${WORK}/NuGet.Config"

mkdir -p "${FEED}" "${CONSUMER}" "${WORK}/packages" "${WORK}/cli-home"
# An empty package cache, so restore cannot be satisfied by a package some earlier build left behind.
export NUGET_PACKAGES="${WORK}/packages"
export DOTNET_CLI_HOME="${WORK}/cli-home"

pack() {
  echo "==> dotnet pack $1"
  dotnet pack "${ROOT}/$1" -c Release -o "${FEED}" -p:PackageVersion="${VERSION}" -v minimal
}

pack src/Ashlar.Brick.Contracts/Ashlar.Brick.Contracts.csproj
pack src/Ashlar.Core.Domain/Ashlar.Core.Domain.csproj
pack src/Ashlar.Abstractions/Ashlar.Abstractions.csproj
pack src/Ashlar.Bricks.GitHub/Ashlar.Bricks.GitHub.csproj

NUPKG="${FEED}/Ashlar.Bricks.GitHub.${VERSION}.nupkg"
[[ -f "${NUPKG}" ]] || { echo "FAIL - ${NUPKG} was not produced" >&2; exit 1; }

echo "==> Inspecting nuspec"
NUSPEC="$(unzip -p "${NUPKG}" Ashlar.Bricks.GitHub.nuspec)"
fail_nuspec() { echo "FAIL - $1" >&2; echo "${NUSPEC}" >&2; exit 1; }
grep -q '<description>[^<]*GitHub' <<<"${NUSPEC}" || fail_nuspec "nuspec has no GitHub description"
grep -q '<readme>README.md</readme>' <<<"${NUSPEC}" || fail_nuspec "nuspec has no README"
unzip -p "${NUPKG}" README.md | head -1 | grep -q '^# Ashlar.Bricks.GitHub' \
  || fail_nuspec "packed README.md is not the package's own README"
for dep in Ashlar.Core.Domain Ashlar.Abstractions Octokit; do
  grep -q "id=\"${dep}\"" <<<"${NUSPEC}" || fail_nuspec "nuspec does not depend on ${dep}"
done
# Neither of these is needed by the bricks; either one would drag the full infrastructure graph or
# a repository build rule into every consumer.
for dep in Ashlar.Infrastructure Ashlar.Analyzers; do
  if grep -q "id=\"${dep}\"" <<<"${NUSPEC}"; then
    fail_nuspec "nuspec depends on ${dep}"
  fi
done

cat > "${CFG}" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="ashlar-local" value="${FEED}" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
  </packageSources>
</configuration>
EOF

cat > "${CONSUMER}/GitHubBricksConsumer.csproj" <<EOF
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <RollForward>Major</RollForward>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Ashlar.Bricks.GitHub" Version="${VERSION}" />
  </ItemGroup>
</Project>
EOF

cat > "${CONSUMER}/Program.cs" <<'CS'
using Ashlar.Bricks.GitHub;
using Ashlar.Core.Domain.Bricks;
using Ashlar.Core.Domain.Execution;
using Microsoft.Extensions.Logging.Abstractions;

var failures = 0;
void Check(bool condition, string what)
{
    Console.WriteLine($"  {(condition ? "ok  " : "FAIL")} - {what}");
    if (!condition) failures++;
}

var pr = new PrStatusBrick(NullLogger<PrStatusBrick>.Instance);
Check(pr.Id == "github.pr-status", $"PrStatusBrick id is github.pr-status (got {pr.Id})");
var prInput = new BrickInput();
prInput.Set("prUrl", "https://example.invalid/not-a-pull-request");
var prOutput = await pr.ExecuteAsync(prInput, ImplementationType.Deterministic, new NoContext());
Check(prOutput.Get<string>("state") == "error", "PrStatusBrick reports a malformed PR URL as state=error");

var ci = new CiStatusBrick(NullLogger<CiStatusBrick>.Instance);
Check(ci.Id == "github.ci-status", $"CiStatusBrick id is github.ci-status (got {ci.Id})");

var blockers = new GitHubBlockersBrick(NullLogger<GitHubBlockersBrick>.Instance);
Check(blockers.Id == "github.blockers", $"GitHubBlockersBrick id is github.blockers (got {blockers.Id})");
var offHost = new BrickInput();
offHost.Set("url", "https://example.invalid/owner/repo/pull/1");
try
{
    await blockers.ExecuteAsync(offHost, ImplementationType.Deterministic, new NoContext());
    Check(false, "GitHubBlockersBrick refuses a URL that is not on github.com");
}
catch (ArgumentException)
{
    Check(true, "GitHubBlockersBrick refuses a URL that is not on github.com");
}

Console.WriteLine(failures == 0 ? "consumer: OK" : $"consumer: {failures} failure(s)");
return failures == 0 ? 0 : 1;

sealed class NoContext : IExecutionContext
{
    public string AgentId => "verify-github-bricks-package";
    public string BehaviorId => "verify-github-bricks-package";
    public bool IsAirGapped => false;
    public bool AuditMode => false;
    public string Provider => "none";
    public IReadOnlyDictionary<string, object> Variables { get; } = new Dictionary<string, object>();
}
CS

echo "==> Restoring consumer from ${FEED} + nuget.org"
dotnet restore "${CONSUMER}/GitHubBricksConsumer.csproj" --configfile "${CFG}" -v minimal
dotnet build "${CONSUMER}/GitHubBricksConsumer.csproj" -c Release --no-restore -v minimal
echo "==> Running consumer"
dotnet run --project "${CONSUMER}/GitHubBricksConsumer.csproj" -c Release --no-build

if [[ -z "${ASHLAR_GITHUB_BRICKS_VERIFY_WORK:-}" ]]; then
  rm -rf "${WORK}"
fi
echo "verify-github-bricks-package: OK (version ${VERSION})"
