# DroneDash_x64
> Repository: `mason82-dotcom/DroneDash_x64`

Windows + DJI RC Pro Enterprise management application for the DJI Mavic 3 Enterprise family.

## Architecture

```text
┌──────────────────────────────────────────────────────────────┐
│ Windows 10/11                                                │
│ DroneDash_x64.Desktop (.NET 10 / WPF)                       │
│  status · configuration · media list · media download        │
└──────────────────────────────┬───────────────────────────────┘
                               │ HTTP/JSON
                     preferred │ adb forward tcp:49152 tcp:49152
                               │
┌──────────────────────────────▼───────────────────────────────┐
│ DJI RC Pro Enterprise                                        │
│ DroneDash RC Bridge Agent (Android / Kotlin / DJI MSDK V5)   │
│  SDK registration · KeyManager · MediaManager                │
└──────────────────────────────┬───────────────────────────────┘
                               │ DJI RC link
┌──────────────────────────────▼───────────────────────────────┐
│ Mavic 3 Enterprise family                                    │
│ M3E / M3T / M3M and MSDK-supported Enterprise variants       │
└──────────────────────────────────────────────────────────────┘
```

There is no native DJI MSDK V5 for Windows for this aircraft family. The Android agent is therefore
the hardware adapter and keeps all DJI-specific code on the controller. The Windows application
stays clean and testable.

## Current development state

Version 0.2 hardens the bridge and build path:

- concurrent desktop requests no longer mutate shared `HttpClient` headers/base address;
- media downloads are written to `.part` files and promoted only after a complete transfer;
- the RC bridge binds to `127.0.0.1` by default, which is ideal for USB + `adb forward`;
- the RC bridge runs as an Android foreground service (persistent "Bridge aktiv" notification), so
  it keeps serving while DJI Pilot 2 or another app is in front;
- RC-side media downloads check free cache space up front, verify the received size against the
  DJI file size and remove partial or orphaned temporary files;
- the desktop status poll times out after 5 s, so a lost bridge is reported promptly;
- LAN binding requires an explicit `BRIDGE_BIND_ADDRESS`, and the server refuses non-loopback
  exposure while the default token `change-me-now` is still configured;
- Android Gradle compatibility flags are aligned with DJI's current MSDK integration guidance;
- GitHub Actions now builds the Windows projects and assembles the Android debug APK on every push/PR;
- live status includes velocity, Home Point, GPS/compass/wind, detailed battery telemetry and read-only RTK telemetry including FIX/FLOAT state, precision and satellite counts;
- the primary camera/gimbal status now includes camera mode/activity, SD/internal storage capacity and main-gimbal attitude without exposing camera or gimbal control actions;
- dedicated Aircraft, RC, Energy, RTK and Camera/Gimbal pages provide a structured device view;
- a local diagnostic/event log records state transitions and warnings (up to 500 entries) and can be exported as CSV without logging every 1 Hz telemetry sample;
- the media workspace includes an image viewer for aircraft and local files, fit/1:1 viewing, standard EXIF/GPS metadata, file hashes and DJI-specific XMP metadata when present;
- DJI DNG/RAW files are supported with WIC preview when a Windows RAW codec is available and metadata-only fallback otherwise;
- a photogrammetry summary highlights DJI XMP fields such as GPS/altitude, aircraft and gimbal attitude, RTK flag/standard deviations, calibrated focal length, optical center and dewarp calibration data;
- optional DJI Thermal SDK v1.8 integration analyzes radiometric DJI R-JPEGs locally on Windows x64, including the full per-pixel FLOAT32 temperature matrix, min/max/average/center values, P05/P50/P95 distribution statistics, standard deviation, a 64-bin histogram, cursor temperature inspection, min/max/center overlay markers and selectable pseudo-color rendering;
- thermal analysis can export an auditable JSON summary, full per-pixel CSV matrix, histogram CSV, marked pseudo-color PNG and histogram PNG; outputs are automatically registered when a DroneDash project is open.

## Versions pinned by this project

- DJI Mobile SDK V5: **5.18.0**
- DJI Thermal SDK (optional Windows x64 runtime): **1.8**
- Android compile/target SDK: **36**
- Android min SDK: **24**
- Android Gradle Plugin: **8.10.1**
- Kotlin: **2.1.0**
- Gradle: **8.12**
- Java: **17**
- Windows desktop: **.NET 10 / WPF / win-x64**
- NVIDIA CUDA (optional Smart Farming acceleration): **13.4.x target**, CPU fallback retained

## Windows x64 target

The desktop application now targets Windows x64 explicitly: `RuntimeIdentifier=win-x64`,
`PlatformTarget=x64` and `Prefer32Bit=false`. This keeps the WPF process aligned with optional
native x64 dependencies such as DJI Thermal SDK, GDAL/OpenCV/CUDA and Orfeo ToolBox.

Create and validate a framework-dependent x64 package with:

```powershell
.\scripts\publish-windows-x64.ps1 -Configuration Release -Clean
```

For a self-contained .NET 10 package:

```powershell
.\scripts\publish-windows-x64.ps1 -Configuration Release -SelfContained -Clean
```

The publish script verifies that the produced apphost is AMD64 (`PE Machine 0x8664`) and that the
planning/PV HTML assets and Smart Farming Python worker are present. CI performs the same check and
uploads a `DroneDash_x64-win-x64` workflow artifact. See `docs/WINDOWS_X64.md`.

## Local environment check

Run the read-only preflight before building:

```powershell
.\scripts\doctor.ps1
```

It checks the Git clone, .NET 10, NuGet, Java 17, Android SDK/API 36, Build Tools 35.0.0,
Gradle, ADB, optional NVIDIA/CUDA/OpenCV-CUDA/CuPy availability and whether DJI credentials are configured without printing their values.
Use `-Strict` when warnings should also fail the check.

## Run the Windows UI without DJI hardware

1. Install the .NET 10 SDK.
2. Open `DroneDash_x64.code-workspace` in VS Code.
3. In terminal 1:
   `dotnet run --project desktop/DroneDash_x64.MockRc/DroneDash_x64.MockRc.csproj`
4. In terminal 2:
   `dotnet run --project desktop/DroneDash_x64.Desktop/DroneDash_x64.Desktop.csproj`
5. Use endpoint `http://127.0.0.1:49152/` and token `change-me-now`.

The mock server returns changing telemetry and sample media so status/config/download flows can be
tested before touching a drone.

## Build and install the RC agent

1. Create a DJI Developer Mobile SDK Android application and bind its App Key to the exact permanent
   Android package name `com.mason82.dronedash.rcbridge`. Do not register the old
   `com.example.m3ebridge` package.
2. Put secrets and optional LAN binding in your user Gradle properties, not in Git:
   `%USERPROFILE%\.gradle\gradle.properties`

```properties
DJI_API_KEY=your_dji_app_key
BRIDGE_TOKEN=replace_with_a_long_random_token
# Keep 127.0.0.1 for USB-only operation.
BRIDGE_BIND_ADDRESS=127.0.0.1
```

For deliberate LAN operation, set `BRIDGE_BIND_ADDRESS=0.0.0.0` and configure a strong
`BRIDGE_TOKEN`. The agent refuses a non-loopback bind when the token is still the default value or shorter than 24 characters.

3. Install Android SDK Platform 36, Build Tools 35.0.0, Java 17 and Gradle 8.12. Generate the wrapper once:
   `cd rc-agent && gradle wrapper --gradle-version 8.12`
4. Build:
   `./gradlew :app:assembleDebug`

   The bridge screen shows the final App ID and only whether a real DJI App Key is configured; it
   never displays the key value. A release build fails if `DJI_API_KEY` is missing/placeholder or
   `BRIDGE_TOKEN` is shorter than 24 characters.

   See `docs/DJI_MSDK_KEY_SETUP.md` for local and GitHub Actions secret setup.
5. Enable USB debugging on the RC Pro Enterprise and install:
   `adb install -r app/build/outputs/apk/debug/app-debug.apk`
6. **Force-stop DJI Pilot 2 before running the MSDK app.**
7. Launch **DroneDash RC Bridge** on the controller. First-time DJI registration needs connectivity unless you
   deploy an appropriate DJI offline/LDM licensing setup.
8. On Windows run `scripts/connect-usb.ps1`, then use
   `http://127.0.0.1:49152/` in the desktop application.

## Media behavior

Opening the media endpoint briefly puts the camera into MSDK media-management mode. In this mode,
shooting/recording and normal image transmission are unavailable; the agent exits that mode after
the operation.

The aircraft SDK can enumerate and download originals. Downloaded Windows files are first written
as `<name>.part`; the final file is only replaced after a successful complete transfer.

The aircraft SDK does not expose arbitrary upload back into camera storage through this bridge.

## Safety and field use

This project deliberately does not expose motor start, takeoff, landing, RTH execution,
virtual-stick control or mission execution over HTTP. Configuration writes are limited to max
altitude and RTH altitude, are sent through DJI's typed KeyManager API, and are rejected while the aircraft is flying.

For wireless LAN operation, never use the default token and do not expose TCP/49152 to an untrusted
network. USB + `adb forward` remains the recommended deployment because the RC bridge then listens
only on loopback.

## Continuous integration

`.github/workflows/ci.yml` verifies both sides of the project:

- Windows: restore and Release-build `DroneDash_x64.slnx` (desktop, mock RC, smoke tools and tests) with a NuGet cache, run Planning/PV/Smart-Farming/Project smoke coverage, execute ThermalSmoke in SDK-optional mode, run xUnit metadata/core tests and verify the Smart Farming Python worker;
- Android: Java 17 + Gradle 8.12 + Android API 36, then `:app:assembleDebug`; pull requests use CI-only placeholders,
  while trusted push/manual runs consume `DJI_API_KEY` and `BRIDGE_TOKEN` repository secrets when configured.

This makes SDK/dependency or compiler regressions visible immediately after a push.

The same checks run locally:

```powershell
dotnet build DroneDash_x64.slnx -c Release
dotnet test desktop/DroneDash_x64.Tests/DroneDash_x64.Tests.csproj -c Release
python tests/python/verify_opencv_m3m_worker.py
python -m pip install numpy pytest
python -m pytest tests/python
```

The xUnit suite covers flight-plan validation and GSD/overlap math, route-to-image matching,
PV hotspot detection edge cases, M3M capture-name parsing, project path storage/rebasing and
processing-job transitions. The pytest suite covers the worker's CPU vegetation-index math,
NDVI zone classes, tiled raster statistics and option parsing without OpenCV, GDAL or CUDA.

Shared compiler settings for all desktop projects live in `desktop/Directory.Build.props`.


## NVIDIA CUDA acceleration

The local DJI Mavic 3M processing worker supports optional NVIDIA CUDA acceleration.
DroneDash-generated processing plans select GPU backends independently per stage:

- OpenCV-CUDA accelerates registration resize and final affine/perspective warping;
- CuPy-CUDA accelerates local NDVI, NDRE and GNDVI raster arithmetic with fused kernels and reusable GPU buffers;
- Python GDAL bindings optionally provide a pipelined tile-based geospatial engine for large ODM orthomosaics;
- a dedicated GDAL read-ahead thread prefetches future tiles while the main thread computes the current tile;
- a dedicated GDAL writer thread writes the previous tile concurrently, giving a bounded read → compute → write pipeline;
- tile size and pipeline depth are auto-tuned from raster size, available RAM/VRAM and real-data Read+Compute+DEFLATE-Write measurements;
- GDAL preserves raster windows, CRS/geotransform and GeoTIFF output while CuPy/NumPy computes each tile;
- OpenCV ECC transform estimation remains CPU-based;
- a persistent Python worker is reused across compatible local-processing steps to reduce interpreter/import/CUDA-context startup;
- each CUDA stage falls back independently to CPU when unavailable.

Radiometric correction remains on OTB and the corrected four-band GDAL VRT is retained.
For georeferenced ODM field products, DroneDash prefers the GDAL tile engine when Python GDAL
bindings are available and falls back to the existing OTB path otherwise. No CUDA runtime,
Python GDAL package or CuPy package is bundled in the repository.

See `docs/NVIDIA_CUDA.md` for Windows setup, `DRONEDASH_CUDA_BIN`, OpenCV `WITH_CUDA=ON`,
CuPy/Python-GDAL setup, adaptive tile/pipeline tuning, bounded pipelining, runtime probing and backend behavior.


## DJI Thermal SDK v1.8

DJI Thermal SDK is an optional local dependency for radiometric R-JPEG analysis. Its native
binaries are not stored in this repository. Download the Windows v1.8 package from DJI, review
DJI's license/EULA, then stage it with:

```powershell
.\scripts\install-dji-thermal-sdk.ps1 -ArchivePath "C:\path\to\dji_thermal_sdk_v1.8_20250829.zip"
```

See `desktop/DroneDash_x64.Desktop/third_party/dji-tsdk/README.md` for details. Without the
native runtime the rest of DroneDash continues to run normally; the Thermal analysis button is
disabled and the existing image/EXIF/XMP viewer remains available.


## Real M3T thermal fixture

The repository root contains `DJI_20261002154302_0001_T.JPG`, a real M3T infrared R-JPEG
used as a hardware-development fixture. CI validates its JPEG structure, 640×512 thermal
resolution, DJI/M3T identity, `InfraredCamera` XMP marker and DJI `iirp` radiometric block
without requiring proprietary TSDK binaries.

Local structural verification:

```powershell
.\scripts\verify-m3t-thermal-fixture.ps1
```

After staging DJI Thermal SDK v1.8, run:

```powershell
.\scripts\test-m3t-thermal-fixture.ps1
```

The script runs the headless `DroneDash_x64.ThermalSmoke` executable against the real
M3T fixture and fails if `dirp_measure_ex` does not return a valid 640×512 FLOAT32
temperature matrix. The same fixture can be opened interactively with
**Medien → M3T Testbild**. After analysis, moving the mouse over the thermal image shows the
exact source pixel and measured temperature. The original fixture is never modified.


## DJI flight planning, mapping and photogrammetry

The Windows client now contains a **Flugplanung** workspace backed by WebView2 and an
OpenStreetMap/Leaflet map. It supports DJI-style **mapping2d**, **mapping3d** and
**mappingStrip** planning for M3E, M3T and M3M, serpentine grid generation,
GSD/footprint/overlap/photo-spacing calculations, distance, photo-count and flight-time
estimates, and DJI WPML 1.0.2 KMZ export. Mapping 3D produces one Nadir plus four oblique
waylines; Strip mode uses a drawn centerline and WPML left/right corridor extension.

The planner uses DJI's Mavic 3 Enterprise WPML identifiers (aircraft type 77 with subtype
0/1/2 and payload 66/67/68). Exported KMZ files contain `wpmz/template.kml`,
`wpmz/waylines.wpml` and `wpmz/res/`. Mapping photo actions use equal-distance triggers per
survey segment. DroneDash does **not** upload or start these missions automatically; exported
missions should be reviewed in DJI Pilot 2 before flight.

M3E and M3M RGB GSD calculations use the 20 MP 4/3 mapping camera geometry. The M3T wide
profile is explicitly marked as an inspection estimate because the 48 MP wide camera lacks the
M3E mechanical-shutter survey profile.


### Smart Oblique and terrain follow

For M3E/M3T/M3M the planner can encode DJI Smart Oblique parameters in a Mapping 2D template
and can set the WPML height mode to `realTimeFollowSurface` with a configured surface-relative
height. These are template-level DJI planning features: DroneDash deliberately marks the locally
generated executable waylines as a flat preview/fallback and expects DJI Pilot 2 to regenerate
and validate Smart Oblique / terrain-follow execution before field use.

The CI planning smoke test now covers all three WPML template types, verifies five executable
folders for Mapping 3D, checks Smart Oblique and real-time terrain-follow fields, and validates
the Mapping Strip `LineString` representation.


### Geometry editor, import and basemaps

The planning map is an editor rather than a click-only sketchpad:

- drag a vertex to move it, drag or click the small midpoint handle on an edge to insert a vertex,
  and right-click a vertex to delete it; this works for survey polygons and strip centerlines;
- area (ha) and perimeter, or the corridor length, are shown live on the map and in the side panel,
  using the same `GeometryMetrics` calculation as the route statistics;
- self-intersecting ("bow tie") polygons are flagged, because they produce a wrong area and grid;
- the view only re-fits on import, project load and route calculation, not after every edit.

**Importieren (KML/KMZ/GeoJSON)** loads a field boundary or corridor centerline. Polygons become a
Mapping 2D area, lines switch the planner to Strip mode. If a file contains several geometries the
largest polygon (otherwise the longest line) is used and the rest is reported; inner rings (holes)
are ignored with a warning. Only WGS84 is accepted: projected data such as UTM / EPSG:25832, which
many agricultural portals export, is rejected with a clear message instead of being placed in the
wrong location. KML is parsed without DTD processing, and file/KMZ entry sizes are limited.

The basemap can be switched between OpenStreetMap and Esri World Imagery (satellite), with an
optional Esri label overlay; the last choice is remembered.

### Planning project files and KMZ validation

Flight-planning work can be saved as a versioned DroneDash \`.ddplan\` JSON project and loaded
back into the editor without losing aircraft profile, mapping mode, geometry, overlaps, altitude,
speed, gimbal settings, Smart Oblique, terrain-follow or strip parameters.

Every exported DJI KMZ is now validated immediately. The same validator can inspect an arbitrary
KMZ from the **DJI KMZ prüfen** button. It checks the required archive entries, KML/WPML namespace
1.0.2, WGS84 coordinate mode, M3E/M3T/M3M aircraft/payload pairing, unique wayline IDs,
continuous waypoint indexes, valid WGS84 coordinates, positive distance-trigger parameters,
the five-wayline Mapping 3D structure, and the required Smart Oblique / terrain-follow fields.

The planning CI test performs a full project round-trip (\`.ddplan\` save/load), regenerates the
plan, exports all supported DJI mapping modes and runs the semantic KMZ validator.


## Photogrammetry dataset workflow

The Windows client now contains a dedicated **Photogrammetrie** workspace. It scans local
JPG/JPEG/DNG/TIF/TIFF datasets without modifying the source images, skips reparse-point traversal,
reports scan progress/cancellation, and extracts DJI XMP fields
needed for downstream survey QA: GPS, absolute/relative altitude, raw RTK flag, RTK standard
deviations, aircraft/gimbal attitude, camera calibration, optical center and dewarp data.

A dataset can be linked to a versioned \`.ddplan\` flight-planning project. Geotagged images are
matched to the nearest generated DroneDash wayline segment and the manifest records wayline ID,
pass name, segment index and geometric distance to the planned route. DroneDash deliberately
keeps the raw RTK flag and precision values instead of inventing a hidden quality score.

The workspace summarizes planned-vs-recorded image count, GPS/RTK/calibration completeness and
QA findings. **Processing-Manifest export** creates:

- \`photogrammetry-manifest.json\` with structured image, RTK, calibration and route-assignment data;
- \`photogrammetry-images.csv\` for QA/spreadsheet workflows;
- \`image-list.txt\` containing the original source paths for downstream processors.

This is the handoff point for future Orthomosaic, DSM/DTM, 3D reconstruction and M3M
multispectral processing. The current implementation does not copy, rename or alter source images.


### DSM, DTM and point clouds with NodeODM

An analyzed photogrammetry dataset can be processed on a NodeODM server directly from the
**Photogrammetrie** workspace (*DSM / DTM / Punktwolke mit NodeODM berechnen*):

- only RGB survey images with GPS are sent; DJI thermal (`_T`), zoom (`_Z`) and M3M multispectral
  bands are excluded, and the JPEG is preferred over DNG/TIFF copies of the same capture;
- three presets (Schnell / Standard / Hoch) set `pc-quality`, `feature-quality`, `dem-resolution`
  (10 / 5 / 2 cm/px) and `orthophoto-resolution`; `dsm` and `dtm` are always requested;
- optional options such as `pc-copc` (cloud-optimized point cloud) and `auto-boundary` are only
  sent when the server's `GET /options` lists them, so older NodeODM versions keep working;
- the task is monitored with progress and console log and can be cancelled;
- the result `all.zip` (downloaded, or an existing one) is extracted with the same zip-slip and
  size guards as the Smart Farming import, then `odm_dem/dsm.tif`, `odm_dem/dtm.tif`,
  `odm_orthophoto/odm_orthophoto.tif` and the georeferenced point cloud (COPC preferred over
  LAZ/LAS/PLY) are located and registered in the open project as *Oberflächenmodell (DSM)*,
  *Geländemodell (DTM)*, *Orthomosaik* and *Punktwolke*.

**DSM auf Karte** / **DTM auf Karte** (or *Höhenmodell (GeoTIFF) öffnen …* for any elevation
GeoTIFF) opens the model on a Leaflet map over OpenStreetMap or Esri satellite imagery:

- the Python worker's `--dem-preview` mode warps the model with GDAL to Web Mercator (max. 2048 px),
  renders a colour relief with hillshade (ground pixel size corrected for Mercator scale) and
  transparent nodata, and writes `dem-preview.png`, a float32 elevation grid and
  `dem-preview.json` into `<model>.preview/` next to the model;
- the map shows a legend (2–98 % display range, min/max/mean, resolution), an opacity slider and
  the elevation under the mouse cursor;
- previews are reused until the model changes; rendering needs Python with GDAL bindings and NumPy
  (set `DRONEDASH_PYTHON`, e.g. OSGeo4W or conda).

**Punktwolke 3D** (or *Punktwolke (LAS/LAZ) öffnen …*) shows the georeferenced point cloud in a
three.js viewer inside the app:

- the worker's `--pointcloud-preview` mode reads LAS/LAZ/COPC in chunks with `laspy` (LAZ via
  `lazrs`), keeps every n-th point up to a budget of 3 million and writes centre-relative float32
  positions (float64 → float32 after subtracting the centre, so UTM coordinates keep sub-millimetre
  precision), 8-bit colours and ASPRS classes to `<cloud>.viewer/`;
- the viewer is Z-up with orbit/pan/zoom, colours by photo RGB, height or classification (with
  legend), adjustable point size, and shows the absolute X/Y/Z and class of a clicked point;
- point colours are drawn verbatim (no sRGB→linear brightening); previews are reused until the
  cloud changes. Requires `pip install "laspy[lazrs]"` in the configured Python.

The elevation map also measures on the **full-resolution** model (not the map preview), when the
configured Python has GDAL:

- **Profil** — draw a line; the worker (`--dem-profile`) samples the model bilinearly at pixel
  spacing (max. 2000 samples) and reports length (true ground distance on the GRS80 ellipsoid),
  min/max, ascent/descent and a chart whose cursor is mirrored on the map;
- **Volumen** — draw a polygon; `--dem-volume` rasterises it on the model grid (pixel centres) and
  integrates cut/fill above/below a base: a least-squares plane through the polygon boundary
  (stockpiles; slope and boundary RMSE are reported), the lowest boundary point, a fixed height or
  the DTM. Volumes need a projected model (e.g. UTM);
- **Bestandshöhe (DSM − DTM)** — `--chm` writes `odm_dem/chm.tif` (DTM resampled onto the DSM grid,
  processed in blocks, small negatives clipped to 0) with mean/median/P95/max, registers it and
  opens it on the map, where the polygon tool with a fixed base of 0 gives e.g. the mean crop
  height of a field.

Terrain checks for flight planning build on these products in a later step.

## PV analysis

The Windows client now contains a dedicated **PV-Analyse** module for radiometric M3T
photovoltaic inspection datasets. It reuses DJI Thermal SDK v1.8 and the photogrammetry metadata
pipeline to combine per-pixel temperatures with GPS, RTK and optional planned-wayline context.

The first implementation deliberately reports **thermal anomaly candidates**, not automatic
electrical fault diagnoses. For each thermal R-JPEG DroneDash calculates local temperature
contrast, groups connected hot pixels into clusters and records peak temperature, local thermal
baseline, delta-T, cluster size, centroid and bounding box. Warning and critical delta-T thresholds,
local window radius and minimum cluster size are operator-configurable.

Batch results preserve DJI radiometric parameters such as emissivity and measurement distance and
can be linked to a \`.ddplan\` flight plan. Export creates:

- \`pv-analysis.json\` with the complete structured analysis;
- \`pv-images.csv\` with per-image thermal/GPS/RTK/route context;
- \`pv-anomaly-candidates.csv\` with one row per detected thermal cluster.

The PV CI smoke test uses a synthetic temperature matrix with a known connected hotspot and verifies
the local-contrast detector, cluster severity and JSON/CSV export without requiring proprietary DJI
native binaries.


### PV inspection workspace

PV analysis now has three result views:

- **Übersicht** for the batch QA table;
- **Thermal-Detail** which reloads only the selected R-JPEG, renders the DJI pseudo-color image and
  overlays every detected anomaly cluster with its bounding box and peak position;
- **Anlagenkarte** which plots every georeferenced thermal image and exposes file, severity, maximum
  delta-T, candidate count and planned-route assignment.

The batch stage intentionally does not retain hundreds of 640×512 FLOAT32 temperature matrices.
A selected image is re-opened through DJI TSDK only when detailed inspection is requested.

In addition to JSON and CSV, export now creates \`pv-inspection-report.html\`. The report contains
the analysis parameters, dataset summary, per-image QA table and one row for every anomaly
candidate. It repeats the important limitation that thermal candidates require expert verification
and are not automatic electrical defect diagnoses.


## Smart Farming for DJI Mavic 3M

The Windows client now contains a dedicated **Smart Farming** module for DJI Mavic 3 Multispectral
datasets. It groups the normal synchronized RGB + Green/Red/Red-Edge/NIR capture set, checks
multispectral completeness and DJI radiometric metadata, and provides read-only dataset QA.

Supported quicklook vegetation indices are **NDVI**, **NDRE** and **GNDVI**. M3M multispectral
TIFFs contain DN values rather than ready-made reflectance. DroneDash therefore applies the
per-band BlackLevel, SensorGain, ExposureTime, SensorGainAdjustment and sunlight-sensor
Irradiance terms before calculating the band ratio.

Quicklooks are deliberately labelled as single-capture inspection products: they are not yet
orthorectified, fully distortion/vignetting corrected, sub-pixel band co-registered or calibrated
against a reflectance panel. Final agronomic maps and prescription layers must use the later
processing stage rather than treating a quicklook as a quantitative field orthomosaic.

The module also documents agriculture workflows for crop-stress scouting, variable-rate zones,
stand/emergence uniformity, weed scouting, drainage/soil-variability investigation and repeated
RTK monitoring. See `docs/SMART_FARMING_M3M.md` for the DJI technical basis and the Avary Drone,
NineTenths and Talos Drones practice references supplied for this module.


### Local Smart Farming image-processing toolchain

Smart Farming can probe optional local GDAL, Orfeo ToolBox and Python/OpenCV installs.
The repository does not bundle those native distributions. A selected complete M3M capture can be
turned into a reviewable processing workspace:

OpenCV ECC registration -> OTB DJI-DN compensation -> GDAL 4-band VRT -> CUDA/NumPy NDVI/NDRE/GNDVI quicklooks.

The generated PowerShell pipeline is review-first and is not started automatically. Environment
overrides are DRONEDASH_GDAL_BIN, DRONEDASH_OTB_BIN and DRONEDASH_PYTHON. See
docs/SMART_FARMING_M3M.md for details and processing limitations.

### Direct processing and NodeODM

The local Smart Farming plan can now run directly inside DroneDash with per-step progress,
stdout/stderr logging, output validation and process-tree cancellation. The generated
PowerShell/JSON plan remains available for review and reproducibility.

An optional NodeODM integration adds the full-field photogrammetry path. DroneDash probes a local
NodeODM endpoint (default `http://127.0.0.1:3000/`), checks the ODM engine version for M3M support,
streams complete Green/Red/Red-Edge/NIR datasets through the NodeODM init/upload/commit API,
monitors task progress, supports task cancellation and downloads the completed `all.zip`.
The M3M task uses NIR as primary band and offers ODM radiometric calibration `camera` or the
experimental `camera+sun` mode.

### NodeODM result import and field products

Smart Farming can import a completed NodeODM `all.zip`, securely extract it, locate the
georeferenced `odm_orthophoto.tif`, inspect its CRS/bands through local GDAL and resolve the
M3M Red/Green/NIR/Red-Edge band mapping. Band descriptions are preferred; a controlled
four-band ODM M3M fallback is surfaced as a warning when descriptions are absent.

DroneDash can then generate georeferenced NDVI, NDRE and GNDVI GeoTIFFs plus a configurable
five-class NDVI scouting-zone GeoTIFF. When Python GDAL bindings are available, the preferred path
uses adaptive GDAL tiles with CUDA/CuPy or NumPy; otherwise the existing OTB implementation remains
the fallback. The defaults are 0.20 / 0.40 / 0.60 / 0.80, and the UI explicitly treats these as
scouting classes rather than agronomic diagnosis or machine-ready application rates.

## Unified DroneDash project workspace

The Windows client now starts with a **Projekt** workspace. A versioned `.ddproj` file links the
previously independent planning, photogrammetry, PV and Smart Farming artifacts into one portable
project without copying raw imagery or processing outputs.

Artifacts inside the project directory use relative paths; external resources remain absolute.
The workspace can add files or source-data folders, discover known DroneDash manifests/exports
below the project directory, rebase references during **Speichern unter**, and verify whether
referenced files are unchanged, modified or missing. Normal files are snapshot with SHA-256;
automatic hashing is skipped for files above 256 MiB in favor of size/time metadata.

Removing an artifact from `.ddproj` never deletes the underlying file. See
`docs/PROJECT_WORKSPACE.md` for the project schema behavior and integrity model.

### Active project integration

An open `.ddproj` is now the shared project context across the desktop workflows. Successful core
outputs are registered automatically: flight plans, validated DJI KMZ, photogrammetry manifests,
PV analyses, Smart Farming dataset/processing artifacts, NodeODM archives and completed vegetation
field products. Re-exporting the same path updates its integrity snapshot instead of creating
duplicate project entries.

The Project tab also shows a four-stage workflow dashboard for **Planning → Dataset → Processing →
Analysis / field product**. The stage display is descriptive and artifact-based; scientific QA,
mission validation and analysis confidence remain in their respective modules.
