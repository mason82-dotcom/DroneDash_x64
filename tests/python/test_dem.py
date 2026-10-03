"""Unit tests for the elevation preview math (numpy only; GDAL is not needed)."""

import pathlib
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

from dronedash_worker import dem  # noqa: E402


def test_web_mercator_origin_and_known_point():
    assert dem.web_mercator_to_lonlat(0.0, 0.0) == pytest.approx((0.0, 0.0))
    radius = 6378137.0
    x = radius * np.radians(8.4)
    y = radius * np.log(np.tan(np.pi / 4 + np.radians(49.0) / 2))
    lon, lat = dem.web_mercator_to_lonlat(x, y)
    assert lon == pytest.approx(8.4, abs=1e-6)
    assert lat == pytest.approx(49.0, abs=1e-6)


def test_robust_range_ignores_outliers_and_nan():
    values = np.concatenate([np.linspace(100, 110, 1000), [np.nan, -9999.0, 5000.0]])
    vmin, vmax = dem.robust_range(values, np)
    assert 100 <= vmin < 101
    assert 109 < vmax <= 110


def test_robust_range_widens_flat_surfaces():
    vmin, vmax = dem.robust_range(np.full(10, 42.0), np)
    assert (vmin, vmax) == (41.5, 42.5)


def test_robust_range_rejects_empty_model():
    with pytest.raises(RuntimeError):
        dem.robust_range(np.full(4, np.nan), np)


def test_hillshade_flat_surface_matches_sun_altitude():
    shade = dem.hillshade(np.full((5, 5), 100.0), 1.0, 1.0, np, altitude_deg=45.0)
    assert shade == pytest.approx(np.full((5, 5), np.cos(np.radians(45.0))))


def test_hillshade_lit_slope_is_brighter_than_shadow_slope():
    # Default sun from the north-west (315°): a surface rising towards the
    # south-east faces the sun, one rising towards the north-west faces away.
    yy, xx = np.mgrid[0:9, 0:9].astype(float)
    facing_sun = dem.hillshade(xx + yy, 1.0, 1.0, np)
    facing_away = dem.hillshade(-(xx + yy), 1.0, 1.0, np)
    assert facing_sun[4, 4] - facing_away[4, 4] > 0.3


def test_hillshade_neutralizes_nan_and_rejects_bad_cells():
    z = np.full((4, 4), 10.0)
    z[1, 1] = np.nan
    shade = dem.hillshade(z, 1.0, 1.0, np)
    assert np.isfinite(shade).all()
    # Horn uses the 8 neighbours, so cells next to a gap get the neutral value.
    assert shade[0, 0] == 1.0 and shade[2, 2] == 1.0
    assert shade[3, 3] == pytest.approx(np.cos(np.radians(45.0)))
    with pytest.raises(ValueError):
        dem.hillshade(z, 0.0, 1.0, np)


def test_colorize_maps_range_ends_and_transparent_nodata():
    z = np.array([[0.0, 10.0, np.nan, 50.0]])
    rgba = dem.colorize(z, 0.0, 10.0, np)
    assert rgba.dtype == np.uint8
    assert tuple(rgba[0, 0]) == (38, 115, 77, 255)      # first ramp stop
    assert tuple(rgba[0, 1]) == (245, 245, 245, 255)    # last ramp stop
    assert rgba[0, 2, 3] == 0                           # nodata transparent
    assert tuple(rgba[0, 3]) == tuple(rgba[0, 1])       # clipped above range


def test_shade_relief_keeps_alpha_and_darkens():
    rgba = np.full((1, 2, 4), 200, dtype=np.uint8)
    out = dem.shade_relief(rgba, np.array([[1.0, 0.0]]), np, strength=0.5)
    assert tuple(out[0, 0]) == (200, 200, 200, 200)
    assert tuple(out[0, 1]) == (100, 100, 100, 200)


@pytest.mark.parametrize(
    ("size", "max_size", "expected"),
    [((1000, 500), 2048, (1000, 500)), ((4000, 2000), 2000, (2000, 1000)), ((300, 9000), 900, (30, 900))],
)
def test_preview_size_keeps_aspect_ratio(size, max_size, expected):
    assert dem._preview_size(*size, max_size) == expected
