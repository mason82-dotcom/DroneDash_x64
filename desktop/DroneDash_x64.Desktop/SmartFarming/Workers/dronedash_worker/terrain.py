"""Terrain and obstacle check of a planned flight route against a surface model (DSM).

DJI waylines fly at a height relative to the take-off point. The flight height above the
model is therefore  take-off ground + route altitude - surface; the route is checked against
the highest surface within a horizontal safety buffer, so trees, buildings and masts next to
the line count as well.
"""

import json
import math

from .analysis import _from_wgs84, _open_model, _read_window, _to_pixels, _to_wgs84, bilinear, densify

_MAX_GRID_CELLS = 25_000_000
_MAX_SAMPLES = 200_000
_MAX_MAP_POINTS = 6000
_MAX_VIOLATIONS = 200
_MAX_PROFILE_POINTS = 1500


def nan_max_filter(grid, radius, np):
    """Square maximum filter (2*radius+1 cells) that ignores NaN; NaN where the window holds no data."""
    if radius <= 0:
        return grid.copy()
    work = np.where(np.isfinite(grid), grid, -np.inf)
    for axis in (0, 1):
        out = work.copy()
        size = work.shape[axis]
        for shift in range(1, min(radius, size - 1) + 1):
            if axis == 0:
                np.maximum(out[shift:, :], work[:-shift, :], out=out[shift:, :])
                np.maximum(out[:-shift, :], work[shift:, :], out=out[:-shift, :])
            else:
                np.maximum(out[:, shift:], work[:, :-shift], out=out[:, shift:])
                np.maximum(out[:, :-shift], work[:, shift:], out=out[:, :-shift])
        work = out
    return np.where(np.isfinite(work), work, np.nan)


def classify(clearance, threshold, np):
    """0 ok, 1 below the minimum clearance, 2 at or below the surface, 3 no model data."""
    status = np.zeros(len(clearance), dtype=np.int8)
    finite = np.isfinite(clearance)
    status[finite & (clearance < threshold)] = 1
    status[finite & (clearance <= 0.0)] = 2
    status[~finite] = 3
    return status


def runs(status, np):
    """(start, end_inclusive, value) for consecutive equal values."""
    if len(status) == 0:
        return []
    edges = np.flatnonzero(np.diff(status)) + 1
    starts = np.concatenate([[0], edges])
    ends = np.concatenate([edges - 1, [len(status) - 1]])
    return [(int(s), int(e), int(status[s])) for s, e in zip(starts, ends)]


_STATUS_NAMES = ("ok", "low", "collision", "nodata")


def profile_indices(clearance, budget, np):
    """About `budget` sample indices: per bucket the tightest one, so no narrow spot is lost,
    plus the first and last sample so the profile spans the whole line."""
    count = len(clearance)
    if count <= budget:
        return np.arange(count)
    edges = np.linspace(0, count, budget + 1).astype(int)
    picked = []
    for start, end in zip(edges[:-1], edges[1:]):
        if end <= start:
            continue
        window = clearance[start:end]
        finite = np.isfinite(window)
        picked.append(start + (int(np.argmin(np.where(finite, window, np.inf))) if finite.any() else 0))
    return np.unique(np.array([0, *picked, count - 1]))


def _sample_nearest(grid, gt, xs, ys, np):
    cols = np.floor((np.asarray(xs) - gt[0]) / gt[1]).astype(int)
    rows = np.floor((np.asarray(ys) - gt[3]) / gt[5]).astype(int)
    inside = (cols >= 0) & (rows >= 0) & (cols < grid.shape[1]) & (rows < grid.shape[0])
    values = np.full(len(cols), np.nan)
    values[inside] = grid[rows[inside], cols[inside]]
    return values


def _surface_grid(ds, srs, gt, xs, ys, buffer, np):
    """Surface model resampled with 'max' (obstacles survive) on the route's bounding box."""
    from osgeo import gdal

    native = min(abs(gt[1]), abs(gt[5]))
    pad = buffer + 2 * native
    min_x, max_x = min(xs) - pad, max(xs) + pad
    min_y, max_y = min(ys) - pad, max(ys) + pad
    cell = max(native, 0.5)
    while (max_x - min_x) * (max_y - min_y) / (cell * cell) > _MAX_GRID_CELLS:
        cell *= 1.5

    band = ds.GetRasterBand(1)
    nodata = band.GetNoDataValue()
    warped = gdal.Warp(
        "",
        ds,
        format="MEM",
        outputBounds=(min_x, min_y, max_x, max_y),
        xRes=cell,
        yRes=cell,
        resampleAlg="max" if cell > native * 1.01 else "near",
        srcNodata=nodata,
        dstNodata=float("nan"),
        outputType=gdal.GDT_Float64,
    )
    grid = warped.GetRasterBand(1).ReadAsArray().astype(np.float64)
    grid[~np.isfinite(grid)] = np.nan
    if nodata is not None and math.isfinite(nodata):
        grid[grid == nodata] = np.nan
    return grid, warped.GetGeoTransform(), cell


def terrain_check(args):
    import numpy as np

    with open(args.route, encoding="utf-8") as handle:
        route = json.load(handle)

    altitude = float(route["altitude"])
    threshold = float(route.get("minClearance", 30.0))
    buffer = max(0.0, float(route.get("buffer", 10.0)))
    follow = route.get("mode") == "follow"
    passes = [p for p in route.get("passes", []) if len(p.get("points", [])) >= 2]
    if not passes:
        raise RuntimeError("the route has no flight lines")

    ds, srs, gt = _open_model(args.dem)
    if not srs.IsProjected():
        raise RuntimeError("the terrain check needs a projected elevation model (e.g. UTM), not geographic degrees")

    projected = []
    for flight_pass in passes:
        xs, ys = _from_wgs84([tuple(p) for p in flight_pass["points"]], srs)
        projected.append((flight_pass, xs, ys))
    all_x = [x for _, xs, _ in projected for x in xs]
    all_y = [y for _, _, ys in projected for y in ys]

    start = route.get("start") or passes[0]["points"][0]
    start_x, start_y = _from_wgs84([tuple(start)], srs)
    if route.get("startElevation") is not None:
        start_elevation = float(route["startElevation"])
        start_source = "manual"
    else:
        window, window_gt = _read_window(ds, gt, start_x, start_y, np)
        cols, rows = _to_pixels(window_gt, start_x, start_y, np)
        start_elevation = float(bilinear(window, cols, rows, np)[0])
        start_source = "model"
        if not math.isfinite(start_elevation):
            raise RuntimeError("the elevation model has no height at the take-off point; enter the take-off height manually")

    surface, surface_gt, cell = _surface_grid(ds, srs, gt, all_x + start_x, all_y + start_y, buffer, np)
    obstacles = nan_max_filter(surface, int(math.ceil(buffer / cell)), np)

    total_length = sum(
        math.hypot(xs[i] - xs[i - 1], ys[i] - ys[i - 1]) for _, xs, ys in projected for i in range(1, len(xs)))
    step = max(cell, total_length / _MAX_SAMPLES)
    flight_elevation = start_elevation + altitude

    pass_results, violations, chunks, profile = [], [], [], []
    profile_offset = 0.0
    # Exact sample count of densify() per pass, so the profile budget is split without overshoot.
    pass_samples = [
        1 + sum(max(1, int(math.ceil(math.hypot(xs[i] - xs[i - 1], ys[i] - ys[i - 1]) / step)))
                for i in range(1, len(xs)))
        for _, xs, ys in projected
    ]
    total_samples = sum(pass_samples)
    overall_min = None
    sample_count = 0
    nodata_count = 0
    terrain_min, terrain_max, obstacle_max = math.inf, -math.inf, -math.inf
    map_budget = max(2, _MAX_MAP_POINTS // max(1, len(projected)))

    for flight_pass, xs, ys in projected:
        sx, sy, distance = densify(xs, ys, step, np)
        ground = _sample_nearest(surface, surface_gt, sx, sy, np)
        obstacle = _sample_nearest(obstacles, surface_gt, sx, sy, np)
        flight = ground + altitude if follow else np.full(len(sx), flight_elevation)
        clearance = flight - obstacle
        status = classify(clearance, threshold, np)

        sample_count += len(sx)
        nodata_count += int((status == 3).sum())
        if np.isfinite(ground).any():
            terrain_min = min(terrain_min, float(np.nanmin(ground)))
            terrain_max = max(terrain_max, float(np.nanmax(ground)))
        if np.isfinite(obstacle).any():
            obstacle_max = max(obstacle_max, float(np.nanmax(obstacle)))

        coords = _to_wgs84(sx, sy, srs)
        pass_min = None
        if np.isfinite(clearance).any():
            index = int(np.nanargmin(clearance))
            pass_min = {
                "clearance": round(float(clearance[index]), 2),
                "obstacle": round(float(obstacle[index]), 2),
                "distance": round(float(distance[index]), 1),
                "lon": coords[index][0],
                "lat": coords[index][1],
                "pass": flight_pass.get("name"),
            }
            if overall_min is None or pass_min["clearance"] < overall_min["clearance"]:
                overall_min = pass_min

        # Profile along the whole route (passes one after another), thinned to a chart-sized list.
        budget = max(2, _MAX_PROFILE_POINTS * len(sx) // total_samples)
        for k in profile_indices(clearance, budget, np):
            k = int(k)
            profile.append({
                "distance": round(profile_offset + float(distance[k]), 1),
                "ground": None if not math.isfinite(ground[k]) else round(float(ground[k]), 2),
                "obstacle": None if not math.isfinite(obstacle[k]) else round(float(obstacle[k]), 2),
                "flight": None if not math.isfinite(flight[k]) else round(float(flight[k]), 2),
                "lon": coords[k][0],
                "lat": coords[k][1],
                "pass": flight_pass.get("name"),
            })
        profile_offset += float(distance[-1])

        pass_results.append({
            "id": flight_pass.get("id"),
            "name": flight_pass.get("name"),
            "lengthMeters": round(float(distance[-1]), 1),
            "minimumClearance": pass_min["clearance"] if pass_min else None,
        })

        # Keep every k-th point of a run so the map stays light; run ends always survive.
        keep_every = max(1, int(math.ceil(len(sx) / map_budget)))
        for first, last, value in runs(status, np):
            picked = list(range(first, last + 1, keep_every))
            if picked[-1] != last:
                picked.append(last)
            # Overlap by one sample so coloured pieces join without gaps.
            if last + 1 < len(sx):
                picked.append(last + 1)
            chunks.append({
                "status": _STATUS_NAMES[value],
                "pass": flight_pass.get("name"),
                "points": [[round(coords[k][1], 7), round(coords[k][0], 7)] for k in picked],
            })

            if value in (1, 2) and len(violations) < _MAX_VIOLATIONS:
                worst = first + int(np.nanargmin(clearance[first:last + 1]))
                violations.append({
                    "pass": flight_pass.get("name"),
                    "fromDistance": round(float(distance[first]), 1),
                    "toDistance": round(float(distance[last]), 1),
                    "minimumClearance": round(float(clearance[worst]), 2),
                    "obstacle": round(float(obstacle[worst]), 2),
                    "lon": coords[worst][0],
                    "lat": coords[worst][1],
                    "collision": value == 2,
                })

    result = {
        "schemaVersion": 1,
        "dem": args.dem,
        "mode": "follow" if follow else "relative",
        "altitude": altitude,
        "minClearance": threshold,
        "bufferMeters": buffer,
        "cellMeters": round(cell, 3),
        "stepMeters": round(step, 3),
        "startElevation": round(start_elevation, 2),
        "startSource": start_source,
        "flightElevation": None if follow else round(flight_elevation, 2),
        "lengthMeters": round(total_length, 1),
        "samples": sample_count,
        "coverage": round(1.0 - nodata_count / max(1, sample_count), 4),
        "terrainRange": None if not math.isfinite(terrain_min) else [round(terrain_min, 2), round(terrain_max, 2)],
        "obstacleMax": None if not math.isfinite(obstacle_max) else round(obstacle_max, 2),
        "minimum": overall_min,
        "passes": pass_results,
        "violations": violations,
        "violationsTruncated": len(violations) >= _MAX_VIOLATIONS,
        "chunks": chunks,
        "profile": profile,
    }
    print(json.dumps(result))
