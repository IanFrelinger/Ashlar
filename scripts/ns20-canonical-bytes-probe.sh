#!/usr/bin/env bash
# ns20-canonical-bytes-probe.sh — check that the netstandard2.0 asset of
# Ashlar.Certification.Contracts produces the SAME canonical signing payload bytes as the
# net8.0 and net10.0 assets.
#
# WHY THIS IS A SCRIPT AND NOT AN xunit LEG
# -----------------------------------------
# netstandard2.0 is not a runtime, so there is nothing to "run the tests on". The only honest
# way to show the ns2.0 asset agrees is to EXECUTE it somewhere that asset is really loaded:
# a .NET Framework consumer. This drives one under Mono, which is the route that works without
# a Windows runner. The bytes matter because they are the message every HMAC and Ed25519
# certification signature is computed over — a target that serialized them even slightly
# differently would recompute a different message and refuse certificates the other targets
# accept, and the package ships netstandard2.0 specifically for external consumers.
#
# Ed25519 is deliberately out of scope: NSec ships lib/net8.0 only, so the ns2.0 lane is
# HMAC-only by construction (see CertificationRecordEd25519). The payload bytes are the shared
# artifact and the only thing that has to match.
#
# THE CONSTANTS ARE NOT DUPLICATED. This probe reads the same
# src/Ashlar.Tests.Infrastructure/Tests/Certification/canonical-payloads.golden.json that
# CanonicalPayloadGoldenTests reads. Two independently typed copies would drift, and the
# cross-target equality claim would quietly evaporate with them.
#
# Usage:
#   scripts/ns20-canonical-bytes-probe.sh                 # build + run (needs dotnet AND docker)
#   scripts/ns20-canonical-bytes-probe.sh --build-only    # produce the consumer, no docker
#   scripts/ns20-canonical-bytes-probe.sh --run-only      # run an already-built consumer
#
# NS20_PROBE_WORKDIR pins the scratch directory, which is what makes the split usable when the
# host that has dotnet and the host that has docker are not the same one.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
GOLDEN="${ROOT}/src/Ashlar.Tests.Infrastructure/Tests/Certification/canonical-payloads.golden.json"
MONO_IMAGE="${NS20_PROBE_MONO_IMAGE:-mono:latest}"
STJ_VERSION="${NS20_PROBE_STJ_VERSION:-10.0.11}"

DO_BUILD=1
DO_RUN=1
case "${1:-}" in
  --build-only) DO_RUN=0 ;;
  --run-only) DO_BUILD=0 ;;
  "") ;;
  *) echo "ns20-canonical-bytes-probe: unknown argument ${1}" >&2; exit 2 ;;
esac

WORK="${NS20_PROBE_WORKDIR:-}"
if [[ -z "${WORK}" ]]; then
  WORK="$(mktemp -d)"
  trap 'rm -rf "${WORK}"' EXIT
fi
mkdir -p "${WORK}"

if [[ ! -f "${GOLDEN}" ]]; then
  echo "ns20-canonical-bytes-probe: golden corpus not found at ${GOLDEN}" >&2
  exit 1
fi

if [[ "${DO_BUILD}" == "1" ]]; then
  echo "== ns2.0 probe: build the shipped netstandard2.0 asset =="
  rm -rf "${WORK}/ns20" "${WORK}/consumer" "${WORK}/net472"
  dotnet build "${ROOT}/src/Ashlar.Certification.Contracts/Ashlar.Certification.Contracts.csproj" \
    -c Release -f netstandard2.0 -o "${WORK}/ns20" --nologo -v minimal

  echo "== ns2.0 probe: author the net472 consumer =="
  mkdir -p "${WORK}/consumer"
  # The consumer lives OUTSIDE the repo on purpose: inside it, Directory.Build.props and
  # central package management would rewrite its graph, and the point is to model what an
  # external consumer of the published package resolves.
  cat > "${WORK}/consumer/Ns20CanonicalBytesProbe.csproj" <<PROJ
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net472</TargetFramework>
    <LangVersion>12.0</LangVersion>
    <Nullable>disable</Nullable>
    <AssemblyName>Ns20CanonicalBytesProbe</AssemblyName>
    <RootNamespace>Ns20CanonicalBytesProbe</RootNamespace>
    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
    <GenerateDocumentationFile>false</GenerateDocumentationFile>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="System.Text.Json" Version="${STJ_VERSION}" />
    <PackageReference Include="Microsoft.NETFramework.ReferenceAssemblies" Version="1.0.3" PrivateAssets="all" />
  </ItemGroup>
  <ItemGroup>
    <Reference Include="Ashlar.Certification.Contracts">
      <HintPath>${WORK}/ns20/Ashlar.Certification.Contracts.dll</HintPath>
    </Reference>
  </ItemGroup>
  <ItemGroup>
    <!-- The same shims the netstandard2.0 asset itself compiles against, so a consumer can see
         required members and init-only setters instead of failing with CS0656. -->
    <Compile Include="${ROOT}/src/Ashlar.Compat/Polyfills/IsExternalInit.cs" Link="Polyfills/IsExternalInit.cs" />
    <Compile Include="${ROOT}/src/Ashlar.Compat/Polyfills/RequiredMemberAttribute.cs" Link="Polyfills/RequiredMemberAttribute.cs" />
    <Compile Include="${ROOT}/src/Ashlar.Compat/Polyfills/CompilerFeatureRequiredAttribute.cs" Link="Polyfills/CompilerFeatureRequiredAttribute.cs" />
    <Compile Include="${ROOT}/src/Ashlar.Compat/Polyfills/SetsRequiredMembersAttribute.cs" Link="Polyfills/SetsRequiredMembersAttribute.cs" />
  </ItemGroup>
</Project>
PROJ

  cat > "${WORK}/consumer/Program.cs" <<'PROG'
using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ashlar.Certification.Contracts;

internal static class Ns20CanonicalBytesProbe
{
    private static int Main(string[] args)
    {
        var path = args.Length > 0 ? args[0] : "canonical-payloads.golden.json";
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var failures = 0;
        var checkedCases = 0;

        Console.WriteLine("runtime: " + RuntimeDescription());
        Console.WriteLine("contracts: " + typeof(CertificationRecordSigning).Assembly.Location);

        using (var document = JsonDocument.Parse(File.ReadAllText(path)))
        {
            foreach (var element in document.RootElement.GetProperty("cases").EnumerateArray())
            {
                var name = element.GetProperty("name").GetString();
                var record = JsonSerializer.Deserialize<CertificationRecordData>(
                    element.GetProperty("record").GetRawText(), options);
                var expectedPayload = element.GetProperty("payload").GetString();
                var expectedSha = element.GetProperty("payloadSha256").GetString();

                var payload = CertificationRecordSigning.BuildPayload(record);
                var bytes = Encoding.UTF8.GetBytes(payload);
                string sha;
                using (var hasher = SHA256.Create())
                {
                    sha = BitConverter.ToString(hasher.ComputeHash(bytes)).Replace("-", string.Empty);
                }

                checkedCases++;
                if (sha == expectedSha && payload == expectedPayload)
                {
                    Console.WriteLine("  OK   " + name + "  " + sha + "  " + bytes.Length + " bytes");
                    continue;
                }

                failures++;
                Console.WriteLine("  FAIL " + name);
                Console.WriteLine("    expected sha256 " + expectedSha);
                Console.WriteLine("    actual   sha256 " + sha);
                Console.WriteLine("    expected payload " + expectedPayload);
                Console.WriteLine("    actual   payload " + payload);
            }
        }

        if (checkedCases == 0)
        {
            Console.WriteLine("FAIL: the golden corpus is empty; a probe that checks nothing is not a passing probe.");
            return 1;
        }

        Console.WriteLine(failures == 0
            ? "PASS: " + checkedCases + " canonical payloads are byte-identical on the netstandard2.0 asset."
            : "FAIL: " + failures + " of " + checkedCases + " canonical payloads differ on the netstandard2.0 asset.");
        return failures == 0 ? 0 : 1;
    }

    private static string RuntimeDescription()
    {
        var monoRuntime = Type.GetType("Mono.Runtime");
        if (monoRuntime == null)
        {
            return "not Mono (" + Environment.Version + ")";
        }

        var displayName = monoRuntime.GetMethod("GetDisplayName", BindingFlags.NonPublic | BindingFlags.Static);
        return "Mono " + (displayName == null ? "(unknown)" : (string)displayName.Invoke(null, null));
    }
}
PROG

  echo "== ns2.0 probe: publish the net472 consumer =="
  dotnet publish "${WORK}/consumer/Ns20CanonicalBytesProbe.csproj" -c Release -o "${WORK}/net472" --nologo -v minimal
  cp "${GOLDEN}" "${WORK}/canonical-payloads.golden.json"
fi

if [[ "${DO_RUN}" == "1" ]]; then
  echo "== ns2.0 probe: execute the netstandard2.0 asset under ${MONO_IMAGE} =="
  # Docker on Windows wants a Windows-shaped host path and no MSYS path mangling.
  MOUNT="${WORK}"
  if command -v cygpath >/dev/null 2>&1; then
    MOUNT="$(cygpath -w "${WORK}" | tr '\\' '/')"
  fi
  MSYS_NO_PATHCONV=1 docker run --rm \
    -v "${MOUNT}:/probe:ro" \
    -w /probe/net472 \
    "${MONO_IMAGE}" \
    mono Ns20CanonicalBytesProbe.exe /probe/canonical-payloads.golden.json
fi
