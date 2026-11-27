<#
.SYNOPSIS
  Downloads the pinned voice helper into engine/voice/ and the pinned speech models into a models folder, and verifies SHA-256.
.DESCRIPTION
  Reads engine/voice.lock.json. Refuses to run while any tag, asset, SHA-256 or size is still a placeholder: nothing is
  downloaded until real values are pinned (owner approval needed). A target folder is replaced only when it carries the
  .engine-fetch marker this script writes; any other folder is left alone and reported. The helper archive must hold
  portal-voice-server.exe at its root.
.PARAMETER ModelRoot
  Folder that receives the model folders "cloning" and "fixed" (the host's voice-models folder). Required with -Models.
.PARAMETER Helper
  Fetch the voice helper (default).
.PARAMETER Models
  Fetch the speech models into -ModelRoot.
.PARAMETER Force
  Re-download even if the folder already holds the pinned build.
#>
[CmdletBinding()]
param(
    [string]$ModelRoot = '',
    [switch]$Models,
    [switch]$Helper = $true,
    [switch]$Force
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$engineRoot = Join-Path $repoRoot 'engine'
$voiceRoot = Join-Path $engineRoot 'voice'
$lock = Get-Content -Raw (Join-Path $engineRoot 'voice.lock.json') | ConvertFrom-Json
$marker = '.engine-fetch'

function Test-Pinned($asset, $sha, $size) {
    return ("$asset$sha" -notmatch 'PLACEHOLDER') -and ("$sha" -match '^[0-9a-fA-F]{64}$') -and ([long]$size -gt 0)
}

# Refuse first, before anything is created or downloaded.
if ($Helper) {
    if ("$($lock.helper.tag)" -match 'PLACEHOLDER' -or [string]::IsNullOrWhiteSpace("$($lock.helper.tag)")) {
        throw "engine/voice.lock.json still holds a placeholder helper tag. Pin a real release (tag, asset, sha256, size) first."
    }
    if ($lock.helper.source -ne 'https://github.com/MindThaApp/PortalAIHost/releases/download') {
        throw "Lock file helper source is not the official release URL."
    }
    foreach ($b in $lock.helper.builds) {
        if (-not (Test-Pinned $b.asset $b.sha256 $b.size)) {
            throw "$($b.flavour): engine/voice.lock.json holds a placeholder asset, SHA-256 or size. Nothing was downloaded."
        }
    }
}
if ($Models) {
    if ([string]::IsNullOrWhiteSpace($ModelRoot)) { throw "-ModelRoot is required with -Models." }
    foreach ($m in $lock.models) {
        if (-not (Test-Pinned $m.asset $m.sha256 $m.size)) {
            throw "$($m.folder): engine/voice.lock.json holds a placeholder asset, SHA-256 or size. Nothing was downloaded."
        }
    }
}

$work = Join-Path ([IO.Path]::GetTempPath()) ("portalaihost-voice-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
function Install-Archive($url, $asset, $sha, $size, $dest, $label, $requiredExe) {
    $markerPath = Join-Path $dest $marker
    if ((Test-Path $markerPath) -and -not $Force) {
        if ((Get-Content -Raw $markerPath).Trim() -eq "$asset $sha") { Write-Host "${label}: up to date"; return }
    }
    if ((Test-Path $dest) -and -not (Test-Path $markerPath)) {
        Write-Warning "${label}: $dest exists but was not created by this script; skipping."
        return
    }
    $zip = Join-Path $work $asset
    Write-Host "${label}: downloading $asset"
    Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing
    if ((Get-Item $zip).Length -ne [long]$size) { throw "${asset}: size mismatch. Nothing was installed." }
    $hash = (Get-FileHash -Algorithm SHA256 $zip).Hash.ToLowerInvariant()
    if ($hash -ne $sha.ToLowerInvariant()) { throw "${asset}: SHA-256 mismatch (got $hash, expected $sha). Nothing was installed." }
    $stage = Join-Path $work ("stage-" + [guid]::NewGuid().ToString('N'))
    Expand-Archive -Path $zip -DestinationPath $stage
    if ($requiredExe -and -not (Test-Path (Join-Path $stage $requiredExe))) { throw "${asset}: $requiredExe not found at the archive root." }
    Set-Content -Path (Join-Path $stage $marker) -Value "$asset $sha"
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $dest) | Out-Null
    # Replace only a folder this script created (it has the marker). The new build is complete in the stage first.
    if (Test-Path $dest) { Remove-Item -Recurse -Force -LiteralPath $dest }
    Move-Item -LiteralPath $stage -Destination $dest
    Write-Host "${label}: installed to $dest"
}
try {
    if ($Helper) {
        foreach ($b in $lock.helper.builds) {
            $url = "$($lock.helper.source)/$($lock.helper.tag)/$($b.asset)"
            Install-Archive $url $b.asset $b.sha256 $b.size $voiceRoot "voice" 'portal-voice-server.exe'
        }
    }
    if ($Models) {
        foreach ($m in $lock.models) {
            $url = "$($m.source)/$($m.asset)"
            Install-Archive $url $m.asset $m.sha256 $m.size (Join-Path $ModelRoot $m.folder) "model/$($m.folder)" $null
        }
    }
}
finally {
    if ((Test-Path $work) -and $work.Contains('portalaihost-voice-')) {
        Remove-Item -Recurse -Force -LiteralPath $work
    }
}
