"""Runtime probes for OpenCV-CUDA, CuPy and Python GDAL."""

import json


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
