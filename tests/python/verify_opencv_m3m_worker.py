"""Smart Farming Python worker verification.

Runs without OpenCV, GDAL or CUDA. Used by CI and runnable locally:

    python tests/python/verify_opencv_m3m_worker.py
"""

import json
import pathlib
import subprocess
import sys

REPO_ROOT = pathlib.Path(__file__).resolve().parents[2]
WORKER_PATH = (
    REPO_ROOT
    / "desktop"
    / "DroneDash_x64.Desktop"
    / "SmartFarming"
    / "Workers"
    / "opencv_m3m.py"
)
PACKAGE_PATH = WORKER_PATH.parent / "dronedash_worker"
sys.path.insert(0, str(WORKER_PATH.parent))


def verify_cli_options():
    result = subprocess.run(
        [sys.executable, str(WORKER_PATH), "--help"],
        check=True,
        capture_output=True,
        text=True,
    )
    required = (
        "--index",
        "--geo-index",
        "--geo-zones",
        "--tile-size",
        "--pipeline-depth",
        "--cuda-min-pixels",
        "--serve-jsonl",
        "--backend",
    )
    missing = [option for option in required if option not in result.stdout]
    if missing:
        raise RuntimeError(
            "Smart Farming worker CLI is missing CUDA/GDAL pipeline options: "
            + ", ".join(missing)
        )


def verify_cuda_processor_source():
    source = "\n".join(
        path.read_text(encoding="utf-8")
        for path in sorted(PACKAGE_PATH.glob("*.py"))
    )
    if ("_CudaIndexProcessor" not in source or
            "_CudaZoneProcessor" not in source or
            "get_current_stream().synchronize" in source):
        raise RuntimeError(
            "Persistent CUDA buffer implementation is incomplete."
        )


def verify_jsonl_protocol():
    result = subprocess.run(
        [sys.executable, str(WORKER_PATH), "--serve-jsonl"],
        input='{"id":"probe","arguments":["--probe"]}\n',
        check=True,
        capture_output=True,
        text=True,
    )
    lines = [line for line in result.stdout.splitlines() if line.strip()]
    if not lines:
        raise RuntimeError("Persistent Python worker returned no response.")
    response = json.loads(lines[-1])
    if not response.get("ok") or response.get("id") != "probe":
        raise RuntimeError("Persistent Python worker JSONL protocol failed.")


def verify_pipeline_helpers():
    # Patches below replace attributes on the package modules for this
    # process only; the CLI checks run the worker in fresh subprocesses.
    from dronedash_worker import autotune, backends, raster_io, tiling

    windows = list(tiling._tile_windows(5, 3, 2))
    expected = [
        (0, 0, 2, 2),
        (2, 0, 2, 2),
        (4, 0, 1, 2),
        (0, 2, 2, 1),
        (2, 2, 2, 1),
        (4, 2, 1, 1),
    ]
    if windows != expected:
        raise RuntimeError(f"unexpected tile windows: {windows}")

    observed = list(
        tiling._prefetched_tiles(
            windows,
            lambda window: ("payload", window),
            2,
        )
    )
    if [window for window, _ in observed] != expected:
        raise RuntimeError("read-ahead pipeline changed tile ordering")
    if [payload[1] for _, payload in observed] != expected:
        raise RuntimeError("read-ahead pipeline payload mismatch")

    writes = []
    statistics = []
    replacements = []

    class FakeBand:
        def WriteArray(self, values, x, y):
            writes.append((x, y, values))

        def SetStatistics(self, minimum, maximum, average, deviation):
            statistics.append((minimum, maximum, average, deviation))

    class FakeTarget:
        def FlushCache(self):
            writes.append(("flush",))

    fake_band = FakeBand()
    fake_target = FakeTarget()

    raster_io._create_geotiff_from_profile = (
        lambda *args, **kwargs:
            ("output.tif", "temp.tif", fake_target, fake_band)
    )
    raster_io.os.replace = (
        lambda source, target:
            replacements.append((source, target))
    )

    writer = raster_io._AsyncGeoTiffWriter(
        {"width": 4, "height": 2, "transform": None, "projection": None},
        "output.tif",
        6,
        float("nan"),
        "NDVI",
        2,
    )
    writer.submit((0, 0, 2, 2), "tile-a")
    writer.submit((2, 0, 2, 2), "tile-b")
    writer.submit((0, 2, 2, 2), "tile-c")
    result = writer.finish(
        {
            "minimum": -1.0,
            "maximum": 1.0,
            "average": 0.25,
            "standardDeviation": 0.5,
        }
    )
    writer.abort()

    if result != "output.tif":
        raise RuntimeError("async writer returned wrong output path")
    if [entry for entry in writes if len(entry) == 3] != [
        (0, 0, "tile-a"),
        (2, 0, "tile-b"),
        (0, 2, "tile-c"),
    ]:
        raise RuntimeError("async writer changed tile ordering")
    if statistics != [(-1.0, 1.0, 0.25, 0.5)]:
        raise RuntimeError("async writer did not preserve raster statistics")
    if replacements != [("temp.tif", "output.tif")]:
        raise RuntimeError("async writer did not atomically publish output")

    autotune._system_available_memory_bytes = (
        lambda: 16 * 1024 ** 3
    )
    autotune._cuda_memory_snapshot = (
        lambda backend: {
            "freeBytes": 8 * 1024 ** 3,
            "totalBytes": 12 * 1024 ** 3,
        }
    )

    timings = {
        128: (0.02, 0.02, 0.01),
        256: (0.02, 0.02, 0.01),
        512: (0.02, 0.02, 0.01),
        1024: (0.02, 0.02, 0.02),
        2048: (0.08, 0.08, 0.20),
        4096: (0.80, 0.80, 0.80),
    }

    def benchmark(tile_size, sample_index):
        if tile_size == 4096:
            raise RuntimeError("synthetic CUDA OOM")

        (
            read_seconds,
            compute_seconds,
            write_seconds,
        ) = timings[tile_size]

        return {
            "pixels": tile_size * tile_size,
            "readSeconds": read_seconds,
            "computeSeconds": compute_seconds,
            "writeSeconds": (
                write_seconds
                if sample_index == 1
                else None
            ),
        }

    tuning = autotune._auto_tune_geospatial(
        "auto",
        "auto",
        12000,
        12000,
        "index",
        "cuda",
        benchmark,
    )

    if tuning["tileSize"] != 1024:
        raise RuntimeError(
            f"auto tuner selected unexpected tile size: {tuning}"
        )
    if tuning["pipelineDepth"] != 2:
        raise RuntimeError(
            f"auto tuner selected unexpected pipeline depth: {tuning}"
        )
    if tuning["mode"] != "auto":
        raise RuntimeError("auto tuner did not report auto mode")
    if abs(
        tuning["selectedBenchmark"]["writeSeconds"] -
        0.02
    ) > 1e-9:
        raise RuntimeError(
            f"write I/O was not included in tuning: {tuning}"
        )
    if tuning["selectedBenchmark"]["pixelsPerSecond"] <= 0:
        raise RuntimeError(
            "pipeline throughput score is missing"
        )

    write_bound_depth = autotune._choose_pipeline_depth(
        "auto",
        16000,
        16000,
        1024,
        "index",
        16 * 1024 ** 3,
        {
            "readSeconds": 0.01,
            "computeSeconds": 0.01,
            "writeSeconds": 0.05,
        },
    )
    if write_bound_depth != 4:
        raise RuntimeError(
            "write-bound tuning did not increase pipeline depth"
        )

    manual = autotune._auto_tune_geospatial(
        "2048",
        "3",
        12000,
        12000,
        "index",
        "cuda",
        lambda *args: (_ for _ in ()).throw(
            RuntimeError("manual tuning must not benchmark")
        ),
    )

    if (
        manual["tileSize"] != 2048
        or manual["pipelineDepth"] != 3
        or manual["mode"] != "manual"
        or manual["benchmarkResults"]
    ):
        raise RuntimeError(
            f"manual GDAL tuning override is inconsistent: {manual}"
        )

    safe = autotune._safe_tile_candidates(
        20000,
        20000,
        "index",
        2,
        "cuda",
        1 * 1024 ** 3,
        512 * 1024 ** 2,
    )

    if 4096 in safe or 2048 not in safe:
        raise RuntimeError(
            f"memory guard returned unexpected candidates: {safe}"
        )

    constrained = autotune._safe_tile_candidates(
        20000,
        20000,
        "index",
        2,
        "cuda",
        16 * 1024 ** 2,
        32 * 1024 ** 2,
    )

    if constrained != [128, 256]:
        raise RuntimeError(
            f"low-memory guard returned unexpected candidates: {constrained}"
        )

    try:
        autotune._safe_tile_candidates(
            20000,
            20000,
            "index",
            2,
            "cuda",
            2 * 1024 ** 2,
            4 * 1024 ** 2,
        )
    except RuntimeError:
        pass
    else:
        raise RuntimeError(
            "memory guard must reject processing when no tile is safe"
        )

    autotune._system_available_memory_bytes = (
        lambda: 1 * 1024 ** 3
    )
    autotune._cuda_memory_snapshot = (
        lambda backend: {
            "freeBytes": 512 * 1024 ** 2,
            "totalBytes": 4 * 1024 ** 3,
        }
    )

    try:
        autotune._auto_tune_geospatial(
            "4096",
            "2",
            20000,
            20000,
            "index",
            "cuda",
            benchmark,
        )
    except RuntimeError:
        pass
    else:
        raise RuntimeError(
            "unsafe manual tile override must be rejected"
        )

    benchmark_results = autotune._benchmark_tiles(
        [512, 1024, 4096],
        benchmark,
    )
    failed_4096 = [
        item
        for item in benchmark_results
        if item["tileSize"] == 4096
    ][0]
    if failed_4096.get("success", True):
        raise RuntimeError(
            "failed CUDA benchmark candidate was not isolated"
        )
    if autotune._choose_benchmark_tile(
        benchmark_results
    )["tileSize"] != 1024:
        raise RuntimeError(
            "failed large candidate incorrectly forced global fallback"
        )

    if backends._select_compute_backend(
        "cuda", "auto", 999, 1000
    ) != "cpu":
        raise RuntimeError(
            "small auto workload did not stay on CPU"
        )
    if backends._select_compute_backend(
        "cuda", "auto", 1000, 1000
    ) != "cuda":
        raise RuntimeError(
            "auto workload did not select CUDA at threshold"
        )
    if backends._select_compute_backend(
        "cuda", "cuda", 1, 1000
    ) != "cuda":
        raise RuntimeError(
            "explicit CUDA must bypass the auto threshold"
        )


def main():
    verify_cli_options()
    verify_cuda_processor_source()
    verify_pipeline_helpers()
    verify_jsonl_protocol()
    print("Smart Farming worker verification passed.")


if __name__ == "__main__":
    main()
