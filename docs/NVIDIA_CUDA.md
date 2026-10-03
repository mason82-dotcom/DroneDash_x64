# NVIDIA CUDA acceleration

DroneDash can optionally use NVIDIA CUDA in the local DJI Mavic 3M processing path.

The integration is deliberately optional:

- systems without an NVIDIA GPU continue to use the existing CPU path;
- no CUDA runtime is bundled in this repository;
- source M3M files remain read-only;
- CUDA is currently used by the Python/OpenCV registration worker for image resize and final affine/perspective warping;
- OpenCV ECC transform estimation remains CPU-based because the OpenCV ECC API does not provide a CUDA implementation.

## Runtime selection

The worker accepts:

```text
--backend auto
--backend cpu
--backend cuda
```

DroneDash-generated processing plans use `--backend auto`.

`auto` selects CUDA only when the active Python/OpenCV environment reports at least one CUDA-enabled device through `cv2.cuda.getCudaEnabledDeviceCount()`. If a CUDA resize/warp operation is not usable, `auto` falls back to CPU. Explicit `--backend cuda` fails instead of silently falling back.

The generated registration JSON records:

- `backendRequested`
- `backendUsed`
- CUDA device count
- CUDA device name when available
- the CUDA line reported by the OpenCV build

This keeps the processing provenance auditable.

## Windows setup

1. Install a current NVIDIA production driver for the installed GPU.
2. Install NVIDIA CUDA Toolkit. DroneDash targets the current CUDA 13.4.x generation but does not hard-code a toolkit ABI.
3. Install Python and NumPy for the Python environment used by DroneDash.
4. Use an OpenCV build that was compiled with CUDA enabled (`WITH_CUDA=ON`). A normal CPU-only OpenCV build remains supported but will not enable GPU acceleration.
5. Point DroneDash at the Python executable when needed:

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
  "cudaBuild": "NVIDIA CUDA: YES ..."
}
```

If `cudaAvailable` is `false`, DroneDash remains fully functional and uses the CPU backend.

## Why CUDA is not mandatory

GDAL and Orfeo ToolBox remain separate external processing stages. The current CUDA integration intentionally accelerates only the OpenCV registration stage for which DroneDash controls the worker implementation. This avoids coupling the desktop application to a specific GPU driver/toolkit and keeps CI and non-NVIDIA workstations supported.

Future GPU work can add CUDA/CuPy implementations for vegetation-index raster arithmetic or dedicated native CUDA kernels without changing the existing fallback contract.
