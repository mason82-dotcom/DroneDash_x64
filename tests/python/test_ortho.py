"""Tests for orthomosaic tiling (zoom math; gdal2tiles round trip when GDAL is installed)."""

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

from dronedash_worker import ortho  # noqa: E402


def test_native_zoom_matches_ground_resolution():
    # Zoom 21 at 49° N is 0.049 m per tile pixel: the first level at least as fine as 5 cm.
    assert ortho.native_zoom(0.05, 49.0) == 21
    assert ortho.native_zoom(0.02, 49.0) == 23
    assert ortho.native_zoom(10.0, 0.0) == 14
    assert ortho.native_zoom(0.001, 0.0) == 23  # capped
    with pytest.raises(ValueError):
        ortho.native_zoom(0.0, 49.0)


def make_ortho(path, gdal, osr, bands=4, dtype=None):
    w, h = 400, 300
    ds = gdal.GetDriverByName("GTiff").Create(str(path), w, h, bands, dtype or gdal.GDT_Byte)
    ds.SetGeoTransform((456000.0, 0.25, 0.0, 5430075.0, 0.0, -0.25))
    srs = osr.SpatialReference()
    srs.ImportFromEPSG(32632)
    ds.SetProjection(srs.ExportToWkt())
    for band in range(1, bands + 1):
        ds.GetRasterBand(band).WriteArray(np.full((h, w), 40 * band, dtype=np.uint8))
    if bands == 4:
        ds.GetRasterBand(4).SetColorInterpretation(gdal.GCI_AlphaBand)
    ds = None


def run(*args):
    return subprocess.run([sys.executable, str(WORKERS / "opencv_m3m.py"), *args],
                          capture_output=True, text=True, check=False)


def test_tiles_round_trip_and_replace(tmp_path):
    gdal = pytest.importorskip("osgeo.gdal")
    from osgeo import osr

    gdal.UseExceptions()
    osr.UseExceptions()
    make_ortho(tmp_path / "ortho.tif", gdal, osr)
    out = tmp_path / "ortho.tiles"
    (out / "stale").mkdir(parents=True)

    completed = run("--ortho-tiles", "--source", str(tmp_path / "ortho.tif"), "--output-dir", str(out))
    assert completed.returncode == 0, completed.stderr
    result = json.loads(completed.stdout.strip().splitlines()[-1])

    assert result["maxZoom"] == ortho.native_zoom(0.25, 49.0)
    assert result["minZoom"] == result["maxZoom"] - 8
    assert result["pixelSizeMeters"] == pytest.approx(0.25)
    bounds = result["bounds"]
    assert 49.0 < bounds["south"] < bounds["north"] < 49.1 and 8.3 < bounds["west"] < bounds["east"] < 8.5
    pngs = list(out.rglob("*.png"))
    assert len(pngs) == result["tiles"] > 0
    assert (out / str(result["maxZoom"])).is_dir()
    assert not (out / "stale").exists()  # the previous pyramid is replaced as a whole
    assert not (tmp_path / "ortho.tiles.partial").exists()
    assert json.loads((out / "ortho-tiles.json").read_text())["tiles"] == result["tiles"]


def test_rejects_non_8bit_and_unreferenced_images(tmp_path):
    gdal = pytest.importorskip("osgeo.gdal")
    from osgeo import osr

    gdal.UseExceptions()
    osr.UseExceptions()
    make_ortho(tmp_path / "float.tif", gdal, osr, bands=3, dtype=gdal.GDT_Float32)
    completed = run("--ortho-tiles", "--source", str(tmp_path / "float.tif"), "--output-dir", str(tmp_path / "t"))
    assert completed.returncode != 0 and "8-bit" in completed.stderr

    plain = gdal.GetDriverByName("GTiff").Create(str(tmp_path / "plain.tif"), 10, 10, 3, gdal.GDT_Byte)
    plain = None
    completed = run("--ortho-tiles", "--source", str(tmp_path / "plain.tif"), "--output-dir", str(tmp_path / "t"))
    assert completed.returncode != 0 and "coordinate reference system" in completed.stderr
