# BobrCam

**Free Webcam for OBS & PC** — turns your Android phone into a Windows webcam.

Use your phone's camera as a high-quality video source for OBS, Zoom, Teams, browsers, or the Windows Camera app. BobrCam streams encrypted video over Wi-Fi or USB with near-zero latency.

## Features

- **Full HD 60fps** camera streaming (resolves to best available resolution on your device)
- **End-to-end TLS 1.2** encryption with certificate fingerprint verification
- **Wi-Fi** — auto-discovery via UDP broadcast or manual IP/port entry
- **USB** — use `adb reverse` for zero-latency wired connection
- **Virtual camera** — appears as `BobrCam (Windows Virtual Camera)` in any app
- **Live phone preview** — hardware-accelerated camera preview on the phone using `TextureView`
- **Auto-fit window** — PC preview window resizes to match the exact camera resolution

## Requirements

- **Android phone** — any device with a camera
- **Windows 11 PC** — for the virtual camera
- **.NET 10 SDK** — for building
- **ADB** (optional) — for USB connection

## Quick Start

### Build

From the repository root:

```powershell
# Android
dotnet build .\BobrCam\BobrCam.csproj -f net10.0-android -m:1 -p:UseSharedCompilation=false -p:NodeReuse=false

# Windows
dotnet build .\BobrCam\BobrCam.csproj -f net10.0-windows10.0.19041.0 -m:1 -p:UseSharedCompilation=false -p:NodeReuse=false
```

### Install

1. Install the Android APK on your phone:
   ```
   adb install BobrCam\bin\Debug\net10.0-android\com.bobrcam.app-Signed.apk
   ```
2. Install the Windows virtual camera from `LumiFlowVirtualCamera/`:
   ```powershell
   powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Install-BobrCamVirtualCamera.ps1 -Quiet
   ```

### Connect

**Wi-Fi:**
1. Open BobrCam on both phone and PC
2. On the phone, tap **Connect Wi-Fi** — the app auto-discovers the Windows receiver

**USB:**
```powershell
adb reverse tcp:28444 tcp:28444
```
Then tap **Connect USB** on the phone.

### Use

Once connected, `BobrCam (Windows Virtual Camera)` appears in the camera list of OBS, Zoom, Teams, browsers, and the Windows Camera app. Select it as your video source.

## Repository Layout

| Path | Purpose |
| --- | --- |
| `BobrCam/` | .NET MAUI app for Android and Windows |
| `LumiFlowVirtualCamera/` | Windows 11 Media Foundation virtual camera |
| `LumiFlowVirtualCamera/CameraVerifier/` | Virtual camera activation verifier |

## Security

- TLS 1.2 with per-receiver certificate pinning
- Per-install 32-byte pairing token — only one phone per Windows receiver
- Certificate fingerprint advertised over UDP (not trusted without pairing)

## License

MIT
