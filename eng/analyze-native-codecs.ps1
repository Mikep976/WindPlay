[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$ffmpegPackage = Join-Path $env:USERPROFILE '.nuget\packages\ffmpeginteropx.desktop.ffmpeg\8.1.2'
if (-not (Test-Path (Join-Path $ffmpegPackage 'include\libavcodec\avcodec.h'))) {
    throw 'Restore the locked application dependencies before native analysis.'
}
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$installation = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath | Select-Object -First 1
if (-not $installation) { throw 'MSVC x64 analysis tools are required.' }
$destination = Join-Path $repositoryRoot 'artifacts\security'
New-Item -ItemType Directory -Path $destination -Force | Out-Null
# Compile-only x64 analysis. No DLL is linked, loaded, published, or built for ARM64.
& (Join-Path $PSScriptRoot 'analyze-native-codecs.cmd') $installation $ffmpegPackage $repositoryRoot
if ($LASTEXITCODE -ne 0) { throw "Native static analysis failed: $LASTEXITCODE" }
