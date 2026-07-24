param(
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'
$installRoot = Join-Path $env:ProgramData 'BobrCam\VirtualCamera'
$stateFile = Join-Path $installRoot 'current.txt'
$currentDirectory = if (Test-Path -LiteralPath $stateFile) {
    (Get-Content -LiteralPath $stateFile -Raw).Trim()
} else {
    $installRoot
}

$registrar = Join-Path $currentDirectory 'BobrCam.VirtualCamera.exe'
$comHost = Join-Path $currentDirectory 'BobrCam.VirtualCameraSource.comhost.dll'

if (Test-Path -LiteralPath $registrar) {
    $arguments = @('--uninstall')
    if ($Quiet) {
        $arguments += '--quiet'
    }
    $cameraRemoval = Start-Process -FilePath $registrar -ArgumentList $arguments -Wait -PassThru
    if ($cameraRemoval.ExitCode -ne 0) {
        throw "Virtual camera removal failed with exit code $($cameraRemoval.ExitCode)."
    }
}

if (Test-Path -LiteralPath $comHost) {
    $regsvr = Join-Path $env:WINDIR 'System32\regsvr32.exe'
    $unregistration = Start-Process -FilePath $regsvr -ArgumentList '/s', '/u', $comHost -Verb RunAs -Wait -PassThru
    if ($unregistration.ExitCode -ne 0) {
        throw "COM unregistration failed with exit code $($unregistration.ExitCode)."
    }
}

$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\BobrCamVirtualCamera'
if (Test-Path -LiteralPath $uninstallKey) {
    Remove-Item -LiteralPath $uninstallKey -Recurse -Force
}
if (Test-Path -LiteralPath $stateFile) {
    Remove-Item -LiteralPath $stateFile -Force
}

Write-Output 'BobrCam virtual camera removed from Windows.'
