"""Unit tests for the Smart Farming worker's CPU math helpers.

Run with:  python -m pytest tests/python
Requires numpy and pytest; OpenCV, GDAL and CUDA are not needed.
"""

import importlib.util
import math
import pathlib

import numpy as np
import pytest

WORKER_PATH = (
    pathlib.Path(__file__).resolve().parents[2]
    / "desktop"
    / "DroneDash_x64.Desktop"
    / "SmartFarming"
    / "Workers"
    / "opencv_m3m.py"
)

DEFAULT_ZONE_THRESHOLDS = (0.20, 0.40, 0.60, 0.80)


@pytest.fixture(scope="module")
def worker():
    spec = importlib.util.spec_from_file_location(
        "dronedash_worker_math", WORKER_PATH
    )
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def _empty_stats():
    return {
        "count": 0,
        "sum": 0.0,
        "sumSquares": 0.0,
        "minimum": None,
        "maximum": None,
    }


def test_index_cpu_computes_normalized_difference(worker):
    nir = np.array([[0.5, 0.8], [0.3, 0.0]], dtype=np.float32)
    red = np.array([[0.1, 0.2], [0.3, 0.4]], dtype=np.float32)

    result = worker._index_cpu(nir, red, 1e-6, np)

    expected = (nir - red) / (nir + red)
    assert result.dtype == np.float32
    np.testing.assert_allclose(result, expected, rtol=1e-6)


def test_index_cpu_stays_within_unit_range_for_reflectance(worker):
    rng = np.random.default_rng(42)
    nir = rng.uniform(0.0, 1.0, (64, 64)).astype(np.float32)
    red = rng.uniform(0.0, 1.0, (64, 64)).astype(np.float32)

    result = worker._index_cpu(nir, red, 1e-6, np)

    finite = result[np.isfinite(result)]
    assert finite.size > 0
    assert finite.min() >= -1.0
    assert finite.max() <= 1.0


def test_index_cpu_marks_invalid_pixels_as_nan(worker):
    positive = np.array([0.0, np.nan, np.inf, 0.4], dtype=np.float32)
    comparison = np.array([0.0, 0.2, 0.1, -0.4], dtype=np.float32)

    with np.errstate(invalid="ignore"):
        result = worker._index_cpu(positive, comparison, 1e-6, np)

    # 0/0, NaN input, infinite input and a zero denominator are all rejected.
    assert np.isnan(result).all()


def test_zones_cpu_assigns_five_classes_and_nodata(worker):
    values = np.array(
        [-0.5, 0.1999, 0.2, 0.3999, 0.4, 0.6, 0.8, 1.0, np.nan],
        dtype=np.float32,
    )

    zones = worker._zones_cpu(values, DEFAULT_ZONE_THRESHOLDS, np)

    assert zones.dtype == np.uint8
    assert zones.tolist() == [1, 1, 2, 2, 3, 4, 5, 5, 0]


def test_stats_accumulate_across_tiles(worker):
    first = np.array([0.1, 0.2, np.nan], dtype=np.float32)
    second = np.array([0.6, np.inf, 0.9], dtype=np.float32)
    stats = _empty_stats()

    worker._update_stats(first, np, stats)
    worker._update_stats(np.array([np.nan], dtype=np.float32), np, stats)
    worker._update_stats(second, np, stats)
    final = worker._final_stats(stats)

    expected = np.array([0.1, 0.2, 0.6, 0.9], dtype=np.float32).astype(
        np.float64
    )
    assert final["validPixels"] == 4
    assert final["minimum"] == pytest.approx(expected.min())
    assert final["maximum"] == pytest.approx(expected.max())
    assert final["average"] == pytest.approx(expected.mean())
    assert final["standardDeviation"] == pytest.approx(expected.std())


def test_final_stats_rejects_raster_without_finite_pixels(worker):
    with pytest.raises(RuntimeError):
        worker._final_stats(_empty_stats())


def test_final_stats_never_reports_negative_variance(worker):
    stats = _empty_stats()
    worker._update_stats(np.full(1000, 0.7, dtype=np.float32), np, stats)

    final = worker._final_stats(stats)

    assert final["standardDeviation"] >= 0.0
    assert not math.isnan(final["standardDeviation"])


@pytest.mark.parametrize(
    ("value", "expected"),
    [("auto", None), (" AUTO ", None), ("512", 512), (256, 256)],
)
def test_parse_auto_or_int_accepts_valid_values(worker, value, expected):
    assert worker._parse_auto_or_int(value, 64, 4096, "--tile-size") == expected


@pytest.mark.parametrize("value", ["abc", "", "12.5", "63", "4097"])
def test_parse_auto_or_int_rejects_invalid_values(worker, value):
    with pytest.raises(RuntimeError, match="--tile-size"):
        worker._parse_auto_or_int(value, 64, 4096, "--tile-size")


@pytest.mark.parametrize(
    ("cuda", "cpu", "expected"),
    [(0, 0, "cpu"), (0, 3, "cpu"), (2, 0, "cuda"), (2, 3, "mixed")],
)
def test_backend_usage_summarizes_operations(worker, cuda, cpu, expected):
    assert worker._backend_usage(cuda, cpu) == expected


def test_tile_windows_cover_raster_exactly_once(worker):
    width, height, tile = 1000, 700, 256
    coverage = np.zeros((height, width), dtype=np.uint8)

    for x, y, w, h in worker._tile_windows(width, height, tile):
        assert 0 < w <= tile and 0 < h <= tile
        coverage[y:y + h, x:x + w] += 1

    assert (coverage == 1).all()
