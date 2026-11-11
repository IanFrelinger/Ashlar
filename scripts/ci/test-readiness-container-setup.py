#!/usr/bin/env python3
"""Hermetic readiness setup, identity and failure-reporting tests; no Docker or host dotnet."""
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

import yaml

ROOT = Path(__file__).resolve().parents[2]
APT = ROOT / "scripts/ci/install-readiness-packages.sh"
IDENTITY = ROOT / "scripts/ci/check-readiness-container.sh"
BUILD = ROOT / "scripts/ci/build-readiness-container.sh"
RUNNER = ROOT / "scripts/ci/run-reported-gates.py"
RUNTIMES = "\n".join(f"{framework} {major}.0.33 [/fixture]"
                     for framework in ("Microsoft.NETCore.App", "Microsoft.AspNetCore.App") for major in (8, 10))


class ReadinessSetupTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.directory = Path(temporary.name)
        self.bin = self.directory / "bin"
        self.bin.mkdir()
        self.calls = self.directory / "calls.jsonl"
        self.env = {**os.environ, "PATH": str(self.bin) + os.pathsep + os.environ["PATH"],
                    "FIXTURE_CALLS": str(self.calls), "FIXTURE_DIR": str(self.directory),
                    "FIXTURE_MODE": "success", "FIXTURE_SDK": "10.0.403", "FIXTURE_RUNTIMES": RUNTIMES}
        self.stub("apt-get", '''
import json,os,sys
from pathlib import Path
args=sys.argv[1:]
with open(os.environ['FIXTURE_CALLS'],'a') as f:f.write(json.dumps(args)+'\\n')
mode=os.environ['FIXTURE_MODE']
if 'update' in args:
 p=Path(os.environ['FIXTURE_DIR'])/'attempts'
 n=int(p.read_text())+1 if p.exists() else 1
 p.write_text(str(n))
 print('index refresh attempt',n,flush=True)
 if mode=='partial':
  print('W: Some index files failed to download',flush=True)
  sys.exit(100 if 'APT::Update::Error-Mode=any' in args else 0)
 if mode=='persistent' or (mode=='transient' and n<3):sys.exit(100)
else:
 print('install invoked',flush=True)
 if mode=='install-failure':sys.exit(37)
''')
        self.stub("timeout", '''
import os,subprocess,sys
print('bounded command:',repr(sys.argv[1:]),flush=True)
if os.environ['FIXTURE_MODE']=='timeout':sys.exit(124)
sys.exit(subprocess.run(sys.argv[3:]).returncode)
''')
        self.stub("sleep", "pass")
        # The real helper cleans apt state only after success. Never touch host apt state.
        self.stub("rm", "print('fixture apt cache cleanup')")
        self.stub("dotnet", '''
import os,sys
print(os.environ['FIXTURE_SDK'] if sys.argv[1]=='--version' else os.environ['FIXTURE_RUNTIMES'])
''')

    def stub(self, name, code):
        target = self.bin / name
        target.write_text(f"#!{sys.executable}\n" + code + "\n")
        target.chmod(0o755)

    def apt(self, mode="success", packages=("curl", "python3")):
        self.env["FIXTURE_MODE"] = mode
        return subprocess.run(["sh", str(APT), *packages], env=self.env, capture_output=True, text=True, timeout=5)

    def invocations(self):
        return [json.loads(line) for line in self.calls.read_text().splitlines()] if self.calls.exists() else []

    def assert_no_install(self):
        self.assertFalse(any("install" in args for args in self.invocations()))

    def test_success_installs_only_after_strict_bounded_index_refresh(self):
        result = self.apt()
        self.assertEqual(result.returncode, 0, result.stderr)
        calls = self.invocations()
        self.assertEqual(len(calls), 2)
        self.assertEqual(calls[0][-1], "update")
        self.assertIn("APT::Update::Error-Mode=any", calls[0])
        self.assertIn("90s", result.stdout)
        self.assertIn("180s", result.stdout)
        self.assertIn("install", calls[1])
        self.assertEqual(calls[1][-2:], ["curl", "python3"])

    def test_transient_update_failure_retries_then_installs(self):
        result = self.apt("transient")
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(sum("update" in args for args in self.invocations()), 3)
        self.assertIn("attempt 3/3", result.stderr)

    def test_exhausted_updates_fail_without_installation(self):
        result = self.apt("persistent")
        self.assertEqual(result.returncode, 100)
        self.assertEqual(len(self.invocations()), 3)
        self.assert_no_install()
        self.assertIn("retries exhausted", result.stderr)

    def test_partial_indexes_are_errors_not_successful_refreshes(self):
        result = self.apt("partial")
        self.assertEqual(result.returncode, 100)
        self.assertEqual(len(self.invocations()), 3)
        self.assert_no_install()
        self.assertIn("Some index files failed", result.stdout)

    def test_stalled_update_is_reported_and_not_retried_or_installed(self):
        result = self.apt("timeout")
        self.assertEqual(result.returncode, 124)
        self.assertIn("stalled; refusing installation", result.stderr)
        self.assertEqual(result.stdout.count("bounded command:"), 1)
        self.assert_no_install()

    def test_installation_failure_preserves_its_exit_status(self):
        result = self.apt("install-failure")
        self.assertEqual(result.returncode, 37)
        self.assertIn("installation failed or stalled (exit 37)", result.stderr)
        self.assertNotIn("fixture apt cache cleanup", result.stdout)

    def test_empty_package_list_is_rejected(self):
        result = self.apt(packages=())
        self.assertEqual(result.returncode, 2)
        self.assertEqual(self.invocations(), [])

    def identity(self, actual="debian", version="12", expected="debian", expected_version="12"):
        release = self.directory / "os-release"
        release.write_text(f'ID="{actual}"\nVERSION_ID="{version}"\n')
        return subprocess.run(["sh", str(IDENTITY), expected, expected_version, str(release)],
                              env=self.env, capture_output=True, text=True, timeout=5)

    def test_actual_distro_sdk_and_runtime_families_are_verified(self):
        result = self.identity()
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("os=debian version=12", result.stdout)
        self.assertIn("sdk=10.0.403", result.stdout)

    def test_ubuntu_cannot_pass_under_the_debian_label(self):
        for version in ("24.04", "12"):
            with self.subTest(version=version):
                # A matching version string cannot compensate for the wrong distro id.
                result = self.identity(actual="ubuntu", version=version)
                self.assertEqual(result.returncode, 3)
                self.assertIn("OS identity mismatch", result.stderr)

    def test_wrong_distro_version_is_refused(self):
        result = self.identity(version="13")
        self.assertEqual(result.returncode, 3)
        self.assertIn("OS identity mismatch", result.stderr)

    def test_sdk8_is_not_sdk10(self):
        self.env["FIXTURE_SDK"] = "8.0.427"
        result = self.identity()
        self.assertEqual(result.returncode, 3)
        self.assertIn("SDK 10 is required", result.stderr)

    def test_each_runtime_family_is_required(self):
        for missing in RUNTIMES.splitlines():
            with self.subTest(missing=missing):
                self.env["FIXTURE_RUNTIMES"] = "\n".join(line for line in RUNTIMES.splitlines() if line != missing)
                result = self.identity()
                self.assertEqual(result.returncode, 3)
                self.assertIn("missing real", result.stderr)

    def build(self, mode="success", timeout=3):
        self.stub("docker", '''
import json,os,sys,time
with open(os.environ['FIXTURE_CALLS'],'a') as f:f.write(json.dumps(sys.argv[1:])+'\\n')
print('docker fixture: '+sys.argv[1],flush=True)
mode=os.environ['FIXTURE_MODE']
if mode=='build-failure' and sys.argv[1]=='build':sys.exit(42)
if mode=='stall':time.sleep(20)
if mode=='wrong-os' and sys.argv[1]=='run':sys.exit(3)
''')
        self.env.update(FIXTURE_MODE=mode, READINESS_DOCKERFILE=".docker/fixture with spaces",
                        READINESS_BUILD_ARG="DOTNET_SDK_VERSION=10.0.403", READINESS_IMAGE="fixture:readiness",
                        READINESS_OS="debian", READINESS_OS_VERSION="12")
        reports = self.directory / "reports"
        result = subprocess.run([sys.executable, str(RUNNER), "--reports", str(reports),
                                 "--timeout", str(timeout), "--budget", str(timeout), str(BUILD)],
                                env=self.env, capture_output=True, text=True, timeout=6)
        report = json.loads((reports / "report.json").read_text())
        row = report["checks"][0]
        return result, report, (reports / row["log"]).read_text()

    def test_successful_build_checks_image_identity_and_preserves_arguments(self):
        result, report, log = self.build()
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(report["status"], "passed")
        calls = self.invocations()
        self.assertEqual(len(calls), 2)
        self.assertIn("--progress=plain", calls[0])
        self.assertIn(".docker/fixture with spaces", calls[0])
        self.assertEqual(calls[1][-3:], ["scripts/ci/check-readiness-container.sh", "debian", "12"])
        self.assertIn("product tests have not started", log)
        self.assertIn("reproduce: READINESS_DOCKERFILE=", log)

    def test_build_failure_reports_real_status_and_retains_log(self):
        result, report, log = self.build("build-failure")
        self.assertEqual(result.returncode, 1)
        self.assertEqual(report["status"], "failed")
        self.assertEqual(report["checks"][0]["exit_code"], 42)
        self.assertEqual(len(self.invocations()), 1)
        self.assertIn("docker fixture: build", log)
        self.assertIn("::error::", result.stdout)

    def test_stalled_build_has_timeout_receipt_and_partial_log(self):
        result, report, log = self.build("stall", timeout=0.2)
        self.assertEqual(result.returncode, 1)
        self.assertEqual(report["checks"][0]["status"], "timed_out")
        self.assertEqual(report["checks"][0]["exit_code"], 124)
        self.assertIn("docker fixture: build", log)

    def test_wrong_os_image_fails_setup_report(self):
        result, report, _ = self.build("wrong-os")
        self.assertEqual(result.returncode, 1)
        self.assertEqual(report["checks"][0]["exit_code"], 3)

    def test_workflow_bounds_setup_and_retains_failure_artifacts(self):
        source = (ROOT / ".github/workflows/full-platform-readiness-gate.yml").read_text()
        workflow = yaml.safe_load(source)
        paths = workflow.get("on", workflow.get(True))["push"]["paths"]
        for script in ("build-readiness-container.sh", "check-readiness-container.sh",
                       "install-readiness-packages.sh", "run-reported-gates.py", "test-readiness-container-setup.py"):
            path = "scripts/ci/" + script
            self.assertIn(path, paths)
            self.assertIn('            "' + path + '"', source)
        job = workflow["jobs"]["container-platform"]
        self.assertLessEqual(job["timeout-minutes"], 45)
        self.assertFalse(job["strategy"]["fail-fast"])
        rows = {row["os_id"]: row for row in job["strategy"]["matrix"]["include"]}
        self.assertEqual(set(rows), {"ubuntu", "debian", "alpine"})
        self.assertEqual(rows["ubuntu"]["os_version"], "24.04")
        self.assertEqual(rows["debian"]["os_version"], "12")
        steps = job["steps"]
        build = next(step for step in steps if step.get("id") == "container-build")
        self.assertIn("scripts/ci/run-reported-gates.py", build["run"])
        self.assertIn("--timeout 1500 --budget 1500", build["run"])
        self.assertIn("container-results/build", build["run"])
        self.assertIn("READINESS_OS", build["env"])
        summary = next(step for step in steps if step.get("name") == "Publish container setup failures and stalls")
        self.assertEqual(summary["if"], "always()")
        upload = next(step for step in steps if step.get("uses", "").startswith("actions/upload-artifact@"))
        self.assertEqual(upload["if"], "always()")
        self.assertEqual(upload["with"]["if-no-files-found"], "error")

    def test_dockerfiles_use_distinct_real_distro_bases_and_shared_strict_setup(self):
        ubuntu = (ROOT / ".docker/Dockerfile.test-caching").read_text()
        debian = (ROOT / ".docker/Dockerfile.test-caching-debian").read_text()
        self.assertIn("FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION}-noble", ubuntu)
        self.assertIn("FROM mcr.microsoft.com/dotnet/sdk:8.0-bookworm-slim", debian)
        self.assertIn("ARG DOTNET_SDK_VERSION=10.0.403", debian)
        self.assertLess(debian.index("--version 8.0.33 --runtime dotnet"), debian.index('--version "${DOTNET_SDK_VERSION}"'))
        for source, distro in ((ubuntu, "ubuntu 24.04"), (debian, "debian 12")):
            self.assertIn("COPY scripts/ci/install-readiness-packages.sh", source)
            self.assertIn("RUN sh /tmp/install-readiness-packages.sh", source)
            self.assertIn("RUN sh scripts/ci/check-readiness-container.sh " + distro, source)


if __name__ == "__main__":
    unittest.main(verbosity=2)
