# BobrCam agent guide
use only english language
## Product and architecture
- BobrCam turns an Android phone into a Windows webcam.
- Android is always the camera sender.
- Windows is always the receiver and preview app.
- Windows 11 exposes `BobrCam (Windows Virtual Camera)` through Media Foundation.
- Stack: .NET 10, C#, .NET MAUI, Media Foundation, PowerShell.

## Repository
- `BobrCam/` — main MAUI project; `BobrCam.csproj` is the main project.
- `LumiFlowVirtualCamera/` — Windows virtual-camera implementation.
- `LumiFlowVirtualCamera/CameraVerifier/` — camera discovery and activation checks.
- Do not rename `LumiFlowVirtualCamera/` during unrelated work. Treat it as a separate verified refactor.

## Current stage
The project is in active development. Prioritize requested features, bug fixes, and build stability.
Do not prepare publishing, installers, release signing, store metadata, or marketing assets unless explicitly requested.

## Work method
Before editing:
- Inspect relevant files, call flow, and existing patterns.
- Reuse existing services and components.
- Prefer the smallest coherent change.
- Do not rewrite working systems without a clear need.

While editing:
- Keep code simple and readable; avoid unnecessary abstractions and dependencies.
- Preserve existing behavior unless the task requires a change.
- Use async/await correctly and pass cancellation tokens where supported.
- Do not suppress warnings without a documented reason.
- Never add secrets, tokens, certificates, keystores, or machine-specific paths.

After editing:
- Build only the affected target.
- Fix errors introduced by the change.
- Ignore unrelated known warnings unless requested.
- Report changed files, build result, and remaining limitations.

## Build
Run from the repository root:

```powershell
dotnet build .\BobrCam\BobrCam.csproj -f net10.0-android -m:1 -p:UseSharedCompilation=false -p:NodeReuse=false
dotnet build .\BobrCam\BobrCam.csproj -f net10.0-windows10.0.19041.0 -m:1 -p:UseSharedCompilation=false -p:NodeReuse=false
```

Known existing warnings: obsolete Android legacy camera APIs and MAUI `MainPage` initialization.

## Branding
- Product: `BobrCam`
- Subtitle: `Free Webcam for OBS & PC`
- Android package: `com.bobrcam.app`
- Palette: warm cream, orange/coral gradients, dark brown.
- Do not introduce green unless explicitly requested.
- Unused historical `lumiflow_*` assets may remain if unreferenced.

## Connection and security invariants
- Transport is TLS 1.2 over TCP.
- Windows advertises its local certificate SHA-256 fingerprint through UDP discovery.
- Android pins the fingerprint after first pairing.
- Android sends a per-install 32-byte pairing token after the TLS handshake.
- Windows stores the first token and rejects different phones afterward.
- Wi-Fi uses UDP discovery or manual IP/port.
- USB uses ADB reverse so Android connects to `127.0.0.1`.

```powershell
adb reverse tcp:28444 tcp:28444
```

Never weaken certificate pinning or pairing-token validation to fix connections. First check ADB reverse, firewall, IP, port, and stale pairing data.
Changing the Android app ID or certificate filename can require reinstalling and pairing again.

## Virtual camera invariants
- Windows 11 is required.
- The camera is a legitimate software virtual camera; do not hide or bypass virtual-camera detection.
- Keep CLSID `{72e86f9b-a6e1-4e73-bd16-8ec1e4bc18ef}` stable.
- When renaming binaries, update installer, registration, uninstall logic, and `CameraVerifier` together.

Verify with:

```powershell
dotnet run --project .\LumiFlowVirtualCamera\CameraVerifier\CameraVerifier.csproj -- --activate
```

Expected output includes `BobrCam (Windows Virtual Camera)` and successful activation.

## Release work
Only when explicitly requested: configure secure release signing, increment versions, create packages, prepare Google Play/Microsoft Store metadata, and test on a real phone plus a clean Windows profile.
