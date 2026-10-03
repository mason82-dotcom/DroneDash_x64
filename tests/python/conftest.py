"""With DRONEDASH_REQUIRE_GIS=1 (the GDAL CI job) a skipped test fails instead.

The GIS round trips skip when GDAL or laspy is missing; in the job that installs them a skip
would hide a broken environment, so it must not pass silently.
"""

import os

import pytest


@pytest.hookimpl(hookwrapper=True)
def pytest_runtest_makereport(item, call):
    outcome = yield
    report = outcome.get_result()
    if report.skipped and os.environ.get("DRONEDASH_REQUIRE_GIS") == "1":
        report.outcome = "failed"
        report.longrepr = f"skipped although DRONEDASH_REQUIRE_GIS=1: {report.longrepr}"
