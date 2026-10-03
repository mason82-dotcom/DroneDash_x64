"""COLMAP support: GPS reference positions, georeferencing and conversion of fused clouds to LAZ + DSM.

Georeferencing is done here (Umeyama similarity between the sparse model's camera centres and
the images' GPS/RTK positions) instead of with COLMAP's model_aligner: model_aligner 3.9.1
reports "Alignment succeeded" but writes the model untransformed, and its options differ
between COLMAP versions.
"""

import json
import math
import os
import struct

_DSM_NODATA = -9999.0

_PLY_TYPES = {
    "char": "i1", "int8": "i1", "uchar": "u1", "uint8": "u1",
    "short": "i2", "int16": "i2", "ushort": "u2", "uint16": "u2",
    "int": "i4", "int32": "i4", "uint": "u4", "uint32": "u4",
    "float": "f4", "float32": "f4", "double": "f8", "float64": "f8",
}


def utm_epsg(lon, lat):
    """WGS84 / UTM zone EPSG code for a position (326xx north, 327xx south)."""
    zone = int(math.floor((lon + 180.0) / 6.0)) + 1
    zone = min(max(zone, 1), 60)
    return (32600 if lat >= 0 else 32700) + zone


def local_offset(eastings, northings):
    """Round offset (100 m) that keeps COLMAP coordinates small and readable."""
    return (
        round(sum(eastings) / len(eastings) / 100.0) * 100.0,
        round(sum(northings) / len(northings) / 100.0) * 100.0,
    )


def read_ply_vertices(path, np):
    """Vertices of a binary little-endian or ASCII PLY as a numpy structured array."""
    with open(path, "rb") as handle:
        if handle.readline().strip() != b"ply":
            raise RuntimeError("not a PLY file")
        fmt, count, props, in_vertex = None, 0, [], False
        while True:
            line = handle.readline()
            if not line:
                raise RuntimeError("PLY header is incomplete")
            words = line.decode("ascii", "replace").split()
            if not words:
                continue
            if words[0] == "format":
                fmt = words[1]
            elif words[0] == "element":
                in_vertex = words[1] == "vertex"
                if in_vertex:
                    count = int(words[2])
                elif not props:
                    raise RuntimeError("PLY vertex element must come first")
            elif words[0] == "property" and in_vertex:
                if words[1] == "list":
                    raise RuntimeError("list properties are not supported for vertices")
                if words[1] not in _PLY_TYPES:
                    raise RuntimeError(f"unsupported PLY type {words[1]}")
                props.append((words[2], _PLY_TYPES[words[1]]))
            elif words[0] == "end_header":
                break

        if fmt == "binary_little_endian":
            dtype = np.dtype([(name, "<" + code) for name, code in props])
            data = np.frombuffer(handle.read(count * dtype.itemsize), dtype=dtype, count=count)
        elif fmt == "ascii":
            dtype = np.dtype([(name, code) for name, code in props])
            rows = np.loadtxt(handle, max_rows=count, ndmin=2)
            data = np.zeros(count, dtype=dtype)
            for i, (name, _) in enumerate(props):
                data[name] = rows[:, i]
        else:
            raise RuntimeError(f"unsupported PLY format {fmt}")

    for axis in ("x", "y", "z"):
        if axis not in data.dtype.names:
            raise RuntimeError("PLY vertices have no x/y/z")
    return data


def read_camera_centers(images_bin):
    """Camera centres (world frame of the sparse model) from COLMAP images.bin, keyed by image name."""
    centers = {}
    with open(images_bin, "rb") as handle:
        (count,) = struct.unpack("<Q", handle.read(8))
        for _ in range(count):
            _image_id, qw, qx, qy, qz, tx, ty, tz, _camera_id = struct.unpack("<I7dI", handle.read(64))
            name = bytearray()
            while True:
                char = handle.read(1)
                if char in (b"", b"\x00"):
                    break
                name += char
            (points,) = struct.unpack("<Q", handle.read(8))
            handle.seek(points * 24, os.SEEK_CUR)
            rotation = (
                (1 - 2 * (qy * qy + qz * qz), 2 * (qx * qy - qz * qw), 2 * (qx * qz + qy * qw)),
                (2 * (qx * qy + qz * qw), 1 - 2 * (qx * qx + qz * qz), 2 * (qy * qz - qx * qw)),
                (2 * (qx * qz - qy * qw), 2 * (qy * qz + qx * qw), 1 - 2 * (qx * qx + qy * qy)),
            )
            # C = -R^T t
            centers[name.decode("utf-8")] = tuple(
                -sum(rotation[row][col] * (tx, ty, tz)[row] for row in range(3)) for col in range(3)
            )
    return centers


def umeyama(source, target, np):
    """Similarity (scale, R, t) with target ~ scale * R @ source + t (least squares)."""
    source = np.asarray(source, dtype=np.float64)
    target = np.asarray(target, dtype=np.float64)
    mu_s, mu_t = source.mean(0), target.mean(0)
    src, dst = source - mu_s, target - mu_t
    variance = (src ** 2).sum() / len(source)
    if variance <= 1e-18:
        raise RuntimeError("camera positions are degenerate (all at one point)")
    u, singular, vt = np.linalg.svd(dst.T @ src / len(source))
    sign = np.eye(3)
    if np.linalg.det(u) * np.linalg.det(vt) < 0:
        sign[2, 2] = -1
    rotation = u @ sign @ vt
    scale = float(np.trace(np.diag(singular) @ sign) / variance)
    translation = mu_t - scale * rotation @ mu_s
    return scale, rotation, translation


def robust_similarity(source, target, np, min_threshold=0.5, iterations=4):
    """Umeyama with iterative rejection of cameras whose residual is far above the median."""
    source = np.asarray(source, dtype=np.float64)
    target = np.asarray(target, dtype=np.float64)
    if len(source) < 3:
        raise RuntimeError("at least 3 registered images with GPS are needed for georeferencing")
    keep = np.ones(len(source), dtype=bool)
    for _ in range(iterations):
        scale, rotation, translation = umeyama(source[keep], target[keep], np)
        residual = np.linalg.norm((scale * (rotation @ source.T)).T + translation - target, axis=1)
        threshold = max(min_threshold, 3.0 * 1.4826 * float(np.median(residual[keep])))
        new_keep = residual <= threshold
        if new_keep.sum() < 3 or np.array_equal(new_keep, keep):
            return scale, rotation, translation, residual, keep
        keep = new_keep
    # Iterations exhausted: fit once more so transform, residuals and keep agree.
    scale, rotation, translation = umeyama(source[keep], target[keep], np)
    residual = np.linalg.norm((scale * (rotation @ source.T)).T + translation - target, axis=1)
    return scale, rotation, translation, residual, keep


def isolated_point_mask(x, y, z, voxel, min_count, np):
    """True for points with at least min_count points (itself included) in the 3x3x3 voxels around it.

    Fused clouds contain single floating points far above or below the surface; with a
    max-per-cell DSM each of them would become a spike.
    """
    if voxel <= 0:
        raise ValueError("voxel size must be positive")
    keys = np.floor(np.column_stack([x, y, z]) / voxel).astype(np.int64)
    keys -= keys.min(axis=0)
    # One voxel of padding on each side so that neighbour offsets never wrap into another row.
    dims = keys.max(axis=0) + 3
    linear = ((keys[:, 0] + 1) * dims[1] + (keys[:, 1] + 1)) * dims[2] + (keys[:, 2] + 1)
    voxels, inverse, counts = np.unique(linear, return_inverse=True, return_counts=True)
    totals = np.zeros(len(voxels), dtype=np.int64)
    for dx in (-1, 0, 1):
        for dy in (-1, 0, 1):
            for dz in (-1, 0, 1):
                neighbours = voxels + (dx * dims[1] + dy) * dims[2] + dz
                index = np.minimum(np.searchsorted(voxels, neighbours), len(voxels) - 1)
                totals += np.where(voxels[index] == neighbours, counts[index], 0)
    return totals[inverse.ravel()] >= min_count


def rasterize_max(x, y, z, resolution, np):
    """Surface grid (max z per cell), north-up, with the grid origin snapped to the resolution."""
    if resolution <= 0:
        raise ValueError("resolution must be positive")
    min_x = math.floor(float(x.min()) / resolution) * resolution
    max_y = math.ceil(float(y.max()) / resolution) * resolution
    width = int(math.floor((float(x.max()) - min_x) / resolution)) + 1
    height = int(math.floor((max_y - float(y.min())) / resolution)) + 1
    cols = np.clip(((x - min_x) / resolution).astype(np.int64), 0, width - 1)
    rows = np.clip(((max_y - y) / resolution).astype(np.int64), 0, height - 1)
    grid = np.full(height * width, -np.inf)
    np.maximum.at(grid, rows * width + cols, z)
    grid = grid.reshape(height, width)
    grid[~np.isfinite(grid)] = np.nan
    return grid, (min_x, resolution, 0.0, max_y, 0.0, -resolution)


def colmap_georef(args):
    """images.json [{name, lat, lon, alt}] -> georef.txt (local UTM) + georef.json (EPSG and offset)."""
    from osgeo import osr

    osr.UseExceptions()
    with open(args.images, encoding="utf-8") as handle:
        images = json.load(handle)
    images = [i for i in images if i.get("lat") is not None and i.get("lon") is not None and i.get("alt") is not None]
    if len(images) < 3:
        raise RuntimeError("at least 3 images with GPS position and altitude are needed for georeferencing")

    mean_lon = sum(i["lon"] for i in images) / len(images)
    mean_lat = sum(i["lat"] for i in images) / len(images)
    epsg = utm_epsg(mean_lon, mean_lat)

    wgs84 = osr.SpatialReference()
    wgs84.ImportFromEPSG(4326)
    utm = osr.SpatialReference()
    utm.ImportFromEPSG(epsg)
    for srs in (wgs84, utm):
        srs.SetAxisMappingStrategy(osr.OAMS_TRADITIONAL_GIS_ORDER)
    transform = osr.CoordinateTransformation(wgs84, utm)

    projected = [transform.TransformPoint(i["lon"], i["lat"])[:2] for i in images]
    e0, n0 = local_offset([p[0] for p in projected], [p[1] for p in projected])

    os.makedirs(args.output_dir, exist_ok=True)
    with open(os.path.join(args.output_dir, "georef.txt"), "w", encoding="utf-8") as handle:
        for image, (e, n) in zip(images, projected):
            if any(c.isspace() for c in image["name"]):
                raise RuntimeError(f"image names with spaces are not supported by COLMAP: {image['name']}")
            handle.write(f"{image['name']} {e - e0:.4f} {n - n0:.4f} {image['alt']:.4f}\n")

    result = {"schemaVersion": 1, "epsg": epsg, "offset": [e0, n0, 0.0], "images": len(images)}
    with open(os.path.join(args.output_dir, "georef.json"), "w", encoding="utf-8") as handle:
        json.dump(result, handle, indent=2)
    print(json.dumps(result))


def colmap_products(args):
    """Fused COLMAP cloud (aligned to georef.txt) -> UTM LAZ point cloud + DSM GeoTIFF."""
    import numpy as np
    from osgeo import gdal, osr

    gdal.UseExceptions()
    osr.UseExceptions()
    with open(args.georef, encoding="utf-8") as handle:
        georef = json.load(handle)
    offset = georef["offset"]
    epsg = int(georef["epsg"])

    references = {}
    with open(os.path.join(os.path.dirname(os.path.abspath(args.georef)), "georef.txt"), encoding="utf-8") as handle:
        for line in handle:
            parts = line.split()
            if len(parts) == 4:
                references[parts[0]] = tuple(float(v) for v in parts[1:])

    centers = read_camera_centers(os.path.join(args.sparse, "images.bin"))
    names = sorted(set(centers) & set(references))
    if len(names) < 3:
        raise RuntimeError(
            f"only {len(names)} reconstructed images have a GPS reference; at least 3 are needed")
    scale, rotation, translation, residual, keep = robust_similarity(
        [centers[n] for n in names], [references[n] for n in names], np)

    vertices = read_ply_vertices(args.ply, np)
    if vertices.size == 0:
        raise RuntimeError("fused point cloud is empty")
    model = np.column_stack([vertices["x"], vertices["y"], vertices["z"]]).astype(np.float64)
    local = (scale * (rotation @ model.T)).T + translation
    resolution = float(args.resolution)
    keep_points = isolated_point_mask(
        local[:, 0], local[:, 1], local[:, 2], max(0.5, 5.0 * resolution), 5, np)
    if not keep_points.any():
        raise RuntimeError("the fused point cloud contains only isolated points")
    removed = int((~keep_points).sum())
    vertices = vertices[keep_points]
    local = local[keep_points]
    x = local[:, 0] + offset[0]
    y = local[:, 1] + offset[1]
    z = local[:, 2] + offset[2]

    os.makedirs(args.output_dir, exist_ok=True)
    cloud_path = os.path.join(args.output_dir, "colmap_georeferenced_model.laz")
    try:
        import laspy
    except ImportError:
        raise RuntimeError('writing the point cloud requires laspy with LAZ support: pip install "laspy[lazrs]"')

    srs = osr.SpatialReference()
    srs.ImportFromEPSG(epsg)

    # LAS 1.4 / point format 7 (RGB) carries the CRS as an OGC WKT record.
    from laspy.vlrs.known import WktCoordinateSystemVlr

    header = laspy.LasHeader(point_format=7, version="1.4")
    header.global_encoding.wkt = True
    header.vlrs.append(WktCoordinateSystemVlr(srs.ExportToWkt()))
    header.scales = [0.001, 0.001, 0.001]
    header.offsets = [math.floor(x.min()), math.floor(y.min()), math.floor(z.min())]
    las = laspy.LasData(header)
    las.x, las.y, las.z = x, y, z
    if {"red", "green", "blue"} <= set(vertices.dtype.names):
        # COLMAP stores 8-bit colours; LAS expects 16 bit.
        las.red = vertices["red"].astype(np.uint16) * 257
        las.green = vertices["green"].astype(np.uint16) * 257
        las.blue = vertices["blue"].astype(np.uint16) * 257
    las.write(cloud_path)

    grid, gt = rasterize_max(x, y, z, resolution, np)
    filled_before = int(np.isfinite(grid).sum())

    dsm_path = os.path.join(args.output_dir, "dsm.tif")
    memory = gdal.GetDriverByName("MEM").Create("", grid.shape[1], grid.shape[0], 1, gdal.GDT_Float32)
    memory.SetGeoTransform(gt)
    memory.SetProjection(srs.ExportToWkt())
    band = memory.GetRasterBand(1)
    band.SetNoDataValue(_DSM_NODATA)
    band.WriteArray(np.where(np.isfinite(grid), grid, _DSM_NODATA).astype(np.float32))
    # Close small gaps between fused points; larger holes stay nodata.
    gdal.FillNodata(band, None, maxSearchDist=int(args.fill_distance), smoothingIterations=0)
    gdal.GetDriverByName("GTiff").CreateCopy(
        dsm_path, memory, options=["TILED=YES", "COMPRESS=DEFLATE", "PREDICTOR=3"])
    filled = band.ReadAsArray()
    valid_after = int((filled != _DSM_NODATA).sum())

    result = {
        "schemaVersion": 1,
        "epsg": epsg,
        "points": int(vertices.size),
        "removedIsolatedPoints": removed,
        "pointCloud": cloud_path,
        "dsm": dsm_path,
        "resolution": resolution,
        "width": grid.shape[1],
        "height": grid.shape[0],
        "cellsWithPoints": filled_before,
        "cellsAfterFill": valid_after,
        "bounds": [float(x.min()), float(y.min()), float(x.max()), float(y.max())],
        "elevationRange": [float(z.min()), float(z.max())],
        "georeferencing": {
            "registeredImages": len(centers),
            "imagesWithReference": len(names),
            "usedImages": int(keep.sum()),
            "scale": scale,
            "rmseMeters": float(np.sqrt(np.mean(residual[keep] ** 2))),
            "maxResidualMeters": float(residual[keep].max()),
            "rejected": [n for n, k in zip(names, keep) if not k],
        },
    }
    with open(os.path.join(args.output_dir, "colmap-products.json"), "w", encoding="utf-8") as handle:
        json.dump(result, handle, indent=2)
    print(json.dumps(result))
