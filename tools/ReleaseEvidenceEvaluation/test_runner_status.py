"""Test the actual generated container command with fake commands, never host .NET or Docker.

The /evaluation-output bind mount is mapped to a disposable directory. No candidate cases,
labels, production source, evaluation result bytes or caller permissions are changed.
"""
from pathlib import Path
import hashlib
import json
import os
import subprocess
import sys
import tempfile
import unittest

SCRIPT = Path(os.environ.get('EVALUATION_SCRIPT_UNDER_TEST', Path(__file__).with_name('run-replay.sh')))


class ContainerCommandStatusTests(unittest.TestCase):
    def run_generated(self, *, runner_status=0, build_status=0, ownership_status=0):
        with tempfile.TemporaryDirectory(prefix='evaluation-command-test-') as tmp:
            root = Path(tmp)
            work = root / 'work'
            inputs = work / 'repo/tools/ReleaseEvidenceEvaluation/replay-inputs/cases'
            inputs.mkdir(parents=True)
            case = root / 'case.json'
            content = b'{"id":"harness-only-case"}\n'
            case.write_bytes(content)
            candidate = 'a' * 40
            manifest = root / 'manifest.json'
            manifest.write_text(json.dumps({'candidate_commit': candidate, 'cases': [
                {'id': 'harness-only-case', 'path': 'case.json', 'sha256': hashlib.sha256(content).hexdigest()}
            ]}))
            truth = root / 'truth.json'
            truth.write_text('{"harnessOnly":true}\n')
            # This also verifies spaces and shell metacharacters remain data in generated paths.
            output = root / 'output parent' / 'result ; literal $(not-a-command)'
            source = SCRIPT.read_text()
            prefix = '<<\'PY\'\n'
            self.assertEqual(source.count(prefix), 1, 'expected one frozen-input/command generator')
            generator = source.split(prefix, 1)[1].split('\nPY\n', 1)[0]
            subprocess.run([sys.executable, '-c', generator, str(root), str(manifest), str(truth),
                            str(output), str(work), candidate, '0'], check=True, capture_output=True, text=True)
            command = (work / 'command').read_text()
            # Simulate only the bind mount. The command's actual status/cleanup control flow is unchanged.
            command = command.replace('/evaluation-output/', str(output.parent) + '/')
            binaries = root / 'bin'
            binaries.mkdir()
            (binaries / 'dotnet').write_text('''#!/usr/bin/env python3
from pathlib import Path
import os,sys
with open(os.environ['COMMAND_LOG'], 'a') as log:
    log.write('build\\n' if sys.argv[1] == 'build' else 'run\\n')
if sys.argv[1] == 'build':
    raise SystemExit(int(os.environ['BUILD_STATUS']))
Path(os.environ['RESULT_DIRECTORY']).mkdir(parents=True)
raise SystemExit(int(os.environ['RUNNER_STATUS']))
''')
            (binaries / 'chown').write_text('''#!/usr/bin/env python3
from pathlib import Path
import json,os,sys
Path(os.environ['OWNERSHIP_LOG']).write_text(json.dumps(sys.argv[1:]))
raise SystemExit(int(os.environ['OWNERSHIP_STATUS']))
''')
            for executable in binaries.iterdir():
                executable.chmod(0o755)
            log, ownership_log = root / 'commands.log', root / 'ownership.json'
            env = dict(os.environ, PATH=str(binaries) + os.pathsep + os.environ['PATH'],
                       COMMAND_LOG=str(log), OWNERSHIP_LOG=str(ownership_log), RESULT_DIRECTORY=str(output),
                       BUILD_STATUS=str(build_status), RUNNER_STATUS=str(runner_status),
                       OWNERSHIP_STATUS=str(ownership_status), PYTHONDONTWRITEBYTECODE='1')
            actual = subprocess.run(['bash', '-euc', command], env=env, capture_output=True, text=True)
            commands = log.read_text().splitlines() if log.exists() else []
            ownership = json.loads(ownership_log.read_text()) if ownership_log.exists() else None
            if ownership is not None:
                self.assertEqual(ownership, ['-R', f'{os.getuid()}:{os.getgid()}', str(output)])
            return actual, commands, ownership

    def test_answered_run_repairs_ownership_then_returns_zero(self):
        result, commands, ownership = self.run_generated()
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(commands, ['build', 'run'])
        self.assertIsNotNone(ownership)

    def test_nonzero_collector_result_repairs_ownership_and_preserves_status(self):
        result, commands, ownership = self.run_generated(runner_status=7)
        self.assertEqual(result.returncode, 7, result.stderr)
        self.assertEqual(commands, ['build', 'run'])
        self.assertIsNotNone(ownership, 'failed evaluation output must still be operator-accessible')

    def test_build_failure_is_preserved_without_running_collector(self):
        result, commands, ownership = self.run_generated(build_status=23)
        self.assertEqual(result.returncode, 23, result.stderr)
        self.assertEqual(commands, ['build'])
        self.assertIsNone(ownership)

    def test_ownership_failure_after_collector_failure_is_a_hard_error(self):
        result, _, ownership = self.run_generated(runner_status=7, ownership_status=19)
        self.assertEqual(result.returncode, 73, result.stderr)
        self.assertIsNotNone(ownership)
        self.assertIn('ownership repair failed', result.stderr)

    def test_ownership_failure_after_success_is_a_hard_error(self):
        result, _, ownership = self.run_generated(ownership_status=19)
        self.assertEqual(result.returncode, 73, result.stderr)
        self.assertIsNotNone(ownership)
        self.assertIn('ownership repair failed', result.stderr)


if __name__ == '__main__':
    unittest.main()
