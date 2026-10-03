# NVIDIA CUDA acceleration

DroneDash can optionally use NVIDIA CUDA in the local DJI Mavic 3M processing path.

The integration is deliberately optional:

- systems without an NVIDIA GPU continue to use the existing CPU path;
- no CUDA runtime is bundled in this repository;
- source M3M files remain read-only;
- OpenCV-CUDA accelerates registration resize and final affine/perspective warping;
- CuPy-CUDA accelerates local NDVI, NDRE and GNDVI raster arithmetic;
- OpenCV ECC transform estimation remains CPU-based because the OpenCV ECC API does not provide a CUDA implementation;
- the georeferenced NodeODM/OTB field-product path remains unchanged.

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

## Why CUDA is not mandatory

GDAL and Orfeo ToolBox remain separate external processing stages. Radiometric correction still uses OTB and the corrected four-band VRT is retained through GDAL. Local NDVI/NDRE/GNDVI quicklooks now use the DroneDash Python worker so their raster arithmetic can run through CuPy on CUDA.

The NodeODM/OTB field-product path remains unchanged because it carries geospatial/orthorectified semantics that the local pixel-space quicklook worker must not silently replace. This keeps CUDA acceleration optional and preserves compatibility with non-NVIDIA workstations and CI.
