#define AppVersion "1.0.0-rc3"
#define PayloadRoot "..\artifacts\BobrCam-1.0.0-rc3"

[Setup]
AppId={{D972784B-D57D-4A86-B558-48FBEAA48728}
AppName=BobrCam
AppVersion={#AppVersion}
AppPublisher=BobrCam
DefaultDirName={autopf}\BobrCam
DefaultGroupName=BobrCam
UninstallDisplayIcon={app}\App\BobrCam.exe
OutputDir=..\artifacts
OutputBaseFilename=BobrCam-{#AppVersion}-win-x64-setup
SetupIconFile=..\publish_win\bobrcam_windows.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes

[Files]
Source: "{#PayloadRoot}\App\*"; DestDir: "{app}\App"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PayloadRoot}\VirtualCamera\*"; DestDir: "{app}\VirtualCamera"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PayloadRoot}\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadRoot}\README.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\BobrCam"; Filename: "{app}\App\BobrCam.exe"
Name: "{autodesktop}\BobrCam"; Filename: "{app}\App\BobrCam.exe"

[Run]
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\VirtualCamera\Install-BobrCamVirtualCamera.ps1"" -Quiet"; StatusMsg: "Installing BobrCam virtual camera..."; Flags: runhidden waituntilterminated
Filename: "{app}\App\BobrCam.exe"; Description: "Launch BobrCam"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\VirtualCamera\Uninstall-BobrCamVirtualCamera.ps1"" -Quiet"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveBobrCamVirtualCamera"
