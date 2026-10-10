"""Orthomosaic -> XYZ web map tiles (Web Mercator PNG pyramid) for the Leaflet maps.

The tiles are written by GDAL's gdal2tiles into a temporary folder that replaces the previous
pyramid only when complete, so a viewer never sees a half-written cache.
"""

import json
import math
import os
import shutil

_EARTH_CIRCUMFERENCE = 40075016.686
_MAX_ZOOM = 23
_ZOOM_LEVELS = 8


def native_zoom(pixel_size_meters, latitude, tile_size=256):
    """Lowest zoom whose tile pixels are at least as fine as the source pixels (capped at 23)."""
    if pixel_size_meters <= 0:
        raise ValueError("pixel size must be positive")
    ground = _EARTH_CIRCUMFERENCE * math.cos(math.radians(latitude)) / tile_size
    return max(0, min(_MAX_ZOOM, int(math.ceil(math.log2(ground / pixel_size_meters)))))


def _source_geometry(ds):
    """WGS84 bounds and ground pixel size (m) of a georeferenced raster."""
    from osgeo import osr

    gt = ds.GetGeoTransform()
    srs = osr.SpatialReference(wkt=ds.GetProjection())
    srs.SetAxisMappingStrategy(osr.OAMS_TRADITIONAL_GIS_ORDER)
    wgs84 = osr.SpatialReference()
    wgs84.ImportFromEPSG(4326)
    wgs84.SetAxisMappingStrategy(osr.OAMS_TRADITIONAL_GIS_ORDER)
    transform = osr.CoordinateTransformation(srs, wgs84)

    width, height = ds.RasterXSize, ds.RasterYSize
    corners = [(0, 0), (width, 0), (0, height), (width, height)]
    lonlats = [
        transform.TransformPoint(gt[0] + c * gt[1] + r * gt[2], gt[3] + c * gt[4] + r * gt[5])[:2]
        for c, r in corners
    ]
    west, east = min(p[0] for p in lonlats), max(p[0] for p in lonlats)
    south, north = min(p[1] for p in lonlats), max(p[1] for p in lonlats)

    pixel = math.hypot(gt[1], gt[4])
    if srs.IsGeographic():
        pixel *= math.pi / 180.0 * 6378137.0 * math.cos(math.radians((south + north) / 2.0))
    elif srs.IsProjected():
        pixel *= srs.GetLinearUnits()
    return {"south": south, "west": west, "north": north, "east": east}, pixel


def ortho_tiles(args):
    from osgeo import gdal, osr
    from osgeo_utils import gdal2tiles

    gdal.UseExceptions()
    osr.UseExceptions()
    source = os.path.abspath(args.source)
    ds = gdal.Open(source)
    if ds is None:
        raise RuntimeError(f"cannot open orthomosaic: {source}")
    if not ds.GetProjection():
        raise RuntimeError("orthomosaic has no coordinate reference system")
    if ds.RasterCount not in (1, 3, 4):
        raise RuntimeError(f"orthomosaic must have 1, 3 or 4 bands, found {ds.RasterCount}")
    if ds.GetRasterBand(1).DataType != gdal.GDT_Byte:
        raise RuntimeError("only 8-bit orthomosaics are supported (convert e.g. with gdal_translate -ot Byte -scale)")

    bounds, pixel = _source_geometry(ds)
    ds = None
    max_zoom = native_zoom(pixel, (bounds["south"] + bounds["north"]) / 2.0)
    min_zoom = max(0, max_zoom - _ZOOM_LEVELS)

    output = os.path.abspath(args.output_dir)
    temp = output + ".partial"
    if os.path.isdir(temp):
        shutil.rmtree(temp)

    processes = max(1, min(8, (os.cpu_count() or 2) - 1))
    gdal2tiles.main([
        "gdal2tiles",
        "--xyz",
        "--profile=mercator",
        f"--zoom={min_zoom}-{max_zoom}",
        "--resampling=average",
        "--webviewer=none",
        "--exclude",
        f"--processes={processes}",
        "--quiet",
        source,
        temp,
    ])

    tile_count = sum(len([f for f in files if f.endswith(".png")]) for _, _, files in os.walk(temp))
    if tile_count == 0:
        shutil.rmtree(temp, ignore_errors=True)
        raise RuntimeError("gdal2tiles produced no tiles")

    result = {
        "schemaVersion": 1,
        "source": source,
        "bounds": bounds,
        "minZoom": min_zoom,
        "maxZoom": max_zoom,
        "tileSize": 256,
        "pixelSizeMeters": pixel,
        "tiles": tile_count,
        "template": "{z}/{x}/{y}.png",
    }
    with open(os.path.join(temp, "ortho-tiles.json"), "w", encoding="utf-8") as handle:
        json.dump(result, handle, indent=2)

    if os.path.isdir(output):
        shutil.rmtree(output)
    os.replace(temp, output)
    print(json.dumps(result))
