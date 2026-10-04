"""Tests for prescription maps from scouting zones (rate parsing; GDAL/OGR round trip when installed)."""

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

from dronedash_worker import prescription  # noqa: E402


def test_parse_rates_requires_every_zone():
    assert prescription.parse_rates("1:120, 2:100,3:80,4:60,5:0") == {1: 120.0, 2: 100.0, 3: 80.0, 4: 60.0, 5: 0.0}
    for bad in ("1:120,2:100,3:80,4:60", "1:120,2:100,3:80,4:60,5:-1", "6:1,1:1,2:1,3:1,4:1,5:1", "1-120"):
        with pytest.raises(RuntimeError):
            prescription.parse_rates(bad)


def test_zone_areas_count_cells():
    grid = np.array([[1, 1, 2], [0, 5, 5]])
    assert prescription.zone_areas(grid, 100.0, np) == {1: 200.0, 2: 100.0, 3: 0.0, 4: 0.0, 5: 200.0}


def test_prescription_round_trip(tmp_path):
    gdal = pytest.importorskip("osgeo.gdal")
    from osgeo import ogr, osr

    gdal.UseExceptions()
    ogr.UseExceptions()
    osr.UseExceptions()
    # 200 x 100 m at 0.5 m: west half zone 2, east half zone 4, a 10 m nodata strip in the north.
    zones = np.zeros((200, 400), dtype=np.uint8)
    zones[:, :200] = 2
    zones[:, 200:] = 4
    zones[:20, :] = 0
    zones[100:102, 100:102] = 5  # 1 m speck: too small to win a 10 m cell
    srs = osr.SpatialReference()
    srs.ImportFromEPSG(32632)
    ds = gdal.GetDriverByName("GTiff").Create(str(tmp_path / "zones.tif"), 400, 200, 1, gdal.GDT_Byte)
    ds.SetGeoTransform((456000.0, 0.5, 0.0, 5430100.0, 0.0, -0.5))
    ds.SetProjection(srs.ExportToWkt())
    ds.GetRasterBand(1).SetNoDataValue(0)
    ds.GetRasterBand(1).WriteArray(zones)
    ds = None

    completed = subprocess.run(
        [sys.executable, str(WORKERS / "opencv_m3m.py"), "--prescription", "--source", str(tmp_path / "zones.tif"),
         "--output-dir", str(tmp_path / "out"), "--rates", "1:150,2:120,3:100,4:80,5:60", "--cell", "10",
         "--unit", "kg/ha", "--name", "rx"],
        capture_output=True, text=True, check=False)
    assert completed.returncode == 0, completed.stderr
    result = json.loads(completed.stdout.strip().splitlines()[-1])

    by_zone = {z["zone"]: z for z in result["zones"]}
    assert by_zone[2]["areaHectares"] == pytest.approx(0.9)   # 100 x 90 m
    assert by_zone[4]["areaHectares"] == pytest.approx(0.9)
    assert by_zone[5]["areaHectares"] == 0.0
    assert result["totalAmount"] == pytest.approx(0.9 * 120 + 0.9 * 80)
    assert result["unit"] == "kg/ha"

    shp = ogr.Open(str(tmp_path / "out" / "rx.shp"))
    layer = shp.GetLayer()
    assert layer.GetSpatialRef().GetAuthorityCode(None) == "4326"
    rows = sorted((f.GetField("ZONE"), f.GetField("RATE"), round(f.GetField("AREA_HA"), 3)) for f in layer)
    assert rows == [(2, 120.0, 0.9), (4, 80.0, 0.9)]
    lon, lat = layer.GetExtent()[0], layer.GetExtent()[2]
    assert 8.39 < lon < 8.41 and 49.0 < lat < 49.1

    geojson = json.loads((tmp_path / "out" / "rx.geojson").read_text())
    assert {f["properties"]["RATE"] for f in geojson["features"]} == {120.0, 80.0}
    assert (tmp_path / "out" / "rx.json").exists()
