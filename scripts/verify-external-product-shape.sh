#!/usr/bin/env bash
# Verifies the external product consumption shape: authored brick + thin host + HTTP client,
# restoring only from a temp local Ashlar.* feed (+ nuget.org) with no repo project references.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
TEMPLATE_DIR="${ROOT}/consumer-template/host"
VERSION="${ASHLAR_EXTERNAL_PRODUCT_VERIFY_VERSION:-9.9.9-local}"
WORK="${ASHLAR_EXTERNAL_PRODUCT_VERIFY_WORK:-$(mktemp -d)}"
FEED="${WORK}/feed"
TOOL_PATH="${WORK}/tools"
CONSUMER_ROOT="${WORK}/consumer"
CFG="${WORK}/NuGet.Config"
HOST_PORT="${ASHLAR_EXTERNAL_PRODUCT_VERIFY_PORT:-0}"
WAIT_SECS="${ASHLAR_EXTERNAL_PRODUCT_VERIFY_WAIT_SECS:-120}"
SOURCE_KEY="${ASHLAR_VERIFY_SOURCE_KEY:-ashlar-local}"
ISOL_CLEANUP=""
USE_PUBLISHED_FEED=0
PROBE_BRICK_SOURCE="${ASHLAR_EXTERNAL_PRODUCT_PROBE_BRICK_SOURCE:-}"
if [[ -n "${ASHLAR_EXTERNAL_PRODUCT_PROBE_BRICK:-}" && -z "${PROBE_BRICK_SOURCE}" ]]; then
  PROBE_BRICK_SOURCE="${ROOT}/spikes/portability/generated/ErrorSummaryExtractorBrick"
fi
USE_PROBE_BRICK=0
if [[ -n "${PROBE_BRICK_SOURCE}" ]]; then
  USE_PROBE_BRICK=1
fi

if [[ -n "${ASHLAR_EXTERNAL_PRODUCT_PACKAGE_FEED:-}" ]]; then
  USE_PUBLISHED_FEED=1
  FEED="${ASHLAR_EXTERNAL_PRODUCT_PACKAGE_FEED}"
fi

mkdir -p "${TOOL_PATH}" "${CONSUMER_ROOT}"
if [[ "${USE_PUBLISHED_FEED}" -eq 0 ]]; then
  mkdir -p "${FEED}"
fi

pack() {
  local project="$1"
  echo "==> dotnet pack ${project}"
  dotnet pack "${ROOT}/${project}" \
    -c Release \
    -o "${FEED}" \
    -p:PackageVersion="${VERSION}" \
    -p:IncludeTestProjectReferences=false \
    -v minimal
}

render() {
  # The host is a checked-in template rather than a heredoc so that consumer-template/host/ is a
  # thing a reader can open, compile and diff. The __TOKEN__ markers are legal C# identifiers and
  # legal MSBuild text, so the template still parses as the file it is a template for.
  local src="$1"
  local dst="$2"
  sed -e "s|__ASHLAR_VERSION__|${VERSION}|g" \
      -e "s|__BRICK_PROJECT_NAME__|${BRICK_PROJECT_NAME}|g" \
      -e "s|__BRICK_NAMESPACE__|${BRICK_NAMESPACE}|g" \
      -e "s|__BRICK_CLASS__|${BRICK_CLASS}|g" \
      "${src}" > "${dst}"
  # The generated-tree guard further down only greps for repo-relative paths, so a token that
  # nobody substituted would sail past it and resurface as a compile error a hundred lines later.
  if grep -q '__[A-Z_]\+__' "${dst}"; then
    echo "Unsubstituted template token in ${dst}:" >&2
    grep -n '__[A-Z_]\+__' "${dst}" >&2
    exit 1
  fi
}

echo "==> Packing consumer surface as version ${VERSION} into ${FEED}"
if [[ "${USE_PUBLISHED_FEED}" -eq 1 ]]; then
  echo "==> Using published feed at ${FEED}; skipping local pack."
else
bash "${ROOT}/scripts/pack-ashlar-hosting-graph.sh" "${VERSION}" "${FEED}"
pack src/Ashlar.Authoring/Ashlar.Authoring.csproj
pack src/Ashlar.Sdk/Ashlar.Sdk.csproj
pack src/Ashlar.Client/Ashlar.Client.csproj

# CLI + dependencies for `ashlar new brick` (same extras as verify-standalone-brick-authoring.sh).
if [[ "${USE_PROBE_BRICK}" -eq 0 ]]; then
pack src/Ashlar.Adapters.Models/Ashlar.Adapters.Models.csproj
pack src/Ashlar.Bricks.Owasp/Ashlar.Bricks.Owasp.csproj
pack src/Ashlar.BackgroundAgents.HostRunners/Ashlar.BackgroundAgents.HostRunners.csproj
pack src/Ashlar.Policies.Dev/Ashlar.Policies.Dev.csproj
pack application/src/Ashlar.CLI/Ashlar.CLI.csproj
fi
fi

if [[ -z "${ASHLAR_EXTERNAL_PRODUCT_VERIFY_NO_ISOLATED_CACHE:-}" ]]; then
  ISOL_BASE="${ASHLAR_EXTERNAL_PRODUCT_VERIFY_ISOLATED_ROOT:-$(mktemp -d "${WORK}/nuget-cache-XXXXXX")}"
  ISOL_CLEANUP="${ISOL_BASE}"
  mkdir -p "${ISOL_BASE}/packages" "${ISOL_BASE}/cli-home"
  export NUGET_PACKAGES="${ISOL_BASE}/packages"
  export DOTNET_CLI_HOME="${ISOL_BASE}/cli-home"
  echo "Isolated restore: NUGET_PACKAGES=${NUGET_PACKAGES} DOTNET_CLI_HOME=${DOTNET_CLI_HOME}"
fi

if [[ -n "${ASHLAR_NUGET_USERNAME:-}" && -n "${ASHLAR_NUGET_PASSWORD:-}" ]]; then
  cat > "${CFG}" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="${SOURCE_KEY}" value="${FEED}" protocolVersion="3" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
  </packageSources>
  <packageSourceCredentials>
    <${SOURCE_KEY}>
      <add key="Username" value="${ASHLAR_NUGET_USERNAME}" />
      <add key="ClearTextPassword" value="${ASHLAR_NUGET_PASSWORD}" />
    </${SOURCE_KEY}>
  </packageSourceCredentials>
</configuration>
EOF
else
  cat > "${CFG}" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="${SOURCE_KEY}" value="${FEED}" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
  </packageSources>
</configuration>
EOF
fi

if [[ "${USE_PROBE_BRICK}" -eq 0 ]]; then
dotnet tool install \
  --tool-path "${TOOL_PATH}" \
  Ashlar.CLI \
  --version "${VERSION}" \
  --add-source "${FEED}" \
  --ignore-failed-sources
fi

BRICK_OUT="${CONSUMER_ROOT}/brick"
mkdir -p "${BRICK_OUT}"

BRICK_ID="intensity"
BRICK_CLASS="IntensityBrick"
BRICK_NAMESPACE="IntensityBrick"
BRICK_PROJECT_NAME="IntensityBrick"
BRICK_PROJECT_DIR="${BRICK_OUT}/IntensityBrick"

if [[ "${USE_PROBE_BRICK}" -eq 1 ]]; then
  BRICK_ID="error-summary-extractor"
  BRICK_CLASS="ErrorSummaryExtractorBrick"
  BRICK_NAMESPACE="ErrorSummaryExtractorBrick"
  BRICK_PROJECT_NAME="ErrorSummaryExtractorBrick"
  BRICK_PROJECT_DIR="${BRICK_OUT}/ErrorSummaryExtractorBrick"
  if [[ ! -f "${PROBE_BRICK_SOURCE}/ErrorSummaryExtractorBrick.cs" ]]; then
    echo "Probe brick source not found at ${PROBE_BRICK_SOURCE}" >&2
    exit 1
  fi
  echo "==> Copying generated probe brick from ${PROBE_BRICK_SOURCE}"
  mkdir -p "${BRICK_PROJECT_DIR}"
  cp "${PROBE_BRICK_SOURCE}/ErrorSummaryExtractorBrick.cs" "${BRICK_PROJECT_DIR}/ErrorSummaryExtractorBrick.cs"
  cat > "${BRICK_PROJECT_DIR}/ErrorSummaryExtractorBrick.csproj" <<EOF
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Ashlar.Brick.Contracts" Version="${VERSION}" />
    <PackageReference Include="Ashlar.Authoring" Version="${VERSION}" />
  </ItemGroup>
</Project>
EOF
else
echo "==> Scaffolding authored brick via ashlar new brick"
"${TOOL_PATH}/ashlar" new brick Intensity \
  --output "${BRICK_OUT}" \
  --ashlar-version "${VERSION}" \
  --json >/dev/null

INTENSITY_BRICK_CS="${BRICK_OUT}/IntensityBrick/IntensityBrick.cs"
cat > "${INTENSITY_BRICK_CS}" <<'CS'
using Ashlar.Core.Domain.Bricks;
using Ashlar.Core.Domain.Execution;

namespace IntensityBrick;

/// <summary>
/// Throwaway intensity echo/transform brick for external product-shape verification.
/// </summary>
public sealed class IntensityBrick : Brick
{
    public IntensityBrick()
    {
        Id = "intensity";
        Name = "Intensity Brick";
        Version = "1.0.0";
        Icon = "📈";
        Category = BrickCategory.Transform;
        Description = "Deterministic intensity transform (input * 2).";
        Interface = new BrickInterface
        {
            Inputs =
            [
                new BrickInputDefinition("intensity", "int", "Input intensity level")
            ],
            Outputs =
            [
                new BrickOutputDefinition("result", "int", "Transformed intensity")
            ]
        };
    }

    public override Task<BrickOutput> ExecuteAsync(
        BrickInput input,
        ImplementationType implementation,
        IExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var intensity = Convert.ToInt32(input.ToDictionary()["intensity"]);
        var transformed = intensity * 2;
        var output = new BrickOutput
        {
            Summary = $"Transformed intensity {intensity} -> {transformed}"
        };
        output.Set("result", transformed);
        return Task.FromResult(output);
    }
}
CS
fi

HOST_DIR="${CONSUMER_ROOT}/host/ExternalProductHost"
CLIENT_DIR="${CONSUMER_ROOT}/client/ExternalProductClient"
mkdir -p "${HOST_DIR}" "${CLIENT_DIR}"

render "${TEMPLATE_DIR}/ExternalProductHost.csproj" "${HOST_DIR}/ExternalProductHost.csproj"
render "${TEMPLATE_DIR}/Program.cs" "${HOST_DIR}/Program.cs"

cat > "${CLIENT_DIR}/ExternalProductClient.csproj" <<EOF
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Ashlar.Sdk" Version="${VERSION}" />
  </ItemGroup>
</Project>
EOF

if [[ "${USE_PROBE_BRICK}" -eq 1 ]]; then
  cp "${ROOT}/spikes/portability/templates/ExternalProductProbeClient.cs" "${CLIENT_DIR}/Program.cs"
else
cat > "${CLIENT_DIR}/Program.cs" <<'CLIENTCS'
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Ashlar.Client;
using Ashlar.Sdk.Client;

if (args.Length < 1 || string.IsNullOrWhiteSpace(args[0]))
{
    Console.Error.WriteLine("Usage: ExternalProductClient <hostBaseUrl>");
    Environment.Exit(2);
}

var hostBaseUrl = args[0].TrimEnd('/');
var services = new ServiceCollection();
services.AddAshlarClientSdk(hostBaseUrl);
await using var provider = services.BuildServiceProvider();
var client = provider.GetRequiredService<IAshlarClient>();

var requestBody = new Dictionary<string, object?>
{
    ["implementation"] = "Deterministic",
    ["input"] = new Dictionary<string, object> { ["intensity"] = 21 }
};

var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
using var content = JsonContent.Create(requestBody, options: jsonOptions);

using var response = await client.InvokeAsync(
    HttpMethod.Post,
    "api/bricks/intensity/execute",
    content).ConfigureAwait(false);

response.EnsureSuccessStatusCode();
await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
using var doc = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);

if (!doc.RootElement.TryGetProperty("success", out var successEl) || !successEl.GetBoolean())
{
    var error = doc.RootElement.TryGetProperty("error", out var errEl) ? errEl.GetString() : "unknown";
    Console.Error.WriteLine($"Brick execute failed: {error}");
    Environment.Exit(1);
}

if (!doc.RootElement.TryGetProperty("output", out var outputEl) ||
    !outputEl.TryGetProperty("result", out var resultEl) ||
    resultEl.ValueKind != JsonValueKind.Number ||
    resultEl.GetInt32() != 42)
{
    Console.Error.WriteLine($"Unexpected brick output: {doc.RootElement.GetRawText()}");
    Environment.Exit(1);
}

Console.WriteLine("external-product-client: brick round-trip OK (intensity 21 -> result 42)");
CLIENTCS
fi

SLN="${CONSUMER_ROOT}/ExternalProduct.sln"
# SDK 10 defaults `dotnet new sln` to the .slnx format; ask for the classic .sln explicitly so the
# path below (and `dotnet sln add`) keep working.
dotnet new sln -n ExternalProduct -o "${CONSUMER_ROOT}" --force --format sln
dotnet sln "${SLN}" add \
  "${BRICK_PROJECT_DIR}/${BRICK_PROJECT_NAME}.csproj" \
  "${HOST_DIR}/ExternalProductHost.csproj" \
  "${CLIENT_DIR}/ExternalProductClient.csproj"

echo "==> Restoring consumer solution from temp feed + nuget.org only"
dotnet restore "${SLN}" \
  --configfile "${CFG}" \
  --force-evaluate \
  -v minimal

echo "==> Building consumer solution"
dotnet build "${SLN}" \
  -c Release \
  --no-restore \
  -v minimal

if rg "Ashlar\\.Core\\.Domain\\.csproj|/workspace|src/Ashlar" "${CONSUMER_ROOT}" >/dev/null; then
  echo "Generated consumer tree contains repo-relative Ashlar paths." >&2
  rg "Ashlar\\.Core\\.Domain\\.csproj|/workspace|src/Ashlar" "${CONSUMER_ROOT}" >&2
  exit 1
fi

if [[ "${HOST_PORT}" == "0" ]]; then
  HOST_PORT="$(python3 - <<'PY'
import socket
s = socket.socket()
s.bind(("127.0.0.1", 0))
print(s.getsockname()[1])
s.close()
PY
)"
fi

HOST_URL="http://127.0.0.1:${HOST_PORT}"
HOST_LOG="${WORK}/host.log"
HOST_PID=""

cleanup() {
  if [[ -n "${HOST_PID}" ]] && kill -0 "${HOST_PID}" 2>/dev/null; then
    kill "${HOST_PID}" 2>/dev/null || true
    wait "${HOST_PID}" 2>/dev/null || true
  fi
  if [[ -n "${ISOL_CLEANUP}" && -d "${ISOL_CLEANUP}" ]]; then
    rm -rf "${ISOL_CLEANUP}"
  fi
}
trap cleanup EXIT

echo "==> Starting thin host at ${HOST_URL}"
ASPNETCORE_URLS="${HOST_URL}" \
  dotnet run --project "${HOST_DIR}/ExternalProductHost.csproj" \
    -c Release \
    --no-build \
    >"${HOST_LOG}" 2>&1 &
HOST_PID=$!

start_ts=$(date +%s)
until curl -fsS "${HOST_URL}/health" >/dev/null 2>&1; do
  if ! kill -0 "${HOST_PID}" 2>/dev/null; then
    echo "Host process exited before /health became ready." >&2
    cat "${HOST_LOG}" >&2 || true
    exit 1
  fi
  now_ts=$(date +%s)
  if (( now_ts - start_ts > WAIT_SECS )); then
    echo "Timeout waiting for ${HOST_URL}/health after ${WAIT_SECS}s" >&2
    cat "${HOST_LOG}" >&2 || true
    exit 1
  fi
  sleep 1
done

echo "GET ${HOST_URL}/health OK"

echo "==> Running HTTP client against hosted brick"
dotnet run --project "${CLIENT_DIR}/ExternalProductClient.csproj" \
  -c Release \
  --no-build \
  -- "${HOST_URL}"

echo "verify-external-product-shape: OK (${WORK})"
