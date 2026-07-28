$ErrorActionPreference = "Stop"
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $arguments = @(
        "-NoProfile",
        "-ExecutionPolicy", "Bypass",
        "-File", "`"$PSCommandPath`""
    )
    Start-Process powershell.exe -ArgumentList $arguments -Verb RunAs -Wait
    exit $LASTEXITCODE
}

$drivers = Get-WindowsDriver -Online |
    Where-Object {
        $_.ProviderName -eq "BobrCam" -and
        $_.ClassName -eq "Extension"
    }
foreach ($driver in $drivers) {
    & pnputil.exe /delete-driver $driver.Driver /uninstall /force
    if ($LASTEXITCODE -ne 0) {
        throw "Could not remove $($driver.Driver)."
    }
}

Write-Host "BobrCam USB filter removed."
