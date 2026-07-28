param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$wdkVersion = "10.0.26100.6584"
$kitVersion = "10.0.26100.0"
$kmdfVersion = "1.33"
$packageRoot = Join-Path $env:USERPROFILE (
    ".nuget\packages\microsoft.windows.wdk.x64\$wdkVersion"
)
$projectDirectory = Join-Path $PSScriptRoot "Filter"
$project = Join-Path $projectDirectory "BobrCamUsbFilter.vcxproj"
$source = Join-Path $projectDirectory "BobrCamUsbFilter.c"
$outputDirectory = Join-Path $projectDirectory (
    "bin\x64\$Configuration"
)
$intermediateDirectory = Join-Path $projectDirectory (
    "obj\x64\$Configuration"
)

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$installationPath = & $vswhere `
    -latest `
    -products * `
    -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
    -property installationPath
if (-not $installationPath) {
    throw "Visual Studio Desktop C++ tools were not found."
}

$msbuild = Join-Path $installationPath "MSBuild\Current\Bin\MSBuild.exe"
& $msbuild $project /t:Restore /m:1
if ($LASTEXITCODE -ne 0) {
    throw "WDK package restore failed with exit code $LASTEXITCODE."
}
if (-not (Test-Path -LiteralPath $packageRoot)) {
    throw "WDK NuGet package $wdkVersion was not restored."
}

New-Item -ItemType Directory -Force -Path `
    $outputDirectory, `
    $intermediateDirectory | Out-Null

$vcvars = Join-Path $installationPath "VC\Auxiliary\Build\vcvars64.bat"
$kitRoot = Join-Path $packageRoot "c"
$kernelIncludes = Join-Path $kitRoot "Include\$kitVersion\km"
$sharedIncludes = Join-Path $kitRoot "Include\$kitVersion\shared"
$wdfIncludes = Join-Path $kitRoot "Include\wdf\kmdf\$kmdfVersion"
$kernelLibraries = Join-Path $kitRoot "Lib\$kitVersion\km\x64"
$wdfLibraries = Join-Path $kitRoot "Lib\wdf\kmdf\x64\$kmdfVersion"
$object = Join-Path $intermediateDirectory "BobrCamUsbFilter.obj"
$driver = Join-Path $outputDirectory "BobrCamUsbFilter.sys"
$symbols = Join-Path $outputDirectory "BobrCamUsbFilter.pdb"
$optimization = if ($Configuration -eq "Release") {
    "/O2 /Oi"
} else {
    "/Od /Zi"
}

$compileCommand = @"
call "$vcvars" >nul && cl.exe /nologo /c /kernel /W4 /WX /wd4324 $optimization /Gy /GS /Zp8 /D_AMD64_ /DAMD64 /D_WIN64 /DWIN64 /D_WIN32_WINNT=0x0A00 /DKMDF_VERSION_MAJOR=1 /DKMDF_VERSION_MINOR=33 /I"$kernelIncludes" /I"$sharedIncludes" /I"$wdfIncludes" /Fo"$object" "$source"
"@
cmd.exe /d /s /c $compileCommand
if ($LASTEXITCODE -ne 0) {
    throw "BobrCam USB filter compilation failed with exit code $LASTEXITCODE."
}

$linkCommand = @"
call "$vcvars" >nul && link.exe /nologo /driver /subsystem:native,10.00 /entry:FxDriverEntry /nodefaultlib /machine:x64 /opt:ref /opt:icf /integritycheck /out:"$driver" /pdb:"$symbols" "$object" "$wdfLibraries\WdfDriverEntry.lib" "$wdfLibraries\WdfLdr.lib" "$kernelLibraries\ntoskrnl.lib" "$kernelLibraries\hal.lib" "$kernelLibraries\wmilib.lib" "$kernelLibraries\usbd.lib" "$kernelLibraries\BufferOverflowK.lib"
"@
cmd.exe /d /s /c $linkCommand
if ($LASTEXITCODE -ne 0) {
    throw "BobrCam USB filter link failed with exit code $LASTEXITCODE."
}

Copy-Item -LiteralPath (
    Join-Path $projectDirectory "BobrCamUsbFilter.inf"
) -Destination $outputDirectory -Force

$infVerifier = Join-Path $kitRoot "tools\$kitVersion\x64\infverif.exe"
& $infVerifier /w (
    Join-Path $outputDirectory "BobrCamUsbFilter.inf"
)
if ($LASTEXITCODE -ne 0) {
    throw "BobrCam USB filter INF validation failed with exit code $LASTEXITCODE."
}

$inf2Cat = Join-Path $kitRoot "bin\$kitVersion\x86\Inf2Cat.exe"
& $inf2Cat /driver:$outputDirectory /os:10_X64
if ($LASTEXITCODE -ne 0) {
    throw "BobrCam USB catalog generation failed with exit code $LASTEXITCODE."
}

Write-Host "BobrCam USB filter package built at $outputDirectory"
