"""Prescription (application) map from NDVI scouting zones.

The zone raster (1-5, 0 = no data) is aggregated to the machine grid with the most frequent zone
per cell, vectorised (adjacent cells of one zone merge into one polygon), given the operator's
rate per zone and written in WGS84 as ESRI Shapefile and GeoJSON, the formats farm terminals and
farm management software import. Rates are the operator's decision; DroneDash only maps them.
"""

import json
import math
import os

_ZONES = (1, 2, 3, 4, 5)


def parse_rates(text):
    """'1:120,2:100,...' -> {zone: rate}; every zone 1-5 needs a finite rate >= 0."""
    rates = {}
    for part in (text or "").split(","):
        part = part.strip()
        if not part:
            continue
        try:
            zone, rate = part.split(":")
            zone, rate = int(zone), float(rate)
        except ValueError:
            raise RuntimeError(f"invalid rate '{part}', expected zone:rate")
        if zone not in _ZONES or not math.isfinite(rate) or rate < 0:
            raise RuntimeError(f"invalid rate '{part}': zone 1-5 and a rate >= 0 are required")
        rates[zone] = rate
    missing = [zone for zone in _ZONES if zone not in rates]
    if missing:
        raise RuntimeError(f"rates missing for zone(s) {missing}")
    return rates


def zone_areas(grid, cell_area, np):
    """Area per zone 1-5 of an aggregated zone grid."""
    return {zone: float((grid == zone).sum()) * cell_area for zone in _ZONES}


def prescription_map(args):
    import numpy as np
    from osgeo import gdal, ogr, osr

    gdal.UseExceptions()
    ogr.UseExceptions()
    osr.UseExceptions()

    rates = parse_rates(args.rates)
    cell = float(args.cell)
    if not math.isfinite(cell) or cell < 1 or cell > 100:
        raise RuntimeError("the machine grid cell must be between 1 and 100 m")

    source = gdal.Open(args.source)
    if source is None or not source.GetProjection():
        raise RuntimeError("the zone map must be a georeferenced GeoTIFF")
    srs = osr.SpatialReference(wkt=source.GetProjection())
    if not srs.IsProjected():
        raise RuntimeError("the zone map must be in a projected CRS (e.g. UTM) to use a metric grid")

    # Majority zone per machine cell; zone 0 (no data) never wins against a real zone in a cell
    # because it is declared as nodata.
    grid_ds = gdal.Warp(
        "", source, format="MEM", xRes=cell, yRes=cell, resampleAlg="mode",
        srcNodata=0, dstNodata=0, outputType=gdal.GDT_Byte)
    band = grid_ds.GetRasterBand(1)
    grid = band.ReadAsArray()
    if not np.isin(grid, _ZONES).any():
        raise RuntimeError("the zone map contains no zones 1-5")
    grid[~np.isin(grid, _ZONES)] = 0
    band.WriteArray(grid)

    # Vectorise in the zone map's CRS, then reproject to WGS84 for the terminals.
    memory = ogr.GetDriverByName("Memory").CreateDataSource("zones")
    local = memory.CreateLayer("zones", srs, ogr.wkbPolygon)
    local.CreateField(ogr.FieldDefn("ZONE", ogr.OFTInteger))
    gdal.Polygonize(band, band, local, 0)

    wgs84 = osr.SpatialReference()
    wgs84.ImportFromEPSG(4326)
    wgs84.SetAxisMappingStrategy(osr.OAMS_TRADITIONAL_GIS_ORDER)
    srs.SetAxisMappingStrategy(osr.OAMS_TRADITIONAL_GIS_ORDER)
    to_wgs84 = osr.CoordinateTransformation(srs, wgs84)

    output_dir = os.path.abspath(args.output_dir)
    os.makedirs(output_dir, exist_ok=True)
    name = args.name or "prescription"
    outputs = {}
    for driver_name, extension in (("ESRI Shapefile", ".shp"), ("GeoJSON", ".geojson")):
        path = os.path.join(output_dir, name + extension)
        driver = ogr.GetDriverByName(driver_name)
        if os.path.exists(path):
            driver.DeleteDataSource(path)
        target = driver.CreateDataSource(path)
        layer = target.CreateLayer(name, wgs84, ogr.wkbPolygon)
        for field_name, field_type in (("ZONE", ogr.OFTInteger), ("RATE", ogr.OFTReal), ("AREA_HA", ogr.OFTReal)):
            layer.CreateField(ogr.FieldDefn(field_name, field_type))

        local.ResetReading()
        for feature in local:
            zone = feature.GetField("ZONE")
            if zone not in rates:
                continue
            geometry = feature.GetGeometryRef().Clone()
            area_ha = geometry.GetArea() / 10000.0
            geometry.Transform(to_wgs84)
            out = ogr.Feature(layer.GetLayerDefn())
            out.SetField("ZONE", int(zone))
            out.SetField("RATE", float(rates[zone]))
            out.SetField("AREA_HA", round(area_ha, 4))
            out.SetGeometry(geometry)
            layer.CreateFeature(out)
        target = None
        outputs[driver_name] = path

    cell_area = cell * cell * (srs.GetLinearUnits() ** 2)
    areas_m2 = zone_areas(grid, cell_area, np)
    zones = [
        {
            "zone": zone,
            "rate": rates[zone],
            "areaHectares": areas_m2[zone] / 10000.0,
            "amount": rates[zone] * areas_m2[zone] / 10000.0,
        }
        for zone in _ZONES
    ]
    result = {
        "schemaVersion": 1,
        "source": os.path.abspath(args.source),
        "cellMeters": cell,
        "unit": args.unit or "",
        "shapefile": outputs["ESRI Shapefile"],
        "geojson": outputs["GeoJSON"],
        "polygons": local.GetFeatureCount(),
        "zones": zones,
        "totalAreaHectares": sum(z["areaHectares"] for z in zones),
        "totalAmount": sum(z["amount"] for z in zones),
    }
    with open(os.path.join(output_dir, name + ".json"), "w", encoding="utf-8") as handle:
        json.dump(result, handle, indent=2)
    print(json.dumps(result))
