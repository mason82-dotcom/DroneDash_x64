"""Point cloud previews: evenly thinned, centre-relative points for the 3D viewer."""

import json
import math
import os

MIN_POINTS = 10_000
MAX_POINTS = 20_000_000
_CHUNK_SIZE = 1_000_000


def plan_stride(total_points, max_points):
    """Keep every n-th point so that at most max_points remain."""
    if total_points <= 0:
        raise RuntimeError("point cloud contains no points")
    return max(1, math.ceil(total_points / max_points))


def selection_mask(start_index, count, stride, np):
    """Mask of the points in [start_index, start_index + count) whose global index is a multiple of stride."""
    first = (-start_index) % stride
    mask = np.zeros(count, dtype=bool)
    mask[first::stride] = True
    return mask


def colors_to_8bit(red, green, blue, np):
    """LAS colours are nominally 16 bit, but some writers store 8-bit values."""
    rgb = np.stack([red, green, blue], axis=1).astype(np.uint32)
    if rgb.size and rgb.max() > 255:
        rgb >>= 8
    return rgb.astype(np.uint8)


def relative_positions(x, y, z, center, np):
    """Float64 world coordinates -> float32 offsets from the centre (keeps mm precision for UTM)."""
    return np.stack(
        [
            np.asarray(x, dtype=np.float64) - center[0],
            np.asarray(y, dtype=np.float64) - center[1],
            np.asarray(z, dtype=np.float64) - center[2],
        ],
        axis=1,
    ).astype(np.float32)


def _replace_atomic(write, path):
    temp = path + "." + os.urandom(8).hex() + ".tmp"
    try:
        write(temp)
        os.replace(temp, path)
    finally:
        if os.path.exists(temp):
            os.remove(temp)


def _crs_name(header):
    try:
        crs = header.parse_crs()
        return crs.name if crs is not None else None
    except Exception:
        # parse_crs needs pyproj; the viewer works in local coordinates anyway.
        return None


def pointcloud_preview(args):
    import numpy as np

    try:
        import laspy
    except ImportError:
        raise RuntimeError(
            'reading point clouds requires laspy with LAZ support: pip install "laspy[lazrs]"'
        )

    max_points = int(args.max_points)
    if max_points < MIN_POINTS or max_points > MAX_POINTS:
        raise RuntimeError(f"--max-points must be within {MIN_POINTS}..{MAX_POINTS}")

    with laspy.open(args.source) as reader:
        header = reader.header
        total = int(header.point_count)
        stride = plan_stride(total, max_points)

        mins = [float(v) for v in header.mins]
        maxs = [float(v) for v in header.maxs]
        center = [(lo + hi) / 2.0 for lo, hi in zip(mins, maxs)]

        dimensions = set(header.point_format.dimension_names)
        has_color = {"red", "green", "blue"} <= dimensions
        has_classification = "classification" in dimensions

        positions, colors, classes = [], [], []
        start = 0
        for chunk in reader.chunk_iterator(_CHUNK_SIZE):
            count = len(chunk)
            mask = selection_mask(start, count, stride, np)
            start += count
            if not mask.any():
                continue

            positions.append(
                relative_positions(chunk.x[mask], chunk.y[mask], chunk.z[mask], center, np)
            )
            if has_color:
                colors.append(
                    colors_to_8bit(chunk.red[mask], chunk.green[mask], chunk.blue[mask], np)
                )
            if has_classification:
                classes.append(np.asarray(chunk.classification[mask], dtype=np.uint8))

    if not positions:
        raise RuntimeError("point cloud contains no points")

    xyz = np.concatenate(positions)
    os.makedirs(args.output_dir, exist_ok=True)

    files = {"positions": "positions.f32"}
    _replace_atomic(lambda p: xyz.astype("<f4").tofile(p), os.path.join(args.output_dir, files["positions"]))

    if has_color:
        rgb = np.concatenate(colors)
        files["colors"] = "colors.u8"
        _replace_atomic(lambda p: rgb.tofile(p), os.path.join(args.output_dir, files["colors"]))

    present_classes = []
    if has_classification:
        cls = np.concatenate(classes)
        files["classification"] = "classification.u8"
        _replace_atomic(lambda p: cls.tofile(p), os.path.join(args.output_dir, files["classification"]))
        present_classes = sorted(int(v) for v in np.unique(cls))

    result = {
        "schemaVersion": 1,
        "source": os.path.abspath(args.source),
        "sourceCrs": _crs_name(header),
        "lasVersion": f"{header.version.major}.{header.version.minor}",
        "pointFormat": int(header.point_format.id),
        "totalPoints": total,
        "points": int(xyz.shape[0]),
        "stride": stride,
        "center": center,
        "min": [m - c for m, c in zip(mins, center)],
        "max": [m - c for m, c in zip(maxs, center)],
        "hasColor": has_color,
        "classes": present_classes,
        "files": files,
    }

    _replace_atomic(
        lambda p: open(p, "w", encoding="utf-8").write(json.dumps(result, indent=2)),
        os.path.join(args.output_dir, "pointcloud-preview.json"),
    )
    print(json.dumps(result))
