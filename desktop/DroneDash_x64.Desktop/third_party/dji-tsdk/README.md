# DJI Thermal SDK v1.8 runtime

DroneDash_x64 integrates DJI Thermal SDK **v1.8** through the native DIRP API on Windows x64.

The DJI native binaries are intentionally not committed to this repository. DJI's package license
states that the public headers/samples are MIT-licensed while other SDK portions are governed by
DJI's SDK EULA.

## Install locally

1. Download **DJI Thermal SDK v1.8 for Windows** from DJI's official Download Center:
   https://www.dji.com/downloads/softwares/dji-thermal-sdk
2. Review the included `License.txt` and DJI SDK EULA.
3. Run from the repository root:

```powershell
.\scripts\install-dji-thermal-sdk.ps1 -ArchivePath "C:\Downloads\dji_thermal_sdk_v1.8_20250829.zip"
```

The script copies only the Windows x64 runtime files into:

```text
desktop/DroneDash_x64.Desktop/third_party/dji-tsdk/runtime/
```

That directory is gitignored. During build, the project stages the runtime into
`thermal-sdk\` beside the Windows application.

Alternatively set `DJI_TSDK_DIR` to either the extracted TSDK root or directly to the
Windows `release_x64` directory containing `libdirp.dll`.

## Runtime features

With TSDK installed, the media viewer can process DJI R-JPEG infrared images and provides:

- full-frame FLOAT32 temperature measurement;
- minimum / maximum / average / center temperature;
- hot/cold pixel coordinates;
- embedded distance, humidity, emissivity, reflected temperature and ambient temperature;
- selectable DJI pseudo-color palettes;
- DIRP API and R-JPEG version details.

This is local post-processing only. It does not change aircraft, camera or thermal-camera settings.
