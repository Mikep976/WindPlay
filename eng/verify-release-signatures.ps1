[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $PublishDirectory,
    [Parameter(Mandatory)] [ValidatePattern('^[0-9A-Fa-f]{64}$')] [string] $ExpectedCertificateSha256
)
$ErrorActionPreference = 'Stop'
$required = @('WindPlay.exe', 'WindPlay.dll', 'WindPlay.Protocol.dll', 'WindPlay.Codecs.dll',
    'avcodec-62.dll', 'avutil-60.dll', 'swresample-6.dll')
foreach ($name in $required) {
    $path = Join-Path $PublishDirectory $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing release binary: $name" }
    $signature = Get-AuthenticodeSignature -LiteralPath $path
    if ($signature.Status -ne 'Valid' -or $null -eq $signature.SignerCertificate -or $null -eq $signature.TimeStamperCertificate) {
        throw "Release binary has no valid timestamped publisher signature: $name"
    }
    $actual = $signature.SignerCertificate.GetCertHashString([System.Security.Cryptography.HashAlgorithmName]::SHA256)
    if ($actual -ne $ExpectedCertificateSha256) { throw "Unexpected publisher certificate: $name" }
}
Write-Output 'Required release binaries have valid timestamped signatures from the configured publisher.'
