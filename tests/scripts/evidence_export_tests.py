"""Behavioral tests for the offline, pinned release-evidence export contract."""
import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("evidence_export", ROOT / "scripts/evidence-export.py")
EXPORT = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(EXPORT)


class EvidenceExportTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.work = Path(self.temp.name)
        self.key = self.work / "key.pem"
        self.public = self.work / "public.pem"
        self.make_key(self.key, self.public)
        self.evidence = self.work / "pack.json"
        self.report = self.work / "report.md"
        self.audit = self.work / "audit.ndjson"
        self.evidence.write_text('{"schemaVersion":"1.0","assurance":"unknown"}')
        self.report.write_text("# Snapshot\nAssurance: unknown\n")
        self.audit.write_text('{"detail":"retained record 1"}\n{"detail":"retained record 2"}\n')
        self.bundle = self.work / "bundle"
        self.anchor = EXPORT.export_snapshot(self.evidence, self.report, [self.audit], self.key, self.bundle)

    def make_key(self, key, public):
        subprocess.run(["openssl", "genpkey", "-algorithm", "ED25519", "-out", str(key)], check=True, capture_output=True)
        subprocess.run(["openssl", "pkey", "-in", str(key), "-pubout", "-out", str(public)], check=True, capture_output=True)

    def verify(self, anchor=None, public=None):
        EXPORT.verify_snapshot(self.bundle, public or self.public, anchor or self.anchor)

    def rewrite_manifest(self, change):
        path = self.bundle / "manifest.json"
        manifest = json.loads(path.read_bytes())
        change(manifest)
        data = json.dumps(manifest).encode()
        path.write_bytes(data)
        (self.bundle / "manifest.sig").write_bytes(EXPORT.openssl(
            "pkeyutl", "-sign", "-rawin", "-inkey", str(self.key), "-in", str(path)))
        return EXPORT.sha256(data)

    def test_pinned_signer_and_anchor_verify_exact_files(self):
        self.verify()
        manifest = json.loads((self.bundle / "manifest.json").read_text())
        self.assertEqual(manifest["actor"]["status"], "unknown")
        self.assertIsNone(manifest["actor"]["authenticatedIdentity"])
        self.assertEqual(len(manifest["files"]), 3)

    def test_modified_evidence_rejected(self):
        (self.bundle / "evidence.json").write_text('{"assurance":"pass"}')
        with self.assertRaises(EXPORT.InvalidExport):
            self.verify()

    def test_modified_report_rejected(self):
        (self.bundle / "evidence.md").write_text("# Release authorized")
        with self.assertRaises(EXPORT.InvalidExport):
            self.verify()

    def test_deleted_audit_tail_rejected(self):
        (self.bundle / "audit-0000.ndjson").write_text('{"detail":"retained record 1"}\n')
        with self.assertRaises(EXPORT.InvalidExport):
            self.verify()

    def test_entire_anchored_audit_file_missing_rejected(self):
        (self.bundle / "audit-0000.ndjson").unlink()
        with self.assertRaises(EXPORT.InvalidExport):
            self.verify()

    def test_rewritten_manifest_and_valid_signature_cannot_replace_retained_anchor(self):
        self.rewrite_manifest(lambda manifest: manifest.update(exportedAt="another time"))
        with self.assertRaisesRegex(EXPORT.InvalidExport, "retained anchor"):
            self.verify()

    def test_wrong_previously_trusted_signer_rejected(self):
        other_key, other_public = self.work / "other.pem", self.work / "other-public.pem"
        self.make_key(other_key, other_public)
        with self.assertRaises(EXPORT.InvalidExport):
            self.verify(public=other_public)

    def test_modified_signature_rejected(self):
        signature = bytearray((self.bundle / "manifest.sig").read_bytes())
        signature[0] ^= 1
        (self.bundle / "manifest.sig").write_bytes(signature)
        with self.assertRaises(EXPORT.InvalidExport):
            self.verify()

    def test_unlisted_file_rejected(self):
        (self.bundle / "extra.json").write_text("{}")
        with self.assertRaises(EXPORT.InvalidExport):
            self.verify()

    def test_symlink_file_rejected(self):
        (self.bundle / "evidence.md").unlink()
        (self.bundle / "evidence.md").symlink_to(self.report)
        with self.assertRaises(EXPORT.InvalidExport):
            self.verify()

    def test_signed_path_traversal_rejected(self):
        anchor = self.rewrite_manifest(lambda manifest: manifest["files"][0].update(path="../pack.json"))
        with self.assertRaisesRegex(EXPORT.InvalidExport, "manifest path"):
            self.verify(anchor=anchor)

    def test_signed_duplicate_path_rejected(self):
        anchor = self.rewrite_manifest(lambda manifest: manifest["files"].append(manifest["files"][0]))
        with self.assertRaisesRegex(EXPORT.InvalidExport, "duplicate"):
            self.verify(anchor=anchor)

    def test_signed_schema_downgrade_rejected(self):
        anchor = self.rewrite_manifest(lambda manifest: manifest.update(schemaVersion="ashlar.evidence-export/0"))
        with self.assertRaisesRegex(EXPORT.InvalidExport, "schema"):
            self.verify(anchor=anchor)

    def test_no_anchor_is_not_verification(self):
        with self.assertRaisesRegex(EXPORT.InvalidExport, "retained"):
            EXPORT.verify_snapshot(self.bundle, self.public, "")

    def test_export_does_not_overwrite_existing_bundle(self):
        with self.assertRaises(EXPORT.InvalidExport):
            EXPORT.export_snapshot(self.evidence, self.report, [], self.key, self.bundle)
        self.verify()

    def test_non_json_evidence_refused(self):
        self.evidence.write_text("not a pack")
        with self.assertRaises(EXPORT.InvalidExport):
            EXPORT.export_snapshot(self.evidence, self.report, [], self.key, self.work / "invalid")
        self.assertFalse((self.work / "invalid").exists())

    def test_duplicate_json_properties_refused(self):
        self.evidence.write_text('{"assurance":"unknown","assurance":"pass"}')
        with self.assertRaisesRegex(EXPORT.InvalidExport, "duplicate"):
            EXPORT.export_snapshot(self.evidence, self.report, [], self.key, self.work / "invalid")

    def test_oversize_file_refused_before_loading(self):
        with (self.bundle / "evidence.json").open("wb") as stream:
            stream.truncate(EXPORT.MAX_FILE_BYTES + 1)
        with self.assertRaisesRegex(EXPORT.InvalidExport, "size limit"):
            self.verify()

    def test_signed_non_object_inventory_refused(self):
        anchor = self.rewrite_manifest(lambda manifest: manifest.update(files="not an inventory"))
        with self.assertRaisesRegex(EXPORT.InvalidExport, "inventory"):
            self.verify(anchor=anchor)

    def test_signed_non_integer_size_refused(self):
        anchor = self.rewrite_manifest(lambda manifest: manifest["files"][0].update(size=True))
        with self.assertRaises(EXPORT.InvalidExport):
            self.verify(anchor=anchor)

    def test_documented_cli_verification(self):
        result = subprocess.run([
            sys.executable, str(ROOT / "scripts/evidence-export.py"), "verify",
            "--bundle", str(self.bundle), "--trusted-public-key", str(self.public),
            "--expected-manifest-sha256", self.anchor], capture_output=True, text=True)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("anchored manifest", result.stdout)

    def test_cli_requires_expected_anchor(self):
        result = subprocess.run([
            sys.executable, str(ROOT / "scripts/evidence-export.py"), "verify",
            "--bundle", str(self.bundle), "--trusted-public-key", str(self.public)],
            capture_output=True, text=True)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("--expected-manifest-sha256", result.stderr)

    def test_concurrent_replacement_cannot_authenticate_different_manifest_bytes(self):
        # Anchor A has no valid signature. B is legitimately signed, but has a different anchor.
        # Replacing caller paths during key validation must not combine A's hash with B's signature.
        anchor_a = self.anchor
        manifest_a = (self.bundle / "manifest.json").read_bytes()
        self.rewrite_manifest(lambda manifest: manifest.update(exportedAt="replacement"))
        manifest_b = (self.bundle / "manifest.json").read_bytes()
        signature_b = (self.bundle / "manifest.sig").read_bytes()
        (self.bundle / "manifest.json").write_bytes(manifest_a)
        (self.bundle / "manifest.sig").write_bytes(bytes(64))
        actual_openssl = EXPORT.openssl

        def replacing_openssl(*arguments):
            if arguments[0] == "pkey":
                (self.bundle / "manifest.json").write_bytes(manifest_b)
                (self.bundle / "manifest.sig").write_bytes(signature_b)
            return actual_openssl(*arguments)

        with patch.object(EXPORT, "openssl", side_effect=replacing_openssl):
            with self.assertRaises(EXPORT.InvalidExport):
                self.verify(anchor=anchor_a)

    def test_concurrent_key_replacement_cannot_change_the_captured_trusted_signer(self):
        other_key, other_public = self.work / "other.pem", self.work / "other-public.pem"
        self.make_key(other_key, other_public)
        (self.bundle / "manifest.sig").write_bytes(EXPORT.openssl(
            "pkeyutl", "-sign", "-rawin", "-inkey", str(other_key),
            "-in", str(self.bundle / "manifest.json")))
        actual_openssl = EXPORT.openssl

        def replacing_openssl(*arguments):
            if "-verify" in arguments:
                self.public.write_bytes(other_public.read_bytes())
            return actual_openssl(*arguments)

        with patch.object(EXPORT, "openssl", side_effect=replacing_openssl):
            with self.assertRaises(EXPORT.InvalidExport):
                self.verify()


if __name__ == "__main__":
    unittest.main()
