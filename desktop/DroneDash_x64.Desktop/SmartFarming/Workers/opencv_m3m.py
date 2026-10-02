#!/usr/bin/env python3
import argparse
import json
import os
import sys

def probe():
    import cv2
    import numpy as np
    print(json.dumps({"opencv": cv2.__version__, "numpy": np.__version__}))

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
            f"band dimensions differ: reference={reference_raw.shape[:2]} moving={moving_raw.shape[:2]}")

    reference = _normalise(reference_raw, cv2, np)
    moving = _normalise(moving_raw, cv2, np)

    height, width = reference.shape[:2]
    scale = min(1.0, float(args.max_dim) / float(max(width, height)))
    if scale < 1.0:
        small_size = (max(8, int(round(width * scale))), max(8, int(round(height * scale))))
        reference_small = cv2.resize(reference, small_size, interpolation=cv2.INTER_AREA)
        moving_small = cv2.resize(moving, small_size, interpolation=cv2.INTER_AREA)
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
            s = np.array([[scale, 0, 0], [0, scale, 0], [0, 0, 1]], dtype=np.float32)
            sinv = np.array([[1.0 / scale, 0, 0], [0, 1.0 / scale, 0], [0, 0, 1]], dtype=np.float32)
            warp_full = sinv @ warp_full @ s
        else:
            warp_full[0, 2] /= scale
            warp_full[1, 2] /= scale

    flags = cv2.INTER_CUBIC | cv2.WARP_INVERSE_MAP
    if args.motion == "homography":
        aligned = cv2.warpPerspective(
            moving_raw, warp_full, (width, height),
            flags=flags, borderMode=cv2.BORDER_REFLECT_101)
    else:
        aligned = cv2.warpAffine(
            moving_raw, warp_full, (width, height),
            flags=flags, borderMode=cv2.BORDER_REFLECT_101)

    os.makedirs(os.path.dirname(os.path.abspath(args.output)), exist_ok=True)
    os.makedirs(os.path.dirname(os.path.abspath(args.transform)), exist_ok=True)

    if not cv2.imwrite(args.output, aligned):
        raise RuntimeError(f"failed to write registered TIFF: {args.output}")

    with open(args.transform, "w", encoding="utf-8") as handle:
        json.dump(
            {
                "schemaVersion": 1,
                "reference": os.path.abspath(args.reference),
                "moving": os.path.abspath(args.moving),
                "output": os.path.abspath(args.output),
                "motion": args.motion,
                "eccScore": float(score),
                "matrix": warp_full.tolist(),
                "note": "Pixel-space registration only. Output TIFF must not be treated as an orthorectified geospatial product.",
            },
            handle,
            indent=2,
        )

    print(json.dumps({"eccScore": float(score), "output": os.path.abspath(args.output)}))

def main():
    parser = argparse.ArgumentParser(description="DroneDash M3M OpenCV registration worker")
    parser.add_argument("--probe", action="store_true")
    parser.add_argument("--register", action="store_true")
    parser.add_argument("--reference")
    parser.add_argument("--moving")
    parser.add_argument("--output")
    parser.add_argument("--transform")
    parser.add_argument("--motion", choices=["affine", "homography"], default="affine")
    parser.add_argument("--max-dim", type=int, default=1600)
    parser.add_argument("--iterations", type=int, default=150)
    parser.add_argument("--epsilon", type=float, default=1e-6)
    args = parser.parse_args()

    if args.probe:
        probe()
        return

    if args.register:
        required = [args.reference, args.moving, args.output, args.transform]
        if any(not value for value in required):
            parser.error("--register requires --reference, --moving, --output and --transform")
        register(args)
        return

    parser.error("choose --probe or --register")

if __name__ == "__main__":
    try:
        main()
    except Exception as exc:
        print(str(exc), file=sys.stderr)
        sys.exit(2)
