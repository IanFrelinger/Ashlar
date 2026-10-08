#!/usr/bin/env python3
"""Run committed 4.10b twins and mutations inside scripts/test-in-container.sh.

Every mutant changes one exact source fragment, observes test assertion failures,
restores the tracked source, proves a clean tree, and reruns the same twins green.
The harness clone is disposable; logs stream to the caller for durable evidence.
"""
import re
import subprocess
from pathlib import Path

INFRA = "src/Ashlar.Tests.Infrastructure/Ashlar.Tests.Infrastructure.csproj"
CLI = "application/src/Ashlar.Tests.CLI/Ashlar.Tests.CLI.csproj"
FILTER = "FullyQualifiedName~AirGappedSecureWorkstationHygieneCertificationTests|FullyQualifiedName~AirGappedProfileApiHostProdStyleTests|FullyQualifiedName~DeploymentProfileReadConventionTests"
CLI_FILTER = "FullyQualifiedName~MeshServeLoopbackProfileTests"


def run(project=INFRA, filter_text=FILTER, framework="net10.0"):
    command = ["dotnet", "test", project, "--framework", framework, "--filter", filter_text, "--nologo", "-v", "minimal"]
    print("== runner: " + " ".join(command), flush=True)
    result = subprocess.run(command, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    print(result.stdout, flush=True)
    if "Test run for " not in result.stdout:
        raise RuntimeError("build or discovery failure, not a mutation kill")
    summaries = re.findall(r"^\w+!\s+- Failed:\s*(\d+), Passed:\s*(\d+), Skipped:\s*(\d+), Total:\s*(\d+)", result.stdout, re.M)
    if len(summaries) != 1:
        raise RuntimeError("expected exactly one genuine test summary")
    failed, passed, skipped, total = map(int, summaries[0])
    if failed + passed == 0 or failed + passed + skipped != total:
        raise RuntimeError("no executed tests or inconsistent counts")
    return result.returncode, failed, passed, total


def clean():
    status = subprocess.check_output(["git", "status", "--porcelain"], text=True)
    if status:
        raise RuntimeError("source tree not clean: " + status)
    print("== git status --porcelain: empty ==", flush=True)


clean()
sha = subprocess.check_output(["git", "rev-parse", "HEAD"], text=True).strip()
for project, filter_text in [(INFRA, FILTER), (CLI, CLI_FILTER)]:
    rc, failed, passed, total = run(project, filter_text)
    if rc or failed:
        raise RuntimeError("baseline is not green")

rc, failed, passed, total = run(framework="net8.0")
if rc or failed:
    raise RuntimeError("net8.0 baseline is not green")

mutants = [
    ("api-missing-addresses", "application/src/Ashlar.API/Security/LoopbackListenerVerifier.cs", "if (_profile?.Value.RequiresLoopback == true && (addresses is null || addresses.Count == 0))", "if (_profile?.Value.RequiresLoopback == true && addresses is { Count: < 0 })", INFRA),
    ("loopback-http-ports", "src/Ashlar.Infrastructure/AshlarInboundListenerPolicy.cs", '            AddPorts("http", configuration["http_ports"]);\n            AddPorts("https", configuration["https_ports"]);', "            // mutation: ignore port-only binding", INFRA),
    ("api-prebind", "application/src/Ashlar.API/Program.cs", 'AshlarInboundListenerPolicy.CollectEndpoints(\n        builder.Configuration,\n        builder.WebHost.GetSetting(WebHostDefaults.ServerUrlsKey))', "Array.Empty<string>()", INFRA),
    ("api-postbind", "application/src/Ashlar.API/Security/LoopbackListenerVerifier.cs", "var violation = AshlarInboundListenerPolicy.Refusal(_profile?.Value, addresses);", "string? violation = null;", INFRA),
    ("api-verifier-wiring", "application/src/Ashlar.API/Program.cs", "builder.Services.AddHostedService<Ashlar.API.Security.LoopbackListenerVerifier>();", "// mutation: omit post-bind verifier", INFRA),
    ("loopback-ipv6", "src/Ashlar.Infrastructure/AshlarInboundListenerPolicy.cs", "return IsLoopbackAddress(literal);", "return false;", INFRA),
    ("listener-scheme", "src/Ashlar.Infrastructure/AshlarInboundListenerPolicy.cs", ' || uri.Scheme is not ("http" or "https")', "", INFRA),
    ("adaptive-multiframe", "src/Ashlar.Infrastructure/Execution/AdaptiveProviderFactory.cs", " || (IsAirGapped && !IsLocalProvider(resolved))", "", INFRA),
    ("profile-postconfigure", "src/Ashlar.Hosting/AshlarServiceCollectionExtensions.cs", ".PostConfigure(resolved => resolved.Profile = notedProfile)", ".PostConfigure(resolved => { })", INFRA),
    ("d5-strictest", "src/Ashlar.Hosting/AshlarServiceCollectionExtensions.cs", "var notedProfile = AshlarDeploymentProfileEnvironment.Effective(canonicalProfile) ?? canonicalProfile;", "var notedProfile = canonicalProfile;", INFRA),
    ("mcp-direct-http", "src/Ashlar.Mcp.Server/ValidateAshlarMcpServerOptions.cs", "services.IsService(handler)", "false", INFRA),
    ("validator-bedrock", "src/Ashlar.AI.Pipeline/ValidateAirGappedMeaiBedrockOptions.cs", "_profile.Value.IsAirGapped && options.Bedrock.Enabled", "!_profile.Value.IsAirGapped && options.Bedrock.Enabled", INFRA),
    ("validator-brickhost", "src/Ashlar.Infrastructure/Execution/ValidateAirGappedBrickHostOptions.cs", "if (!_profile.Value.IsAirGapped)", "if (_profile.Value.IsAirGapped)", INFRA),
    ("validator-meshlab", "src/Ashlar.Infrastructure/MeshLab/ValidateAirGappedMeshLabWorkerOptions.cs", "_profile.Value.IsAirGapped && options.Enabled", "!_profile.Value.IsAirGapped && options.Enabled", INFRA),
    ("router-peer-refusal", "src/Ashlar.Infrastructure/Execution/Routing/NcrCapabilityRouter.cs", "throw new InvalidOperationException(refused);", "return ResolveRemoteTarget(requirements, refused);", INFRA),
    ("profile-reader-convention", "src/Ashlar.Infrastructure/Execution/AdaptiveProviderFactory.cs", "_inner = inner ?? throw new ArgumentNullException(nameof(inner));", '_inner = inner ?? throw new ArgumentNullException(nameof(inner));\n        _ = Environment.GetEnvironmentVariable("ASHLAR_DEPLOYMENT_PROFILE");', INFRA),
    ("mcp-callers-convention", "application/src/Ashlar.API/Program.cs", ".WithAshlarHttpTransport()", ".WithHttpTransport()", INFRA),
    ("mesh-anyip", "application/src/Ashlar.CLI/Commands/BackgroundAgent/MeshServeService.cs", "ListenLoopback(k, ConfigureListen);", "k.ListenAnyIP(_settings.Port, ConfigureListen);", CLI),
    ("mesh-refusal", "application/src/Ashlar.CLI/Commands/BackgroundAgent/MeshServeService.cs", "if (loopbackRefusal is not null)", 'if (loopbackRefusal == "mutation")', CLI),
]

for ident, filename, old, new, project in mutants:
    clean()
    path = Path(filename)
    original = path.read_text()
    if original.count(old) != 1:
        raise RuntimeError(ident + ": replacement does not match exactly once")
    try:
        path.write_text(original.replace(old, new))
        print("== applied mutation " + ident + " ==", flush=True)
        subprocess.run(["git", "diff", "--", filename], check=True)
        filter_text = CLI_FILTER if project == CLI else FILTER
        rc, red_failed, red_passed, red_total = run(project, filter_text)
        if not rc or not red_failed:
            raise RuntimeError(ident + ": SURVIVED or invalid red result")
    finally:
        subprocess.run(["git", "checkout", "--", filename], check=True)
    clean()
    rc, green_failed, green_passed, green_total = run(project, filter_text)
    if rc or green_failed:
        raise RuntimeError(ident + ": restored source is not green")
    print(f"mutation {ident}: KILLED red=failed:{red_failed}/{red_total} green=passed:{green_passed}/{green_total} ref={sha}", flush=True)
