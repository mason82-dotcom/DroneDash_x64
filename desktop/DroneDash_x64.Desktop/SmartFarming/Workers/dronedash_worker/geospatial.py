"""Tiled GDAL vegetation index and NDVI scouting-zone products."""

import json
import os

from .autotune import _auto_tune_geospatial, _benchmark_window
from .backends import (
    _backend_usage,
    _release_cuda_memory_pool,
    _resolve_index_backend,
    _select_compute_backend,
)
from .indices import _index_cpu, _index_cuda
from .raster_io import (
    _AsyncGeoTiffWriter,
    _benchmark_geotiff_write,
    _gdal_profile,
    _prepare_source_tile,
    _write_json,
)
from .tiling import (
    _final_stats,
    _prefetched_tiles,
    _tile_windows,
    _update_stats,
)


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


_CUDA_ZONE_PROCESSOR = None


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
