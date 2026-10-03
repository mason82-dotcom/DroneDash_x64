"""Elevation model previews: hillshaded colour relief in Web Mercator for map display."""

import json
import math
import os

_MERCATOR_RADIUS = 6378137.0

# Elevation colour ramp (position 0..1, RGB): low green -> yellow -> brown -> grey/white.
_RAMP = (
    (0.00, (38, 115, 77)),
    (0.20, (104, 166, 87)),
    (0.40, (209, 204, 112)),
    (0.60, (196, 145, 82)),
    (0.80, (150, 110, 90)),
    (1.00, (245, 245, 245)),
)


def web_mercator_to_lonlat(x, y):
    lon = math.degrees(x / _MERCATOR_RADIUS)
    lat = math.degrees(2.0 * math.atan(math.exp(y / _MERCATOR_RADIUS)) - math.pi / 2.0)
    return lon, lat


def robust_range(values, np, lower=2.0, upper=98.0):
    finite = values[np.isfinite(values)]
    if finite.size == 0:
        raise RuntimeError("elevation model contains no valid pixels")

    vmin, vmax = (float(v) for v in np.percentile(finite, [lower, upper]))
    if vmax - vmin < 1e-6:
        vmin -= 0.5
        vmax += 0.5
    return vmin, vmax


def hillshade(z, cell_x, cell_y, np, azimuth_deg=315.0, altitude_deg=45.0):
    """Horn hillshade in 0..1; NaN cells and their edges get a neutral 1.0."""
    if cell_x <= 0 or cell_y <= 0:
        raise ValueError("cell size must be positive")

    padded = np.pad(z.astype(np.float64), 1, mode="edge")
    a, b, c = padded[:-2, :-2], padded[:-2, 1:-1], padded[:-2, 2:]
    d, f = padded[1:-1, :-2], padded[1:-1, 2:]
    g, h, i = padded[2:, :-2], padded[2:, 1:-1], padded[2:, 2:]

    dz_dx = ((c + 2 * f + i) - (a + 2 * d + g)) / (8.0 * cell_x)
    dz_dy = ((g + 2 * h + i) - (a + 2 * b + c)) / (8.0 * cell_y)

    slope = np.arctan(np.hypot(dz_dx, dz_dy))
    aspect = np.arctan2(dz_dy, -dz_dx)

    zenith = math.radians(90.0 - altitude_deg)
    azimuth = math.radians((360.0 - azimuth_deg + 90.0) % 360.0)

    shade = (
        math.cos(zenith) * np.cos(slope)
        + math.sin(zenith) * np.sin(slope) * np.cos(azimuth - aspect)
    )
    shade = np.clip(shade, 0.0, 1.0)
    return np.where(np.isfinite(shade), shade, 1.0)


def colorize(z, vmin, vmax, np):
    """RGBA uint8 colour relief; non-finite cells are fully transparent."""
    finite = np.isfinite(z)
    t = np.zeros(z.shape, dtype=np.float64)
    np.divide(z - vmin, vmax - vmin, out=t, where=finite)
    t = np.clip(t, 0.0, 1.0)

    positions = np.array([stop[0] for stop in _RAMP])
    rgba = np.zeros(z.shape + (4,), dtype=np.uint8)
    for channel in range(3):
        values = np.array([stop[1][channel] for stop in _RAMP], dtype=np.float64)
        rgba[..., channel] = np.interp(t, positions, values).round().astype(np.uint8)
    rgba[..., 3] = np.where(finite, 255, 0).astype(np.uint8)
    return rgba


def shade_relief(rgba, shade, np, strength=0.55):
    """Darkens colour by hillshade; strength 0 keeps colours, 1 is pure shading."""
    factor = (1.0 - strength) + strength * shade
    out = rgba.copy()
    out[..., :3] = np.clip(rgba[..., :3] * factor[..., None], 0, 255).round().astype(np.uint8)
    return out


def _preview_size(width, height, max_size):
    scale = min(1.0, float(max_size) / max(width, height))
    return max(1, int(round(width * scale))), max(1, int(round(height * scale)))


def _replace_atomic(write, path):
    temp = path + "." + os.urandom(8).hex() + ".tmp"
    try:
        write(temp)
        os.replace(temp, path)
    finally:
        if os.path.exists(temp):
            os.remove(temp)


def dem_preview(args):
    import numpy as np
    from osgeo import gdal, osr

    gdal.UseExceptions()

    max_size = int(args.max_size)
    if max_size < 256 or max_size > 8192:
        raise RuntimeError("--max-size must be within 256..8192")

    source = gdal.Open(args.source)
    if source is None:
        raise RuntimeError(f"cannot open elevation model: {args.source}")

    projection = source.GetProjection()
    if not projection:
        raise RuntimeError("elevation model has no coordinate reference system")

    band = source.GetRasterBand(1)
    nodata = band.GetNoDataValue()

    warp_options = dict(
        dstSRS="EPSG:3857",
        outputType=gdal.GDT_Float32,
        resampleAlg="bilinear",
        srcNodata=nodata,
        dstNodata=float("nan"),
    )

    # Plan the full-resolution Web Mercator grid first, then warp once at preview size.
    planned = gdal.Warp("", source, format="VRT", **warp_options)
    width, height = _preview_size(planned.RasterXSize, planned.RasterYSize, max_size)
    planned = None

    warped = gdal.Warp("", source, format="MEM", width=width, height=height, **warp_options)
    z = warped.GetRasterBand(1).ReadAsArray().astype(np.float32)
    z[~np.isfinite(z)] = np.nan
    if nodata is not None and math.isfinite(nodata):
        z[z == np.float32(nodata)] = np.nan

    gt = warped.GetGeoTransform()
    min_x, max_y = gt[0], gt[3]
    max_x = min_x + gt[1] * width
    min_y = max_y + gt[5] * height
    west, south = web_mercator_to_lonlat(min_x, min_y)
    east, north = web_mercator_to_lonlat(max_x, max_y)

    # Web Mercator stretches distances by 1/cos(lat); use true ground size for slopes.
    center_lat = math.radians((south + north) / 2.0)
    ground_x = abs(gt[1]) * math.cos(center_lat)
    ground_y = abs(gt[5]) * math.cos(center_lat)

    vmin, vmax = robust_range(z, np)
    rgba = shade_relief(colorize(z, vmin, vmax, np), hillshade(z, ground_x, ground_y, np), np)

    os.makedirs(args.output_dir, exist_ok=True)
    png_path = os.path.join(args.output_dir, "dem-preview.png")
    grid_path = os.path.join(args.output_dir, "dem-grid.f32")
    json_path = os.path.join(args.output_dir, "dem-preview.json")

    def write_png(path):
        memory = gdal.GetDriverByName("MEM").Create("", width, height, 4, gdal.GDT_Byte)
        for channel in range(4):
            memory.GetRasterBand(channel + 1).WriteArray(rgba[..., channel])
        gdal.GetDriverByName("PNG").CreateCopy(path, memory)

    def write_grid(path):
        z.astype("<f4").tofile(path)

    _replace_atomic(write_png, png_path)
    _replace_atomic(write_grid, grid_path)

    finite = z[np.isfinite(z)]
    srs = osr.SpatialReference(wkt=projection)
    result = {
        "schemaVersion": 1,
        "source": os.path.abspath(args.source),
        "sourceCrs": srs.GetName(),
        "sourceWidth": source.RasterXSize,
        "sourceHeight": source.RasterYSize,
        "width": width,
        "height": height,
        "bounds": {"south": south, "west": west, "north": north, "east": east},
        "mercatorBounds": {"minX": min_x, "minY": min_y, "maxX": max_x, "maxY": max_y},
        "groundPixelSizeMeters": (ground_x + ground_y) / 2.0,
        "minimum": float(finite.min()),
        "maximum": float(finite.max()),
        "mean": float(finite.mean()),
        "displayMinimum": vmin,
        "displayMaximum": vmax,
        "validPixels": int(finite.size),
        "image": os.path.basename(png_path),
        "grid": os.path.basename(grid_path),
        "gridType": "float32-le",
    }

    _replace_atomic(
        lambda path: open(path, "w", encoding="utf-8").write(json.dumps(result, indent=2)),
        json_path,
    )
    print(json.dumps(result))
