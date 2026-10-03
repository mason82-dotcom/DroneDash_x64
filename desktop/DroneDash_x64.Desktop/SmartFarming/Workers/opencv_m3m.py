#!/usr/bin/env python3
"""DroneDash Smart Farming worker entry point.

DroneDash runs this file by path (smart-farming/opencv_m3m.py); the
implementation lives in the dronedash_worker package next to it.
"""
import os
import sys

_WORKER_DIR = os.path.dirname(os.path.abspath(__file__))
if _WORKER_DIR not in sys.path:
    # Also works under "python -I" and when loaded via importlib by path.
    sys.path.insert(0, _WORKER_DIR)

from dronedash_worker.cli import main  # noqa: E402


if __name__ == "__main__":
    try:
        main()
    except Exception as exc:
        print(str(exc), file=sys.stderr)
        sys.exit(2)
