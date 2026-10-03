"""Tests for the point-cloud preview (numpy helpers; LAZ round trip when laspy is installed)."""

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

from dronedash_worker import pointcloud  # noqa: E402


@pytest.mark.parametrize(
    ("total", "budget", "expected"),
    [(100, 1000, 1), (1000, 1000, 1), (1001, 1000, 2), (2_400_000, 500_000, 5)],
)
def test_plan_stride(total, budget, expected):
    assert pointcloud.plan_stride(total, budget) == expected


def test_plan_stride_rejects_empty_cloud():
    with pytest.raises(RuntimeError):
        pointcloud.plan_stride(0, 1000)


def test_selection_mask_is_continuous_across_chunks():
    # Chunk sizes that do not divide the stride must still pick every 3rd global index.
    stride, picked, start = 3, [], 0
    for count in (7, 5, 11, 1):
        mask = pointcloud.selection_mask(start, count, stride, np)
        picked.extend(np.flatnonzero(mask) + start)
        start += count
    assert picked == list(range(0, 24, 3))


def test_colors_to_8bit_scales_16bit_and_keeps_8bit():
    sixteen = pointcloud.colors_to_8bit(np.array([65535, 0]), np.array([257, 0]), np.array([32896, 0]), np)
    assert sixteen.tolist() == [[255, 1, 128], [0, 0, 0]]
    eight = pointcloud.colors_to_8bit(np.array([200]), np.array([100]), np.array([0]), np)
    assert eight.tolist() == [[200, 100, 0]]


def test_relative_positions_keep_millimetres_for_utm():
    center = (456100.0, 5430075.0, 120.0)
    x = np.array([456000.001, 456199.999])
    rel = pointcloud.relative_positions(x, np.array([5430000.0, 5430150.0]), np.array([115.0, 125.0]), center, np)
    assert rel.dtype == np.float32
    world = rel.astype(np.float64) + np.array(center)
    assert np.abs(world[:, 0] - x).max() < 1e-5


def test_preview_round_trip_with_laspy(tmp_path):
    laspy = pytest.importorskip("laspy")
    if not laspy.LazBackend.detect_available():
        pytest.skip("no LAZ backend installed")

    n = 25_000
    rng = np.random.default_rng(1)
    header = laspy.LasHeader(point_format=2, version="1.2")
    header.scales = [0.001] * 3
    header.offsets = [456000, 5430000, 0]
    las = laspy.LasData(header)
    las.x = 456000 + rng.uniform(0, 100, n)
    las.y = 5430000 + rng.uniform(0, 80, n)
    las.z = 110 + rng.uniform(0, 5, n)
    las.classification = np.where(np.arange(n) % 2 == 0, 2, 5).astype(np.uint8)
    las.red = las.green = las.blue = np.full(n, 51400, dtype=np.uint16)
    source = tmp_path / "cloud.laz"
    las.write(source)

    out = tmp_path / "preview"
    result = subprocess.run(
        [sys.executable, str(WORKERS / "opencv_m3m.py"), "--pointcloud-preview",
         "--source", str(source), "--output-dir", str(out), "--max-points", "10000"],
        check=True, capture_output=True, text=True,
    )
    meta = json.loads(result.stdout.strip().splitlines()[-1])

    assert meta["totalPoints"] == n
    assert meta["stride"] == 3
    assert meta["points"] == len(range(0, n, 3))
    assert meta["classes"] == [2, 5]
    positions = np.fromfile(out / meta["files"]["positions"], "<f4").reshape(-1, 3)
    colors = np.fromfile(out / meta["files"]["colors"], np.uint8).reshape(-1, 3)
    assert positions.shape[0] == meta["points"]
    assert (colors == 200).all()
    world = positions.astype(np.float64) + np.array(meta["center"])
    reread = laspy.read(source)
    assert np.abs(world[:, 0] - reread.x[::3]).max() < 1e-4
