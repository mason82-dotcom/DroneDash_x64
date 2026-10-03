#!/usr/bin/env python3
import argparse
import json
import os
import sys


_CUDA_INDEX_PROCESSORS = {}
_CUDA_ZONE_PROCESSOR = None


def _cuda_probe(cv2):
    count = 0
    name = None
    build = None

    try:
        if hasattr(cv2, "cuda"):
            count = int(cv2.cuda.getCudaEnabledDeviceCount())
    except Exception:
        count = 0

    if count > 0:
        try:
            info = cv2.cuda.DeviceInfo(0)
            candidate = info.name()
            if candidate:
                name = str(candidate)
        except Exception:
            pass

    try:
        for line in cv2.getBuildInformation().splitlines():
            stripped = line.strip()
            if stripped.upper().startswith("NVIDIA CUDA"):
                build = stripped
                break
    except Exception:
        pass

    return {
        "cudaAvailable": count > 0,
        "cudaDeviceCount": count,
        "cudaDeviceName": name,
        "cudaBuild": build,
    }


def _cupy_probe():
    try:
        import cupy as cp
    except Exception:
        return {
            "cupyAvailable": False,
            "cupyVersion": None,
            "cupyDeviceCount": 0,
            "cupyDeviceName": None,
        }

    try:
        count = int(cp.cuda.runtime.getDeviceCount())
    except Exception:
        count = 0

    name = None

    if count > 0:
        try:
            properties = cp.cuda.runtime.getDeviceProperties(0)
            candidate = properties.get("name")
            if isinstance(candidate, bytes):
                candidate = candidate.decode("utf-8", errors="replace")
            if candidate:
                name = str(candidate)
        except Exception:
            pass

    return {
        "cupyAvailable": count > 0,
        "cupyVersion": getattr(cp, "__version__", None),
        "cupyDeviceCount": count,
        "cupyDeviceName": name,
    }


def _gdal_python_probe():
    try:
        from osgeo import gdal
    except Exception:
        return {
            "gdalPythonAvailable": False,
            "gdalPythonVersion": None,
        }

    try:
        version = gdal.VersionInfo("RELEASE_NAME")
    except Exception:
        version = None

    return {
        "gdalPythonAvailable": True,
        "gdalPythonVersion": version,
    }


def probe():
    result = {
        "opencvAvailable": False,
        "opencv": None,
        "numpyAvailable": False,
        "numpy": None,
    }

    try:
        import numpy as np

        result["numpyAvailable"] = True
        result["numpy"] = np.__version__
    except Exception:
        pass

    try:
        import cv2

        result["opencvAvailable"] = True
        result["opencv"] = cv2.__version__
        result.update(_cuda_probe(cv2))
    except Exception:
        result.update(
            {
                "cudaAvailable": False,
                "cudaDeviceCount": 0,
                "cudaDeviceName": None,
                "cudaBuild": None,
            }
        )

    result.update(_cupy_probe())
    result.update(_gdal_python_probe())
    print(json.dumps(result))


def _normalise(image, cv2, np):
    if image.ndim == 3:
        image = cv2.cvtColor(image, cv2.COLOR_BGR2GRAY)
    image = image.astype(np.float32)
    finite = np.isfinite(image)
    if not finite.any():
        raise RuntimeError("image contains no finite pixels")
    lo, hi = np.percentile(image[finite], [2.0, 98.0])
    if not np.isfinite(lo) or not np.isfinite(hi) or hi <= lo:
        lo = float(np.min(image[finite]))
        hi = float(np.max(image[finite]))
    if hi <= lo:
        return np.zeros_like(image, dtype=np.float32)
    return np.clip((image - lo) / (hi - lo), 0.0, 1.0).astype(np.float32)


def _cuda_resize(image, size, cv2):
    gpu = cv2.cuda_GpuMat()
    gpu.upload(image)
    resized = cv2.cuda.resize(
        gpu,
        size,
        interpolation=cv2.INTER_AREA,
    )
    return resized.download()


def _cpu_resize(image, size, cv2):
    return cv2.resize(
        image,
        size,
        interpolation=cv2.INTER_AREA,
    )


def _cuda_warp(image, warp, width, height, motion, flags, cv2):
    gpu = cv2.cuda_GpuMat()
    gpu.upload(image)

    if motion == "homography":
        warped = cv2.cuda.warpPerspective(
            gpu,
            warp,
            (width, height),
            flags=flags,
            borderMode=cv2.BORDER_REFLECT_101,
        )
    else:
        warped = cv2.cuda.warpAffine(
            gpu,
            warp,
            (width, height),
            flags=flags,
            borderMode=cv2.BORDER_REFLECT_101,
        )

    return warped.download()


def _cpu_warp(image, warp, width, height, motion, flags, cv2):
    if motion == "homography":
        return cv2.warpPerspective(
            image,
            warp,
            (width, height),
            flags=flags,
            borderMode=cv2.BORDER_REFLECT_101,
        )

    return cv2.warpAffine(
        image,
        warp,
        (width, height),
        flags=flags,
        borderMode=cv2.BORDER_REFLECT_101,
    )


def _resolve_registration_backend(requested, cv2):
    cuda = _cuda_probe(cv2)
    available = bool(cuda["cudaAvailable"])

    if requested == "cuda" and not available:
        raise RuntimeError(
            "CUDA backend requested, but this OpenCV build reports no CUDA-enabled device"
        )

    if requested == "cpu":
        return "cpu", cuda

    if available:
        return "cuda", cuda

    return "cpu", cuda


def _resolve_index_backend(requested):
    cupy = _cupy_probe()
    available = bool(cupy["cupyAvailable"])

    if requested == "cuda" and not available:
        raise RuntimeError(
            "CUDA index backend requested, but CuPy reports no CUDA-enabled device"
        )

    if requested == "cpu":
        return "cpu", cupy

    if available:
        return "cuda", cupy

    return "cpu", cupy


def _select_compute_backend(
    capability_backend,
    requested_backend,
    pixel_count,
    cuda_min_pixels,
):
    if capability_backend != "cuda":
        return "cpu"

    if requested_backend == "cuda":
        return "cuda"

    threshold = max(
        0,
        int(cuda_min_pixels),
    )

    return (
        "cuda"
        if int(pixel_count) >= threshold
        else "cpu"
    )


def _backend_usage(cuda_operations, cpu_operations):
    if cuda_operations > 0 and cpu_operations > 0:
        return "mixed"
    if cuda_operations > 0:
        return "cuda"
    return "cpu"


def register(args):
    import cv2
    import numpy as np

    reference_raw = cv2.imread(args.reference, cv2.IMREAD_UNCHANGED)
    moving_raw = cv2.imread(args.moving, cv2.IMREAD_UNCHANGED)

    if reference_raw is None:
        raise RuntimeError(f"cannot read reference: {args.reference}")
    if moving_raw is None:
        raise RuntimeError(f"cannot read moving image: {args.moving}")
    if reference_raw.shape[:2] != moving_raw.shape[:2]:
        raise RuntimeError(
            f"band dimensions differ: reference={reference_raw.shape[:2]} moving={moving_raw.shape[:2]}"
        )

    backend, cuda = _resolve_registration_backend(args.backend, cv2)

    reference = _normalise(reference_raw, cv2, np)
    moving = _normalise(moving_raw, cv2, np)

    height, width = reference.shape[:2]
    scale = min(1.0, float(args.max_dim) / float(max(width, height)))

    if scale < 1.0:
        small_size = (
            max(8, int(round(width * scale))),
            max(8, int(round(height * scale))),
        )

        if backend == "cuda":
            try:
                reference_small = _cuda_resize(reference, small_size, cv2)
                moving_small = _cuda_resize(moving, small_size, cv2)
            except Exception:
                if args.backend == "cuda":
                    raise
                backend = "cpu"
                reference_small = _cpu_resize(reference, small_size, cv2)
                moving_small = _cpu_resize(moving, small_size, cv2)
        else:
            reference_small = _cpu_resize(reference, small_size, cv2)
            moving_small = _cpu_resize(moving, small_size, cv2)
    else:
        reference_small = reference
        moving_small = moving

    if args.motion == "homography":
        motion = cv2.MOTION_HOMOGRAPHY
        warp = np.eye(3, 3, dtype=np.float32)
    else:
        motion = cv2.MOTION_AFFINE
        warp = np.eye(2, 3, dtype=np.float32)

    criteria = (
        cv2.TERM_CRITERIA_EPS | cv2.TERM_CRITERIA_COUNT,
        args.iterations,
        args.epsilon,
    )

    # OpenCV exposes ECC on the CPU. CUDA accelerates the expensive resize and
    # final warp stages while ECC itself remains the deterministic CPU step.
    score, warp = cv2.findTransformECC(
        reference_small,
        moving_small,
        warp,
        motion,
        criteria,
        None,
        5,
    )

    warp_full = warp.astype(np.float32).copy()
    if scale < 1.0:
        if args.motion == "homography":
            s = np.array(
                [[scale, 0, 0], [0, scale, 0], [0, 0, 1]],
                dtype=np.float32,
            )
            sinv = np.array(
                [[1.0 / scale, 0, 0], [0, 1.0 / scale, 0], [0, 0, 1]],
                dtype=np.float32,
            )
            warp_full = sinv @ warp_full @ s
        else:
            warp_full[0, 2] /= scale
            warp_full[1, 2] /= scale

    flags = cv2.INTER_CUBIC | cv2.WARP_INVERSE_MAP

    if backend == "cuda":
        try:
            aligned = _cuda_warp(
                moving_raw,
                warp_full,
                width,
                height,
                args.motion,
                flags,
                cv2,
            )
        except Exception:
            if args.backend == "cuda":
                raise
            backend = "cpu"
            aligned = _cpu_warp(
                moving_raw,
                warp_full,
                width,
                height,
                args.motion,
                flags,
                cv2,
            )
    else:
        aligned = _cpu_warp(
            moving_raw,
            warp_full,
            width,
            height,
            args.motion,
            flags,
            cv2,
        )

    os.makedirs(os.path.dirname(os.path.abspath(args.output)), exist_ok=True)
    os.makedirs(os.path.dirname(os.path.abspath(args.transform)), exist_ok=True)

    if not cv2.imwrite(args.output, aligned):
        raise RuntimeError(f"failed to write registered TIFF: {args.output}")

    with open(args.transform, "w", encoding="utf-8") as handle:
        json.dump(
            {
                "schemaVersion": 2,
                "reference": os.path.abspath(args.reference),
                "moving": os.path.abspath(args.moving),
                "output": os.path.abspath(args.output),
                "motion": args.motion,
                "eccScore": float(score),
                "matrix": warp_full.tolist(),
                "backendRequested": args.backend,
                "backendUsed": backend,
                "cudaDeviceCount": int(cuda["cudaDeviceCount"]),
                "cudaDeviceName": cuda["cudaDeviceName"],
                "cudaBuild": cuda["cudaBuild"],
                "note": (
                    "Pixel-space registration only. CUDA accelerates resize/warp when available; "
                    "ECC remains CPU. Output TIFF must not be treated as an orthorectified "
                    "geospatial product."
                ),
            },
            handle,
            indent=2,
        )

    print(
        json.dumps(
            {
                "eccScore": float(score),
                "output": os.path.abspath(args.output),
                "backendUsed": backend,
                "cudaDeviceName": cuda["cudaDeviceName"],
            }
        )
    )


def _load_single_band(path, cv2, np):
    image = cv2.imread(path, cv2.IMREAD_UNCHANGED)

    if image is None:
        raise RuntimeError(f"cannot read image: {path}")

    if image.ndim != 2:
        raise RuntimeError(
            f"vegetation-index input must be single-band: {path} shape={image.shape}"
        )

    return image.astype(np.float32, copy=False)


def _index_cpu(positive, comparison, epsilon, np):
    denominator = positive + comparison
    valid = (
        np.isfinite(positive)
        & np.isfinite(comparison)
        & (np.abs(denominator) >= epsilon)
    )

    result = np.full(
        positive.shape,
        np.nan,
        dtype=np.float32,
    )

    np.divide(
        positive - comparison,
        denominator,
        out=result,
        where=valid,
    )

    return result


class _CudaIndexProcessor:
    def __init__(self, epsilon):
        import cupy as cp

        self._cp = cp
        self._epsilon = float(epsilon)
        self._shape = None
        self._positive = None
        self._comparison = None
        self._result = None
        self._kernel = cp.ElementwiseKernel(
            "float32 positive, float32 comparison, float32 epsilon",
            "float32 result",
            """
            const float denominator = positive + comparison;
            if (
                isfinite(positive) &&
                isfinite(comparison) &&
                fabsf(denominator) >= epsilon
            ) {
                result = (positive - comparison) / denominator;
            } else {
                result = NAN;
            }
            """,
            "dronedash_vegetation_index",
        )

    def _ensure_buffers(self, shape):
        if self._shape == shape:
            return

        cp = self._cp
        self._shape = shape
        self._positive = cp.empty(shape, dtype=cp.float32)
        self._comparison = cp.empty(shape, dtype=cp.float32)
        self._result = cp.empty(shape, dtype=cp.float32)

    def compute(self, positive, comparison):
        self._ensure_buffers(positive.shape)
        self._positive.set(positive)
        self._comparison.set(comparison)

        self._kernel(
            self._positive,
            self._comparison,
            self._cp.float32(self._epsilon),
            self._result,
        )

        # Device-to-host transfer synchronizes the active stream.
        return self._cp.asnumpy(self._result)


def _get_cuda_index_processor(epsilon):
    key = float(epsilon)
    processor = _CUDA_INDEX_PROCESSORS.get(key)

    if processor is None:
        processor = _CudaIndexProcessor(key)
        _CUDA_INDEX_PROCESSORS[key] = processor

    return processor


def _index_cuda(positive, comparison, epsilon):
    return _get_cuda_index_processor(epsilon).compute(
        positive,
        comparison,
    )

def vegetation_index(args):
    import cv2
    import numpy as np

    positive = _load_single_band(
        args.positive_band,
        cv2,
        np,
    )
    comparison = _load_single_band(
        args.comparison_band,
        cv2,
        np,
    )

    if positive.shape != comparison.shape:
        raise RuntimeError(
            "vegetation-index band dimensions differ: "
            f"positive={positive.shape} comparison={comparison.shape}"
        )

    capability_backend, cupy = _resolve_index_backend(
        args.backend
    )
    backend = _select_compute_backend(
        capability_backend,
        args.backend,
        int(positive.size),
        args.cuda_min_pixels,
    )
    backend_fallback_reason = None

    if backend == "cuda":
        try:
            values = _index_cuda(
                positive,
                comparison,
                args.index_epsilon,
            )
        except Exception as exc:
            if args.backend == "cuda":
                raise

            backend = "cpu"
            backend_fallback_reason = (
                f"CUDA quicklook processing failed; "
                f"CPU fallback selected: {exc}"
            )
            _release_cuda_memory_pool()
            values = _index_cpu(
                positive,
                comparison,
                args.index_epsilon,
                np,
            )
    else:
        values = _index_cpu(
            positive,
            comparison,
            args.index_epsilon,
            np,
        )

    finite = np.isfinite(values)
    valid_pixels = int(np.count_nonzero(finite))

    if valid_pixels == 0:
        raise RuntimeError(
            "vegetation index contains no finite pixels"
        )

    finite_values = values[finite]

    os.makedirs(
        os.path.dirname(
            os.path.abspath(
                args.output
            )
        ),
        exist_ok=True,
    )

    if not cv2.imwrite(
        args.output,
        values.astype(
            np.float32,
            copy=False,
        ),
    ):
        raise RuntimeError(
            f"failed to write vegetation-index TIFF: {args.output}"
        )

    metadata = {
        "schemaVersion": 1,
        "index": args.index_type.upper(),
        "positiveBand": os.path.abspath(
            args.positive_band
        ),
        "comparisonBand": os.path.abspath(
            args.comparison_band
        ),
        "output": os.path.abspath(
            args.output
        ),
        "width": int(values.shape[1]),
        "height": int(values.shape[0]),
        "validPixels": valid_pixels,
        "minimum": float(np.min(finite_values)),
        "maximum": float(np.max(finite_values)),
        "average": float(np.mean(finite_values)),
        "backendRequested": args.backend,
        "backendUsed": backend,
        "cudaMinPixels": max(0, int(args.cuda_min_pixels)),
        "backendFallbackReason":
            backend_fallback_reason,
        "cupyVersion": cupy["cupyVersion"],
        "cudaDeviceCount": int(cupy["cupyDeviceCount"]),
        "cudaDeviceName": cupy["cupyDeviceName"],
        "note": (
            "Pixel-space vegetation-index quicklook. "
            "Output TIFF is not an orthorectified geospatial field product."
        ),
    }

    if args.metadata:
        os.makedirs(
            os.path.dirname(
                os.path.abspath(
                    args.metadata
                )
            ),
            exist_ok=True,
        )

        with open(
            args.metadata,
            "w",
            encoding="utf-8",
        ) as handle:
            json.dump(
                metadata,
                handle,
                indent=2,
            )

    print(
        json.dumps(
            metadata
        )
    )


def _copy_gdal_georeference(source, target):
    try:
        transform = source.GetGeoTransform(
            can_return_null=True
        )
    except TypeError:
        try:
            transform = source.GetGeoTransform()
        except Exception:
            transform = None

    if transform is not None:
        target.SetGeoTransform(transform)

    projection = source.GetProjection()
    if projection:
        target.SetProjection(projection)


def _prepare_source_tile(array, nodata, np):
    values = array.astype(np.float32, copy=False)

    if nodata is not None and np.isfinite(nodata):
        values = values.copy()
        values[values == np.float32(nodata)] = np.nan

    return values


def _write_json(path, payload):
    if not path:
        return

    os.makedirs(
        os.path.dirname(
            os.path.abspath(path)
        ),
        exist_ok=True,
    )

    temp = (
        os.path.abspath(path)
        + "."
        + os.urandom(8).hex()
        + ".tmp"
    )

    try:
        with open(
            temp,
            "w",
            encoding="utf-8",
        ) as handle:
            json.dump(
                payload,
                handle,
                indent=2,
            )

        os.replace(
            temp,
            os.path.abspath(path),
        )
    finally:
        try:
            if os.path.exists(temp):
                os.remove(temp)
        except Exception:
            pass


def _gdal_profile(source):
    try:
        transform = source.GetGeoTransform(
            can_return_null=True
        )
    except TypeError:
        try:
            transform = source.GetGeoTransform()
        except Exception:
            transform = None

    projection = source.GetProjection()

    return {
        "width": int(source.RasterXSize),
        "height": int(source.RasterYSize),
        "transform": transform,
        "projection": projection or None,
    }


def _create_geotiff_from_profile(
    profile,
    output,
    data_type,
    nodata,
    description,
):
    from osgeo import gdal

    driver = gdal.GetDriverByName("GTiff")
    if driver is None:
        raise RuntimeError("GDAL GTiff driver is unavailable")

    absolute_output = os.path.abspath(output)
    os.makedirs(
        os.path.dirname(absolute_output),
        exist_ok=True,
    )

    temp = (
        absolute_output
        + "."
        + os.urandom(8).hex()
        + ".tmp.tif"
    )

    target = driver.Create(
        temp,
        profile["width"],
        profile["height"],
        1,
        data_type,
        options=[
            "TILED=YES",
            "COMPRESS=DEFLATE",
            "BIGTIFF=IF_SAFER",
            "BLOCKXSIZE=512",
            "BLOCKYSIZE=512",
        ],
    )

    if target is None:
        raise RuntimeError(
            f"GDAL failed to create output: {temp}"
        )

    if profile["transform"] is not None:
        target.SetGeoTransform(
            profile["transform"]
        )

    if profile["projection"]:
        target.SetProjection(
            profile["projection"]
        )

    band = target.GetRasterBand(1)
    band.SetNoDataValue(nodata)
    band.SetDescription(description)

    return absolute_output, temp, target, band


def _benchmark_geotiff_write(
    values,
    output,
    data_type,
    nodata,
    description,
):
    import time

    height, width = values.shape[:2]
    directory = os.path.dirname(
        os.path.abspath(output)
    )
    os.makedirs(
        directory,
        exist_ok=True,
    )

    benchmark_output = os.path.join(
        directory,
        ".dronedash-write-benchmark-" +
        os.urandom(8).hex() +
        ".tif",
    )

    profile = {
        "width": int(width),
        "height": int(height),
        "transform": None,
        "projection": None,
    }

    absolute_output = None
    temp = None
    target = None
    band = None

    try:
        (
            absolute_output,
            temp,
            target,
            band,
        ) = _create_geotiff_from_profile(
            profile,
            benchmark_output,
            data_type,
            nodata,
            description,
        )

        started = time.perf_counter()

        band.WriteArray(
            values,
            0,
            0,
        )
        band = None
        target.FlushCache()

        return max(
            time.perf_counter() - started,
            0.0,
        )
    finally:
        band = None
        target = None
        _cleanup_temp(temp)
        _cleanup_temp(absolute_output)


class _AsyncGeoTiffWriter:
    def __init__(
        self,
        profile,
        output,
        data_type,
        nodata,
        description,
        pipeline_depth,
    ):
        from concurrent.futures import ThreadPoolExecutor

        self._profile = profile
        self._output = output
        self._data_type = data_type
        self._nodata = nodata
        self._description = description
        self._depth = max(
            1,
            min(
                int(pipeline_depth),
                4,
            ),
        )
        self._executor = ThreadPoolExecutor(
            max_workers=1,
            thread_name_prefix="dronedash-gdal-write",
        )
        self._pending = []
        self._state = {}
        self._finished = False

    def _ensure_open(self):
        if "target" in self._state:
            return

        (
            absolute_output,
            temp,
            target,
            band,
        ) = _create_geotiff_from_profile(
            self._profile,
            self._output,
            self._data_type,
            self._nodata,
            self._description,
        )

        self._state.update(
            {
                "absoluteOutput": absolute_output,
                "temp": temp,
                "target": target,
                "band": band,
            }
        )

    def _write(self, window, values):
        self._ensure_open()

        x, y, _, _ = window
        self._state["band"].WriteArray(
            values,
            x,
            y,
        )

    def submit(self, window, values):
        if self._finished:
            raise RuntimeError(
                "cannot submit a tile after writer finalization"
            )

        if len(self._pending) >= self._depth:
            self._pending.pop(0).result()

        self._pending.append(
            self._executor.submit(
                self._write,
                window,
                values,
            )
        )

    def _finish_on_writer(self, statistics):
        self._ensure_open()

        if statistics is not None:
            self._state["band"].SetStatistics(
                statistics["minimum"],
                statistics["maximum"],
                statistics["average"],
                statistics["standardDeviation"],
            )

        self._state["band"] = None
        self._state["target"].FlushCache()
        self._state["target"] = None

        os.replace(
            self._state["temp"],
            self._state["absoluteOutput"],
        )

        self._state["temp"] = None
        return self._state["absoluteOutput"]

    def finish(self, statistics=None):
        if self._finished:
            return self._state.get(
                "absoluteOutput"
            )

        try:
            for future in self._pending:
                future.result()

            self._pending.clear()

            result = self._executor.submit(
                self._finish_on_writer,
                statistics,
            ).result()

            self._finished = True
            return result
        finally:
            if self._finished:
                self._executor.shutdown(
                    wait=True,
                    cancel_futures=True,
                )

    def _abort_on_writer(self):
        self._state["band"] = None
        self._state["target"] = None
        _cleanup_temp(
            self._state.get("temp")
        )
        self._state["temp"] = None

    def abort(self):
        if self._finished:
            return

        for future in self._pending:
            future.cancel()

        self._pending.clear()

        try:
            self._executor.submit(
                self._abort_on_writer
            ).result()
        except Exception:
            _cleanup_temp(
                self._state.get("temp")
            )
        finally:
            self._executor.shutdown(
                wait=True,
                cancel_futures=True,
            )
            self._finished = True

def _cleanup_temp(path):
    try:
        if path and os.path.exists(path):
            os.remove(path)
    except Exception:
        pass


def _update_stats(values, np, stats):
    finite = np.isfinite(values)

    if not finite.any():
        return

    selected = values[finite].astype(
        np.float64,
        copy=False,
    )

    count = int(selected.size)
    tile_sum = float(np.sum(selected))
    tile_sum_squares = float(
        np.sum(selected * selected)
    )
    tile_min = float(np.min(selected))
    tile_max = float(np.max(selected))

    stats["count"] += count
    stats["sum"] += tile_sum
    stats["sumSquares"] += tile_sum_squares
    stats["minimum"] = (
        tile_min
        if stats["minimum"] is None
        else min(stats["minimum"], tile_min)
    )
    stats["maximum"] = (
        tile_max
        if stats["maximum"] is None
        else max(stats["maximum"], tile_max)
    )


def _final_stats(stats):
    count = stats["count"]

    if count <= 0:
        raise RuntimeError(
            "geospatial raster contains no finite output pixels"
        )

    average = stats["sum"] / count
    variance = max(
        0.0,
        stats["sumSquares"] / count
        - average * average,
    )

    return {
        "validPixels": count,
        "minimum": stats["minimum"],
        "maximum": stats["maximum"],
        "average": average,
        "standardDeviation": variance ** 0.5,
    }


def _tile_windows(width, height, tile_size):
    for y in range(0, height, tile_size):
        tile_height = min(tile_size, height - y)

        for x in range(0, width, tile_size):
            tile_width = min(tile_size, width - x)
            yield (x, y, tile_width, tile_height)


def _prefetched_tiles(windows, reader, pipeline_depth):
    from concurrent.futures import ThreadPoolExecutor

    depth = max(1, min(int(pipeline_depth), 4))
    iterator = iter(windows)
    pending = []

    with ThreadPoolExecutor(
        max_workers=1,
        thread_name_prefix="dronedash-gdal-read",
    ) as executor:
        for _ in range(depth):
            try:
                window = next(iterator)
            except StopIteration:
                break

            pending.append(
                (
                    window,
                    executor.submit(reader, window),
                )
            )

        while pending:
            window, future = pending.pop(0)
            payload = future.result()

            try:
                next_window = next(iterator)
            except StopIteration:
                next_window = None

            if next_window is not None:
                pending.append(
                    (
                        next_window,
                        executor.submit(reader, next_window),
                    )
                )

            yield (window, payload)


def _parse_auto_or_int(value, minimum, maximum, name):
    text = str(value).strip().lower()

    if text == "auto":
        return None

    try:
        parsed = int(text)
    except (TypeError, ValueError):
        raise RuntimeError(
            f"{name} must be 'auto' or an integer"
        )

    if parsed < minimum or parsed > maximum:
        raise RuntimeError(
            f"{name} must be within {minimum}..{maximum}"
        )

    return parsed


def _system_available_memory_bytes():
    if os.name == "nt":
        try:
            import ctypes

            class MemoryStatusEx(ctypes.Structure):
                _fields_ = [
                    ("dwLength", ctypes.c_ulong),
                    ("dwMemoryLoad", ctypes.c_ulong),
                    ("ullTotalPhys", ctypes.c_ulonglong),
                    ("ullAvailPhys", ctypes.c_ulonglong),
                    ("ullTotalPageFile", ctypes.c_ulonglong),
                    ("ullAvailPageFile", ctypes.c_ulonglong),
                    ("ullTotalVirtual", ctypes.c_ulonglong),
                    ("ullAvailVirtual", ctypes.c_ulonglong),
                    ("ullAvailExtendedVirtual", ctypes.c_ulonglong),
                ]

            status = MemoryStatusEx()
            status.dwLength = ctypes.sizeof(
                MemoryStatusEx
            )

            if ctypes.windll.kernel32.GlobalMemoryStatusEx(
                ctypes.byref(status)
            ):
                return int(status.ullAvailPhys)
        except Exception:
            return None

    try:
        pages = os.sysconf("SC_AVPHYS_PAGES")
        page_size = os.sysconf("SC_PAGE_SIZE")

        if pages > 0 and page_size > 0:
            return int(pages * page_size)
    except Exception:
        pass

    return None


def _cuda_memory_snapshot(backend):
    if backend != "cuda":
        return {
            "freeBytes": None,
            "totalBytes": None,
        }

    try:
        import cupy as cp

        free_bytes, total_bytes = (
            cp.cuda.runtime.memGetInfo()
        )

        return {
            "freeBytes": int(free_bytes),
            "totalBytes": int(total_bytes),
        }
    except Exception:
        return {
            "freeBytes": None,
            "totalBytes": None,
        }


def _estimated_host_pipeline_bytes(
    tile_size,
    pipeline_depth,
    operation,
):
    pixels = int(tile_size) * int(tile_size)
    depth = max(
        1,
        min(
            int(pipeline_depth),
            4,
        ),
    )

    if operation == "index":
        bytes_per_pixel = 20 + 12 * depth
    else:
        bytes_per_pixel = 12 + 5 * depth

    return pixels * bytes_per_pixel


def _estimated_cuda_working_bytes(
    tile_size,
    operation,
):
    pixels = int(tile_size) * int(tile_size)

    if operation == "index":
        return pixels * 20

    return pixels * 10


def _safe_tile_candidates(
    width,
    height,
    operation,
    pipeline_depth,
    backend,
    system_available_bytes,
    cuda_free_bytes,
):
    candidates = [
        128,
        256,
        512,
        1024,
        2048,
        4096,
    ]

    max_dimension = max(
        int(width),
        int(height),
    )

    if max_dimension <= 128:
        return [128]

    safe = []

    for tile_size in candidates:
        host_bytes = _estimated_host_pipeline_bytes(
            tile_size,
            pipeline_depth,
            operation,
        )

        host_ok = (
            system_available_bytes is None
            or host_bytes <=
            int(system_available_bytes * 0.20)
        )

        cuda_bytes = _estimated_cuda_working_bytes(
            tile_size,
            operation,
        )

        cuda_ok = (
            backend != "cuda"
            or cuda_free_bytes is None
            or cuda_bytes <=
            int(cuda_free_bytes * 0.30)
        )

        if host_ok and cuda_ok:
            safe.append(tile_size)

    if not safe:
        raise RuntimeError(
            "no safe GDAL tile size fits the current RAM/VRAM budget"
        )

    return safe


def _release_cuda_memory_pool():
    try:
        import cupy as cp

        cp.get_default_memory_pool().free_all_blocks()
        cp.get_default_pinned_memory_pool().free_all_blocks()
    except Exception:
        pass


def _benchmark_tiles(candidates, benchmark):
    import statistics

    results = []

    for tile_size in candidates:
        try:
            benchmark(tile_size, -1)
            _release_cuda_memory_pool()

            samples = [
                benchmark(tile_size, sample_index)
                for sample_index in range(3)
            ]

            read_seconds = statistics.median(
                max(
                    float(sample["readSeconds"]),
                    0.0,
                )
                for sample in samples
            )
            compute_seconds = statistics.median(
                max(
                    float(sample["computeSeconds"]),
                    0.0,
                )
                for sample in samples
            )
            write_samples = [
                max(
                    float(sample["writeSeconds"]),
                    0.0,
                )
                for sample in samples
                if sample.get("writeSeconds") is not None
            ]
            write_seconds = (
                statistics.median(write_samples)
                if write_samples
                else 0.0
            )
            pixels = max(
                int(statistics.median(
                    int(sample["pixels"])
                    for sample in samples
                )),
                1,
            )
            serial_seconds = max(
                read_seconds +
                compute_seconds +
                write_seconds,
                1e-9,
            )
            pipeline_stage_seconds = max(
                read_seconds,
                compute_seconds,
                write_seconds,
                1e-9,
            )
            stages = {
                "read": read_seconds,
                "compute": compute_seconds,
                "write": write_seconds,
            }
            bottleneck_stage = max(
                stages,
                key=stages.get,
            )

            results.append(
                {
                    "tileSize": int(tile_size),
                    "success": True,
                    "pixels": pixels,
                    "readSeconds": read_seconds,
                    "computeSeconds": compute_seconds,
                    "writeSeconds": write_seconds,
                    "serialPixelsPerSecond": (
                        pixels / serial_seconds
                    ),
                    "pixelsPerSecond": (
                        pixels / pipeline_stage_seconds
                    ),
                    "bottleneckStage":
                        bottleneck_stage,
                }
            )
        except Exception as exc:
            results.append(
                {
                    "tileSize": int(tile_size),
                    "success": False,
                    "error": str(exc),
                }
            )
        finally:
            _release_cuda_memory_pool()

    return results


def _choose_benchmark_tile(results):
    successful = [
        item
        for item in results
        if item.get("success", True)
    ]

    if not successful:
        errors = "; ".join(
            f"{item.get('tileSize')}: {item.get('error', 'failed')}"
            for item in results
        )
        raise RuntimeError(
            "auto-tuning produced no successful tile benchmark results" +
            (f": {errors}" if errors else "")
        )

    best_score = max(
        item["pixelsPerSecond"]
        for item in successful
    )

    threshold = best_score * 0.90

    near_best = [
        item
        for item in successful
        if item["pixelsPerSecond"] >= threshold
    ]

    return min(
        near_best,
        key=lambda item: item["tileSize"],
    )


def _choose_pipeline_depth(
    requested_depth,
    width,
    height,
    tile_size,
    operation,
    system_available_bytes,
    benchmark_result,
):
    manual = _parse_auto_or_int(
        requested_depth,
        1,
        4,
        "pipeline-depth",
    )

    if manual is not None:
        return manual

    columns = (
        int(width) + tile_size - 1
    ) // tile_size
    rows = (
        int(height) + tile_size - 1
    ) // tile_size
    tile_count = max(
        1,
        columns * rows,
    )

    if tile_count <= 2:
        target = 1
    else:
        read_seconds = max(
            float(
                benchmark_result.get(
                    "readSeconds",
                    0.0,
                )
            ),
            1e-9,
        )
        compute_seconds = max(
            float(
                benchmark_result.get(
                    "computeSeconds",
                    0.0,
                )
            ),
            1e-9,
        )
        write_seconds = max(
            float(
                benchmark_result.get(
                    "writeSeconds",
                    0.0,
                )
            ),
            1e-9,
        )
        io_seconds = max(
            read_seconds,
            write_seconds,
        )

        if (
            tile_count >= 16
            and io_seconds >
            compute_seconds * 3.0
        ):
            target = 4
        elif (
            tile_count >= 8
            and io_seconds >
            compute_seconds * 1.5
        ):
            target = 3
        elif (
            compute_seconds >
            io_seconds * 4.0
        ):
            target = 1
        else:
            target = 2

    while target > 1:
        estimated = _estimated_host_pipeline_bytes(
            tile_size,
            target,
            operation,
        )

        if (
            system_available_bytes is None
            or estimated <=
            int(system_available_bytes * 0.20)
        ):
            break

        target -= 1

    return target


def _benchmark_window(
    raster_width,
    raster_height,
    tile_size,
    sample_index,
):
    width = min(
        int(tile_size),
        int(raster_width),
    )
    height = min(
        int(tile_size),
        int(raster_height),
    )

    max_x = max(
        0,
        int(raster_width) - width,
    )
    max_y = max(
        0,
        int(raster_height) - height,
    )

    if sample_index < 0:
        x = max_x // 2
        y = max_y // 2
    elif sample_index == 0:
        x = 0
        y = 0
    elif sample_index == 1:
        x = max_x // 2
        y = max_y // 2
    else:
        x = max_x
        y = max_y

    return x, y, width, height


def _auto_tune_geospatial(
    requested_tile_size,
    requested_pipeline_depth,
    width,
    height,
    operation,
    backend,
    benchmark,
):
    import time

    tuning_started = time.perf_counter()

    manual_tile = _parse_auto_or_int(
        requested_tile_size,
        128,
        8192,
        "tile-size",
    )
    manual_depth = _parse_auto_or_int(
        requested_pipeline_depth,
        1,
        4,
        "pipeline-depth",
    )

    system_available = (
        _system_available_memory_bytes()
    )
    cuda_memory = _cuda_memory_snapshot(
        backend
    )

    provisional_depth = (
        manual_depth
        if manual_depth is not None
        else 2
    )

    if manual_tile is not None:
        host_bytes = _estimated_host_pipeline_bytes(
            manual_tile,
            provisional_depth,
            operation,
        )
        cuda_bytes = _estimated_cuda_working_bytes(
            manual_tile,
            operation,
        )

        if (
            system_available is not None
            and host_bytes >
            int(system_available * 0.20)
        ):
            raise RuntimeError(
                "manual tile-size exceeds the current RAM safety budget"
            )

        if (
            backend == "cuda"
            and cuda_memory["freeBytes"] is not None
            and cuda_bytes >
            int(cuda_memory["freeBytes"] * 0.30)
        ):
            raise RuntimeError(
                "manual tile-size exceeds the current VRAM safety budget"
            )

        candidates = [manual_tile]
    else:
        candidates = _safe_tile_candidates(
            width,
            height,
            operation,
            provisional_depth,
            backend,
            system_available,
            cuda_memory["freeBytes"],
        )

    benchmark_results = []

    if (
        manual_tile is None
        or manual_depth is None
    ):
        benchmark_results = _benchmark_tiles(
            candidates,
            benchmark,
        )

    if manual_tile is not None:
        tile_size = manual_tile

        if (
            benchmark_results
            and not benchmark_results[0].get(
                "success",
                True,
            )
        ):
            raise RuntimeError(
                "manual tile benchmark failed: " +
                benchmark_results[0].get(
                    "error",
                    "unknown error",
                )
            )

        selected_benchmark = (
            benchmark_results[0]
            if benchmark_results
            else {
                "tileSize": manual_tile,
                "success": True,
                "pixels": 0,
                "readSeconds": 0.0,
                "computeSeconds": 0.0,
                "writeSeconds": 0.0,
                "serialPixelsPerSecond": 0.0,
                "pixelsPerSecond": 0.0,
                "bottleneckStage": "none",
            }
        )
    else:
        selected_benchmark = (
            _choose_benchmark_tile(
                benchmark_results
            )
        )
        tile_size = int(
            selected_benchmark["tileSize"]
        )

    pipeline_depth = _choose_pipeline_depth(
        requested_pipeline_depth,
        width,
        height,
        tile_size,
        operation,
        system_available,
        selected_benchmark,
    )

    return {
        "mode": (
            "manual"
            if manual_tile is not None
            and manual_depth is not None
            else "auto"
        ),
        "tileSizeRequested": str(
            requested_tile_size
        ),
        "pipelineDepthRequested": str(
            requested_pipeline_depth
        ),
        "tileSize": tile_size,
        "pipelineDepth": pipeline_depth,
        "systemAvailableMemoryBytes": (
            system_available
        ),
        "cudaFreeMemoryBytes": (
            cuda_memory["freeBytes"]
        ),
        "cudaTotalMemoryBytes": (
            cuda_memory["totalBytes"]
        ),
        "benchmarkResults": benchmark_results,
        "selectedBenchmark": (
            selected_benchmark
            if benchmark_results
            else None
        ),
        "tuningSeconds": max(
            time.perf_counter() - tuning_started,
            0.0,
        ),
    }


def geospatial_index(args):
    import time

    import numpy as np
    from osgeo import gdal

    started_at = time.perf_counter()

    source_path = os.path.abspath(
        args.source
    )

    source = gdal.Open(
        source_path,
        gdal.GA_ReadOnly,
    )

    if source is None:
        raise RuntimeError(
            f"GDAL cannot open source raster: {source_path}"
        )

    positive_index = int(args.positive_band_index)
    comparison_index = int(args.comparison_band_index)

    if (
        positive_index < 1
        or positive_index > source.RasterCount
        or comparison_index < 1
        or comparison_index > source.RasterCount
    ):
        raise RuntimeError(
            "requested source band index is outside the GDAL raster"
        )

    backend, cupy = _resolve_index_backend(
        args.backend
    )

    profile = _gdal_profile(
        source
    )

    positive_band = source.GetRasterBand(
        positive_index
    )
    comparison_band = source.GetRasterBand(
        comparison_index
    )
    positive_nodata = positive_band.GetNoDataValue()
    comparison_nodata = (
        comparison_band.GetNoDataValue()
    )

    def benchmark_index(
        tile_size,
        sample_index,
    ):
        x, y, width, height = _benchmark_window(
            source.RasterXSize,
            source.RasterYSize,
            tile_size,
            sample_index,
        )

        read_started = time.perf_counter()

        positive = positive_band.ReadAsArray(
            x,
            y,
            width,
            height,
        )
        comparison = comparison_band.ReadAsArray(
            x,
            y,
            width,
            height,
        )

        if positive is None or comparison is None:
            raise RuntimeError(
                "GDAL auto-tuning failed to read sample tile"
            )

        positive = _prepare_source_tile(
            positive,
            positive_nodata,
            np,
        )
        comparison = _prepare_source_tile(
            comparison,
            comparison_nodata,
            np,
        )

        read_seconds = (
            time.perf_counter() - read_started
        )
        compute_started = time.perf_counter()

        sample_backend = _select_compute_backend(
            backend,
            args.backend,
            width * height,
            args.cuda_min_pixels,
        )

        if sample_backend == "cuda":
            result_values = _index_cuda(
                positive,
                comparison,
                args.index_epsilon,
            )
        else:
            result_values = _index_cpu(
                positive,
                comparison,
                args.index_epsilon,
                np,
            )

        compute_seconds = (
            time.perf_counter() -
            compute_started
        )
        write_seconds = None

        if sample_index == 1:
            write_seconds = _benchmark_geotiff_write(
                result_values,
                args.output,
                gdal.GDT_Float32,
                float("nan"),
                args.index_type.upper(),
            )

        return {
            "pixels": width * height,
            "readSeconds": read_seconds,
            "computeSeconds": compute_seconds,
            "writeSeconds": write_seconds,
        }

    backend_fallback_reason = None
    cuda_tiles = 0
    cpu_tiles = 0

    try:
        tuning = _auto_tune_geospatial(
            args.tile_size,
            args.pipeline_depth,
            source.RasterXSize,
            source.RasterYSize,
            "index",
            backend,
            benchmark_index,
        )
    except Exception as exc:
        if (
            backend == "cuda"
            and args.backend == "auto"
        ):
            backend = "cpu"
            backend_fallback_reason = (
                "CUDA auto-tuning failed; "
                f"CPU fallback selected: {exc}"
            )
            tuning = _auto_tune_geospatial(
                args.tile_size,
                args.pipeline_depth,
                source.RasterXSize,
                source.RasterYSize,
                "index",
                backend,
                benchmark_index,
            )
        else:
            raise

    tile_size = int(
        tuning["tileSize"]
    )
    pipeline_depth = int(
        tuning["pipelineDepth"]
    )

    writer = _AsyncGeoTiffWriter(
        profile,
        args.output,
        gdal.GDT_Float32,
        float("nan"),
        args.index_type.upper(),
        pipeline_depth,
    )

    processing_started_at = time.perf_counter()
    absolute_output = None

    stats = {
        "count": 0,
        "sum": 0.0,
        "sumSquares": 0.0,
        "minimum": None,
        "maximum": None,
    }

    tiles = 0

    try:
        reader_state = {}

        def read_index_tile(window):
            if "source" not in reader_state:
                reader_source = gdal.Open(
                    source_path,
                    gdal.GA_ReadOnly,
                )

                if reader_source is None:
                    raise RuntimeError(
                        f"GDAL read-ahead thread cannot open source raster: {source_path}"
                    )

                reader_state["source"] = reader_source
                reader_state["positive"] = (
                    reader_source.GetRasterBand(
                        positive_index
                    )
                )
                reader_state["comparison"] = (
                    reader_source.GetRasterBand(
                        comparison_index
                    )
                )
                reader_state["positiveNoData"] = (
                    reader_state["positive"]
                    .GetNoDataValue()
                )
                reader_state["comparisonNoData"] = (
                    reader_state["comparison"]
                    .GetNoDataValue()
                )

            x, y, width, height = window

            positive = (
                reader_state["positive"]
                .ReadAsArray(
                    x,
                    y,
                    width,
                    height,
                )
            )
            comparison = (
                reader_state["comparison"]
                .ReadAsArray(
                    x,
                    y,
                    width,
                    height,
                )
            )

            if positive is None or comparison is None:
                raise RuntimeError(
                    f"GDAL failed reading tile x={x} y={y}"
                )

            return (
                _prepare_source_tile(
                    positive,
                    reader_state["positiveNoData"],
                    np,
                ),
                _prepare_source_tile(
                    comparison,
                    reader_state["comparisonNoData"],
                    np,
                ),
            )

        windows = _tile_windows(
            source.RasterXSize,
            source.RasterYSize,
            tile_size,
        )

        for (
            (x, y, width, height),
            (positive, comparison),
        ) in _prefetched_tiles(
            windows,
            read_index_tile,
            pipeline_depth,
        ):
            tile_backend = _select_compute_backend(
                backend,
                args.backend,
                width * height,
                args.cuda_min_pixels,
            )

            if tile_backend == "cuda":
                try:
                    values = _index_cuda(
                        positive,
                        comparison,
                        args.index_epsilon,
                    )
                    cuda_tiles += 1
                except Exception as exc:
                    if args.backend == "cuda":
                        raise

                    backend = "cpu"
                    backend_fallback_reason = (
                        f"CUDA processing failed at tile x={x} y={y}; "
                        f"CPU fallback selected: {exc}"
                    )
                    _release_cuda_memory_pool()
                    values = _index_cpu(
                        positive,
                        comparison,
                        args.index_epsilon,
                        np,
                    )
                    cpu_tiles += 1
            else:
                values = _index_cpu(
                    positive,
                    comparison,
                    args.index_epsilon,
                    np,
                )
                cpu_tiles += 1

            writer.submit(
                (x, y, width, height),
                values,
            )

            _update_stats(
                values,
                np,
                stats,
            )

            tiles += 1

        final_stats = _final_stats(
            stats
        )

        absolute_output = writer.finish(
            final_stats
        )

        elapsed_seconds = max(
            time.perf_counter() - started_at,
            1e-9,
        )
        processing_elapsed_seconds = max(
            time.perf_counter() -
            processing_started_at,
            1e-9,
        )
        backend_used = _backend_usage(
            cuda_tiles,
            cpu_tiles,
        )

        metadata = {
            "schemaVersion": 1,
            "operation": "geospatial-index",
            "index": args.index_type.upper(),
            "source": source_path,
            "positiveBandIndex": positive_index,
            "comparisonBandIndex": comparison_index,
            "output": absolute_output,
            "width": int(source.RasterXSize),
            "height": int(source.RasterYSize),
            "tileSize": tile_size,
            "pipelineDepth": pipeline_depth,
            "tuning": tuning,
            "readAheadEnabled": True,
            "asyncWriteEnabled": True,
            "tilesProcessed": tiles,
            "elapsedSeconds": elapsed_seconds,
            "processingElapsedSeconds":
                processing_elapsed_seconds,
            "tilesPerSecond":
                tiles / processing_elapsed_seconds,
            "pixelsPerSecond":
                (
                    int(source.RasterXSize) *
                    int(source.RasterYSize)
                ) / processing_elapsed_seconds,
            "backendRequested": args.backend,
            "backendUsed": backend_used,
            "cudaMinPixels": max(0, int(args.cuda_min_pixels)),
            "cudaTiles": cuda_tiles,
            "cpuTiles": cpu_tiles,
            "backendFallbackReason":
                backend_fallback_reason,
            "gdalVersion": gdal.VersionInfo(
                "RELEASE_NAME"
            ),
            "cupyVersion": cupy["cupyVersion"],
            "cudaDeviceCount": int(
                cupy["cupyDeviceCount"]
            ),
            "cudaDeviceName": cupy["cupyDeviceName"],
            **final_stats,
            "note": (
                "Tile-based geospatial vegetation-index product. "
                "GDAL preserves source raster geometry/CRS; "
                "CuPy accelerates tile arithmetic when available."
            ),
        }

        _write_json(
            args.metadata,
            metadata,
        )

        print(
            json.dumps(
                metadata
            )
        )
    finally:
        source = None
        writer.abort()


def _zones_cpu(values, thresholds, np):
    finite = np.isfinite(values)
    output = np.zeros(
        values.shape,
        dtype=np.uint8,
    )

    output[
        finite & (values < thresholds[0])
    ] = 1
    output[
        finite
        & (values >= thresholds[0])
        & (values < thresholds[1])
    ] = 2
    output[
        finite
        & (values >= thresholds[1])
        & (values < thresholds[2])
    ] = 3
    output[
        finite
        & (values >= thresholds[2])
        & (values < thresholds[3])
    ] = 4
    output[
        finite & (values >= thresholds[3])
    ] = 5

    return output


class _CudaZoneProcessor:
    def __init__(self):
        import cupy as cp

        self._cp = cp
        self._shape = None
        self._values = None
        self._result = None
        self._kernel = cp.ElementwiseKernel(
            (
                "float32 value, "
                "float32 threshold1, "
                "float32 threshold2, "
                "float32 threshold3, "
                "float32 threshold4"
            ),
            "uint8 result",
            """
            if (!isfinite(value)) {
                result = 0;
            } else if (value < threshold1) {
                result = 1;
            } else if (value < threshold2) {
                result = 2;
            } else if (value < threshold3) {
                result = 3;
            } else if (value < threshold4) {
                result = 4;
            } else {
                result = 5;
            }
            """,
            "dronedash_ndvi_zones",
        )

    def _ensure_buffers(self, shape):
        if self._shape == shape:
            return

        cp = self._cp
        self._shape = shape
        self._values = cp.empty(shape, dtype=cp.float32)
        self._result = cp.empty(shape, dtype=cp.uint8)

    def compute(self, values, thresholds):
        self._ensure_buffers(values.shape)
        self._values.set(values)

        self._kernel(
            self._values,
            self._cp.float32(thresholds[0]),
            self._cp.float32(thresholds[1]),
            self._cp.float32(thresholds[2]),
            self._cp.float32(thresholds[3]),
            self._result,
        )

        return self._cp.asnumpy(self._result)


def _zones_cuda(values, thresholds):
    global _CUDA_ZONE_PROCESSOR

    if _CUDA_ZONE_PROCESSOR is None:
        _CUDA_ZONE_PROCESSOR = _CudaZoneProcessor()

    return _CUDA_ZONE_PROCESSOR.compute(
        values,
        thresholds,
    )

def geospatial_zones(args):
    import time

    import numpy as np
    from osgeo import gdal

    started_at = time.perf_counter()

    thresholds = [
        float(args.threshold1),
        float(args.threshold2),
        float(args.threshold3),
        float(args.threshold4),
    ]

    if any(
        not np.isfinite(value)
        or value < -1.0
        or value > 1.0
        for value in thresholds
    ) or not (
        thresholds[0]
        < thresholds[1]
        < thresholds[2]
        < thresholds[3]
    ):
        raise RuntimeError(
            "scouting thresholds must be finite, within [-1, 1], and strictly increasing"
        )

    source_path = os.path.abspath(
        args.source
    )

    source = gdal.Open(
        source_path,
        gdal.GA_ReadOnly,
    )

    if source is None or source.RasterCount < 1:
        raise RuntimeError(
            f"GDAL cannot open NDVI source raster: {source_path}"
        )

    backend, cupy = _resolve_index_backend(
        args.backend
    )

    profile = _gdal_profile(
        source
    )

    input_band = source.GetRasterBand(1)
    input_nodata = input_band.GetNoDataValue()

    def benchmark_zones(
        tile_size,
        sample_index,
    ):
        x, y, width, height = _benchmark_window(
            source.RasterXSize,
            source.RasterYSize,
            tile_size,
            sample_index,
        )

        read_started = time.perf_counter()

        values = input_band.ReadAsArray(
            x,
            y,
            width,
            height,
        )

        if values is None:
            raise RuntimeError(
                "GDAL auto-tuning failed to read NDVI sample tile"
            )

        values = _prepare_source_tile(
            values,
            input_nodata,
            np,
        )

        read_seconds = (
            time.perf_counter() - read_started
        )
        compute_started = time.perf_counter()

        sample_backend = _select_compute_backend(
            backend,
            args.backend,
            width * height,
            args.cuda_min_pixels,
        )

        if sample_backend == "cuda":
            result_zones = _zones_cuda(
                values,
                thresholds,
            )
        else:
            result_zones = _zones_cpu(
                values,
                thresholds,
                np,
            )

        compute_seconds = (
            time.perf_counter() -
            compute_started
        )
        write_seconds = None

        if sample_index == 1:
            write_seconds = _benchmark_geotiff_write(
                result_zones,
                args.output,
                gdal.GDT_Byte,
                0,
                "NDVI_SCOUTING_ZONES",
            )

        return {
            "pixels": width * height,
            "readSeconds": read_seconds,
            "computeSeconds": compute_seconds,
            "writeSeconds": write_seconds,
        }

    backend_fallback_reason = None
    cuda_tiles = 0
    cpu_tiles = 0

    try:
        tuning = _auto_tune_geospatial(
            args.tile_size,
            args.pipeline_depth,
            source.RasterXSize,
            source.RasterYSize,
            "zones",
            backend,
            benchmark_zones,
        )
    except Exception as exc:
        if (
            backend == "cuda"
            and args.backend == "auto"
        ):
            backend = "cpu"
            backend_fallback_reason = (
                "CUDA auto-tuning failed; "
                f"CPU fallback selected: {exc}"
            )
            tuning = _auto_tune_geospatial(
                args.tile_size,
                args.pipeline_depth,
                source.RasterXSize,
                source.RasterYSize,
                "zones",
                backend,
                benchmark_zones,
            )
        else:
            raise

    tile_size = int(
        tuning["tileSize"]
    )
    pipeline_depth = int(
        tuning["pipelineDepth"]
    )

    writer = _AsyncGeoTiffWriter(
        profile,
        args.output,
        gdal.GDT_Byte,
        0,
        "NDVI_SCOUTING_ZONES",
        pipeline_depth,
    )

    processing_started_at = time.perf_counter()
    absolute_output = None
    tiles = 0
    counts = [0, 0, 0, 0, 0]

    try:
        reader_state = {}

        def read_zone_tile(window):
            if "source" not in reader_state:
                reader_source = gdal.Open(
                    source_path,
                    gdal.GA_ReadOnly,
                )

                if reader_source is None:
                    raise RuntimeError(
                        f"GDAL read-ahead thread cannot open NDVI raster: {source_path}"
                    )

                reader_state["source"] = reader_source
                reader_state["band"] = (
                    reader_source.GetRasterBand(1)
                )
                reader_state["nodata"] = (
                    reader_state["band"]
                    .GetNoDataValue()
                )

            x, y, width, height = window

            values = (
                reader_state["band"]
                .ReadAsArray(
                    x,
                    y,
                    width,
                    height,
                )
            )

            if values is None:
                raise RuntimeError(
                    f"GDAL failed reading NDVI tile x={x} y={y}"
                )

            return _prepare_source_tile(
                values,
                reader_state["nodata"],
                np,
            )

        windows = _tile_windows(
            source.RasterXSize,
            source.RasterYSize,
            tile_size,
        )

        for (
            (x, y, width, height),
            values,
        ) in _prefetched_tiles(
            windows,
            read_zone_tile,
            pipeline_depth,
        ):
            tile_backend = _select_compute_backend(
                backend,
                args.backend,
                width * height,
                args.cuda_min_pixels,
            )

            if tile_backend == "cuda":
                try:
                    zones = _zones_cuda(
                        values,
                        thresholds,
                    )
                    cuda_tiles += 1
                except Exception as exc:
                    if args.backend == "cuda":
                        raise

                    backend = "cpu"
                    backend_fallback_reason = (
                        f"CUDA processing failed at tile x={x} y={y}; "
                        f"CPU fallback selected: {exc}"
                    )
                    _release_cuda_memory_pool()
                    zones = _zones_cpu(
                        values,
                        thresholds,
                        np,
                    )
                    cpu_tiles += 1
            else:
                zones = _zones_cpu(
                    values,
                    thresholds,
                    np,
                )
                cpu_tiles += 1

            writer.submit(
                (x, y, width, height),
                zones,
            )

            for zone in range(1, 6):
                counts[zone - 1] += int(
                    np.count_nonzero(
                        zones == zone
                    )
                )

            tiles += 1

        absolute_output = writer.finish()

        elapsed_seconds = max(
            time.perf_counter() - started_at,
            1e-9,
        )
        processing_elapsed_seconds = max(
            time.perf_counter() -
            processing_started_at,
            1e-9,
        )
        backend_used = _backend_usage(
            cuda_tiles,
            cpu_tiles,
        )

        metadata = {
            "schemaVersion": 1,
            "operation": "geospatial-zones",
            "source": source_path,
            "output": absolute_output,
            "width": int(source.RasterXSize),
            "height": int(source.RasterYSize),
            "tileSize": tile_size,
            "pipelineDepth": pipeline_depth,
            "tuning": tuning,
            "readAheadEnabled": True,
            "asyncWriteEnabled": True,
            "tilesProcessed": tiles,
            "elapsedSeconds": elapsed_seconds,
            "processingElapsedSeconds":
                processing_elapsed_seconds,
            "tilesPerSecond":
                tiles / processing_elapsed_seconds,
            "pixelsPerSecond":
                (
                    int(source.RasterXSize) *
                    int(source.RasterYSize)
                ) / processing_elapsed_seconds,
            "thresholds": thresholds,
            "zonePixelCounts": counts,
            "backendRequested": args.backend,
            "backendUsed": backend_used,
            "cudaMinPixels": max(0, int(args.cuda_min_pixels)),
            "cudaTiles": cuda_tiles,
            "cpuTiles": cpu_tiles,
            "backendFallbackReason":
                backend_fallback_reason,
            "gdalVersion": gdal.VersionInfo(
                "RELEASE_NAME"
            ),
            "cupyVersion": cupy["cupyVersion"],
            "cudaDeviceCount": int(
                cupy["cupyDeviceCount"]
            ),
            "cudaDeviceName": cupy["cupyDeviceName"],
            "note": (
                "Tile-based geospatial NDVI scouting zones. "
                "Zone 0 is NoData; zones 1-5 follow configured thresholds."
            ),
        }

        _write_json(
            args.metadata,
            metadata,
        )

        print(
            json.dumps(
                metadata
            )
        )
    finally:
        source = None
        writer.abort()


def _serve_jsonl():
    import contextlib
    import io

    for line in sys.stdin:
        if not line.strip():
            continue

        request_id = None

        try:
            request = json.loads(line)
            request_id = request.get("id")

            if request.get("shutdown"):
                print(
                    json.dumps(
                        {
                            "id": request_id,
                            "ok": True,
                            "stdout": [],
                            "stderr": [],
                        }
                    ),
                    flush=True,
                )
                return

            arguments = request.get("arguments")

            if (
                not isinstance(arguments, list)
                or not all(isinstance(value, str) for value in arguments)
                or "--serve-jsonl" in arguments
            ):
                raise ValueError(
                    "server request requires a string arguments array"
                )

            stdout_buffer = io.StringIO()
            stderr_buffer = io.StringIO()

            with contextlib.redirect_stdout(stdout_buffer), contextlib.redirect_stderr(stderr_buffer):
                main(arguments)

            print(
                json.dumps(
                    {
                        "id": request_id,
                        "ok": True,
                        "stdout": stdout_buffer.getvalue().splitlines(),
                        "stderr": stderr_buffer.getvalue().splitlines(),
                    }
                ),
                flush=True,
            )
        except BaseException as exc:
            if isinstance(exc, KeyboardInterrupt):
                raise

            print(
                json.dumps(
                    {
                        "id": request_id,
                        "ok": False,
                        "stdout": [],
                        "stderr": [],
                        "error": f"{type(exc).__name__}: {exc}",
                    }
                ),
                flush=True,
            )


def main(argv=None):
    parser = argparse.ArgumentParser(
        description="DroneDash M3M OpenCV/CUDA processing worker"
    )
    parser.add_argument("--serve-jsonl", action="store_true")
    parser.add_argument("--probe", action="store_true")
    parser.add_argument("--register", action="store_true")
    parser.add_argument("--index", action="store_true")
    parser.add_argument("--geo-index", action="store_true")
    parser.add_argument("--geo-zones", action="store_true")
    parser.add_argument("--reference")
    parser.add_argument("--moving")
    parser.add_argument("--positive-band")
    parser.add_argument("--comparison-band")
    parser.add_argument("--source")
    parser.add_argument("--positive-band-index", type=int)
    parser.add_argument("--comparison-band-index", type=int)
    parser.add_argument("--index-type", choices=["ndvi", "ndre", "gndvi"])
    parser.add_argument("--threshold1", type=float)
    parser.add_argument("--threshold2", type=float)
    parser.add_argument("--threshold3", type=float)
    parser.add_argument("--threshold4", type=float)
    parser.add_argument("--output")
    parser.add_argument("--transform")
    parser.add_argument("--metadata")
    parser.add_argument(
        "--motion",
        choices=["affine", "homography"],
        default="affine",
    )
    parser.add_argument(
        "--backend",
        choices=["auto", "cpu", "cuda"],
        default="auto",
        help=(
            "auto uses an available CUDA backend and otherwise falls back to CPU; "
            "registration uses OpenCV-CUDA while vegetation indices use CuPy"
        ),
    )
    parser.add_argument("--max-dim", type=int, default=1600)
    parser.add_argument("--iterations", type=int, default=150)
    parser.add_argument("--epsilon", type=float, default=1e-6)
    parser.add_argument("--index-epsilon", type=float, default=1e-12)
    parser.add_argument(
        "--cuda-min-pixels",
        type=int,
        default=1_048_576,
        help=(
            "minimum pixel count for CUDA in auto mode; "
            "smaller operations stay on CPU to avoid transfer overhead"
        ),
    )
    parser.add_argument(
        "--tile-size",
        default="auto",
        help="auto or a fixed GDAL tile size in pixels (128..8192)",
    )
    parser.add_argument(
        "--pipeline-depth",
        default="auto",
        help="auto or a bounded GDAL read/write pipeline depth (1..4)",
    )
    args = parser.parse_args(argv)

    selected_modes = sum(
        [
            bool(args.serve_jsonl),
            bool(args.probe),
            bool(args.register),
            bool(args.index),
            bool(args.geo_index),
            bool(args.geo_zones),
        ]
    )

    if selected_modes != 1:
        parser.error(
            "choose exactly one of --serve-jsonl, --probe, --register, --index, --geo-index or --geo-zones"
        )

    if args.serve_jsonl:
        _serve_jsonl()
        return

    if args.probe:
        probe()
        return

    if args.register:
        required = [
            args.reference,
            args.moving,
            args.output,
            args.transform,
        ]
        if any(not value for value in required):
            parser.error(
                "--register requires --reference, --moving, --output and --transform"
            )
        register(args)
        return

    if args.geo_index:
        required = [
            args.source,
            args.positive_band_index,
            args.comparison_band_index,
            args.index_type,
            args.output,
        ]
        if any(value is None or value == "" for value in required):
            parser.error(
                "--geo-index requires --source, --positive-band-index, --comparison-band-index, --index-type and --output"
            )
        geospatial_index(args)
        return

    if args.geo_zones:
        required = [
            args.source,
            args.threshold1,
            args.threshold2,
            args.threshold3,
            args.threshold4,
            args.output,
        ]
        if any(value is None or value == "" for value in required):
            parser.error(
                "--geo-zones requires --source, --threshold1..4 and --output"
            )
        geospatial_zones(args)
        return

    required = [
        args.positive_band,
        args.comparison_band,
        args.index_type,
        args.output,
    ]

    if any(not value for value in required):
        parser.error(
            "--index requires --positive-band, --comparison-band, --index-type and --output"
        )

    vegetation_index(args)


if __name__ == "__main__":
    try:
        main()
    except Exception as exc:
        print(str(exc), file=sys.stderr)
        sys.exit(2)
