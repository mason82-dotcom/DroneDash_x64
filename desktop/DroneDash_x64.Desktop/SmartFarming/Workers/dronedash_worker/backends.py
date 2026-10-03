"""Compute backend selection shared by all processing stages."""

from .probes import _cuda_probe, _cupy_probe


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


def _release_cuda_memory_pool():
    try:
        import cupy as cp

        cp.get_default_memory_pool().free_all_blocks()
        cp.get_default_pinned_memory_pool().free_all_blocks()
    except Exception:
        pass
