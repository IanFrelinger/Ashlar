#!/usr/bin/env python3
"""Entry point: python3 tools/autonomy-controller/controller.py --state ... --policy ... <command>."""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from ashlar_controller.cli import main  # noqa: E402

if __name__ == "__main__":
    sys.exit(main())
