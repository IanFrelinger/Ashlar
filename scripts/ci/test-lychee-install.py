#!/usr/bin/env python3
"""Offline regressions for required link-check installation and fail-closed execution."""
import os
import io
import tarfile
from pathlib import Path
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]


class LycheeTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.cwd = Path(self.temp.name)
        self.bin = self.cwd / "bin"
        self.bin.mkdir()
        self.env = dict(os.environ, PATH=f"{self.bin}:{os.environ['PATH']}",
                        RUNNER_TEMP=str(self.cwd), GITHUB_PATH=str(self.cwd / "path"),
                        GITHUB_STEP_SUMMARY=str(self.cwd / "summary"),
                        GITHUB_OUTPUT=str(self.cwd / "output"))
        self.stub("uname", '[[ "$1" == -s ]] && echo Linux || echo x86_64')

    def stub(self, name, body):
        path = self.bin / name
        path.write_text("#!/usr/bin/env bash\nset -eu\n" + body + "\n")
        path.chmod(0o755)

    def run_script(self, name):
        return subprocess.run(["bash", str(ROOT / "scripts/ci" / name)],
                              cwd=self.cwd, env=self.env, text=True, capture_output=True)

    def mock_download(self):
        # Match the published v0.24.2 archive layout, using real tar extraction.
        data = b"#!/bin/sh\nexit 0\n"
        with tarfile.open(self.cwd / "fixture.tar.gz", "w:gz") as archive:
            member = tarfile.TarInfo("lychee-x86_64-unknown-linux-gnu/lychee")
            member.size = len(data)
            archive.addfile(member, io.BytesIO(data))
        self.stub("curl", """printf '%s\n' "$@" > "$RUNNER_TEMP/curl-args"
while [[ "$1" != --output ]]; do shift; done
cp "$RUNNER_TEMP/fixture.tar.gz" "$2"
""")

    def mock_extraction(self):
        self.stub("sha256sum", """[[ "$*" == "--check --strict -" ]]
read -r digest asset
[[ "$digest" == 1f4e0ef7f6554a6ed33dd7ac144fb2e1bbed98598e7af973042fc5cd43951c9a ]]
[[ "$asset" == lychee-x86_64-unknown-linux-gnu.tar.gz ]]
""")

    def test_bounded_retry_and_verified_install(self):
        self.mock_download()
        self.mock_extraction()
        result = self.run_script("install-lychee.sh")
        self.assertEqual(result.returncode, 0, result.stderr)
        args = (self.cwd / "curl-args").read_text().splitlines()
        for flag in ("--fail", "--show-error", "--location", "--retry-all-errors"):
            self.assertIn(flag, args)
        for flag, value in (("--retry", "5"), ("--retry-delay", "2"),
                            ("--retry-max-time", "180"), ("--max-time", "60"),
                            ("--connect-timeout", "15")):
            self.assertEqual(args[args.index(flag) + 1], value)
        self.assertEqual(args[-1], "https://github.com/lycheeverse/lychee/releases/download/"
                         "lychee-v0.24.2/lychee-x86_64-unknown-linux-gnu.tar.gz")
        self.assertTrue((self.cwd / "lychee/bin/lychee").is_file())
        self.assertEqual((self.cwd / "path").read_text(), f"{self.cwd}/lychee/bin\n")
        self.assertEqual(list(self.cwd.glob("lychee-download.*")), [])

    def mock_fallback(self, corrupt=False):
        self.mock_download()
        self.stub("curl", """printf '%s\n' "$@" >> "$RUNNER_TEMP/all-curl-args"
url="${!#}"
if [[ "$url" == https://github.com/* ]]; then
  echo primary >> "$RUNNER_TEMP/routes"
  exit 22
fi
[[ "$url" == https://api.github.com/repos/lycheeverse/lychee/releases/assets/409959156 ]]
echo api >> "$RUNNER_TEMP/routes"
printf '%s\n' "$@" > "$RUNNER_TEMP/api-args"
while [[ "$1" != --output ]]; do shift; done
""" + ('echo corrupted > "$2"' if corrupt else
       'cp "$RUNNER_TEMP/fixture.tar.gz" "$2"'))

    def test_api_fallback_uses_exact_asset_and_verifies_before_install(self):
        self.mock_fallback()
        self.mock_extraction()
        result = self.run_script("install-lychee.sh")
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual((self.cwd / "routes").read_text(), "primary\napi\n")
        args = (self.cwd / "api-args").read_text().splitlines()
        self.assertEqual(args[args.index("--header") + 1], "Accept: application/octet-stream")
        self.assertEqual(args[args.index("--retry") + 1], "2")
        self.assertEqual(args[args.index("--retry-max-time") + 1], "90")
        self.assertIn("--fail", args)
        self.assertIn("--retry-all-errors", args)
        self.assertNotIn("Authorization", " ".join(args))
        self.assertTrue((self.cwd / "lychee/bin/lychee").is_file())
        self.assertEqual(list(self.cwd.glob("lychee-download.*")), [])

    def test_corrupt_api_fallback_fails_checksum_before_extraction(self):
        self.mock_fallback(corrupt=True)
        self.stub("tar", 'touch "$RUNNER_TEMP/extracted"')
        result = self.run_script("install-lychee.sh")
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("FAILED", result.stdout)
        self.assertEqual((self.cwd / "routes").read_text(), "primary\napi\n")
        self.assertFalse((self.cwd / "extracted").exists())
        self.assertFalse((self.cwd / "path").exists())

    def test_exhausted_download_fails_without_install(self):
        self.stub("curl", 'echo called >> "$RUNNER_TEMP/curl-calls"; exit 22')
        self.mock_extraction()
        result = self.run_script("install-lychee.sh")
        self.assertEqual(result.returncode, 22)
        self.assertEqual((self.cwd / "curl-calls").read_text(), "called\ncalled\n")
        self.assertFalse((self.cwd / "extracted").exists())
        self.assertFalse((self.cwd / "path").exists())
        self.assertEqual(list(self.cwd.glob("lychee-download.*")), [])

    def test_real_checksum_rejects_corrupt_archive_before_extraction(self):
        self.mock_download()  # Fixture archive cannot match the published digest.
        self.stub("tar", 'touch "$RUNNER_TEMP/extracted"')
        result = self.run_script("install-lychee.sh")
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("FAILED", result.stdout)
        self.assertFalse((self.cwd / "extracted").exists())
        self.assertFalse((self.cwd / "path").exists())

    def mock_lychee(self, total=2, status=0, report=True):
        self.stub("lychee", f"""printf '%s\n' "$@" > "$RUNNER_TEMP/link-args"
echo called >> "$RUNNER_TEMP/calls"
{"echo '| Total | " + str(total) + " |' > lychee/out.md" if report else ":"}
exit {status}
""")

    def test_real_inputs_and_summary_preserved(self):
        self.mock_lychee()
        result = self.run_script("check-docs-links.sh")
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual((self.cwd / "link-args").read_text().splitlines(),
                         ["--mode", "task", "--format", "markdown", "--output", "lychee/out.md",
                          "--no-progress", "--max-concurrency", "8", "--user-agent",
                          "ashlar-docs-link-check", "README.md", "docs/**/*.md"])
        self.assertIn("| Total | 2 |", (self.cwd / "summary").read_text())

    def test_link_failure_is_not_retried_or_masked(self):
        self.mock_lychee(status=2)
        result = self.run_script("check-docs-links.sh")
        self.assertEqual(result.returncode, 2)
        self.assertEqual((self.cwd / "calls").read_text(), "called\n")
        self.assertEqual((self.cwd / "output").read_text(), "exit_code=2\n")

    def test_empty_check_fails(self):
        self.mock_lychee(total=0)
        self.assertNotEqual(self.run_script("check-docs-links.sh").returncode, 0)

    def test_missing_report_fails(self):
        (self.cwd / "lychee").mkdir()
        (self.cwd / "lychee/out.md").write_text("| Total | 42 |\n")
        self.mock_lychee(report=False)
        self.assertNotEqual(self.run_script("check-docs-links.sh").returncode, 0)

    def test_workflow_keeps_required_check_and_does_not_mask_failure(self):
        workflow = (ROOT / ".github/workflows/docs-link-check.yml").read_text()
        self.assertIn("name: lychee (README + docs)", workflow)
        self.assertIn("run: bash scripts/ci/install-lychee.sh", workflow)
        self.assertIn("run: bash scripts/ci/check-docs-links.sh", workflow)
        self.assertNotIn("continue-on-error:", workflow)
        self.assertNotIn("if:", workflow)


if __name__ == "__main__":
    unittest.main()
