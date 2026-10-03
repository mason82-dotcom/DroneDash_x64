"""Command-line interface and persistent JSONL worker protocol."""

import argparse
import json
import sys

from .dem import dem_preview
from .geospatial import geospatial_index, geospatial_zones
from .indices import vegetation_index
from .probes import probe
from .registration import register


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
    parser.add_argument("--dem-preview", action="store_true")
    parser.add_argument("--output-dir")
    parser.add_argument("--max-size", type=int, default=2048)
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
            bool(args.dem_preview),
        ]
    )

    if selected_modes != 1:
        parser.error(
            "choose exactly one of --serve-jsonl, --probe, --register, --index, --geo-index, --geo-zones or --dem-preview"
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

    if args.dem_preview:
        if not args.source or not args.output_dir:
            parser.error("--dem-preview requires --source and --output-dir")
        dem_preview(args)
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
