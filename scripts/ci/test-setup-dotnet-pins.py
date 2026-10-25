#!/usr/bin/env python3
"""Hermetic fixture controls for verify-setup-dotnet-pins.py; no SDK or network needed."""
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

SCRIPT = Path(__file__).with_name("verify-setup-dotnet-pins.py")
SPEC = importlib.util.spec_from_file_location("pins", SCRIPT)
PINS = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(PINS)
REPO = Path(__file__).resolve().parents[2]

NAMED_STEP = """jobs:
  build:
    steps:
      - uses: actions/checkout@v7
      - name: Setup .NET
        uses: actions/setup-dotnet@v6
        with:
          dotnet-version: |
            {first}
            8.0.427
      - name: Build
        run: dotnet build
"""

DASH_STEP = """jobs:
  pack:
    steps:
      - uses: actions/setup-dotnet@v6
        with:
          dotnet-version: '{first}'

      - run: dotnet pack
"""


class PinTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        (self.root / ".github/workflows").mkdir(parents=True)
        self.global_json("10.0.100", "latestFeature")

    def tearDown(self):
        self.temp.cleanup()

    def global_json(self, version, policy):
        (self.root / "global.json").write_text(json.dumps({"sdk": {"version": version, "rollForward": policy}}))

    def workflow(self, name, text):
        (self.root / ".github/workflows" / name).write_text(text)

    def errors(self):
        return PINS.check(self.root)[0]

    def test_exact_pins_in_both_step_shapes_pass(self):
        self.workflow("a.yml", NAMED_STEP.format(first="10.0.403"))
        self.workflow("b.yml", DASH_STEP.format(first="10.0.403"))
        errors, steps, pins = PINS.check(self.root)
        self.assertEqual(errors, [])
        self.assertEqual(steps, 2)
        self.assertEqual(pins, {"10.0": ["10.0.403"], "8.0": ["8.0.427"]})

    def test_a_channel_is_refused_in_a_list_and_inline(self):
        self.workflow("a.yml", NAMED_STEP.format(first="10.0.x"))
        self.workflow("b.yml", DASH_STEP.format(first="10.0.x"))
        errors = self.errors()
        self.assertEqual(sum("'10.0.x' is not exact" in e for e in errors), 2, errors)

    def test_global_json_file_is_refused(self):
        self.workflow("a.yml", "jobs:\n  j:\n    steps:\n      - uses: actions/setup-dotnet@v6\n"
                               "        with:\n          global-json-file: global.json\n")
        errors = self.errors()
        self.assertTrue(any("global-json-file resolves through aka.ms" in e for e in errors), errors)
        self.assertTrue(any("without an exact dotnet-version" in e for e in errors), errors)

    def test_a_version_in_the_next_step_is_not_credited_to_setup_dotnet(self):
        self.workflow("a.yml", "jobs:\n  j:\n    steps:\n      - uses: actions/setup-dotnet@v6\n"
                               "      - name: other\n        with:\n          dotnet-version: 10.0.403\n")
        self.assertTrue(any("without an exact dotnet-version" in e for e in self.errors()))

    def test_two_pins_in_one_band_are_refused(self):
        self.workflow("a.yml", NAMED_STEP.format(first="10.0.403"))
        self.workflow("b.yml", DASH_STEP.format(first="10.0.402"))
        self.assertTrue(any("band 10.0 is pinned to 2 different versions" in e for e in self.errors()))

    def test_the_pin_must_satisfy_global_json(self):
        self.workflow("a.yml", NAMED_STEP.format(first="10.0.403"))
        self.global_json("10.0.100", "latestPatch")
        self.assertTrue(any("does not satisfy global.json" in e for e in self.errors()))
        self.global_json("10.0.500", "latestFeature")
        self.assertTrue(any("does not satisfy global.json" in e for e in self.errors()))
        self.global_json("10.0.403", "disable")
        self.assertEqual(self.errors(), [])

    def test_the_global_json_band_must_be_pinned(self):
        self.workflow("a.yml", DASH_STEP.format(first="8.0.427"))
        self.assertTrue(any("no setup-dotnet step pins the global.json band 10.0" in e for e in self.errors()))

    def test_composite_actions_are_scanned(self):
        (self.root / "actions/x").mkdir(parents=True)
        (self.root / "actions/x/action.yml").write_text(
            "runs:\n  using: composite\n  steps:\n    - uses: actions/setup-dotnet@v6\n"
            "      with:\n        dotnet-version: 10.0.x\n")
        self.workflow("a.yml", DASH_STEP.format(first="10.0.403"))
        self.assertTrue(any("actions/x/action.yml:4" in e for e in self.errors()))

    def test_a_channel_dotnet_install_in_an_image_is_refused_even_across_continuation_lines(self):
        self.workflow("a.yml", DASH_STEP.format(first="10.0.403"))
        (self.root / ".docker").mkdir()
        (self.root / ".docker/Dockerfile.x").write_text(
            "RUN curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh && \\\n"
            "    bash /tmp/dotnet-install.sh --version 8.0.33 --runtime dotnet && \\\n"
            "    bash /tmp/dotnet-install.sh \\\n"
            "      --channel 8.0 --runtime aspnetcore\n"
            "# bash dotnet-install.sh --channel 8.0 (a comment is not an install)\n")
        errors = self.errors()
        self.assertEqual([e for e in errors if "dotnet-install with a channel" in e],
                         [".docker/Dockerfile.x:1: dotnet-install with a channel resolves through aka.ms; pass an exact --version"],
                         errors)

    def test_the_repository_passes(self):
        errors, steps, _ = PINS.check(REPO)
        self.assertEqual(errors, [])
        self.assertGreaterEqual(steps, 50, "population floor: the scan must keep finding the repository's steps")


if __name__ == "__main__":
    unittest.main()
