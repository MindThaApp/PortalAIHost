<#
.SYNOPSIS
  Downloads the pinned ffmpeg (LGPL shared build) into engine/ffmpeg/ and verifies SHA-256.
.DESCRIPTION
  Reads engine/ffmpeg.lock.json (tag + asset + SHA-256 + size). Downloads only from the official BtbN/FFmpeg-Builds
  GitHub releases. Pinned: autobuild-2026-11-27-12-42, ffmpeg-n8.1.3-41-ga8029b6528-win64-lgpl-shared-8.1.zip,
  SHA-256 bb63313643f91add6ab97f546c216b07378476f4953f983c9e1c8875f4ff9558 (81544084 bytes). Refuses to run while
  the lock file holds placeholders.
  Only bin\ffmpeg.exe, bin\ffprobe.exe, the DLLs of bin\ and the licence text are copied (no ffplay, headers or docs).
  The folder is replaced only when it carries the .engine-fetch marker this script writes; any other folder is left
  alone and reported. The new build is complete in a temp folder before the old one is touched.
.PARAMETER FromZip
  Use this local copy of the pinned zip instead of downloading it. The size and SHA-256 are still checked.
.PARAMETER Force
  Re-install even if the folder already holds the pinned build.
#>
[CmdletBinding()]
param(
    [string]$FromZip,
    [switch]$Force
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$engineRoot = Join-Path $repoRoot 'engine'
$dest = Join-Path $engineRoot 'ffmpeg'
$lock = Get-Content -Raw (Join-Path $engineRoot 'ffmpeg.lock.json') | ConvertFrom-Json
if ($lock.source -ne 'https://github.com/BtbN/FFmpeg-Builds/releases/download') {
    throw "Lock file source is not the official BtbN/FFmpeg-Builds release URL."
}
if ("$($lock.tag)" -match 'PLACEHOLDER' -or [string]::IsNullOrWhiteSpace("$($lock.tag)")) {
    throw "engine/ffmpeg.lock.json still holds a placeholder tag. Pin a real release (tag, asset, sha256, size) first."
}
$b = $lock.builds | Where-Object { $_.flavour -eq 'x64' } | Select-Object -First 1
if (-not $b -or "$($b.asset)$($b.sha256)" -match 'PLACEHOLDER' -or "$($b.sha256)" -notmatch '^[0-9a-fA-F]{64}$' -or [long]$b.size -le 0) {
    throw "engine/ffmpeg.lock.json holds a placeholder asset, SHA-256 or size. Nothing was installed."
}
$marker = '.engine-fetch'
$markerPath = Join-Path $dest $marker
$markerText = "$($lock.tag) $($b.sha256)"
if ((Test-Path $markerPath) -and -not $Force -and (Get-Content -Raw $markerPath).Trim() -eq $markerText) {
    Write-Host "ffmpeg: up to date ($($lock.tag))"
    return
}
if ((Test-Path $dest) -and -not (Test-Path $markerPath)) {
    Write-Warning "ffmpeg: $dest exists but was not created by this script; skipping."
    return
}
$work = Join-Path ([IO.Path]::GetTempPath()) ("portalaihost-ffmpeg-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
try {
    if ($FromZip) {
        $zip = (Resolve-Path -LiteralPath $FromZip).Path
        Write-Host "ffmpeg: using local copy $zip"
    }
    else {
        $zip = Join-Path $work $b.asset
        Write-Host "ffmpeg: downloading $($b.asset)"
        Invoke-WebRequest -Uri "$($lock.source)/$($lock.tag)/$($b.asset)" -OutFile $zip -UseBasicParsing
    }
    if ((Get-Item -LiteralPath $zip).Length -ne [long]$b.size) {
        throw "$($b.asset): size mismatch (got $((Get-Item -LiteralPath $zip).Length), expected $($b.size)). Nothing was installed."
    }
    $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $zip).Hash.ToLowerInvariant()
    if ($hash -ne $b.sha256.ToLowerInvariant()) {
        throw "$($b.asset): SHA-256 mismatch (got $hash, expected $($b.sha256)). Nothing was installed."
    }
    $extract = Join-Path $work 'extract'
    Expand-Archive -LiteralPath $zip -DestinationPath $extract
    $bin = Get-ChildItem -LiteralPath $extract -Directory | ForEach-Object { Join-Path $_.FullName 'bin' } |
        Where-Object { Test-Path (Join-Path $_ 'ffmpeg.exe') } | Select-Object -First 1
    if (-not $bin) { throw "$($b.asset): bin\ffmpeg.exe not found in the archive." }
    $root = Split-Path -Parent $bin
    $stage = Join-Path $work 'stage'
    New-Item -ItemType Directory -Path $stage | Out-Null
    foreach ($n in @('ffmpeg.exe', 'ffprobe.exe')) {
        if (-not (Test-Path (Join-Path $bin $n))) { throw "$($b.asset): bin\$n not found in the archive." }
        Copy-Item -LiteralPath (Join-Path $bin $n) -Destination $stage
    }
    Get-ChildItem -LiteralPath $bin -Filter '*.dll' | Copy-Item -Destination $stage
    $licence = Get-ChildItem -LiteralPath $root -File | Where-Object { $_.Name -like 'LICENSE*' } | Select-Object -First 1
    if (-not $licence) { throw "$($b.asset): no LICENSE file in the archive." }
    Copy-Item -LiteralPath $licence.FullName -Destination (Join-Path $stage 'LICENSE.txt')
    Set-Content -Path (Join-Path $stage $marker) -Value $markerText
    New-Item -ItemType Directory -Force -Path $engineRoot | Out-Null
    # Replace only a folder this script created (it has the marker). The new build is complete in the stage first.
    if (Test-Path $dest) { Remove-Item -Recurse -Force -LiteralPath $dest }
    Move-Item -LiteralPath $stage -Destination $dest
    Write-Host "ffmpeg: installed to $dest"
}
finally {
    if ((Test-Path $work) -and $work.Contains('portalaihost-ffmpeg-')) {
        Remove-Item -Recurse -Force -LiteralPath $work
    }
}
