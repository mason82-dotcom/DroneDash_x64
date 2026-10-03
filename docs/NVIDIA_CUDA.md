# NVIDIA CUDA acceleration

DroneDash can optionally use NVIDIA CUDA in the local DJI Mavic 3M processing path.

The integration is deliberately optional:

- systems without an NVIDIA GPU continue to use the existing CPU path;
- no CUDA runtime is bundled in this repository;
- source M3M files remain read-only;
- OpenCV-CUDA accelerates registration resize and final affine/perspective warping;
- CuPy-CUDA accelerates local NDVI, NDRE and GNDVI raster arithmetic;
- GDAL Python bindings enable a tile-based geospatial engine for large ODM orthomosaics; GDAL handles windowed I/O, CRS/geotransform and GeoTIFF output while CuPy accelerates per-tile math;
- OpenCV ECC transform estimation remains CPU-based because the OpenCV ECC API does not provide a CUDA implementation;
- Orfeo ToolBox remains the fallback for georeferenced field products when the GDAL Python tile engine is unavailable.

## Runtime selection

The worker accepts:

```text
--backend auto
--backend cpu
--backend cuda
```

DroneDash-generated processing plans use `--backend auto`.

`auto` selects the available CUDA implementation independently per operation. Registration uses OpenCV-CUDA when `cv2.cuda.getCudaEnabledDeviceCount()` reports a device. Vegetation-index raster arithmetic uses CuPy when CuPy exposes a CUDA device. Each path falls back to CPU independently. Explicit `--backend cuda` fails instead of silently falling back.

The generated registration and vegetation-index JSON sidecars record:

- `backendRequested`
- `backendUsed`
- CUDA device count
- CUDA device name when available
- the CUDA line reported by the OpenCV build for registration
- CuPy version and device information for vegetation-index calculations
- index statistics (valid pixels, minimum, maximum and average)

This keeps the processing provenance auditable.

## Windows setup

1. Install a current NVIDIA production driver for the installed GPU.
2. Install NVIDIA CUDA Toolkit. DroneDash targets the current CUDA 13.4.x generation but does not hard-code a toolkit ABI.
3. Install Python and NumPy for the Python environment used by DroneDash.
4. For GPU registration, use an OpenCV build compiled with CUDA enabled (`WITH_CUDA=ON`). A normal CPU-only OpenCV build remains supported.
5. For GPU NDVI/NDRE/GNDVI, install a CuPy package compatible with the installed CUDA runtime. CuPy is optional; NumPy remains the CPU fallback.
6. Point DroneDash at the Python executable when needed:

```powershell
$env:DRONEDASH_PYTHON = "C:\Path\To\python.exe"
```

Optional CUDA binary override:

```powershell
$env:DRONEDASH_CUDA_BIN = "C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v13.4\bin"
```

Standard `CUDA_PATH` is also detected for `nvcc`.

Run:

```powershell
.\scripts\doctor.ps1
```

The doctor reports:

- NVIDIA GPU/driver through `nvidia-smi`
- CUDA Toolkit through `nvcc`
- whether the configured OpenCV build exposes a CUDA device
- whether CuPy exposes a CUDA device for NDVI/NDRE/GNDVI
- whether Python GDAL bindings are available for tile-based geospatial field products

## Direct worker probe

From the repository root:

```powershell
python desktop\DroneDash_x64.Desktop\SmartFarming\Workers\opencv_m3m.py --probe
```

Example CUDA-capable output:

```json
{
  "opencv": "4.x",
  "numpy": "2.x",
  "cudaAvailable": true,
  "cudaDeviceCount": 1,
  "cudaDeviceName": "NVIDIA ...",
  "cudaBuild": "NVIDIA CUDA: YES ...",
  "cupyAvailable": true,
  "cupyVersion": "14.x",
  "cupyDeviceCount": 1,
  "cupyDeviceName": "NVIDIA ..."
}
```

If either CUDA backend is unavailable, DroneDash falls back only for that processing stage. For example, CPU-only OpenCV can coexist with CuPy GPU vegetation-index calculation.

## GDAL + CUDA tile engine

DroneDash does not require or assume a native CUDA backend inside GDAL itself. Instead it uses a cvTile-style split of responsibilities:

```text
GDAL read-ahead thread
        ↓
bounded queue (auto depth 1..4)
        ↓
auto-tuned tiles (512..4096 typical)
        ↓
CuPy/CUDA or NumPy
        ↓
GDAL async writer
        ↓
tiled GeoTIFF output
```

The worker modes are:

```text
--geo-index
--geo-zones
```

For `--geo-index`, GDAL reads only the requested source-band windows. NDVI, NDRE and GNDVI are calculated tile-by-tile and immediately written to a tiled DEFLATE-compressed GeoTIFF. The source geotransform and projection are copied to the result. This prevents large ODM orthomosaics from being loaded completely into CPU or GPU memory.

The source raster is opened a second time by a dedicated read-ahead worker. That worker is the only thread that accesses its GDAL dataset instance. A separate writer thread owns the output GeoTIFF dataset, while the main thread performs CUDA/NumPy calculation. This allows source I/O for tile N+1, compute for tile N, and output I/O for tile N-1 to overlap without sharing a GDAL dataset instance across threads.

`--geo-zones` classifies the NDVI result into the configured five scouting zones using the same tile pipeline. Zone 0 is reserved for NoData.

Both operations use unique temporary GeoTIFFs and publish the final output only after the writer thread has flushed and closed its GDAL dataset. JSON sidecars record tile size, tile count, pipeline depth, read-ahead/async-write status, elapsed time, processing tiles/second, backend, GDAL version, CUDA/CuPy device data, raster statistics and the complete auto-tuning decision.

### Adaptive tile and pipeline tuning

DroneDash field-product plans now pass:

```text
--tile-size auto
--pipeline-depth auto
```

The worker first snapshots currently available system RAM and, for CUDA processing, free/total VRAM. Unsafe tile candidates are removed before benchmarking. Automatic candidates are normally 512, 1024, 2048 and 4096 pixels; explicit manual values from 128 through 8192 remain supported.

The remaining candidates are benchmarked against a real sample window from the current orthomosaic. Read time and compute time are measured separately. The tuner selects the smallest tile whose measured pixels/second is within 90% of the fastest candidate, avoiding unnecessary memory use for marginal throughput gains.

Pipeline depth is then selected from 1–4. Small jobs use depth 1. I/O-bound jobs may use depth 3 or 4, compute-bound jobs can use depth 1, and balanced workloads normally use depth 2. The result is clamped again by the host-memory budget.

The sidecar `tuning` object records requested and resolved values, available RAM/VRAM and all benchmark measurements. `processingElapsedSeconds` and `tilesPerSecond` exclude the tuning benchmark itself so production throughput remains comparable between runs.

Manual overrides remain available, for example:

```powershell
python ...\opencv_m3m.py --geo-index ... --tile-size 2048 --pipeline-depth 3
```

When both values are manual, no tuning benchmark is executed.

### Reused CUDA buffers and persistent Python worker

CuPy vegetation-index and scouting-zone arithmetic now uses fused `ElementwiseKernel` operations. Device buffers are reused while tile dimensions remain unchanged, reducing repeated GPU allocation and intermediate-array churn. GDAL still owns the CPU-side source windows, so host/device transfer remains explicit. The worker relies on the device-to-host `cp.asnumpy()` copy for the required synchronization instead of issuing a second explicit stream synchronization.

For direct DroneDash local-processing plans, the desktop keeps one compatible Python worker process alive across OpenCV/CuPy steps through the line-delimited JSON protocol exposed by `--serve-jsonl`. Python imports, the CUDA context and reusable buffers can therefore survive across compatible steps instead of being rebuilt for every command. External OTB/GDAL commands remain isolated processes, and cancellation still terminates the Python process tree.


Requirements for this optional path:

- Python + NumPy
- Python GDAL bindings (`osgeo.gdal`) compatible with the installed GDAL runtime
- CuPy for GPU arithmetic; without CuPy the same GDAL tile pipeline uses NumPy on CPU

The standard OTB field-product path remains available as fallback when Python GDAL bindings are not installed.

## Why CUDA is not mandatory

GDAL and Orfeo ToolBox remain separate external processing stages. Radiometric correction still uses OTB and the corrected four-band VRT is retained through GDAL. Local NDVI/NDRE/GNDVI quicklooks now use the DroneDash Python worker so their raster arithmetic can run through CuPy on CUDA.

For georeferenced NodeODM field products, DroneDash now prefers the GDAL tile engine when Python GDAL bindings are available and retains OTB as the fallback. The local pixel-space quicklook path remains separate from these orthorectified products. This keeps CUDA acceleration optional and preserves compatibility with non-NVIDIA workstations and CI.
