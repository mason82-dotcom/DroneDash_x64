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
- optional DJI Thermal SDK v1.8 integration analyzes radiometric DJI R-JPEGs locally on Windows x64, including the full per-pixel FLOAT32 temperature matrix, min/max/average/center values, cursor temperature inspection and selectable pseudo-color rendering.

## Versions pinned by this project

- DJI Mobile SDK V5: **5.18.0**
- DJI Thermal SDK (optional Windows x64 runtime): **1.8**
- Android compile/target SDK: **35**
- Android min SDK: **24**
- Android Gradle Plugin: **8.7.0**
- Kotlin: **2.1.0**
- Gradle: **8.12**
- Java: **17**
- Windows desktop: **.NET 10 / WPF**

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

1. Create a DJI Developer application and bind its App Key to the exact Android package name
   `com.example.m3ebridge`, or change both `namespace` and `applicationId` before registering.
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

3. Install Android SDK Platform 35, Build Tools 35.x, Java 17 and Gradle 8.12. Generate the wrapper once:
   `cd rc-agent && gradle wrapper --gradle-version 8.12`
4. Build:
   `./gradlew :app:assembleDebug`
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

- Windows: restore and Release-build the WPF desktop application and mock RC;
- Android: Java 17 + Gradle 8.12, then `:app:assembleDebug` with CI-only placeholder credentials.

This makes SDK/dependency or compiler regressions visible immediately after a push.


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
JPG/JPEG/DNG/TIF/TIFF datasets without modifying the source images and extracts DJI XMP fields
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
