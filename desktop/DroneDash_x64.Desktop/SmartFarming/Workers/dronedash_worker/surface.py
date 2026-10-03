"""Surface model products: ground model (DTM) from a DSM and the difference of two surveys.

DTM: progressive morphological filter (Zhang et al. 2003) on the DSM resampled with the cell
minimum. Openings with growing windows remove objects up to the largest window; a cell counts
as ground while it stays within a slope-dependent height of the opened surface. The non-ground
cells are then interpolated from the ground around them.
"""

import json
import math

from .analysis import _open_model, histogram_percentile
from .terrain import nan_max_filter

_NODATA = -9999.0
_MAX_GRID_CELLS = 25_000_000
_DIFF_HIST_RANGE = 100.0
_DIFF_HIST_STEP = 0.01


def nan_min_filter(grid, radius, np):
    """Square minimum filter that ignores NaN (mirror of terrain.nan_max_filter)."""
    return -nan_max_filter(-grid, radius, np)


def window_radii(max_object_meters, cell):
    """Window radii in cells: 1, 2, 4, ... up to half the largest object size."""
    limit = max(1, int(math.ceil(max_object_meters / 2.0 / cell)))
    radii, radius = [], 1
    while radius < limit:
        radii.append(radius)
        radius *= 2
    radii.append(limit)
    return radii


def progressive_morphological_filter(grid, cell, np, max_object=20.0, slope=0.15,
                                     initial_threshold=0.3, max_threshold=5.0):
    """Ground mask of a surface grid (True = ground, False = object or no data)."""
    surface = grid.copy()
    ground = np.isfinite(grid)
    previous_size = 1
    for radius in window_radii(max_object, cell):
        opened = nan_max_filter(nan_min_filter(surface, radius, np), radius, np)
        size = 2 * radius + 1
        threshold = min(max_threshold, slope * (size - previous_size) * cell + initial_threshold)
        with np.errstate(invalid="ignore"):
            ground &= ~((surface - opened) > threshold)
        surface = np.where(np.isfinite(opened), opened, surface)
        previous_size = size
    return ground


def _resampled_minimum(ds, gt, cell):
    """The model on a cell grid with the minimum of each cell (ground survives, objects shrink)."""
    from osgeo import gdal

    native = min(abs(gt[1]), abs(gt[5]))
    width = ds.RasterXSize * abs(gt[1])
    height = ds.RasterYSize * abs(gt[5])
    cell = max(cell, native)
    while width * height / (cell * cell) > _MAX_GRID_CELLS:
        cell *= 1.5

    band = ds.GetRasterBand(1)
    warped = gdal.Warp(
        "", ds, format="MEM", xRes=cell, yRes=cell,
        resampleAlg="min" if cell > native * 1.01 else "near",
        srcNodata=band.GetNoDataValue(), dstNodata=float("nan"), outputType=gdal.GDT_Float64)
    grid = warped.GetRasterBand(1).ReadAsArray().astype("float64")
    return grid, warped.GetGeoTransform(), cell


def _write_float(path, grid, gt, projection, np):
    from osgeo import gdal

    out = gdal.GetDriverByName("GTiff").Create(
        path, grid.shape[1], grid.shape[0], 1, gdal.GDT_Float32,
        options=["TILED=YES", "COMPRESS=DEFLATE", "PREDICTOR=3", "BIGTIFF=IF_SAFER"])
    out.SetGeoTransform(gt)
    out.SetProjection(projection)
    band = out.GetRasterBand(1)
    band.SetNoDataValue(_NODATA)
    band.WriteArray(np.where(np.isfinite(grid), grid, _NODATA).astype(np.float32))
    return out


def dtm_from_dsm(args):
    import numpy as np
    from osgeo import gdal

    ds, srs, gt = _open_model(args.source)
    if not srs.IsProjected():
        raise RuntimeError("the ground filter needs a projected model (e.g. UTM), not geographic degrees")

    grid, cell_gt, cell = _resampled_minimum(ds, gt, float(args.cell))
    valid = np.isfinite(grid)
    if not valid.any():
        raise RuntimeError("the surface model contains no valid heights")

    ground = progressive_morphological_filter(
        grid, cell, np, max_object=float(args.max_object), slope=float(args.slope))
    ground_cells = int(ground.sum())
    if ground_cells == 0:
        raise RuntimeError("no ground cells found; is the maximum object size too small for this area?")

    # Interpolate the objects from the surrounding ground (GDAL inverse-distance fill).
    out = _write_float(args.output, np.where(ground, grid, np.nan), cell_gt, ds.GetProjection(), np)
    band = out.GetRasterBand(1)
    search = int(math.ceil(float(args.max_object) / cell)) * 4 + 10
    gdal.FillNodata(band, None, maxSearchDist=search, smoothingIterations=2)
    # Keep the model's own data gaps empty: only object cells are interpolated.
    filled = band.ReadAsArray().astype(np.float64)
    filled[~valid] = _NODATA
    band.WriteArray(filled.astype(np.float32))
    band.FlushCache()
    out = None

    result = {
        "schemaVersion": 1,
        "source": args.source,
        "output": args.output,
        "cellMeters": cell,
        "maxObjectMeters": float(args.max_object),
        "slope": float(args.slope),
        "validCells": int(valid.sum()),
        "groundCells": ground_cells,
        "groundFraction": ground_cells / int(valid.sum()),
        "objectCells": int(valid.sum()) - ground_cells,
    }
    print(json.dumps(result))


def dem_difference(args):
    """new - reference on the grid of the new model, with cut/fill volumes and change statistics."""
    import numpy as np
    from osgeo import gdal

    new, srs, gt = _open_model(args.source)
    width, height = new.RasterXSize, new.RasterYSize
    reference = gdal.Warp(
        "", args.reference, format="VRT", dstSRS=new.GetProjection(),
        outputBounds=(gt[0], gt[3] + height * gt[5], gt[0] + width * gt[1], gt[3]),
        width=width, height=height, resampleAlg="bilinear",
        outputType=gdal.GDT_Float32, dstNodata=float("nan"))

    out = gdal.GetDriverByName("GTiff").Create(
        args.output, width, height, 1, gdal.GDT_Float32,
        options=["TILED=YES", "COMPRESS=DEFLATE", "PREDICTOR=3", "BIGTIFF=IF_SAFER"])
    out.SetGeoTransform(gt)
    out.SetProjection(new.GetProjection())
    out_band = out.GetRasterBand(1)
    out_band.SetNoDataValue(_NODATA)

    new_band = new.GetRasterBand(1)
    new_nodata = new_band.GetNoDataValue()
    ref_band = reference.GetRasterBand(1)
    threshold = float(args.threshold)
    pixel_area = abs(gt[1] * gt[5]) * (srs.GetLinearUnits() ** 2) if srs.IsProjected() else None

    bins = int(2 * _DIFF_HIST_RANGE / _DIFF_HIST_STEP)
    hist = np.zeros(bins, dtype=np.int64)
    count, total = 0, 0.0
    raised = lowered = 0.0
    raised_cells = lowered_cells = 0
    minimum = maximum = None
    block = 1024

    for r0 in range(0, height, block):
        rows = min(block, height - r0)
        a = new_band.ReadAsArray(0, r0, width, rows).astype(np.float64)
        if new_nodata is not None and math.isfinite(new_nodata):
            a[a == new_nodata] = np.nan
        b = ref_band.ReadAsArray(0, r0, width, rows).astype(np.float64)
        diff = a - b
        valid = np.isfinite(diff)
        values = diff[valid]
        if values.size:
            count += values.size
            total += float(values.sum())
            minimum = float(values.min()) if minimum is None else min(minimum, float(values.min()))
            maximum = float(values.max()) if maximum is None else max(maximum, float(values.max()))
            up = values[values > threshold]
            down = values[values < -threshold]
            raised += float(up.sum())
            lowered += float(-down.sum())
            raised_cells += up.size
            lowered_cells += down.size
            clipped = np.clip(values, -_DIFF_HIST_RANGE, _DIFF_HIST_RANGE - 1e-9)
            hist += np.histogram(clipped, bins=bins, range=(-_DIFF_HIST_RANGE, _DIFF_HIST_RANGE))[0]
        out_band.WriteArray(np.where(valid, diff, _NODATA).astype(np.float32), 0, r0)

    out_band.FlushCache()
    out = None
    if count == 0:
        raise RuntimeError("the two models do not overlap with valid heights")

    def percentile(fraction):
        return histogram_percentile(hist, _DIFF_HIST_STEP, fraction) - _DIFF_HIST_RANGE

    result = {
        "schemaVersion": 1,
        "source": args.source,
        "reference": args.reference,
        "output": args.output,
        "pixelSizeMeters": abs(gt[1]),
        "validPixels": count,
        "threshold": threshold,
        "mean": total / count,
        "minimum": minimum,
        "maximum": maximum,
        "p05": percentile(0.05),
        "p95": percentile(0.95),
        "raisedAreaSquareMeters": raised_cells * pixel_area if pixel_area else None,
        "loweredAreaSquareMeters": lowered_cells * pixel_area if pixel_area else None,
        "raisedCubicMeters": raised * pixel_area if pixel_area else None,
        "loweredCubicMeters": lowered * pixel_area if pixel_area else None,
    }
    print(json.dumps(result))
