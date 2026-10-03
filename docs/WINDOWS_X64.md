# Windows x64 build and deployment

DroneDash_x64.Desktop is a Windows-only .NET 10 WPF application. The desktop project is explicitly
targeted at:

- runtime identifier: \`win-x64\`
- platform target: \`x64\`
- \`Prefer32Bit=false\`

This is intentional. DroneDash integrates optional native x64 components such as DJI Thermal SDK,
GDAL, OpenCV-CUDA, CuPy/CUDA and Orfeo ToolBox. A 32-bit desktop process would make those native
integration paths unreliable or impossible.

The Android RC bridge remains a separate DJI MSDK V5 hardware adapter. DJI MSDK V5 is not moved into
the Windows process; the Windows application communicates with the RC Pro Enterprise through the
small HTTP/JSON bridge.

## Requirements

For development:

- Windows 10/11 x64
- .NET 10 SDK
- Microsoft Edge WebView2 Runtime
- 64-bit PowerShell recommended
- 64-bit Python when local Smart Farming processing is used

Optional processing dependencies remain external:

- DJI Thermal SDK v1.8 Windows x64 runtime
- GDAL
- Orfeo ToolBox
- OpenCV / OpenCV-CUDA
- NVIDIA driver and CUDA Toolkit
- CuPy
- Python GDAL bindings
- NodeODM

Run the environment check first:

\`\`\`powershell
.\scripts\doctor.ps1
\`\`\`

The doctor reports the Windows/process architecture, .NET 10, Windows Desktop Runtime, Android/DJI
toolchain, NVIDIA/CUDA availability, 64-bit Python, OpenCV-CUDA, CuPy and Python GDAL.

## Build

Build the complete desktop solution:

\`\`\`powershell
.\scripts\build-desktop.ps1
\`\`\`

Build and run the xUnit suite:

\`\`\`powershell
.\scripts\build-desktop.ps1 -Test
\`\`\`

Build, test and create a validated Windows x64 publish:

\`\`\`powershell
.\scripts\build-desktop.ps1 -Test -Publish
\`\`\`

## Framework-dependent Windows x64 publish

The normal package keeps .NET outside the application bundle:

\`\`\`powershell
.\scripts\publish-windows-x64.ps1 -Configuration Release -Clean
\`\`\`

Default output:

\`\`\`text
artifacts\windows-x64\framework-dependent\
\`\`\`

The target PC requires the .NET 10 Windows Desktop Runtime. The publish script verifies the generated
application host directly and fails unless its PE machine value is AMD64 (\`0x8664\`).

It also verifies that runtime content required by DroneDash is present:

- \`planning\route-editor.html\`
- \`pv\pv-map.html\`
- \`smart-farming\opencv_m3m.py\`
- \`smart-farming\dronedash_worker\*.py\`

A \`dronedash-publish.json\` manifest is written to the publish directory with the runtime identifier,
platform target, PE architecture, .NET SDK version and DJI Thermal SDK inclusion state.

## Self-contained Windows x64 publish

A self-contained package can be produced when the target workstation should not need a separately
installed .NET 10 runtime:

\`\`\`powershell
.\scripts\publish-windows-x64.ps1 -Configuration Release -SelfContained -Clean
\`\`\`

Default output:

\`\`\`text
artifacts\windows-x64\self-contained\
\`\`\`

Self-contained .NET does not bundle the optional external geospatial/CUDA/Thermal toolchains. Those
remain separately installed or staged.

## DJI Thermal SDK

When the licensed DJI Thermal SDK v1.8 Windows x64 runtime has been staged under:

\`\`\`text
desktop\DroneDash_x64.Desktop\third_party\dji-tsdk\runtime\
\`\`\`

the desktop project includes the runtime in both normal build output and Windows x64 publish output
under:

\`\`\`text
thermal-sdk\
\`\`\`

If the SDK is absent, publishing still succeeds and thermal analysis remains disabled at runtime.

## Smart Farming native toolchains

Use x64 builds consistently:

- Python x64
- GDAL x64 and matching Python GDAL bindings
- Orfeo ToolBox x64
- OpenCV x64; CUDA-enabled build only when GPU registration acceleration is wanted
- CuPy matching the installed NVIDIA CUDA/runtime generation

The DroneDash process does not load GDAL, OTB or Python directly as in-process DLLs. Processing is
isolated through external executables and the persistent Python JSONL worker. Nevertheless, keeping
all local toolchains x64 avoids mixed-architecture subprocess and native-extension failures.

## CI

The Windows GitHub Actions job now performs:

1. Python worker validation and pytest
2. real M3T structural fixture validation
3. .NET restore/build
4. Thermal, Planning, PV, Smart Farming and Project smoke tests
5. xUnit tests
6. framework-dependent \`win-x64\` publish
7. AMD64 PE and required-resource validation
8. upload of the \`DroneDash_x64-win-x64\` workflow artifact

The CI artifact is intended as a verified build output. Proprietary DJI Thermal SDK binaries and
other optional native third-party runtimes are not injected into GitHub Actions unless explicitly
and legally provisioned.
