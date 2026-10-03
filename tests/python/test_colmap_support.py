"""Tests for the COLMAP support module (pure numpy parts; GDAL/laspy round trips when installed)."""

import json
import math
import pathlib
import struct
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

from dronedash_worker import colmap_support  # noqa: E402


def rotation_matrix(yaw, pitch, roll):
    cy, sy = math.cos(yaw), math.sin(yaw)
    cp, sp = math.cos(pitch), math.sin(pitch)
    cr, sr = math.cos(roll), math.sin(roll)
    rz = np.array([[cy, -sy, 0], [sy, cy, 0], [0, 0, 1]])
    ry = np.array([[cp, 0, sp], [0, 1, 0], [-sp, 0, cp]])
    rx = np.array([[1, 0, 0], [0, cr, -sr], [0, sr, cr]])
    return rz @ ry @ rx


def quaternion(r):
    """Rotation matrix -> COLMAP quaternion (qw, qx, qy, qz)."""
    qw = math.sqrt(max(0.0, 1 + r[0, 0] + r[1, 1] + r[2, 2])) / 2
    return (
        qw,
        (r[2, 1] - r[1, 2]) / (4 * qw),
        (r[0, 2] - r[2, 0]) / (4 * qw),
        (r[1, 0] - r[0, 1]) / (4 * qw),
    )


def write_images_bin(path, cameras):
    """cameras: list of (name, R world->camera, centre); 2 dummy 2D points each."""
    with open(path, "wb") as handle:
        handle.write(struct.pack("<Q", len(cameras)))
        for image_id, (name, rotation, center) in enumerate(cameras, start=1):
            translation = -rotation @ np.asarray(center, dtype=float)
            handle.write(struct.pack("<I7dI", image_id, *quaternion(rotation), *translation, 1))
            handle.write(name.encode("utf-8") + b"\x00")
            handle.write(struct.pack("<Q", 2))
            handle.write(struct.pack("<ddq", 1.0, 2.0, -1) * 2)


def write_ply(path, xyz, rgb=None, binary=True):
    names = ["x", "y", "z"] + (["red", "green", "blue"] if rgb is not None else [])
    header = ["ply", "format binary_little_endian 1.0" if binary else "format ascii 1.0",
              f"element vertex {len(xyz)}", "property float x", "property float y", "property float z"]
    if rgb is not None:
        header += ["property uchar red", "property uchar green", "property uchar blue"]
    header += ["element face 0", "property list uchar int vertex_indices", "end_header"]
    dtype = np.dtype([(n, "<f4") for n in names[:3]] + [(n, "u1") for n in names[3:]])
    data = np.zeros(len(xyz), dtype=dtype)
    data["x"], data["y"], data["z"] = xyz[:, 0], xyz[:, 1], xyz[:, 2]
    if rgb is not None:
        data["red"], data["green"], data["blue"] = rgb[:, 0], rgb[:, 1], rgb[:, 2]
    with open(path, "wb") as handle:
        handle.write(("\n".join(header) + "\n").encode("ascii"))
        if binary:
            handle.write(data.tobytes())
        else:
            for row in data:
                handle.write((" ".join(str(v) for v in row) + "\n").encode("ascii"))


def test_utm_epsg_zones_and_hemispheres():
    assert colmap_support.utm_epsg(8.4, 49.0) == 32632
    assert colmap_support.utm_epsg(-74.0, 40.7) == 32618
    assert colmap_support.utm_epsg(151.2, -33.9) == 32756
    assert colmap_support.utm_epsg(180.0, 10.0) == 32660


def test_local_offset_rounds_to_100_m():
    assert colmap_support.local_offset([456020.0, 456100.0], [5430018.0, 5430090.0]) == (456100.0, 5430100.0)


def test_umeyama_recovers_similarity():
    rng = np.random.default_rng(1)
    source = rng.normal(size=(20, 3)) * 10
    rotation = rotation_matrix(0.7, -0.2, 0.1)
    target = (2.5 * (rotation @ source.T)).T + np.array([100.0, -50.0, 20.0])

    scale, r, t = colmap_support.umeyama(source, target, np)

    assert scale == pytest.approx(2.5)
    np.testing.assert_allclose(r, rotation, atol=1e-9)
    np.testing.assert_allclose(t, [100.0, -50.0, 20.0], atol=1e-8)


def test_umeyama_rejects_degenerate_input():
    with pytest.raises(RuntimeError):
        colmap_support.umeyama(np.ones((4, 3)), np.zeros((4, 3)), np)


def test_robust_similarity_rejects_outlier_camera():
    rng = np.random.default_rng(2)
    source = rng.uniform(-50, 50, size=(15, 3))
    target = 0.5 * source + np.array([10.0, 20.0, 150.0]) + rng.normal(scale=0.02, size=source.shape)
    target[4] += [8.0, 0.0, 0.0]  # a camera with a wrong GPS fix

    scale, _, _, residual, keep = colmap_support.robust_similarity(source, target, np)

    assert scale == pytest.approx(0.5, rel=1e-3)
    assert not keep[4] and keep.sum() == 14
    assert residual[4] > 7.0
    assert np.sqrt(np.mean(residual[keep] ** 2)) < 0.05


def test_robust_similarity_needs_three_cameras():
    with pytest.raises(RuntimeError):
        colmap_support.robust_similarity(np.zeros((2, 3)), np.zeros((2, 3)), np)


def test_read_camera_centers_from_images_bin(tmp_path):
    cameras = [
        ("a.jpg", rotation_matrix(0.3, 0.1, -0.2), (1.0, 2.0, 3.0)),
        ("sub/b.jpg", rotation_matrix(-1.2, 0.0, 3.0), (-4.0, 5.5, 0.25)),
    ]
    write_images_bin(tmp_path / "images.bin", cameras)

    centers = colmap_support.read_camera_centers(str(tmp_path / "images.bin"))

    assert sorted(centers) == ["a.jpg", "sub/b.jpg"]
    np.testing.assert_allclose(centers["a.jpg"], (1.0, 2.0, 3.0), atol=1e-9)
    np.testing.assert_allclose(centers["sub/b.jpg"], (-4.0, 5.5, 0.25), atol=1e-9)


@pytest.mark.parametrize("binary", [True, False])
def test_read_ply_vertices(tmp_path, binary):
    xyz = np.array([[1.5, 2.0, 3.0], [-1.0, 0.0, 10.25]])
    rgb = np.array([[255, 0, 10], [1, 2, 3]])
    write_ply(tmp_path / "cloud.ply", xyz, rgb, binary=binary)

    vertices = colmap_support.read_ply_vertices(str(tmp_path / "cloud.ply"), np)

    np.testing.assert_allclose(vertices["x"], [1.5, -1.0])
    np.testing.assert_allclose(vertices["z"], [3.0, 10.25])
    assert list(vertices["red"]) == [255, 1]


def test_read_ply_rejects_other_files(tmp_path):
    (tmp_path / "x.ply").write_bytes(b"not a ply\n")
    with pytest.raises(RuntimeError):
        colmap_support.read_ply_vertices(str(tmp_path / "x.ply"), np)


def test_rasterize_max_keeps_highest_point_per_cell():
    x = np.array([0.05, 0.06, 0.95, 0.15])
    y = np.array([0.95, 0.96, 0.05, 0.95])
    z = np.array([1.0, 3.0, 5.0, 2.0])

    grid, gt = colmap_support.rasterize_max(x, y, z, 0.1, np)

    assert gt == (0.0, 0.1, 0.0, 1.0, 0.0, -0.1)
    assert grid.shape == (10, 10)
    assert grid[0, 0] == 3.0
    assert grid[0, 1] == 2.0
    assert grid[9, 9] == 5.0
    assert np.isnan(grid[5, 5])
    with pytest.raises(ValueError):
        colmap_support.rasterize_max(x, y, z, 0.0, np)


def test_isolated_point_mask_drops_floaters_only():
    gx, gy = np.meshgrid(np.arange(0, 10, 0.2), np.arange(0, 10, 0.2))
    x = np.concatenate([gx.ravel(), [5.0, 2.0]])
    y = np.concatenate([gy.ravel(), [5.0, 8.0]])
    z = np.concatenate([np.full(gx.size, 100.0), [160.0, 40.0]])  # two floating points

    keep = colmap_support.isolated_point_mask(x, y, z, 0.5, 5, np)

    assert keep[:-2].all()
    assert not keep[-2:].any()
    with pytest.raises(ValueError):
        colmap_support.isolated_point_mask(x, y, z, 0.0, 5, np)


def run_worker(*args):
    completed = subprocess.run(
        [sys.executable, str(WORKERS / "opencv_m3m.py"), *args],
        capture_output=True, text=True, check=False)
    assert completed.returncode == 0, completed.stderr
    return json.loads(completed.stdout.strip().splitlines()[-1])


def test_georef_and_products_round_trip(tmp_path):
    pytest.importorskip("osgeo.gdal")
    laspy = pytest.importorskip("laspy")
    if not laspy.LazBackend.detect_available():
        pytest.skip("laspy without LAZ backend")
    from osgeo import gdal, osr

    gdal.UseExceptions()
    osr.UseExceptions()
    # Nine cameras on a 3x3 grid, 60 m above a 10 m high flat surface (UTM 32N near Karlsruhe).
    wgs84, utm = osr.SpatialReference(), osr.SpatialReference()
    wgs84.ImportFromEPSG(4326)
    utm.ImportFromEPSG(32632)
    for srs in (wgs84, utm):
        srs.SetAxisMappingStrategy(osr.OAMS_TRADITIONAL_GIS_ORDER)
    to_wgs = osr.CoordinateTransformation(utm, wgs84)

    images, world = [], []
    for i in range(3):
        for j in range(3):
            e, n, h = 456000.0 + 20 * i, 5430000.0 + 20 * j, 170.0
            lon, lat = to_wgs.TransformPoint(e, n)[:2]
            images.append({"name": f"img_{i}{j}.jpg", "lat": lat, "lon": lon, "alt": h})
            world.append((e, n, h))
    (tmp_path / "images.json").write_text(json.dumps(images))

    georef = run_worker("--colmap-georef", "--images", str(tmp_path / "images.json"),
                        "--output-dir", str(tmp_path / "georef"))
    assert georef["epsg"] == 32632
    assert georef["offset"] == [456000.0, 5430000.0, 0.0]

    # The COLMAP model frame: world = s * R * model + t, unknown to the worker.
    scale, rotation, shift = 4.0, rotation_matrix(0.4, 0.05, 3.1), np.array([456010.0, 5430030.0, 90.0])

    def to_model(points):
        return ((rotation.T @ (np.asarray(points, dtype=float) - shift).T) / scale).T

    centers = to_model(world)
    write_images_bin(tmp_path / "images.bin",
                     [(img["name"], rotation_matrix(0.1, 2.9, 0.2), c) for img, c in zip(images, centers)])

    gx, gy = np.meshgrid(np.arange(456000.0, 456040.0, 0.25), np.arange(5430000.0, 5430040.0, 0.25))
    surface = np.column_stack([gx.ravel(), gy.ravel(), np.full(gx.size, 10.0)])
    surface[(np.abs(surface[:, 0] - 456020) < 2) & (np.abs(surface[:, 1] - 5430020) < 2), 2] = 14.0  # a 4 m box
    rgb = np.tile([[10, 200, 30]], (len(surface), 1))
    write_ply(tmp_path / "fused.ply", to_model(surface), rgb)

    result = run_worker("--colmap-products", "--ply", str(tmp_path / "fused.ply"),
                        "--sparse", str(tmp_path), "--georef", str(tmp_path / "georef" / "georef.json"),
                        "--output-dir", str(tmp_path / "products"), "--resolution", "0.5")

    geo = result["georeferencing"]
    assert geo["usedImages"] == 9 and geo["rejected"] == []
    assert geo["rmseMeters"] < 0.01
    assert result["points"] == len(surface) and result["removedIsolatedPoints"] == 0
    assert result["elevationRange"] == pytest.approx([10.0, 14.0], abs=0.01)
    assert json.loads((tmp_path / "products" / "colmap-products.json").read_text())["points"] == len(surface)

    las = laspy.read(result["pointCloud"])
    np.testing.assert_allclose(np.sort(np.asarray(las.x))[[0, -1]], [456000.0, 456039.75], atol=0.01)
    assert int(las.red[0]) == 10 * 257

    dsm = gdal.Open(result["dsm"])
    band = dsm.GetRasterBand(1)
    values = band.ReadAsArray()
    assert band.GetNoDataValue() == -9999.0
    assert osr.SpatialReference(wkt=dsm.GetProjection()).GetAuthorityCode(None) == "32632"
    gt = dsm.GetGeoTransform()
    col, row = int((456020.0 - gt[0]) / gt[1]), int((5430020.0 - gt[3]) / gt[5])
    assert values[row, col] == pytest.approx(14.0, abs=0.01)
    assert values[2, 2] == pytest.approx(10.0, abs=0.01)


def test_products_needs_three_referenced_cameras(tmp_path):
    pytest.importorskip("osgeo.gdal")
    georef = tmp_path / "georef"
    georef.mkdir()
    (georef / "georef.json").write_text(json.dumps({"epsg": 32632, "offset": [0, 0, 0]}))
    (georef / "georef.txt").write_text("a.jpg 0 0 100\nb.jpg 10 0 100\n")
    write_images_bin(tmp_path / "images.bin", [("a.jpg", np.eye(3), (0, 0, 0)), ("b.jpg", np.eye(3), (1, 0, 0))])
    write_ply(tmp_path / "fused.ply", np.zeros((1, 3)))

    completed = subprocess.run(
        [sys.executable, str(WORKERS / "opencv_m3m.py"), "--colmap-products", "--ply", str(tmp_path / "fused.ply"),
         "--sparse", str(tmp_path), "--georef", str(georef / "georef.json"), "--output-dir", str(tmp_path / "out")],
        capture_output=True, text=True, check=False)

    assert completed.returncode != 0
    assert "at least 3" in completed.stderr + completed.stdout
