"""Tests for DTM extraction and DSM differences (numpy helpers; GDAL round trips when installed)."""

import json
import math
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

from dronedash_worker import surface  # noqa: E402

CELL = 0.5
SIZE = 400  # 200 x 200 m


def terrain_and_objects():
    """Sloped terrain with a gentle hill, a 30 x 20 m building of 8 m and three 6 m trees."""
    yy, xx = np.mgrid[0:SIZE, 0:SIZE] * CELL
    terrain = 100.0 + 0.04 * xx + 0.02 * yy + 3.0 * np.exp(-((xx - 150) ** 2 + (yy - 60) ** 2) / (2 * 25.0 ** 2))
    objects = np.zeros_like(terrain)
    objects[(np.abs(xx - 60) < 15) & (np.abs(yy - 120) < 10)] = 8.0
    for cx, cy in ((140, 140), (40, 40), (170, 170)):
        objects = np.maximum(objects, np.clip(6.0 * (1 - np.hypot(xx - cx, yy - cy) / 3.0), 0, None))
    return terrain, objects


def test_min_filter_mirrors_max_filter():
    grid = np.array([[5.0, 1.0, 5.0], [5.0, 5.0, np.nan], [5.0, 5.0, 5.0]])
    filtered = surface.nan_min_filter(grid, 1, np)
    assert filtered[0, 0] == 1.0 and filtered[1, 2] == 1.0 and filtered[2, 0] == 5.0


def test_window_radii_double_up_to_half_the_object():
    assert surface.window_radii(20.0, 0.5) == [1, 2, 4, 8, 16, 20]
    assert surface.window_radii(1.0, 1.0) == [1]


def test_morphological_filter_separates_objects_from_terrain():
    terrain, objects = terrain_and_objects()
    dsm = terrain + objects
    dsm[0:4, 0:4] = np.nan

    ground = surface.progressive_morphological_filter(dsm, CELL, np, max_object=40.0)

    building = objects >= 7.9
    trees = (objects > 1.0) & ~building
    open_ground = objects == 0
    assert not ground[building].any()
    assert ground[trees].mean() < 0.05
    assert ground[open_ground & np.isfinite(dsm)].mean() > 0.97  # the hill and slope stay ground
    assert not ground[0:4, 0:4].any()


def make_raster(path, grid, gdal, osr, nodata=-9999.0):
    srs = osr.SpatialReference()
    srs.ImportFromEPSG(32632)
    ds = gdal.GetDriverByName("GTiff").Create(str(path), grid.shape[1], grid.shape[0], 1, gdal.GDT_Float32)
    ds.SetGeoTransform((456000.0, CELL, 0.0, 5430200.0, 0.0, -CELL))
    ds.SetProjection(srs.ExportToWkt())
    band = ds.GetRasterBand(1)
    band.SetNoDataValue(nodata)
    band.WriteArray(np.where(np.isfinite(grid), grid, nodata).astype(np.float32))
    ds = None


def run(*args):
    completed = subprocess.run([sys.executable, str(WORKERS / "opencv_m3m.py"), *args],
                               capture_output=True, text=True, check=False)
    assert completed.returncode == 0, completed.stderr
    return json.loads(completed.stdout.strip().splitlines()[-1])


@pytest.fixture
def gis():
    gdal = pytest.importorskip("osgeo.gdal")
    from osgeo import osr

    gdal.UseExceptions()
    osr.UseExceptions()
    return gdal, osr


def test_dtm_from_dsm_recovers_terrain_under_objects(tmp_path, gis):
    gdal, osr = gis
    terrain, objects = terrain_and_objects()
    make_raster(tmp_path / "dsm.tif", terrain + objects, gdal, osr)

    result = run("--dtm-from-dsm", "--source", str(tmp_path / "dsm.tif"), "--output", str(tmp_path / "dtm.tif"),
                 "--cell", "1.0", "--max-object", "40")

    assert result["cellMeters"] == 1.0
    assert 0.9 < result["groundFraction"] < 0.99
    dtm = gdal.Open(str(tmp_path / "dtm.tif"))
    assert dtm.GetGeoTransform()[1] == 1.0
    z = dtm.GetRasterBand(1).ReadAsArray().astype(float)
    # Compare with the true terrain at the 1 m cell centres.
    truth = terrain[::2, ::2][: z.shape[0], : z.shape[1]] + 0.04 * 0.25 + 0.02 * 0.25
    under_building = objects[::2, ::2][: z.shape[0], : z.shape[1]] >= 7.9
    error = z - truth
    assert np.abs(error[under_building]).max() < 0.6
    assert np.sqrt(np.mean(error[2:-2, 2:-2] ** 2)) < 0.15


def test_dtm_rejects_geographic_models(tmp_path, gis):
    gdal, osr = gis
    ds = gdal.GetDriverByName("GTiff").Create(str(tmp_path / "geo.tif"), 10, 10, 1, gdal.GDT_Float32)
    ds.SetGeoTransform((8.4, 1e-5, 0, 49.0, 0, -1e-5))
    srs = osr.SpatialReference()
    srs.ImportFromEPSG(4326)
    ds.SetProjection(srs.ExportToWkt())
    ds = None
    completed = subprocess.run([sys.executable, str(WORKERS / "opencv_m3m.py"), "--dtm-from-dsm", "--source",
                                str(tmp_path / "geo.tif"), "--output", str(tmp_path / "o.tif")],
                               capture_output=True, text=True, check=False)
    assert completed.returncode != 0 and "projected" in completed.stderr


def test_difference_measures_stockpile_and_excavation(tmp_path, gis):
    gdal, osr = gis
    yy, xx = np.mgrid[0:SIZE, 0:SIZE] * CELL + CELL / 2
    before = 100.0 + 0.03 * xx
    after = before + np.clip(5.0 * (1 - np.hypot(xx - 60, yy - 60) / 15.0), 0, None)  # cone r 15 m, h 5 m
    pit = (np.abs(xx - 150) < 10) & (np.abs(yy - 150) < 10)
    after[pit] -= 2.0  # 20 x 20 x 2 m = 800 m³ removed
    after[0:10, :] = np.nan
    make_raster(tmp_path / "after.tif", after, gdal, osr)
    make_raster(tmp_path / "before.tif", before, gdal, osr)

    result = run("--dem-diff", "--source", str(tmp_path / "after.tif"), "--reference", str(tmp_path / "before.tif"),
                 "--output", str(tmp_path / "diff.tif"), "--threshold", "0.05")

    cone = math.pi * 15 ** 2 * 5 / 3  # 1178 m³
    assert result["raisedCubicMeters"] == pytest.approx(cone, rel=0.02)
    assert result["loweredCubicMeters"] == pytest.approx(800.0, rel=0.01)
    assert result["loweredAreaSquareMeters"] == pytest.approx(400.0, rel=0.01)
    assert result["minimum"] == pytest.approx(-2.0, abs=1e-3)
    assert result["maximum"] == pytest.approx(5.0, abs=0.15)  # cone tip falls between cell centres
    assert result["validPixels"] == SIZE * (SIZE - 10)
    dataset = gdal.Open(str(tmp_path / "diff.tif"))
    band = dataset.GetRasterBand(1)
    assert band.GetNoDataValue() == -9999.0
    assert band.ReadAsArray()[0, 0] == -9999.0


def test_diverging_preview_is_symmetric(tmp_path, gis):
    gdal, osr = gis
    grid = np.tile(np.linspace(-1.0, 3.0, 200), (100, 1))
    make_raster(tmp_path / "diff.tif", grid, gdal, osr)

    result = run("--dem-preview", "--source", str(tmp_path / "diff.tif"), "--output-dir", str(tmp_path / "p"),
                 "--palette", "diverging")

    assert result["palette"] == "diverging"
    assert result["displayMinimum"] == pytest.approx(-result["displayMaximum"])
    assert result["displayMaximum"] > 2.5
