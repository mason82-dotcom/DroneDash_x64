"""RAM/VRAM-aware tile size and pipeline depth auto-tuning."""

import os

from .backends import _release_cuda_memory_pool
from .tiling import _parse_auto_or_int


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
