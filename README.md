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
- live status includes velocity, Home Point, GPS/compass/wind, detailed battery telemetry and read-only RTK telemetry including FIX/FLOAT state, precision and satellite counts.

## Versions pinned by this project

- DJI Mobile SDK V5: **5.18.0**
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
