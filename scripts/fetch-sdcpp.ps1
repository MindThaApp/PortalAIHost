<#
.SYNOPSIS
  Downloads the pinned stable-diffusion.cpp Windows build into engine/sdcpp/<flavour>/ and verifies SHA-256.
.DESCRIPTION
  Reads engine/sdcpp.lock.json (tag + asset + SHA-256 + size per build). Downloads only from the official
  leejet/stable-diffusion.cpp GitHub releases. Pinned: master-1142-908965d,
  sd-master-908965d-bin-win-vulkan-x64.zip, SHA-256 fc8eb570e1bd7b71eb7010b89f148f20bd349b471a96f40aeabb6e8ae5b394a9
  (31076374 bytes). Refuses to run while the lock file holds placeholders.
  A flavour folder is replaced only when it carries the .engine-fetch marker this script writes; any other folder
  is left alone and reported. The archive must hold sd-cli.exe (current releases) or sd.exe (older ones) at its root.
.PARAMETER Flavour
  Which builds to fetch. Default and only pinned build: vulkan (it runs processor-only jobs too, with --backend cpu).
.PARAMETER Force
  Re-download even if the folder already holds the pinned tag.
#>
[CmdletBinding()]
param(
    [string[]]$Flavour = @('vulkan'),
    [switch]$Force
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$engineRoot = Join-Path $repoRoot 'engine'
$sdRoot = Join-Path $engineRoot 'sdcpp'
$lock = Get-Content -Raw (Join-Path $engineRoot 'sdcpp.lock.json') | ConvertFrom-Json
if ($lock.source -ne 'https://github.com/leejet/stable-diffusion.cpp/releases/download') {
    throw "Lock file source is not the official stable-diffusion.cpp release URL."
}
if ("$($lock.tag)" -match 'PLACEHOLDER' -or [string]::IsNullOrWhiteSpace("$($lock.tag)")) {
    throw "engine/sdcpp.lock.json still holds a placeholder tag. Pin a real release (tag, asset, sha256, size) first."
}
$marker = '.engine-fetch'
$exeNames = @('sd-cli.exe', 'sd.exe')
$work = Join-Path ([IO.Path]::GetTempPath()) ("portalaihost-sdcpp-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
try {
    foreach ($b in $lock.builds) {
        if ($Flavour -notcontains $b.flavour) { continue }
        if ("$($b.asset)$($b.sha256)" -match 'PLACEHOLDER' -or "$($b.sha256)" -notmatch '^[0-9a-fA-F]{64}$' -or [long]$b.size -le 0) {
            throw "$($b.flavour): engine/sdcpp.lock.json holds a placeholder asset, SHA-256 or size. Nothing was downloaded."
        }
        $dest = Join-Path $sdRoot $b.flavour
        $markerPath = Join-Path $dest $marker
        if ((Test-Path $markerPath) -and -not $Force) {
            if ((Get-Content -Raw $markerPath).Trim() -eq "$($lock.tag) $($b.sha256)") {
                Write-Host "sdcpp/$($b.flavour): up to date ($($lock.tag))"
                continue
            }
        }
        if ((Test-Path $dest) -and -not (Test-Path $markerPath)) {
            Write-Warning "sdcpp/$($b.flavour): $dest exists but was not created by this script; skipping."
            continue
        }
        $url = "$($lock.source)/$($lock.tag)/$($b.asset)"
        $zip = Join-Path $work $b.asset
        Write-Host "sdcpp/$($b.flavour): downloading $($b.asset)"
        Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing
        if ((Get-Item $zip).Length -ne [long]$b.size) {
            throw "$($b.asset): size mismatch (got $((Get-Item $zip).Length), expected $($b.size)). Nothing was installed."
        }
        $hash = (Get-FileHash -Algorithm SHA256 $zip).Hash.ToLowerInvariant()
        if ($hash -ne $b.sha256.ToLowerInvariant()) {
            throw "$($b.asset): SHA-256 mismatch (got $hash, expected $($b.sha256)). Nothing was installed."
        }
        $stage = Join-Path $work ("stage-" + $b.flavour)
        Expand-Archive -Path $zip -DestinationPath $stage
        $exe = $exeNames | Where-Object { Test-Path (Join-Path $stage $_) } | Select-Object -First 1
        if (-not $exe) {
            throw "$($b.asset): neither sd-cli.exe nor sd.exe at the archive root."
        }
        Set-Content -Path (Join-Path $stage $marker) -Value "$($lock.tag) $($b.sha256)"
        New-Item -ItemType Directory -Force -Path $sdRoot | Out-Null
        # Replace only a folder this script created (it has the marker). The new build is complete in the stage first.
        if (Test-Path $dest) { Remove-Item -Recurse -Force -LiteralPath $dest }
        Move-Item -LiteralPath $stage -Destination $dest
        Write-Host "sdcpp/$($b.flavour): installed $exe to $dest"
    }
}
finally {
    if ((Test-Path $work) -and $work.Contains('portalaihost-sdcpp-')) {
        Remove-Item -Recurse -Force -LiteralPath $work
    }
}
