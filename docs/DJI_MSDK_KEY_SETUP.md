# DJI MSDK V5 App Key setup

DroneDash RC Bridge uses the permanent Android application ID:

```text
com.mason82.dronedash.rcbridge
```

The DJI Developer application must use this exact Android package name. The old development package
`com.example.m3ebridge` is no longer used.

## 1. Create the DJI Mobile SDK application

1. Sign in to DJI Developer Center.
2. Open **Apps** and create a **Mobile SDK / Android** application.
3. Use the exact package name `com.mason82.dronedash.rcbridge`.
4. Complete DJI's activation/verification flow.
5. Copy the generated **App Key**.

Do not commit the App Key to this repository.

## 2. Local Windows build

The Android toolchain is pinned to API 36 / AGP 8.10.1 / Gradle 8.12 / Java 17.
Install Android SDK Platform 36 and Build Tools 35.0.0 before building. You can verify
the local environment without exposing secret values:

```powershell
.\scripts\doctor.ps1
```

Create or edit:

```text
%USERPROFILE%\.gradle\gradle.properties
```

Add:

```properties
DJI_API_KEY=YOUR_DJI_MSDK_V5_APP_KEY
BRIDGE_TOKEN=REPLACE_WITH_A_RANDOM_TOKEN_OF_AT_LEAST_24_CHARACTERS
BRIDGE_BIND_ADDRESS=127.0.0.1
```

The RC bridge build reads project properties first and environment variables second. Therefore an
ephemeral PowerShell build is also possible without saving the DJI key to disk:

```powershell
$env:DJI_API_KEY = "YOUR_DJI_MSDK_V5_APP_KEY"
$env:BRIDGE_TOKEN = "REPLACE_WITH_A_RANDOM_TOKEN_OF_AT_LEAST_24_CHARACTERS"
$env:BRIDGE_BIND_ADDRESS = "127.0.0.1"

cd rc-agent
gradle --no-daemon :app:assembleDebug
```

The debug APK is written to:

```text
rc-agent\app\build\outputs\apk\debug\app-debug.apk
```

A release build performs an additional credential check and fails when the DJI App Key is empty,
uses the CI placeholder, or the bridge token is shorter than 24 characters:

```powershell
gradle --no-daemon :app:assembleRelease
```

## 3. GitHub Actions

In the GitHub repository open:

**Settings -> Secrets and variables -> Actions -> New repository secret**

Create these two secrets:

```text
DJI_API_KEY
BRIDGE_TOKEN
```

Their values are the DJI MSDK V5 App Key and a strong DroneDash bridge token respectively.

The CI workflow deliberately does not expose repository secrets to pull-request builds. Pull
requests compile with `ci-placeholder` / `ci-only-token`. Pushes to `main` and manual workflow
runs use the repository secrets when they exist, otherwise they still compile with placeholders.

This separation keeps untrusted PR code away from production credentials while retaining dependency
and compiler coverage.

## 4. Verify the installed APK

After installation on the DJI RC Pro Enterprise, open **DroneDash RC Bridge**. The status area shows:

- `App ID: com.mason82.dronedash.rcbridge`
- `DJI App Key: konfiguriert` when a non-placeholder key was compiled into the APK
- DJI SDK phase and registration state

The key value itself is never displayed.

On the first valid registration, the runtime should progress through:

```text
INITIALIZING -> REGISTERING -> REGISTERED
```

and with a connected supported aircraft:

```text
READY
```

If the application reaches `REGISTRATION_FAILED`, first verify that the DJI Developer package name
exactly matches `com.mason82.dronedash.rcbridge`, that the intended App Key was used for the build,
and that the controller has the connectivity required for first-time DJI registration.

## 5. Install over ADB

With USB debugging enabled on the RC Pro Enterprise:

```powershell
adb install -r rc-agent\app\build\outputs\apk\debug\app-debug.apk
adb shell am force-stop dji.go.v5
```

Then launch **DroneDash RC Bridge** manually. For the recommended USB-only bridge mode, keep
`BRIDGE_BIND_ADDRESS=127.0.0.1` and create the forwarding rule from Windows:

```powershell
adb forward tcp:49152 tcp:49152
```

The bridge remains on loopback and is not exposed to the RC's LAN interface.
