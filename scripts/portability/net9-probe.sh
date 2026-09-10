#!/usr/bin/env bash
# net9-probe.sh — EXECUTE the shipped Ashlar.Certification.Contracts package on the .NET 9
# runtime and check that it mints, verifies and serializes exactly as net8.0/net10.0 do.
#
# WHY THIS LANE EXISTS, AND WHY IT EXECUTES INSTEAD OF COMPILING
# ----------------------------------------------------------------
# The package ships lib/netstandard2.0, lib/net8.0 and lib/net10.0. A net9.0 consumer has no
# asset of its own, so NuGet binds it to lib/net8.0 — an asset that was built, tested and
# gated on the 8.0 and 10.0 runtimes and never once on 9.0. Nothing in CI pins a 9.0.x
# runtime; this script is the only place one is exercised. A compile-only leg would prove
# nothing: the ns2.0 asset was compiled in three CI places and that caught nothing until
# scripts/ns20-canonical-bytes-probe.sh actually RAN it. So this probe builds a consumer
# OUTSIDE the repo against the packed nupkg (so NuGet's asset selection is what gets
# measured, not a ProjectReference), then runs it on a real 9.0.x runtime and checks:
#   - the process really is on 9.x (a silent roll-forward onto 10.x would test nothing new);
#   - the contracts and NSec assemblies resolved from lib/net8.0 (the asset-selection claim);
#   - all canonical signing payloads are byte-identical to the checked-in golden corpus;
#   - a v2 record mints and verifies with HMAC AND Ed25519 (NSec's net8.0 asset and its
#     libsodium native load on 9.0 — measured, not assumed);
#   - Default / Strict / pinned verdicts are TRUSTED and every tamper path returns the same
#     failure code net8.0 and net10.0 return.
#
# THE CONSTANTS ARE NOT DUPLICATED. This probe is the third reader of
# src/Ashlar.Tests.Infrastructure/Tests/Certification/canonical-payloads.golden.json, next to
# CanonicalPayloadGoldenTests and the ns2.0 probe. One constant, three readers.
#
# Usage:
#   scripts/portability/net9-probe.sh                  # build + run locally (needs a 9.0.x runtime)
#   scripts/portability/net9-probe.sh --build-only     # pack + publish the consumer, do not run
#   scripts/portability/net9-probe.sh --run-only       # run an already-built consumer locally
#   scripts/portability/net9-probe.sh --run-in-docker  # run an already-built consumer in the
#                                                      # mcr.microsoft.com/dotnet/sdk:9.0 image,
#                                                      # for hosts that have no 9.0 runtime
#
# NET9_PROBE_WORKDIR pins the scratch directory, which is what makes the build/run split usable
# when the host that has the SDK and the host that has the 9.0 runtime are not the same one.
# The build side needs the SDK from global.json (10.x) plus network access for the net9.0
# targeting pack; the run side needs only a Microsoft.NETCore.App 9.0.x runtime.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
GOLDEN="${ROOT}/src/Ashlar.Tests.Infrastructure/Tests/Certification/canonical-payloads.golden.json"
VERSION="${NET9_PROBE_VERSION:-9.9.9-net9probe}"
SDK9_IMAGE="${NET9_PROBE_SDK9_IMAGE:-mcr.microsoft.com/dotnet/sdk:9.0}"

DO_BUILD=1
DO_RUN=1
RUN_IN_DOCKER=0
case "${1:-}" in
  --build-only) DO_RUN=0 ;;
  --run-only) DO_BUILD=0 ;;
  --run-in-docker) DO_BUILD=0; RUN_IN_DOCKER=1 ;;
  "") ;;
  *) echo "net9-probe: unknown argument ${1}" >&2; exit 2 ;;
esac

WORK="${NET9_PROBE_WORKDIR:-}"
if [[ -z "${WORK}" ]]; then
  WORK="$(mktemp -d)"
  trap 'rm -rf "${WORK}"' EXIT
fi
mkdir -p "${WORK}"
LOG="${WORK}/net9-probe.log"

if [[ ! -f "${GOLDEN}" ]]; then
  echo "net9-probe: golden corpus not found at ${GOLDEN}" >&2
  exit 1
fi

if [[ "${DO_BUILD}" == "1" ]]; then
  echo "== net9 probe: pack Ashlar.Certification.Contracts with the SDK from global.json =="
  rm -rf "${WORK}/feed" "${WORK}/consumer" "${WORK}/net9" "${LOG}"
  # pack, not build: the consumer must restore the nupkg so that NuGet's nearest-asset choice
  # (net9.0 -> lib/net8.0) is what the lane measures. A ProjectReference would skip that step.
  dotnet pack "${ROOT}/src/Ashlar.Certification.Contracts/Ashlar.Certification.Contracts.csproj" \
    -c Release -o "${WORK}/feed" -p:PackageVersion="${VERSION}" --nologo -v minimal
  NUPKG="${WORK}/feed/Ashlar.Certification.Contracts.${VERSION}.nupkg"
  if [[ ! -f "${NUPKG}" ]]; then
    echo "net9-probe: pack produced no ${NUPKG}" >&2
    exit 1
  fi

  echo "== net9 probe: author the net9.0 consumer =="
  mkdir -p "${WORK}/consumer"
  # The consumer lives OUTSIDE the repo on purpose: inside it, Directory.Build.props/.targets and
  # central package management would rewrite its graph (and RollForward=Major would hide the
  # runtime under test). The point is to model what an external consumer of the published
  # package resolves and runs on.
  cat > "${WORK}/consumer/nuget.config" <<CONF
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="ashlar-local" value="${WORK}/feed" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
  </packageSources>
</configuration>
CONF

  cat > "${WORK}/consumer/Net9Probe.csproj" <<PROJ
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net9.0</TargetFramework>
    <LangVersion>12.0</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AssemblyName>Net9Probe</AssemblyName>
    <RootNamespace>Net9Probe</RootNamespace>
    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
    <GenerateDocumentationFile>false</GenerateDocumentationFile>
    <!-- No RollForward and no TreatWarningsAsErrors on purpose. An external consumer's default
         (Minor) must land on a 9.0.x runtime even when 10.0.x is installed beside it, and a
         future SDK's end-of-support warning for net9.0 must not turn this lane red for a
         reason that has nothing to do with portability. -->
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Ashlar.Certification.Contracts" Version="${VERSION}" />
  </ItemGroup>
</Project>
PROJ

  cat > "${WORK}/consumer/Program.cs" <<'PROG'
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ashlar.Certification.Contracts;

internal static class Net9Probe
{
    private const string HmacKey = "net9-probe-hmac-key";
    private const string BrickSource = "class Net9ProbeBrick { }";

    private static int Main(string[] args)
    {
        var path = args.Length > 0 ? args[0] : "canonical-payloads.golden.json";
        Console.WriteLine("runtime:   " + RuntimeInformation.FrameworkDescription + " (Environment.Version=" + Environment.Version + ")");
        var contracts = typeof(CertificationRecordSigning).Assembly;
        Console.WriteLine("contracts: " + contracts.Location + "  [" + Tfm(contracts) + "]");
        var failures = 0;

        // The runtimeconfig asks for 9.0 and the default roll-forward policy is Minor, so a
        // 10.x runtime beside it must NOT be selected. If it were, this lane would re-test
        // what cert-gate already covers and prove nothing about 9.
        if (Environment.Version.Major != 9)
        {
            Console.WriteLine("FAIL: expected to execute on the .NET 9 runtime, got major " + Environment.Version.Major);
            failures++;
        }

        failures += CheckCanonicalBytes(path);
        failures += CheckMintAndVerify();
        Console.WriteLine(failures == 0 ? "PASS: net9.0 probe" : "FAIL: net9.0 probe (" + failures + " failures)");
        return failures == 0 ? 0 : 1;
    }

    private static string Tfm(Assembly a) =>
        a.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName ?? "(no TargetFrameworkAttribute)";

    // Same loop as scripts/ns20-canonical-bytes-probe.sh: the payload string, its SHA-256 and
    // its UTF-8 byte length must all match the checked-in corpus.
    private static int CheckCanonicalBytes(string path)
    {
        Console.WriteLine("== canonical bytes vs golden corpus ==");
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var failures = 0;
        var checkedCases = 0;
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var element in document.RootElement.GetProperty("cases").EnumerateArray())
        {
            var name = element.GetProperty("name").GetString();
            var record = JsonSerializer.Deserialize<CertificationRecordData>(element.GetProperty("record").GetRawText(), options)!;
            var expectedPayload = element.GetProperty("payload").GetString();
            var expectedSha = element.GetProperty("payloadSha256").GetString();
            var expectedLen = element.GetProperty("payloadByteLength").GetInt32();
            var payload = CertificationRecordSigning.BuildPayload(record);
            var bytes = Encoding.UTF8.GetBytes(payload);
            var sha = Convert.ToHexString(SHA256.HashData(bytes));
            checkedCases++;
            if (sha == expectedSha && payload == expectedPayload && bytes.Length == expectedLen)
            {
                Console.WriteLine("  OK   " + name + "  " + sha + "  " + bytes.Length + " bytes");
                continue;
            }

            failures++;
            Console.WriteLine("  FAIL " + name);
            Console.WriteLine("    expected sha256 " + expectedSha + " (" + expectedLen + " bytes)");
            Console.WriteLine("    actual   sha256 " + sha + " (" + bytes.Length + " bytes)");
            Console.WriteLine("    expected payload " + expectedPayload);
            Console.WriteLine("    actual   payload " + payload);
        }

        if (checkedCases == 0)
        {
            Console.WriteLine("FAIL: the golden corpus is empty; a probe that checks nothing is not a passing probe.");
            failures++;
        }

        return failures;
    }

    private static int CheckMintAndVerify()
    {
        Console.WriteLine("== mint + verify on this runtime ==");
        var failures = 0;

        // Ed25519 first: does the net8.0 NSec asset (and its libsodium native) load on 9.0?
        // Nothing below is meaningful if it does not, so that is a hard stop, not a skip.
        string edPublicKey;
        var privateKey = RandomNumberGenerator.GetBytes(32);
        try
        {
            edPublicKey = CertificationRecordEd25519.DerivePublicKeyBase64(privateKey);
            var nsecName = typeof(CertificationRecordEd25519).Assembly.GetReferencedAssemblies().First(a => a.Name == "NSec.Cryptography");
            var nsec = Assembly.Load(nsecName);
            Console.WriteLine("nsec:      " + nsec.Location + "  [" + Tfm(nsec) + "]");
        }
        catch (Exception ex)
        {
            Console.WriteLine("  FAIL ed25519 unavailable on this runtime: " + ex.GetType().Name + ": " + ex.Message);
            return failures + 1;
        }

        var unsigned = new CertificationRecordData
        {
            Status = "PASS",
            Stage = "S0-S3",
            Admitted = true,
            Signed = true,
            Timestamp = new DateTimeOffset(2026, 9, 10, 0, 0, 0, TimeSpan.Zero),
            BrickId = "net9-probe-brick",
            ContentHash = BrickContentHasher.ComputeSha256(BrickSource),
            SchemaVersion = CertificationRecordData.TrustLoopSchemaVersion,
            Gate = "Net9Probe.Gate",
            GatesPassed = new[] { new CertificationGatePass { Name = "mutation-gate", Version = "1" } },
            Inputs = new[]
            {
                new CertificationInput { Kind = CertificationInputKinds.CertifierIdentity, Id = "net9-probe", Hash = BrickContentHasher.ComputeSha256("judge") },
                new CertificationInput { Kind = CertificationInputKinds.GateEmittedArtifact, Id = "Net9ProbeBrick.dll", Hash = BrickContentHasher.ComputeSha256(new byte[] { 1, 2, 3 }) },
            },
            Ed25519PublicKey = edPublicKey,
        };
        var edSig = CertificationRecordEd25519.Sign(unsigned, privateKey);
        var hmac = CertificationRecordSigning.Sign(unsigned, HmacKey);
        var minted = unsigned with { Signature = hmac, Ed25519Signature = edSig };

        failures += Expect("hmac VerifySignature", CertificationRecordSigning.VerifySignature(minted, HmacKey), true);
        failures += Expect("ed25519 VerifySignature", CertificationRecordEd25519.VerifySignature(minted), true);
        failures += ExpectVerdict("Default", CertificationTrustVerifier.Verify(minted, BrickSource, HmacKey, CertificationVerifyOptions.Default), null);
        failures += ExpectVerdict("Strict", CertificationTrustVerifier.Verify(minted, BrickSource, HmacKey, CertificationVerifyOptions.Strict), null);
        failures += ExpectVerdict("Strict + artifact bytes", CertificationTrustVerifier.Verify(minted, BrickSource, new byte[] { 1, 2, 3 }, HmacKey, CertificationVerifyOptions.Strict), null);
        failures += ExpectVerdict("pinned to minter", CertificationTrustVerifier.Verify(minted, BrickSource, HmacKey,
            new CertificationVerifyOptions { MinimumSchemaVersion = 2, TrustedEd25519PublicKeys = new[] { edPublicKey } }), null);

        // Tamper paths must refuse with the same codes net8.0/net10.0 report.
        var forged = Convert.FromBase64String(edSig);
        forged[0] ^= 0x01;
        failures += ExpectVerdict("ed25519 tampered", CertificationTrustVerifier.Verify(minted with { Ed25519Signature = Convert.ToBase64String(forged) }, BrickSource, HmacKey, CertificationVerifyOptions.Default), "ed25519-signature-invalid");
        failures += ExpectVerdict("ed25519 stripped", CertificationTrustVerifier.Verify(minted with { Ed25519Signature = null }, BrickSource, HmacKey, CertificationVerifyOptions.Default), "ed25519-signature-required");
        failures += ExpectVerdict("hmac wrong key", CertificationTrustVerifier.Verify(minted, BrickSource, "other-key", CertificationVerifyOptions.Default), "signature-invalid");
        failures += ExpectVerdict("content mismatch", CertificationTrustVerifier.Verify(minted, BrickSource + " ", HmacKey, CertificationVerifyOptions.Default), "content-hash-mismatch");
        failures += ExpectVerdict("pinned to other key", CertificationTrustVerifier.Verify(minted, BrickSource, HmacKey,
            new CertificationVerifyOptions { MinimumSchemaVersion = 2, TrustedEd25519PublicKeys = new[] { Convert.ToBase64String(new byte[32]) } }), "ed25519-key-not-trusted");

        // Legacy v1 HMAC-only record: the lane every target can evaluate, and the one the
        // Default floor must refuse.
        var legacy = new CertificationRecordData
        {
            Status = "PASS",
            Stage = "S0-S2",
            Admitted = true,
            Signed = true,
            Timestamp = new DateTimeOffset(2026, 9, 10, 0, 0, 0, TimeSpan.Zero),
            BrickId = "net9-probe-legacy",
            ContentHash = BrickContentHasher.ComputeSha256(BrickSource),
        };
        legacy = legacy with { Signature = CertificationRecordSigning.Sign(legacy, HmacKey) };
        failures += ExpectVerdict("legacy v1 under Legacy", CertificationTrustVerifier.Verify(legacy, BrickSource, HmacKey, CertificationVerifyOptions.Legacy), null);
        failures += ExpectVerdict("legacy v1 under Default", CertificationTrustVerifier.Verify(legacy, BrickSource, HmacKey, CertificationVerifyOptions.Default), "schema-version-below-floor");
        return failures;
    }

    private static int Expect(string what, bool actual, bool expected)
    {
        Console.WriteLine((actual == expected ? "  OK   " : "  FAIL ") + what + " = " + actual);
        return actual == expected ? 0 : 1;
    }

    private static int ExpectVerdict(string what, CertificationTrustResult result, string? expectedCode)
    {
        var ok = expectedCode is null ? result.Trusted : (!result.Trusted && result.FailureCode == expectedCode);
        Console.WriteLine((ok ? "  OK   " : "  FAIL ") + what + " -> " + (result.Trusted ? "TRUSTED" : result.FailureCode + " (" + result.Reason + ")"));
        return ok ? 0 : 1;
    }
}
PROG

  echo "== net9 probe: publish the net9.0 consumer against the packed feed =="
  dotnet publish "${WORK}/consumer/Net9Probe.csproj" -c Release -f net9.0 -o "${WORK}/net9" --nologo -v minimal

  echo "== net9 probe: check the asset NuGet bound for net9.0 =="
  ASSETS="${WORK}/consumer/obj/project.assets.json"
  DEPS="${WORK}/net9/Net9Probe.deps.json"
  # The nupkg must still ship all three asset groups (project.assets.json lists the package's
  # files), and the consumer's deps.json must show that net9.0 was bound to lib/net8.0 for
  # both the contracts and NSec. That binding IS the claim the runtime run then exercises.
  for tfm in netstandard2.0 net8.0 net10.0; do
    if ! grep -q "\"lib/${tfm}/Ashlar.Certification.Contracts.dll\"" "${ASSETS}"; then
      echo "net9-probe: the packed nupkg does not ship lib/${tfm}/Ashlar.Certification.Contracts.dll" >&2
      exit 1
    fi
  done
  for dll in Ashlar.Certification.Contracts NSec.Cryptography; do
    if ! grep -q "\"lib/net8.0/${dll}.dll\"" "${DEPS}"; then
      echo "net9-probe: ${DEPS} does not bind ${dll} from lib/net8.0 — the net9.0 asset selection changed" >&2
      exit 1
    fi
    echo "  bound lib/net8.0/${dll}.dll"
  done
  cp "${GOLDEN}" "${WORK}/canonical-payloads.golden.json"
fi

if [[ "${DO_RUN}" == "1" ]]; then
  if [[ ! -f "${WORK}/net9/Net9Probe.dll" ]]; then
    echo "net9-probe: no built consumer under ${WORK}/net9 — run --build-only first (same NET9_PROBE_WORKDIR)" >&2
    exit 1
  fi
  if [[ "${RUN_IN_DOCKER}" == "1" ]]; then
    echo "== net9 probe: execute the net8.0 asset on the 9.0 runtime in ${SDK9_IMAGE} =="
    # Docker on Windows wants a Windows-shaped host path and no MSYS path mangling.
    MOUNT="${WORK}"
    if command -v cygpath >/dev/null 2>&1; then
      MOUNT="$(cygpath -w "${WORK}" | tr '\\' '/')"
    fi
    MSYS_NO_PATHCONV=1 docker run --rm \
      -v "${MOUNT}:/probe:ro" \
      -e DOTNET_NOLOGO=1 -e DOTNET_CLI_TELEMETRY_OPTOUT=1 \
      "${SDK9_IMAGE}" \
      dotnet /probe/net9/Net9Probe.dll /probe/canonical-payloads.golden.json | tee "${LOG}"
  else
    echo "== net9 probe: execute the net8.0 asset on the local 9.0 runtime =="
    if ! dotnet --list-runtimes | grep -q '^Microsoft.NETCore.App 9\.'; then
      echo "net9-probe: no Microsoft.NETCore.App 9.x runtime is installed; install one (setup-dotnet 9.0.x) or use --run-in-docker" >&2
      dotnet --list-runtimes >&2
      exit 1
    fi
    # The consumer's runtimeconfig asks for 9.0 with the default (Minor) roll-forward, so a
    # 10.x runtime installed beside it is not selected; the consumer asserts the major in-process.
    dotnet "${WORK}/net9/Net9Probe.dll" "${WORK}/canonical-payloads.golden.json" | tee "${LOG}"
  fi
fi
