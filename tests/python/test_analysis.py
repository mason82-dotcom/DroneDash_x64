"""Unit tests for profile/volume/canopy-height math (numpy only)."""

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

from dronedash_worker import analysis  # noqa: E402


def test_parse_coordinates_validates_count_and_range():
    assert analysis.parse_coordinates("8.4,49.0; 8.5,49.1", 2) == [(8.4, 49.0), (8.5, 49.1)]
    with pytest.raises(RuntimeError):
        analysis.parse_coordinates("8.4,49.0", 2)
    with pytest.raises(RuntimeError):
        analysis.parse_coordinates("8.4,95.0;8.5,49.1", 2)
    with pytest.raises(RuntimeError):
        analysis.parse_coordinates("8.4;49.0", 1)


def test_densify_keeps_vertices_and_distance():
    x, y, d = analysis.densify([0, 10, 10], [0, 0, 5], 2.0, np)
    assert d[-1] == pytest.approx(15.0)
    assert (x[5], y[5]) == (10, 0)          # vertex preserved
    assert np.diff(d).max() <= 2.0 + 1e-9


def test_densify_closed_ring_returns_to_start():
    x, y, d = analysis.densify([0, 4, 4, 0], [0, 0, 4, 4], 1.0, np, closed=True)
    assert (x[-1], y[-1]) == (0, 0)
    assert d[-1] == pytest.approx(16.0)


def test_bilinear_reproduces_linear_surface_and_flags_outside():
    rows, cols = np.mgrid[0:5, 0:6].astype(float)
    grid = 2 * (cols + 0.5) + 3 * (rows + 0.5)          # value at pixel centres
    values = analysis.bilinear(grid, [1.25, 3.0, 4.5], [2.75, 1.0, 3.5], np)
    assert values == pytest.approx([2 * 1.25 + 3 * 2.75, 2 * 3 + 3 * 1, 2 * 4.5 + 3 * 3.5])
    assert np.isnan(analysis.bilinear(grid, [-1.0, 7.0], [1.0, 1.0], np)).all()


def test_bilinear_propagates_nodata():
    grid = np.ones((3, 3))
    grid[1, 1] = np.nan
    assert np.isnan(analysis.bilinear(grid, [1.5], [1.5], np))[0]


def test_fit_plane_recovers_slope_and_ignores_nan():
    rng = np.random.default_rng(3)
    x = rng.uniform(456000, 456200, 200)
    y = rng.uniform(5430000, 5430150, 200)
    z = 0.02 * (x - 456000) - 0.01 * (y - 5430000) + 115
    z[::17] = np.nan
    a, b, c = analysis.fit_plane(x, y, z, np)
    assert (a, b) == pytest.approx((0.02, -0.01), abs=1e-9)
    assert a * 456000 + b * 5430000 + c == pytest.approx(115, abs=1e-6)


def test_fit_plane_rejects_collinear_boundary():
    with pytest.raises(RuntimeError):
        analysis.fit_plane([0, 1, 2, 3], [0, 1, 2, 3], [1, 2, 3, 4], np)


def test_volume_stats_cut_fill_and_area():
    z = np.array([[2.0, 0.0], [1.0, np.nan]])
    base = np.full((2, 2), 1.0)
    mask = np.array([[True, True], [True, True]])
    stats = analysis.volume_stats(z, base, mask, 0.25, np)
    assert stats["areaSquareMeters"] == pytest.approx(1.0)
    assert stats["cutCubicMeters"] == pytest.approx(0.25)
    assert stats["fillCubicMeters"] == pytest.approx(0.25)
    assert stats["netCubicMeters"] == pytest.approx(0.0)
    assert stats["validFraction"] == pytest.approx(0.75)


def test_volume_stats_rejects_empty_mask():
    with pytest.raises(RuntimeError):
        analysis.volume_stats(np.ones((2, 2)), np.ones((2, 2)), np.zeros((2, 2), bool), 1.0, np)


def test_ellipsoid_distance_east_west_at_49_degrees():
    # 1 degree of longitude at 49 deg N on GRS80 is 73,171 m (prime vertical radius * cos(lat)).
    d = analysis.ellipsoid_distance(8.0, 49.0, 9.0, 49.0, np)
    assert d == pytest.approx(73_171, rel=2e-4)
    assert analysis.ellipsoid_distance(8.0, 49.0, 8.0, 49.001, np) == pytest.approx(111.2, rel=1e-3)


def test_histogram_percentile():
    hist = np.zeros(100, dtype=np.int64)
    hist[10] = 50
    hist[90] = 50
    assert analysis.histogram_percentile(hist, 0.1, 0.5) == pytest.approx(1.05)
    assert analysis.histogram_percentile(hist, 0.1, 0.95) == pytest.approx(9.05)
    assert analysis.histogram_percentile(np.zeros(5, dtype=np.int64), 0.1, 0.5) is None
