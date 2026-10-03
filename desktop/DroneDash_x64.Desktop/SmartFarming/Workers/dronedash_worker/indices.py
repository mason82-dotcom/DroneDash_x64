"""Single-capture NDVI/NDRE/GNDVI quicklooks (NumPy or CuPy)."""

import json
import os

from .backends import (
    _release_cuda_memory_pool,
    _resolve_index_backend,
    _select_compute_backend,
)


_CUDA_INDEX_PROCESSORS = {}


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
