# Local photogrammetry with COLMAP (CUDA)

DroneDash can compute a georeferenced point cloud and a digital surface model (DSM) locally with
[COLMAP](https://colmap.github.io/) as an alternative to a NodeODM server. The products appear in
the same places as ODM results: **DSM auf Karte**, **Punktwolke 3D**, profile and volume tools, and
the active project (*Oberflächenmodell (DSM)*, *Punktwolke*).

## Requirements

- NVIDIA GPU with a current driver. COLMAP's dense reconstruction (`patch_match_stereo`) only
  exists in the CUDA build; DroneDash refuses to start the pipeline with a COLMAP without CUDA.
- COLMAP Windows **CUDA** release (3.9 or newer).
- The DroneDash Python (`DRONEDASH_PYTHON`) with NumPy, GDAL (`osgeo.gdal`) and
  `laspy[lazrs]` — the same environment the elevation and point cloud views use.
- Free disk space: the dense workspace holds undistorted images and depth maps, roughly
  3–5× the size of the input JPEGs.

## Installation

```powershell
# Latest COLMAP release, installed to %LOCALAPPDATA%\DroneDash\colmap
.\scripts\install-colmap.ps1

# A specific release, or offline from a downloaded zip
.\scripts\install-colmap.ps1 -Version 3.11.1
.\scripts\install-colmap.ps1 -ArchivePath C:\Downloads\colmap-x64-windows-cuda.zip
```

The script picks the Windows CUDA asset of the release, extracts it, checks `colmap -h` for
"with CUDA" and sets the user variable `DRONEDASH_COLMAP`. Restart DroneDash afterwards.
`scripts\doctor.ps1` reports the detected COLMAP version and CUDA support.

DroneDash looks for COLMAP in this order: `DRONEDASH_COLMAP` (the `COLMAP.bat` / `colmap.exe`
file or the install folder), `%LOCALAPPDATA%\DroneDash\colmap`, then `colmap` on the `PATH`.

## Usage

1. Analyze the image folder in **Photogrammetrie**.
2. Open *DSM / Punktwolke lokal mit COLMAP (CUDA) berechnen*, press **COLMAP prüfen**.
3. Choose a quality preset and **Lokal berechnen**; pick a working folder (the project's
   `03_Processing\Photogrammetry` folder is suggested).

| Preset   | Feature image size | Dense image size | DSM resolution |
|----------|-------------------:|-----------------:|---------------:|
| Schnell  | 1600 px | 1200 px | 20 cm |
| Standard | 3200 px | 2000 px | 10 cm |
| Hoch     | 4800 px | 3200 px | 5 cm  |

Image selection follows the NodeODM path: only RGB images with GPS are used; thermal, zoom and
multispectral images and DNG duplicates are excluded. Paths with spaces are skipped (COLMAP image
list).

## Pipeline

Each run writes into `colmap_<timestamp>\` and logs every command to `local-processing.log`;
`colmap-plan.json` lists the exact commands.

| Step | Tool | Output |
|------|------|--------|
| georef | DroneDash worker `--colmap-georef` | `georef\georef.txt`, `georef.json` (UTM zone, local offset) |
| features | `colmap feature_extractor` (GPU) | `database.db` |
| matching | `colmap spatial_matcher` (GPU, GPS neighbours) | `database.db` |
| mapper | `colmap mapper` | `sparse\0` |
| undistort | `colmap image_undistorter` | `dense\` |
| stereo | `colmap patch_match_stereo` (CUDA) | `dense\stereo\depth_maps` |
| fusion | `colmap stereo_fusion` | `dense\fused.ply` |
| products | DroneDash worker `--colmap-products` | `products\colmap_georeferenced_model.laz`, `dsm.tif`, `colmap-products.json` |

Option names that changed between COLMAP versions (for example `SiftExtraction.use_gpu` →
`FeatureExtraction.use_gpu`) are read from `colmap <command> -h`.

### Camera model

DroneDash uses the `RADIAL` model with one camera per folder. When all selected images carry the
same DJI calibration (`CalibratedFocalLength`, `CalibratedOpticalCenterX/Y` in the XMP), it is
passed as `ImageReader.camera_params` and bundle adjustment keeps focal length and principal point
fixed; only the distortion is refined. Letting COLMAP refine the focal length on a nadir grid lets
it drift, which shows up as a wrong flying height and a vertically scaled surface. Without a
common calibration DroneDash warns and lets COLMAP estimate the focal length.

### Georeferencing

COLMAP reconstructs in an arbitrary frame. DroneDash fits a similarity transform (scale, rotation,
translation; Umeyama) from the reconstructed camera centres to the image positions in UTM, rejects
cameras whose residual is far above the median and applies the transform to the fused cloud. The
result (`colmap-products.json`) reports the number of images used, the RMSE and the maximum
residual; DroneDash warns above 1 m RMSE. Heights are the drone's `AbsoluteAltitude`, so the DSM
uses the same vertical reference as the images. With RTK positions the fit is typically at the
centimetre level.

COLMAP's own `model_aligner` is not used: in COLMAP 3.9.1 it reports a successful alignment but
writes the model untransformed.

### DSM

Isolated points (fewer than five points in the surrounding 3×3×3 voxels of max(0.5 m, 5 × DSM
resolution)) are removed first; fused clouds contain single floating points that would otherwise
become spikes. The number is reported as `removedIsolatedPoints`. The remaining points are rasterized with the highest point per cell (a surface model), gaps
of up to about one metre are filled (`gdal.FillNodata`), larger holes stay nodata (-9999). The
GeoTIFF is tiled and DEFLATE-compressed in the UTM zone of the survey.

COLMAP does not classify ground points, so there is no DTM and no canopy height from this path;
use NodeODM for DSM + DTM.
