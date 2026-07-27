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
adb reverse tcp:28444 tcp:28444    # USB tunnel
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
