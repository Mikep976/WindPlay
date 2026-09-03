[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $PublishDirectory
)

$ErrorActionPreference = 'Stop'
$publishPath = (Resolve-Path $PublishDirectory).Path
$expectedNativeFiles = @(
    'WindPlay.exe',
    'WindPlay.Codecs.dll',
    'avcodec-62.dll',
    'avutil-60.dll',
    'swresample-6.dll'
)

function Get-PeMachine {
    param([Parameter(Mandatory)][string] $Path)

    $stream = [System.IO.File]::OpenRead($Path)
    try {
        $reader = [System.IO.BinaryReader]::new($stream)
        if ($reader.ReadUInt16() -ne 0x5A4D) {
            throw "'$Path' is not a PE image."
        }

        $stream.Position = 0x3C
        $peOffset = $reader.ReadUInt32()
        if ($peOffset -gt ($stream.Length - 6)) {
            throw "'$Path' contains an invalid PE header offset."
        }

        $stream.Position = $peOffset
        if ($reader.ReadUInt32() -ne 0x00004550) {
            throw "'$Path' does not contain a valid PE signature."
        }

        return $reader.ReadUInt16()
    }
    finally {
        $stream.Dispose()
    }
}

foreach ($fileName in $expectedNativeFiles) {
    $filePath = Join-Path $publishPath $fileName
    if (-not (Test-Path $filePath -PathType Leaf)) {
        throw "Required ARM64 file '$fileName' is missing from the published app."
    }

    $machine = Get-PeMachine $filePath
    if ($machine -ne 0xAA64) {
        throw "'$fileName' has PE machine 0x$($machine.ToString('X4')); expected ARM64 (0xAA64)."
    }
}

$unexpectedIntelCodecs = Get-ChildItem $publishPath -File |
    Where-Object { $_.Name -match '^LibALAC(32|64)?\.dll$' }
if ($unexpectedIntelCodecs) {
    throw "Legacy Intel codec binaries were included: $($unexpectedIntelCodecs.Name -join ', ')."
}

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class WindPlayCodecSmokeTypes
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate IntPtr CreateDecoder(int format, int sampleRate, int channels, int bitDepth, int frameLength);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void DestroyDecoder(IntPtr decoder);
}
'@

$libraryPath = Join-Path $publishPath 'WindPlay.Codecs.dll'
$library = [System.Runtime.InteropServices.NativeLibrary]::Load($libraryPath)
try {
    $createPointer = [System.Runtime.InteropServices.NativeLibrary]::GetExport($library, 'windplay_audio_decoder_create')
    $destroyPointer = [System.Runtime.InteropServices.NativeLibrary]::GetExport($library, 'windplay_audio_decoder_destroy')
    $create = [System.Runtime.InteropServices.Marshal]::GetDelegateForFunctionPointer(
        $createPointer,
        [WindPlayCodecSmokeTypes+CreateDecoder])
    $destroy = [System.Runtime.InteropServices.Marshal]::GetDelegateForFunctionPointer(
        $destroyPointer,
        [WindPlayCodecSmokeTypes+DestroyDecoder])

    $profiles = @(
        @(0x00040000, 352),
        @(0x00400000, 1024),
        @(0x01000000, 480)
    )

    foreach ($profile in $profiles) {
        $decoder = $create.Invoke($profile[0], 44_100, 2, 16, $profile[1])
        if ($decoder -eq [IntPtr]::Zero) {
            throw "Native decoder initialization failed for format 0x$($profile[0].ToString('X8'))."
        }

        $destroy.Invoke($decoder)
    }

    $unexpectedDecoder = $create.Invoke(0x00040000, 48_000, 2, 16, 352)
    if ($unexpectedDecoder -ne [IntPtr]::Zero) {
        $destroy.Invoke($unexpectedDecoder)
        throw 'Native decoder accepted a profile outside the advertised AirPlay boundary.'
    }
}
finally {
    [System.Runtime.InteropServices.NativeLibrary]::Free($library)
}

Write-Host 'Published ARM64 codecs passed architecture and initialization checks.'
