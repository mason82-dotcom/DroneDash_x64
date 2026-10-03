"""DroneDash Smart Farming M3M processing worker.

Modules:
    probes        runtime probes for OpenCV-CUDA, CuPy and Python GDAL
    backends      compute backend selection shared by all stages
    registration  OpenCV ECC band registration
    indices       single-capture vegetation index quicklooks
    raster_io     GDAL GeoTIFF helpers and asynchronous tile writer
    tiling        tile windows, prefetching, statistics, option parsing
    autotune      RAM/VRAM-aware tile size and pipeline depth tuning
    geospatial    tiled GDAL vegetation index and NDVI zone products
    cli           command-line interface and persistent JSONL protocol

The entry point stays opencv_m3m.py next to this package.
"""
