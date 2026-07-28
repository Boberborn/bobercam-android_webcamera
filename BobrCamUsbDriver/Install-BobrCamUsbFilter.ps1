param(
    [string]$PackageDirectory = (
        Join-Path $PSScriptRoot "Filter\bin\x64\Release"
    )
)

$ErrorActionPreference = "Stop"
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $arguments = @(
        "-NoProfile",
        "-ExecutionPolicy", "Bypass",
        "-File", "`"$PSCommandPath`"",
        "-PackageDirectory", "`"$PackageDirectory`""
    )
    $process = Start-Process powershell.exe `
        -ArgumentList $arguments `
        -Verb RunAs `
        -Wait `
        -PassThru
    exit $process.ExitCode
}

$inf = Get-ChildItem -LiteralPath $PackageDirectory `
    -Filter "BobrCamUsbFilter.inf" `
    -Recurse |
    Select-Object -First 1
if (-not $inf) {
    throw "BobrCamUsbFilter.inf was not found under $PackageDirectory."
}

& pnputil.exe /add-driver $inf.FullName /install
if ($LASTEXITCODE -ne 0) {
    throw "Driver installation failed with exit code $LASTEXITCODE."
}

Write-Host "BobrCam USB filter installed. Reconnect the phone once."
