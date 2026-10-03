# VS Code development setup

DroneDash_x64 is prepared for Visual Studio Code development on Windows x64. Open the repository
through `DroneDash_x64.code-workspace` so the Windows desktop, Mock RC and Android RC agent are
available as dedicated workspace roots.

## Prerequisites

Required for the complete repository:

- Windows 10/11 x64
- Visual Studio Code
- .NET 10 SDK (the repository is pinned by `global.json`)
- Java 17
- Android SDK Platform 36 and Build Tools 35.0.0
- Gradle 8.12 when the repository has no generated Gradle wrapper
- PowerShell
- Git

Recommended VS Code extensions are stored in `.vscode/extensions.json`:

- C# Dev Kit
- PowerShell
- Python
- Extension Pack for Java
- Gradle for Java
- Kotlin

Python/GDAL/OpenCV/CUDA, DJI Thermal SDK, COLMAP and NodeODM remain optional and are only needed for
the corresponding processing workflows.

## First start

From a Windows PowerShell terminal in the repository:

```powershell
.\scripts\vscode-setup.ps1
```

The script reports missing VS Code extensions and then runs the existing DroneDash environment
doctor. It does not install extensions unless requested.

To install the recommended extensions explicitly:

```powershell
.\scripts\vscode-setup.ps1 -InstallRecommendedExtensions
```

To install/check and open the tracked workspace:

```powershell
.\scripts\vscode-setup.ps1 -InstallRecommendedExtensions -OpenWorkspace
```

Alternatively open `DroneDash_x64.code-workspace` manually.

## Build tasks

Use **Terminal → Run Task** or `Ctrl+Shift+B`.

The default build task is:

- `Desktop: build Debug`

Additional tasks include:

- `Environment: doctor`
- `Desktop: restore`
- `Desktop: build Release`
- `Desktop: test`
- `Desktop: run`
- `Desktop: publish x64`
- `Desktop: publish x64 self-contained`
- `Mock RC: build Debug`
- `Mock RC: run`
- `Python: worker verification`
- `Python: worker tests`
- `RC Agent: build debug`
- `RC Agent: USB forward`
- `Build: Windows + RC Agent`

The desktop tasks call the repository PowerShell build scripts rather than duplicating build policy,
so .NET 10 and Windows-x64 checks remain identical between terminal and VS Code.

## Debugging

Open **Run and Debug** and choose one of:

- `DroneDash Desktop (Debug x64)` — builds and starts the WPF application under the .NET debugger.
- `Mock RC (Debug)` — starts the hardware-free HTTP bridge simulator.
- `DroneDash + Mock RC` — launches both debug configurations together.
- `Attach to .NET process` — attaches to an already running .NET process.

The Desktop debug target is the RID-specific output:

```text
desktop/DroneDash_x64.Desktop/bin/Debug/net10.0-windows/win-x64/DroneDash_x64.Desktop.dll
```

The Mock RC target is:

```text
desktop/DroneDash_x64.MockRc/bin/Debug/net10.0/DroneDash_x64.MockRc.dll
```

For normal development without DJI hardware, start `DroneDash + Mock RC`, then use
`http://127.0.0.1:49152/` and the development token `change-me-now` in the desktop UI.

## Android RC agent

The Android source lives in `rc-agent/`. The Gradle extension can discover it as a workspace root.
The supported command-line path remains:

```powershell
.\scripts\build-rc-agent.ps1
```

DJI secrets must remain outside the repository in:

```text
%USERPROFILE%\.gradle\gradle.properties
```

with `DJI_API_KEY` and `BRIDGE_TOKEN` as documented in `docs/DJI_MSDK_KEY_SETUP.md`.

After connecting and authorizing the RC Pro Enterprise over USB, run the VS Code task
`RC Agent: USB forward` to establish `adb forward tcp:49152 tcp:49152`.

## Python processing

The Python worker can be edited with the Python extension. Run the dependency-light verification
from VS Code with `Python: worker verification`.

For the pytest suite install the test dependencies in the selected 64-bit Python environment:

```powershell
python -m pip install numpy pytest "laspy[lazrs]"
python -m pytest tests/python -q
```

Full DSM/DTM profile, volume and CHM end-to-end tests additionally require Python GDAL
(`osgeo.gdal`). The Smart Farming CUDA workflows may additionally use OpenCV-CUDA and CuPy.

## Release build

Before producing a build for another Windows machine run:

```powershell
.\scripts\build-desktop.ps1 -Configuration Release -Test
.\scripts\publish-windows-x64.ps1 -Configuration Release -Clean
```

or use the corresponding VS Code tasks. The publish script verifies the generated application host
as AMD64 and checks that all required HTML and Python runtime resources are included.
