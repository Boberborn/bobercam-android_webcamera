param(
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'
$cameraClsid = '{72e86f9b-a6e1-4e73-bd16-8ec1e4bc18ef}'
$sourceOutput = Join-Path $PSScriptRoot 'VCamNetSampleSource\bin\x64\Release\net10.0-windows10.0.22621.0'
$registrarOutput = Join-Path $PSScriptRoot 'VCamNetSample\bin\x64\Release\net10.0-windows10.0.22621.0'
$installRoot = Join-Path $env:ProgramData 'BobrCam\VirtualCamera'
$installDirectory = Join-Path $installRoot ('v10-' + (Get-Date -Format 'yyyyMMddHHmmss'))
$framesDirectory = Join-Path $env:ProgramData 'BobrCam\Frames'

$requiredFiles = @(
    (Join-Path $sourceOutput 'BobrCam.VirtualCameraSource.comhost.dll'),
    (Join-Path $sourceOutput 'BobrCam.VirtualCameraSource.dll'),
    (Join-Path $sourceOutput 'BobrCam.VirtualCameraSource.deps.json'),
    (Join-Path $sourceOutput 'BobrCam.VirtualCameraSource.runtimeconfig.json'),
    (Join-Path $sourceOutput 'DirectNCore.dll'),
    (Join-Path $sourceOutput 'FFmpeg.AutoGen.dll'),
    (Join-Path $sourceOutput 'Microsoft.Windows.SDK.NET.dll'),
    (Join-Path $sourceOutput 'WinRT.Runtime.dll'),
    (Join-Path $sourceOutput 'libs\avcodec-62.dll'),
    (Join-Path $sourceOutput 'libs\avutil-60.dll'),
    (Join-Path $sourceOutput 'libs\swscale-9.dll'),
    (Join-Path $sourceOutput 'libs\swresample-6.dll'),
    (Join-Path $sourceOutput 'libs\avformat-62.dll'),
    (Join-Path $sourceOutput 'libs\avdevice-62.dll'),
    (Join-Path $sourceOutput 'libs\avfilter-11.dll'),
    (Join-Path $registrarOutput 'BobrCam.VirtualCamera.exe'),
    (Join-Path $registrarOutput 'BobrCam.VirtualCamera.dll'),
    (Join-Path $registrarOutput 'BobrCam.VirtualCamera.deps.json'),
    (Join-Path $registrarOutput 'BobrCam.VirtualCamera.runtimeconfig.json')
)

foreach ($file in $requiredFiles) {
    if (-not (Test-Path -LiteralPath $file)) {
        throw "Missing camera build output: $file"
    }
}

New-Item -ItemType Directory -Force -Path $installDirectory, $framesDirectory | Out-Null
foreach ($file in $requiredFiles) {
    if ($file.StartsWith($sourceOutput, [System.StringComparison]::OrdinalIgnoreCase)) {
        $relativePath = $file.Substring($sourceOutput.Length).TrimStart('\')
    }
    else {
        $relativePath = [System.IO.Path]::GetFileName($file)
    }
    $destination = Join-Path $installDirectory $relativePath
    New-Item -ItemType Directory -Force -Path ([System.IO.Path]::GetDirectoryName($destination)) | Out-Null
    Copy-Item -LiteralPath $file -Destination $destination -Force
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Uninstall-BobrCamVirtualCamera.ps1') `
    -Destination $installDirectory -Force

& icacls.exe $framesDirectory /grant '*S-1-5-32-545:(OI)(CI)M' /T /C | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "Could not grant BobrCam frame-directory access."
}

$comHost = Join-Path $installDirectory 'BobrCam.VirtualCameraSource.comhost.dll'
$registrar = Join-Path $installDirectory 'BobrCam.VirtualCamera.exe'
$regsvr = Join-Path $env:WINDIR 'System32\regsvr32.exe'
$registration = Start-Process -FilePath $regsvr -ArgumentList '/s', $comHost -Verb RunAs -Wait -PassThru
if ($registration.ExitCode -ne 0) {
    throw "COM registration failed with exit code $($registration.ExitCode)."
}

$arguments = @('--install')
if ($Quiet) {
    $arguments += '--quiet'
}
$cameraInstall = Start-Process -FilePath $registrar -ArgumentList $arguments -Wait -PassThru
if ($cameraInstall.ExitCode -ne 0) {
    throw "Virtual camera registration failed with exit code $($cameraInstall.ExitCode)."
}

$stateFile = Join-Path $installRoot 'current.txt'
Set-Content -LiteralPath $stateFile -Value $installDirectory -Encoding UTF8

$uninstallScript = Join-Path $installDirectory 'Uninstall-BobrCamVirtualCamera.ps1'
$uninstallCommand = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$uninstallScript`""
$quietUninstallCommand = "$uninstallCommand -Quiet"
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\BobrCamVirtualCamera'
New-Item -Path $uninstallKey -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name DisplayName -Value 'BobrCam Virtual Camera' -PropertyType String -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name DisplayVersion -Value '1.0.0' -PropertyType String -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name Publisher -Value 'BobrCam' -PropertyType String -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name InstallLocation -Value $installDirectory -PropertyType String -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name UninstallString -Value $uninstallCommand -PropertyType String -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name QuietUninstallString -Value $quietUninstallCommand -PropertyType String -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name NoModify -Value 1 -PropertyType DWord -Force | Out-Null
New-ItemProperty -Path $uninstallKey -Name NoRepair -Value 1 -PropertyType DWord -Force | Out-Null

Write-Output "BobrCam virtual camera installed from $installDirectory"
Write-Output "CLSID: $cameraClsid"
