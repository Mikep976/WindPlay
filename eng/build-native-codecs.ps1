[CmdletBinding()]
param(
    [ValidateSet('Release')]
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$packageVersion = '8.1.2'
$ffmpegPackage = Join-Path $env:USERPROFILE ".nuget\packages\ffmpeginteropx.desktop.ffmpeg\$packageVersion"
if (-not (Test-Path $ffmpegPackage)) {
    throw "FFmpeg package $packageVersion is not restored. Run 'dotnet restore WindPlay.slnx' first."
}

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path $vswhere)) {
    throw 'Visual Studio Installer discovery tool was not found.'
}

$msbuild = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.ARM64 -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) {
    throw 'Visual Studio with the MSVC ARM64 build tools is required.'
}

$project = Join-Path $repositoryRoot 'native\WindPlay.Codecs\WindPlay.Codecs.vcxproj'
& $msbuild $project /m /restore:false /p:Configuration=$Configuration /p:Platform=ARM64 "/p:FFmpegPackageDir=$ffmpegPackage"
if ($LASTEXITCODE -ne 0) {
    throw "Native codec build failed with exit code $LASTEXITCODE."
}
