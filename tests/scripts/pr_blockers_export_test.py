"""Black-box Action client checks; stub evidence is not a GitHub accuracy evaluation."""
import json
import importlib.util
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest import mock
from types import SimpleNamespace

ROOT = Path(__file__).resolve().parents[2]
CLIENT = ROOT / 'actions/pr-blockers/pr_blockers.py'
STUB = ROOT / 'tests/scripts/fixtures/pr-blockers/stub_mcp_server.py'


class EvidenceActionTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.work = Path(self.temp.name)

    def run_action(self, mode, **changes):
        env = dict(os.environ, STUB_MODE=mode, STUB_LOG=str(self.work / 'log'),
                   ASHLAR_PR_BLOCKERS_URL='https://github.com/o/r/pull/7',
                   ASHLAR_GITHUB_TOKEN='synthetic-secret', ASHLAR_PR_BLOCKERS_TIMEOUT='0.5',
                   ASHLAR_EVIDENCE_DIRECTORY=str(self.work / 'evidence'),
                   GITHUB_OUTPUT=str(self.work / 'outputs'), GITHUB_STEP_SUMMARY=str(self.work / 'summary'))
        env.update(changes)
        result = subprocess.run([sys.executable, str(CLIENT), '--', sys.executable, str(STUB)],
                                env=env, capture_output=True, text=True, timeout=15)
        outputs = self.work / 'outputs'
        values = dict(line.split('=', 1) for line in outputs.read_text().splitlines()) if outputs.exists() else {}
        self.assertNotIn('synthetic-secret', result.stdout + result.stderr)
        if (self.work / 'log').exists():
            self.assertNotIn('synthetic-secret', (self.work / 'log').read_text())
        return result, values

    def test_ready_export_matches_report_and_audit(self):
        result, out = self.run_action('pack-ready')
        self.assertEqual(result.returncode, 0, result.stderr)
        pack = json.loads(Path(out['evidence-json']).read_text())
        report = Path(out['evidence-markdown']).read_text()
        self.assertEqual(pack['assurance'], out['assurance'])
        self.assertEqual(pack['auditId'], out['audit-id'])
        self.assertIn(pack['assurance'], report)
        self.assertIn(pack['auditId'], report)
        self.assertEqual(report, (self.work / 'summary').read_text())

    def test_unknown_assurance_preserves_ready_legacy_verdict(self):
        result, out = self.run_action('pack-unknown')
        self.assertEqual(result.returncode, 0)
        self.assertEqual(out['verdict'], 'ready')
        self.assertEqual(out['assurance'], 'unknown')

    def test_old_host_never_synthesizes_passing_assurance(self):
        result, out = self.run_action('ready')
        self.assertEqual(result.returncode, 0)
        self.assertNotIn('assurance', out)
        self.assertNotIn('evidence-json', out)

    def test_untrusted_answer_is_not_a_workflow_command(self):
        result, _ = self.run_action('hostile-answer')
        self.assertEqual(result.returncode, 0)
        self.assertNotIn('::error::untrusted', result.stdout)

    def test_protocol_mismatch_is_no_answer(self):
        result, out = self.run_action('wrong-protocol')
        self.assertEqual(result.returncode, 2)
        self.assertEqual(out, {'verdict': 'error'})

    def test_hostile_metadata_is_no_answer(self):
        for mode in ('hostile-verdict', 'hostile-audit'):
            with self.subTest(mode=mode):
                (self.work / 'outputs').unlink(missing_ok=True)
                result, out = self.run_action(mode)
                self.assertEqual(result.returncode, 2)
                self.assertEqual(out, {'verdict': 'error'})

    def test_conflicting_or_unsupported_pack_fails_export(self):
        for mode in ('pack-mismatch', 'pack-new-schema'):
            with self.subTest(mode=mode):
                (self.work / 'outputs').unlink(missing_ok=True)
                result, out = self.run_action(mode)
                self.assertEqual(result.returncode, 2)
                self.assertEqual(out, {'verdict': 'error'})
                self.assertFalse((self.work / 'evidence').exists())

    def test_no_untrusted_host_can_receive_credential(self):
        result, out = self.run_action('ready', ASHLAR_PR_BLOCKERS_URL='https://github.com.evil/o/r/pull/7')
        self.assertEqual(result.returncode, 2)
        self.assertEqual(out, {'verdict': 'error'})
        self.assertFalse((self.work / 'log').exists())

    def test_nonfinite_timeout_is_rejected(self):
        result, out = self.run_action('ready', ASHLAR_PR_BLOCKERS_TIMEOUT='nan')
        self.assertEqual(result.returncode, 2)
        self.assertEqual(out, {'verdict': 'error'})
        self.assertFalse((self.work / 'log').exists())

    def test_self_check_name_is_explicit(self):
        result, _ = self.run_action('pack-ready', ASHLAR_COLLECTION_CHECK_NAME='Evidence summary')
        self.assertEqual(result.returncode, 0)
        requests = [json.loads(line) for line in (self.work / 'log').read_text().splitlines()]
        call = next(x for x in requests if x.get('method') == 'tools/call')
        self.assertEqual(call['params']['arguments'], {'url': 'https://github.com/o/r/pull/7',
                                                     'collectionCheckName': 'Evidence summary'})

    def test_queued_notifications_cannot_extend_response_deadline(self):
        spec = importlib.util.spec_from_file_location('pr_blockers', CLIENT)
        client = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(client)
        server = client.Server.__new__(client.Server)
        server.timeout = 1
        server.send = lambda message: None
        messages = iter([json.dumps({'method': 'notifications/message'}),
                         json.dumps({'id': 1, 'result': {'ok': True}})])
        server.lines = SimpleNamespace(get=lambda **kwargs: next(messages))
        # A notification is read at 0.25s; the response is available only after the 1s deadline.
        with mock.patch.object(client.time, 'monotonic', side_effect=[0, 0.25, 2]):
            with self.assertRaises(client.NoAnswer):
                server.request(1, 'tools/call', {})


if __name__ == '__main__':
    unittest.main()
