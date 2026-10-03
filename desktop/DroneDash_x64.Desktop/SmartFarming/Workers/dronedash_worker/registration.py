"""OpenCV ECC band registration (CPU or OpenCV-CUDA)."""

import json
import os

from .backends import _resolve_registration_backend


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
