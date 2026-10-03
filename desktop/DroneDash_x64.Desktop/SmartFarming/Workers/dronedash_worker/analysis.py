"""Elevation analysis on full-resolution models: profiles, volumes and canopy height (DSM - DTM)."""

import json
import math

_GRS80_A = 6378137.0
_GRS80_E2 = 0.00669438002290
_CHM_NODATA = -9999.0
_CHM_HIST_MAX = 100.0
_CHM_HIST_STEP = 0.01


def parse_coordinates(text, minimum):
    """'lon,lat;lon,lat;...' (WGS84) -> list of (lon, lat)."""
    points = []
    for part in (text or "").split(";"):
        part = part.strip()
        if not part:
            continue
        try:
            lon, lat = (float(v) for v in part.split(","))
        except ValueError:
            raise RuntimeError(f"invalid coordinate '{part}', expected lon,lat")
        if not (-180.0 <= lon <= 180.0 and -90.0 <= lat <= 90.0):
            raise RuntimeError(f"coordinate outside WGS84: {part}")
        points.append((lon, lat))
    if len(points) < minimum:
        raise RuntimeError(f"at least {minimum} coordinates are required")
    return points


def densify(xs, ys, step, np, closed=False):
    """Points every `step` along a polyline (vertices kept) with the cumulative planar distance."""
    xs, ys = list(xs), list(ys)
    if closed:
        xs.append(xs[0])
        ys.append(ys[0])
    out_x, out_y, out_d = [xs[0]], [ys[0]], [0.0]
    travelled = 0.0
    for i in range(1, len(xs)):
        dx, dy = xs[i] - xs[i - 1], ys[i] - ys[i - 1]
        length = math.hypot(dx, dy)
        pieces = max(1, int(math.ceil(length / step)))
        for k in range(1, pieces + 1):
            t = k / pieces
            out_x.append(xs[i - 1] + dx * t)
            out_y.append(ys[i - 1] + dy * t)
            out_d.append(travelled + length * t)
        travelled += length
    return np.array(out_x), np.array(out_y), np.array(out_d)


def bilinear(grid, cols, rows, np):
    """Bilinear sample at fractional pixel-centre coordinates; NaN outside or next to nodata."""
    cols = np.asarray(cols, dtype=np.float64) - 0.5
    rows = np.asarray(rows, dtype=np.float64) - 0.5
    height, width = grid.shape
    c0 = np.floor(cols).astype(int)
    r0 = np.floor(rows).astype(int)
    fc, fr = cols - c0, rows - r0
    c0 = np.clip(c0, 0, width - 1)
    r0 = np.clip(r0, 0, height - 1)
    c1 = np.clip(c0 + 1, 0, width - 1)
    r1 = np.clip(r0 + 1, 0, height - 1)
    top = grid[r0, c0] * (1 - fc) + grid[r0, c1] * fc
    bottom = grid[r1, c0] * (1 - fc) + grid[r1, c1] * fc
    value = top * (1 - fr) + bottom * fr
    outside = (cols < -0.5) | (rows < -0.5) | (cols > width - 0.5) | (rows > height - 0.5)
    return np.where(outside, np.nan, value)


def fit_plane(x, y, z, np):
    """Least-squares plane z = a*x + b*y + c through finite samples."""
    x, y, z = (np.asarray(v, dtype=np.float64) for v in (x, y, z))
    ok = np.isfinite(z)
    if ok.sum() < 3:
        raise RuntimeError("not enough valid boundary heights to fit a base plane")
    x0, y0 = x[ok].mean(), y[ok].mean()
    design = np.column_stack([x[ok] - x0, y[ok] - y0, np.ones(ok.sum())])
    (a, b, c), _, rank, _ = np.linalg.lstsq(design, z[ok], rcond=None)
    if rank < 3:
        raise RuntimeError("boundary points are collinear; cannot fit a base plane")
    return a, b, c - a * x0 - b * y0


def volume_stats(z, base, mask, pixel_area, np):
    """Cut (above base), fill (below base) and height statistics inside mask."""
    inside = mask & np.isfinite(z) & np.isfinite(base)
    covered = int(mask.sum())
    if covered == 0:
        raise RuntimeError("polygon does not cover any model pixel")
    diff = (z - base)[inside].astype(np.float64)
    cut = float(np.clip(diff, 0, None).sum() * pixel_area)
    fill = float(np.clip(-diff, 0, None).sum() * pixel_area)
    return {
        "areaSquareMeters": covered * pixel_area,
        "validFraction": float(inside.sum()) / covered,
        "cutCubicMeters": cut,
        "fillCubicMeters": fill,
        "netCubicMeters": cut - fill,
        "meanHeightAboveBase": float(diff.mean()) if diff.size else None,
        "maxHeightAboveBase": float(diff.max()) if diff.size else None,
        "minHeightAboveBase": float(diff.min()) if diff.size else None,
    }


def ellipsoid_distance(lon1, lat1, lon2, lat2, np):
    """Ground distance of short segments on the GRS80 ellipsoid (local radii of curvature).

    A mean-radius sphere underestimates east-west distances by ~0.3 % at 49 deg latitude;
    using the meridional (M) and prime-vertical (N) radii keeps segments of up to a few
    kilometres within ~1e-6.
    """
    lat = np.radians((np.asarray(lat1) + np.asarray(lat2)) / 2.0)
    sin2 = np.sin(lat) ** 2
    w = np.sqrt(1.0 - _GRS80_E2 * sin2)
    meridional = _GRS80_A * (1.0 - _GRS80_E2) / w ** 3
    prime_vertical = _GRS80_A / w
    dy = np.radians(np.asarray(lat2) - np.asarray(lat1)) * meridional
    dx = np.radians(np.asarray(lon2) - np.asarray(lon1)) * prime_vertical * np.cos(lat)
    return np.hypot(dx, dy)


def histogram_percentile(hist, step, fraction):
    total = hist.sum()
    if total == 0:
        return None
    index = int((hist.cumsum() >= fraction * total).argmax())
    return (index + 0.5) * step


# ---------------------------------------------------------------- GDAL

def _open_model(path):
    from osgeo import gdal, osr

    gdal.UseExceptions()
    ds = gdal.Open(path)
    if ds is None:
        raise RuntimeError(f"cannot open elevation model: {path}")
    if not ds.GetProjection():
        raise RuntimeError("elevation model has no coordinate reference system")
    srs = osr.SpatialReference(wkt=ds.GetProjection())
    srs.SetAxisMappingStrategy(osr.OAMS_TRADITIONAL_GIS_ORDER)
    gt = ds.GetGeoTransform()
    if gt[2] != 0 or gt[4] != 0:
        raise RuntimeError("rotated elevation models are not supported")
    return ds, srs, gt


def _from_wgs84(points, srs):
    from osgeo import osr

    wgs84 = osr.SpatialReference()
    wgs84.ImportFromEPSG(4326)
    wgs84.SetAxisMappingStrategy(osr.OAMS_TRADITIONAL_GIS_ORDER)
    transform = osr.CoordinateTransformation(wgs84, srs)
    projected = [transform.TransformPoint(lon, lat)[:2] for lon, lat in points]
    return [p[0] for p in projected], [p[1] for p in projected]


def _to_wgs84(xs, ys, srs):
    from osgeo import osr

    wgs84 = osr.SpatialReference()
    wgs84.ImportFromEPSG(4326)
    wgs84.SetAxisMappingStrategy(osr.OAMS_TRADITIONAL_GIS_ORDER)
    transform = osr.CoordinateTransformation(srs, wgs84)
    return [transform.TransformPoint(float(x), float(y))[:2] for x, y in zip(xs, ys)]


def _read_window(ds, gt, xs, ys, np, pad=2):
    """Float64 window (nodata -> NaN) covering the given model coordinates."""
    cols = [(x - gt[0]) / gt[1] for x in xs]
    rows = [(y - gt[3]) / gt[5] for y in ys]
    c0 = max(0, int(math.floor(min(cols))) - pad)
    r0 = max(0, int(math.floor(min(rows))) - pad)
    c1 = min(ds.RasterXSize, int(math.ceil(max(cols))) + pad)
    r1 = min(ds.RasterYSize, int(math.ceil(max(rows))) + pad)
    if c1 <= c0 or r1 <= r0:
        raise RuntimeError("geometry lies outside the elevation model")
    band = ds.GetRasterBand(1)
    grid = band.ReadAsArray(c0, r0, c1 - c0, r1 - r0).astype(np.float64)
    nodata = band.GetNoDataValue()
    if nodata is not None and math.isfinite(nodata):
        grid[grid == nodata] = np.nan
    grid[~np.isfinite(grid)] = np.nan
    window_gt = (gt[0] + c0 * gt[1], gt[1], 0.0, gt[3] + r0 * gt[5], 0.0, gt[5])
    return grid, window_gt


def _to_pixels(window_gt, xs, ys, np):
    return (np.asarray(xs) - window_gt[0]) / window_gt[1], (np.asarray(ys) - window_gt[3]) / window_gt[5]


def dem_profile(args):
    import numpy as np

    ds, srs, gt = _open_model(args.source)
    lonlats = parse_coordinates(args.line, 2)
    xs, ys = _from_wgs84(lonlats, srs)

    pixel = min(abs(gt[1]), abs(gt[5]))
    planar_length = sum(math.hypot(xs[i] - xs[i - 1], ys[i] - ys[i - 1]) for i in range(1, len(xs)))
    step = max(pixel, planar_length / max(1, int(args.max_samples) - 1))
    sx, sy, _ = densify(xs, ys, step, np)

    grid, window_gt = _read_window(ds, gt, sx, sy, np)
    cols, rows = _to_pixels(window_gt, sx, sy, np)
    z = bilinear(grid, cols, rows, np)

    coords = _to_wgs84(sx, sy, srs)
    lon = np.array([c[0] for c in coords])
    lat = np.array([c[1] for c in coords])
    # True ground distance (not grid distance) for projected and geographic models alike.
    distance = np.concatenate([[0.0], np.cumsum(ellipsoid_distance(lon[:-1], lat[:-1], lon[1:], lat[1:], np))])

    finite = z[np.isfinite(z)]
    rises = np.diff(z)
    rises = rises[np.isfinite(rises)]
    result = {
        "schemaVersion": 1,
        "source": args.source,
        "lengthMeters": float(distance[-1]),
        "stepMeters": float(distance[-1] / max(1, len(distance) - 1)),
        "minimum": float(finite.min()) if finite.size else None,
        "maximum": float(finite.max()) if finite.size else None,
        "ascentMeters": float(rises[rises > 0].sum()),
        "descentMeters": float(-rises[rises < 0].sum()),
        "samples": [
            {"distance": round(float(d), 3), "elevation": None if not math.isfinite(v) else round(float(v), 3),
             "lon": float(lo), "lat": float(la)}
            for d, v, lo, la in zip(distance, z, lon, lat)
        ],
    }
    print(json.dumps(result))


def dem_volume(args):
    import numpy as np
    from osgeo import gdal, ogr

    ds, srs, gt = _open_model(args.source)
    if srs.IsGeographic():
        raise RuntimeError("volume calculation needs a projected elevation model (e.g. UTM)")

    lonlats = parse_coordinates(args.polygon, 3)
    xs, ys = _from_wgs84(lonlats, srs)
    grid, window_gt = _read_window(ds, gt, xs, ys, np)
    height, width = grid.shape

    ring = ogr.Geometry(ogr.wkbLinearRing)
    for x, y in zip(xs + xs[:1], ys + ys[:1]):
        ring.AddPoint_2D(x, y)
    polygon = ogr.Geometry(ogr.wkbPolygon)
    polygon.AddGeometry(ring)
    if not polygon.IsValid():
        raise RuntimeError("polygon is self-intersecting or otherwise invalid")

    memory = ogr.GetDriverByName("Memory").CreateDataSource("")
    layer = memory.CreateLayer("area", srs=srs)
    feature = ogr.Feature(layer.GetLayerDefn())
    feature.SetGeometry(polygon)
    layer.CreateFeature(feature)

    mask_ds = gdal.GetDriverByName("MEM").Create("", width, height, 1, gdal.GDT_Byte)
    mask_ds.SetGeoTransform(window_gt)
    mask_ds.SetProjection(srs.ExportToWkt())
    gdal.RasterizeLayer(mask_ds, [1], layer, burn_values=[1])   # pixel centres inside
    mask = mask_ds.GetRasterBand(1).ReadAsArray().astype(bool)

    pixel_area = abs(window_gt[1] * window_gt[5])
    cols = (np.arange(width) + 0.5)
    rows = (np.arange(height) + 0.5)
    px = window_gt[0] + cols * window_gt[1]
    py = window_gt[3] + rows * window_gt[5]
    grid_x, grid_y = np.meshgrid(px, py)

    base_mode = args.base
    detail = {}
    if base_mode in ("plane", "lowest"):
        bx, by, _ = densify(xs, ys, min(abs(gt[1]), abs(gt[5])), np, closed=True)
        bcols, brows = _to_pixels(window_gt, bx, by, np)
        boundary = bilinear(grid, bcols, brows, np)
        if not np.isfinite(boundary).any():
            raise RuntimeError("no valid model heights along the polygon boundary")
        if base_mode == "plane":
            a, b, c = fit_plane(bx, by, boundary, np)
            base = a * grid_x + b * grid_y + c
            residual = boundary - (a * bx + b * by + c)
            detail = {"planeSlopePercent": float(math.hypot(a, b) * 100),
                      "boundaryRmse": float(np.sqrt(np.nanmean(residual ** 2)))}
        else:
            level = float(np.nanmin(boundary))
            base = np.full(grid.shape, level)
            detail = {"baseHeight": level}
    elif base_mode == "fixed":
        if args.base_height is None:
            raise RuntimeError("--base fixed requires --base-height")
        base = np.full(grid.shape, float(args.base_height))
        detail = {"baseHeight": float(args.base_height)}
    elif base_mode == "dtm":
        if not args.base_dem:
            raise RuntimeError("--base dtm requires --base-dem")
        warped = gdal.Warp(
            "", args.base_dem, format="MEM", dstSRS=srs.ExportToWkt(),
            outputBounds=(window_gt[0], window_gt[3] + height * window_gt[5],
                          window_gt[0] + width * window_gt[1], window_gt[3]),
            width=width, height=height, resampleAlg="bilinear",
            outputType=gdal.GDT_Float32, dstNodata=float("nan"))
        base = warped.GetRasterBand(1).ReadAsArray().astype(np.float64)
        detail = {"baseModel": args.base_dem}
    else:
        raise RuntimeError(f"unknown base '{base_mode}'")

    result = {"schemaVersion": 1, "source": args.source, "base": base_mode,
              "pixelSizeMeters": math.sqrt(pixel_area)}
    result.update(detail)
    result.update(volume_stats(grid, base, mask, pixel_area, np))
    print(json.dumps(result))


def canopy_height(args):
    import numpy as np
    from osgeo import gdal

    dsm, srs, gt = _open_model(args.dsm)
    width, height = dsm.RasterXSize, dsm.RasterYSize

    # DTM resampled onto the DSM grid lazily; blocks are read on demand.
    dtm = gdal.Warp(
        "", args.dtm, format="VRT", dstSRS=dsm.GetProjection(),
        outputBounds=(gt[0], gt[3] + height * gt[5], gt[0] + width * gt[1], gt[3]),
        width=width, height=height, resampleAlg="bilinear",
        outputType=gdal.GDT_Float32, dstNodata=float("nan"))

    out = gdal.GetDriverByName("GTiff").Create(
        args.output, width, height, 1, gdal.GDT_Float32,
        options=["TILED=YES", "COMPRESS=DEFLATE", "PREDICTOR=3", "BIGTIFF=IF_SAFER"])
    out.SetGeoTransform(gt)
    out.SetProjection(dsm.GetProjection())
    out_band = out.GetRasterBand(1)
    out_band.SetNoDataValue(_CHM_NODATA)

    dsm_band = dsm.GetRasterBand(1)
    dsm_nodata = dsm_band.GetNoDataValue()
    dtm_band = dtm.GetRasterBand(1)

    bins = int(_CHM_HIST_MAX / _CHM_HIST_STEP)
    hist = np.zeros(bins, dtype=np.int64)
    count, total, maximum, clipped = 0, 0.0, None, 0
    block = 1024

    for r0 in range(0, height, block):
        rows = min(block, height - r0)
        surface = dsm_band.ReadAsArray(0, r0, width, rows).astype(np.float64)
        if dsm_nodata is not None and math.isfinite(dsm_nodata):
            surface[surface == dsm_nodata] = np.nan
        terrain = dtm_band.ReadAsArray(0, r0, width, rows).astype(np.float64)
        chm = surface - terrain
        valid = np.isfinite(chm)
        clipped += int((chm[valid] < 0).sum())
        chm[valid] = np.clip(chm[valid], 0, None)   # small negatives are model noise

        values = chm[valid]
        if values.size:
            count += values.size
            total += float(values.sum())
            maximum = float(values.max()) if maximum is None else max(maximum, float(values.max()))
            hist += np.histogram(np.minimum(values, _CHM_HIST_MAX - 1e-9), bins=bins, range=(0, _CHM_HIST_MAX))[0]

        out_band.WriteArray(np.where(valid, chm, _CHM_NODATA).astype(np.float32), 0, r0)

    out_band.FlushCache()
    out = None
    if count == 0:
        raise RuntimeError("DSM and DTM do not overlap with valid heights")

    result = {
        "schemaVersion": 1,
        "output": args.output,
        "dsm": args.dsm,
        "dtm": args.dtm,
        "validPixels": count,
        "pixelSizeMeters": abs(gt[1]),
        "mean": total / count,
        "median": histogram_percentile(hist, _CHM_HIST_STEP, 0.5),
        "p95": histogram_percentile(hist, _CHM_HIST_STEP, 0.95),
        "maximum": maximum,
        "negativeClippedFraction": clipped / count,
    }
    print(json.dumps(result))
