# BobrCam continuation guide

## Product

**BobrCam – Free Webcam for OBS & PC** turns an Android phone into a Windows
webcam. The Android app is always the camera sender. The Windows app is always
the receiver and preview. A separate Windows 11 Media Foundation virtual camera
keeps **BobrCam (Windows Virtual Camera)** in the camera list for OBS, Zoom,
Teams, browsers, and Camera while it is installed.

## Repository layout

| Path | Purpose |
| --- | --- |
| `BobrCam/` | .NET MAUI app for Android and Windows. `BobrCam.csproj` is the main project. |
| `LumiFlowVirtualCamera/` | Windows 11 virtual-camera implementation. The folder is legacy-named but its product/output names are BobrCam. |
| `LumiFlowVirtualCamera/CameraVerifier/` | Lists Windows cameras and verifies the BobrCam virtual camera can activate. |

Do not rename `LumiFlowVirtualCamera/` casually: its scripts and paths work as
checked in. Rename it only as a separate, verified refactor.

## Current branding and assets

- App/launcher display name: `BobrCam`
- Marketing subtitle: `Free Webcam for OBS & PC`
- Android package: `com.bobrcam.app`
- Android adaptive icon: `BobrCam/Resources/AppIcon/bobrcam_background.svg` and
  `bobrcam_foreground.png`.
- Windows icon: `BobrCam/Resources/AppIcon/bobrcam_windows.png`.
- Startup splash: square beaver-camera mark (`Resources/Splash/bobrcam_splash_icon.png`);
  the horizontal wordmark (`Resources/Splash/bobrcam_logo.png`) is shown inside the app.
- Palette: warm cream `#FFF8F2`, orange/coral gradients, dark brown; do not
  introduce green unless a future design explicitly requires it.

The source may contain unused historical `lumiflow_*` image files. They are not
referenced by the project and do not affect the built app.

## Build commands

Run from the repository root unless a different working directory is shown.

```powershell
dotnet build .\BobrCam\BobrCam.csproj -f net10.0-android -m:1 -p:UseSharedCompilation=false -p:NodeReuse=false
dotnet build .\BobrCam\BobrCam.csproj -f net10.0-windows10.0.19041.0 -m:1 -p:UseSharedCompilation=false -p:NodeReuse=false
```

The Android signed APK is normally:

```text
BobrCam\bin\Debug\net10.0-android\com.bobrcam.app-Signed.apk
```

The Windows output is normally under:

```text
BobrCam\bin\Debug\net10.0-windows10.0.19041.0\win-x64\BobrCam.exe
```

Build warnings currently include obsolete Android legacy camera APIs and MAUI
`MainPage` initialization. They are known warnings; both Android and Windows
builds succeeded on .NET 10.0.302.

## Phone connection design

- Transport is TLS 1.2 over TCP.
- The Windows receiver creates a local certificate and advertises its SHA-256
  fingerprint over UDP discovery.
- Android pins the receiver fingerprint after first pairing.
- The phone sends a per-install 32-byte pairing token after the TLS handshake.
  Windows saves the first token and rejects different phones thereafter.
- Wi-Fi: Android listens for UDP broadcast discovery, or can use manually
  entered IP/port.
- USB: use ADB reverse so Android connects to `127.0.0.1`.

Useful USB command:

```powershell
adb reverse tcp:28444 tcp:28444
```

The default port is defined in `BobrCam/VideoProtocol.cs`. The Windows UI lets
the user modify IP and port. The Android UI has USB and Wi-Fi buttons.

Important: the rebrand changed both the Android package and Windows certificate
file name. Existing pairing data is intentionally reset; pair again after
installing BobrCam.

## Virtual camera

Windows 11 is required. This is a legitimate Media Foundation software virtual
camera and is deliberately detectable as virtual hardware; it does not bypass
services that block virtual cameras.

Build and install from `LumiFlowVirtualCamera/`:

```powershell
dotnet build .\VCamNetSampleSource\VCamNetSampleSource.csproj -c Release -p:Platform=x64 -p:UseSharedCompilation=false -p:NodeReuse=false
dotnet build .\VCamNetSample\VCamNetSample.csproj -c Release -p:Platform=x64 -p:UseSharedCompilation=false -p:NodeReuse=false
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Install-BobrCamVirtualCamera.ps1 -Quiet
```

The installer requests elevation to register the COM media source. It installs
under `C:\ProgramData\BobrCam\VirtualCamera\`, reads frames from
`C:\ProgramData\BobrCam\Frames\latest.jpg`, and creates the Installed Apps
entry `BobrCam Virtual Camera`.

Verify after installation:

```powershell
dotnet run --project .\CameraVerifier\CameraVerifier.csproj -- --activate
```

Expected output contains `BobrCam (Windows Virtual Camera)` (localized on some
Windows systems) followed by `BobrCam activation succeeded.`

Uninstall with:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Uninstall-BobrCamVirtualCamera.ps1 -Quiet
```

The virtual-camera source CLSID is
`{72e86f9b-a6e1-4e73-bd16-8ec1e4bc18ef}`. Keep it stable for update/uninstall
compatibility.

## Deployment status on this PC

- Windows virtual camera is installed and verified as BobrCam.
- BobrCam Android app was installed to the connected device
  `GM5PUCHETWFQ4HTC`.
- Desktop shortcut: `%USERPROFILE%\Desktop\BobrCam Windows.lnk`.
- The previous `com.lumiflow.app` Android app and `LumiFlow Windows.lnk` were
  removed after BobrCam installation.

## Safe continuation checklist

1. Preserve the phone-is-sender / Windows-is-receiver roles.
2. Do not weaken certificate pinning or pairing-token validation merely to fix
   connection errors; diagnose ADB reverse, firewall, address, or stale pairing
   first.
3. When changing virtual-camera binary names, update both installer scripts and
   test registration plus `CameraVerifier`.
4. When changing Android app ID, expect an uninstall/reinstall and pairing reset.
5. Before release, replace debug signing with a securely stored release keystore,
   increment version codes, and test on a real phone and a clean Windows profile.
