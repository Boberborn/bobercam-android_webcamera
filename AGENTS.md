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

## Tooling

### Build & deploy

```powershell
dotnet --version                   # 10.0.302
dotnet build .\BobrCam.csproj -f net10.0-android   # Android
dotnet build .\BobrCam.csproj -f net10.0-windows10.0.19041.0  # Windows
msbuild .\BobrCam.csproj          # MSBuild alternative
adb devices                        # check phone connection
adb reverse tcp:28444 tcp:28446    # USB tunnel: phone port -> local-only PC port
gh pr create / gh pr checkout      # GitHub workflow
docker ps                          # container management
```

### Code search

```powershell
rg "pattern" --type cs             # ripgrep — fast code search
fd "CameraPreview"                 # fd — find files fast
bat MainPage.xaml.cs               # bat — syntax-highlighted cat
fzf                                # fzf — interactive fuzzy finder
jq '.Width' < config.json          # jq — parse JSON
yq eval '.field' config.yaml       # yq — parse YAML
```

### H.264 / TLS / Network

```powershell
ffprobe -show_packets captured.h264        # inspect encoded stream
openssl s_client -connect 192.168.1.5:28444 # debug TLS handshake
wireshark                              # capture & inspect H.264 over TLS
curl.exe -o test.h264 http://...        # download test stream
```

### Video info

```powershell
mediainfo captured.mp4             # container/codec metadata
ffprobe -v quiet -print_format json -show_format input.mp4  # inspect as JSON
```

### Debug (GUI)

```powershell
procmon                             # Process Monitor — syscall/file/registry trace
procexp                             # Process Explorer — process tree + handles
windbgx                             # WinDbg — crash dump analysis
```

### Scripting & helpers

```powershell
python -m http.server 8080          # ad-hoc test server
sqlite3 .dump bobrcam.db            # inspect local state (if applicable)
7z x ffmpeg.7z                      # extract archives
pwsh -c "command"                   # PowerShell 7 operations
```

### Nice to have (not agent-usable)

```powershell
code .\BobrCam\                     # VS Code quick edit
```

## Build (MSBuild commands)

## Build
Run from the repository root:

```powershell
dotnet build .\BobrCam\BobrCam.csproj -f net10.0-android -m:1 -p:UseSharedCompilation=false -p:NodeReuse=false
dotnet build .\BobrCam\BobrCam.csproj -f net10.0-windows10.0.19041.0 -m:1 -p:UseSharedCompilation=false -p:NodeReuse=false
```

Known existing warnings: obsolete Android legacy camera APIs and MAUI `MainPage` initialization.

## Deploying to a phone
When updating the Android app on a phone:
1. Run `adb devices` once to check the connection.
2. If the device is listed, continue with the deploy.
3. If the device is not listed after that single check, stop and notify the user to enable USB debugging on the phone and reconnect it. Do not retry multiple times or continue the deploy.

## Branding
- Product: `BobrCam`
- Subtitle: `Free Webcam for OBS & PC`
- Android package: `com.bobrcam.app`
- Palette: warm cream, orange/coral gradients, dark brown.
- Do not introduce green unless explicitly requested.
- Unused historical `lumiflow_*` assets may remain if unreferenced.

## Connection and security invariants
- Wi-Fi transport is TLS 1.2 over TCP.
- Windows advertises its local certificate SHA-256 fingerprint through UDP discovery.
- Android pins the fingerprint after first pairing.
- Windows sends a fresh 32-byte nonce and Android proves possession of its
  per-install token with HMAC-SHA256.
- Windows stores token hashes for multiple paired phones. Unknown Wi-Fi phones
  are accepted only while the user opens the 60-second pairing window.
- Wi-Fi uses UDP discovery or manual IP/port.
- Wi-Fi may bind only to a selected private IPv4 address.
- The unencrypted USB endpoint binds only to `127.0.0.1:28446`.
- ADB reverse is the current working USB fallback. Android connects to its local
  port `28444`, which maps to Windows port `28446`.
- Initial Wi-Fi certificate pairing remains trust-on-first-use. A release still
  needs explicit short-code or QR confirmation on both devices to prevent a
  same-LAN discovery race.

```powershell
adb reverse tcp:28444 tcp:28446
```

Production no-debug USB is partially implemented:

- `Platforms/Android/AndroidUsbAccessoryTransport.cs` opens Android Open
  Accessory bulk streams and reuses the existing H.264 sender.
- `Platforms/Windows/ProductionUsbHostManager.cs` detects accessory-mode
  `18D1:2D00/2D01` devices and bridges WinUSB to the local authenticated receiver.
- `Platforms/Windows/AndroidOpenAccessoryActivator.cs` implements AOA protocol
  requests 51/52/53 for Android interfaces that Windows already exposes through
  WinUSB, then the existing accessory bridge takes over after re-enumeration.
- `Platforms/Windows/BobrCamUsbFilterClient.cs` opens the BobrCam filter device
  interface and requests AOA activation before the WinUSB/MTP fallback.
- `BobrCamUsbDriver/Filter/` contains a KMDF pass-through upper filter, a
  signability-validated extension INF, and the shared IOCTL contract. It attaches
  to USB composite devices but exposes its interface only for recognized Android
  vendor IDs.
- `BobrCamUsbDriver/BobrCamUsbAccessory.inf` is a development INF only.
- `BobrCamUsbDriver/Build-BobrCamUsbFilter.ps1` restores WDK 26100.6584, compiles
  and links the x64 driver, validates the INF, and generates an unsigned catalog.
- Remaining external blocker: Microsoft production-sign the driver package and
  test installation, MTP activation, and charge-only activation on clean Windows
  systems. Do not claim no-debug USB is release-complete before those tests pass.

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
