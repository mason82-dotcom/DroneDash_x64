"""Tests for the route terrain check (numpy helpers; GDAL round trip when installed)."""

import json
import pathlib
import subprocess
import sys

import numpy as np
import pytest

WORKERS = (
    pathlib.Path(__file__).resolve().parents[2]
    / "desktop"
    / "DroneDash_x64.Desktop"
    / "SmartFarming"
    / "Workers"
)
sys.path.insert(0, str(WORKERS))

from dronedash_worker import terrain  # noqa: E402


def test_nan_max_filter_spreads_obstacles_and_ignores_gaps():
    grid = np.zeros((7, 7))
    grid[3, 3] = 10.0
    grid[0, 0] = np.nan

    filtered = terrain.nan_max_filter(grid, 1, np)

    assert (filtered[2:5, 2:5] == 10.0).all()
    assert filtered[1, 1] == 0.0 and filtered[5, 5] == 0.0
    assert filtered[0, 0] == 0.0  # the gap takes its neighbours' maximum
    only_gap = terrain.nan_max_filter(np.full((3, 3), np.nan), 1, np)
    assert np.isnan(only_gap).all()
    np.testing.assert_array_equal(terrain.nan_max_filter(grid, 0, np)[3], grid[3])


def test_classify_and_runs():
    clearance = np.array([50.0, 40.0, 20.0, 5.0, -1.0, np.nan, 35.0])

    status = terrain.classify(clearance, 30.0, np)

    assert list(status) == [0, 0, 1, 1, 2, 3, 0]
    assert terrain.runs(status, np) == [(0, 1, 0), (2, 3, 1), (4, 4, 2), (5, 5, 3), (6, 6, 0)]
    assert terrain.runs(np.array([], dtype=np.int8), np) == []


def make_dsm(path, gdal, osr):
    """400 x 400 m at 0.5 m in UTM 32N: flat 100 m, a 60 m mast and a hill rising to 140 m in the east."""
    size, cell = 800, 0.5
    x0, y0 = 456000.0, 5430400.0
    xs = x0 + (np.arange(size) + 0.5) * cell
    z = np.full((size, size), 100.0)
    z += np.clip((xs - 456300.0) / 100.0, 0.0, 1.0)[None, :] * 40.0  # hill from 300 m to 400 m east
    mast = (slice(399, 402), slice(399, 402))  # 1.5 m wide mast at (456200, 5430200)
    z[mast] = 160.0
    z[700:705, 10:15] = -9999.0  # a small hole

    srs = osr.SpatialReference()
    srs.ImportFromEPSG(32632)
    ds = gdal.GetDriverByName("GTiff").Create(str(path), size, size, 1, gdal.GDT_Float32)
    ds.SetGeoTransform((x0, cell, 0.0, y0, 0.0, -cell))
    ds.SetProjection(srs.ExportToWkt())
    band = ds.GetRasterBand(1)
    band.SetNoDataValue(-9999.0)
    band.WriteArray(z.astype(np.float32))
    ds = None


def lonlat(e, n, osr):
    utm, wgs = osr.SpatialReference(), osr.SpatialReference()
    utm.ImportFromEPSG(32632)
    wgs.ImportFromEPSG(4326)
    for srs in (utm, wgs):
        srs.SetAxisMappingStrategy(osr.OAMS_TRADITIONAL_GIS_ORDER)
    lon, lat = osr.CoordinateTransformation(utm, wgs).TransformPoint(e, n)[:2]
    return [lon, lat]


def run_check(tmp_path, route):
    (tmp_path / "route.json").write_text(json.dumps(route))
    completed = subprocess.run(
        [sys.executable, str(WORKERS / "opencv_m3m.py"), "--terrain-check",
         "--dem", str(tmp_path / "dsm.tif"), "--route", str(tmp_path / "route.json")],
        capture_output=True, text=True, check=False)
    return completed


@pytest.fixture
def gis(tmp_path):
    gdal = pytest.importorskip("osgeo.gdal")
    from osgeo import osr

    gdal.UseExceptions()
    osr.UseExceptions()
    make_dsm(tmp_path / "dsm.tif", gdal, osr)
    return osr


def test_relative_route_finds_mast_and_rising_terrain(tmp_path, gis):
    osr = gis
    route = {
        "altitude": 80.0,
        "minClearance": 45.0,
        "buffer": 10.0,
        "start": lonlat(456050.0, 5430050.0, osr),
        "passes": [
            # West-east line over the mast and up the hill, then a leg back north-west.
            {"id": 1, "name": "Nadir", "points": [lonlat(456100.0, 5430200.0, osr), lonlat(456390.0, 5430200.0, osr),
                                                  lonlat(456100.0, 5430300.0, osr)]},
        ],
    }

    completed = run_check(tmp_path, route)
    assert completed.returncode == 0, completed.stderr
    result = json.loads(completed.stdout.strip().splitlines()[-1])

    assert result["startElevation"] == pytest.approx(100.0, abs=0.01)
    assert result["startSource"] == "model"
    assert result["flightElevation"] == pytest.approx(180.0, abs=0.01)
    assert result["coverage"] == 1.0
    # The mast (160 m) leaves 20 m below the 180 m flight level.
    minimum = result["minimum"]
    assert minimum["clearance"] == pytest.approx(20.0, abs=0.01)
    assert minimum["pass"] == "Nadir"
    mast_hits = [v for v in result["violations"] if v["obstacle"] == pytest.approx(160.0, abs=0.01)]
    assert len(mast_hits) == 1
    # Buffer 10 m: the mast counts within about +-11 m of the line crossing.
    assert 15.0 <= mast_hits[0]["toDistance"] - mast_hits[0]["fromDistance"] <= 26.0
    # The hill reaches 140 m within the 10 m buffer of the line end at 390 m east: 180 - 140 < 45 m.
    hill = [v for v in result["violations"] if v["obstacle"] < 160.0]
    assert hill and min(v["minimumClearance"] for v in hill) == pytest.approx(40.0, abs=0.2)
    statuses = {chunk["status"] for chunk in result["chunks"]}
    assert {"ok", "low"} <= statuses and "collision" not in statuses


def test_manual_start_and_collision(tmp_path, gis):
    osr = gis
    route = {
        "altitude": 50.0,
        "minClearance": 20.0,
        "buffer": 0.0,
        "startElevation": 95.0,
        "passes": [{"id": 1, "name": "Linie", "points": [lonlat(456150.0, 5430200.25, osr),
                                                          lonlat(456250.0, 5430200.25, osr)]}],
    }

    result = json.loads(run_check(tmp_path, route).stdout.strip().splitlines()[-1])

    assert result["startSource"] == "manual"
    assert result["flightElevation"] == pytest.approx(145.0)
    assert result["minimum"]["clearance"] == pytest.approx(-15.0, abs=0.01)
    assert any(v["collision"] for v in result["violations"])
    assert "collision" in {chunk["status"] for chunk in result["chunks"]}


def test_follow_mode_keeps_height_above_ground(tmp_path, gis):
    osr = gis
    route = {
        "altitude": 60.0,
        "minClearance": 30.0,
        "buffer": 0.0,
        "mode": "follow",
        "passes": [{"id": 1, "name": "Hang", "points": [lonlat(456250.0, 5430100.0, osr),
                                                         lonlat(456395.0, 5430100.0, osr)]}],
    }

    result = json.loads(run_check(tmp_path, route).stdout.strip().splitlines()[-1])

    assert result["mode"] == "follow" and result["flightElevation"] is None
    assert result["minimum"]["clearance"] == pytest.approx(60.0, abs=0.5)
    assert result["violations"] == []
    assert result["terrainRange"][1] > 135.0


def test_reports_missing_start_height_and_geographic_models(tmp_path, gis):
    osr = gis
    route = {"altitude": 80.0, "start": lonlat(456006.25, 5430048.75, osr),
             "passes": [{"name": "A", "points": [lonlat(456100.0, 5430100.0, osr), lonlat(456200.0, 5430100.0, osr)]}]}
    completed = run_check(tmp_path, route)
    assert completed.returncode != 0
    assert "take-off" in completed.stderr

    route["passes"] = []
    completed = run_check(tmp_path, route)
    assert completed.returncode != 0 and "no flight lines" in completed.stderr
