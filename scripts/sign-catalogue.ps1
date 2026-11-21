<#
.SYNOPSIS
  Signs catalogue/catalogue.json with the owner's offline ECDSA P-256 key, or creates a new key pair.

.DESCRIPTION
  Sign:     ./scripts/sign-catalogue.ps1 -PrivateKeyPath D:\keys\catalogue-current.pem
  New key:  ./scripts/sign-catalogue.ps1 -NewKeyPair -OutDir D:\keys

  The signature is written next to the catalogue as catalogue.json.sig: base64 of the IEEE P1363 (r||s)
  ECDSA P-256 / SHA-256 signature over the exact file bytes. The private key is only read from the path
  given; it is never copied, and the script refuses key paths inside this repository.
  Requires PowerShell 7 (.NET ECDsa PEM import).
#>
[CmdletBinding(DefaultParameterSetName = 'Sign')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Sign')]
    [string] $PrivateKeyPath,

    [Parameter(ParameterSetName = 'Sign')]
    [string] $CataloguePath = (Join-Path $PSScriptRoot '..\catalogue\catalogue.json'),

    [Parameter(Mandatory, ParameterSetName = 'NewKey')]
    [switch] $NewKeyPair,

    [Parameter(Mandatory, ParameterSetName = 'NewKey')]
    [string] $OutDir
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))

function Assert-OutsideRepo([string] $path) {
    $full = [IO.Path]::GetFullPath($path)
    if ($full.StartsWith($repoRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing a key path inside the repository: $full"
    }
    return $full
}

if ($PSCmdlet.ParameterSetName -eq 'NewKey') {
    $dir = Assert-OutsideRepo $OutDir
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $keyFile = Join-Path $dir "catalogue-key-$stamp.pem"
    if (Test-Path -LiteralPath $keyFile) { throw "Key file already exists: $keyFile" }
    $ecdsa = [Security.Cryptography.ECDsa]::Create([Security.Cryptography.ECCurve+NamedCurves]::nistP256)
    try {
        # CreateNew: never replaces an existing key file.
        $stream = [IO.File]::Open($keyFile, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write)
        try {
            $bytes = [Text.Encoding]::ASCII.GetBytes($ecdsa.ExportPkcs8PrivateKeyPem())
            $stream.Write($bytes, 0, $bytes.Length)
        } finally { $stream.Dispose() }
        $spki = [Convert]::ToBase64String($ecdsa.ExportSubjectPublicKeyInfo())
    } finally { $ecdsa.Dispose() }
    Write-Host "Private key written to $keyFile (keep it offline)."
    Write-Host "Public key (add to CatalogueTrust.PublicKeysSpkiBase64):"
    Write-Output $spki
    return
}

$keyFull = Assert-OutsideRepo $PrivateKeyPath
$catFull = [IO.Path]::GetFullPath($CataloguePath)
$json = [IO.File]::ReadAllBytes($catFull)

# Basic sanity before signing: parseable and has a positive catalogue_version.
$parsed = [Text.Encoding]::UTF8.GetString($json) | ConvertFrom-Json
if (-not ($parsed.catalogue_version -gt 0)) { throw 'catalogue_version must be a positive integer.' }

$ecdsa = [Security.Cryptography.ECDsa]::Create()
try {
    $ecdsa.ImportFromPem([IO.File]::ReadAllText($keyFull))
    if ($ecdsa.KeySize -ne 256) { throw 'Key must be ECDSA P-256.' }
    $sig = $ecdsa.SignData($json, [Security.Cryptography.HashAlgorithmName]::SHA256,
        [Security.Cryptography.DSASignatureFormat]::IeeeP1363FixedFieldConcatenation)
    $spki = [Convert]::ToBase64String($ecdsa.ExportSubjectPublicKeyInfo())
} finally { $ecdsa.Dispose() }

# The .sig file is this script's own output for this catalogue; it is replaced on each signing run.
$sigPath = "$catFull.sig"
[IO.File]::WriteAllText($sigPath, [Convert]::ToBase64String($sig))
Write-Host "Signed catalogue_version $($parsed.catalogue_version) -> $sigPath"
Write-Host "Signing public key: $spki"
