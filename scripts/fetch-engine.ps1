<#
.SYNOPSIS
  Downloads the pinned llama.cpp Windows builds into engine/<flavour>/ and verifies SHA-256.
.DESCRIPTION
  Reads engine/engine.lock.json (tag + SHA-256 per asset). Downloads only from the official
  ggml-org/llama.cpp GitHub releases. A flavour folder is replaced only when it carries the
  .engine-fetch marker this script writes; any other folder is left alone and reported.
.PARAMETER Flavour
  Which builds to fetch: vulkan, cpu, cpu-arm64. Default: all.
.PARAMETER Force
  Re-download even if the folder already holds the pinned tag.
#>
[CmdletBinding()]
param(
    [string[]]$Flavour = @(),
    [switch]$Force
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$engineRoot = Join-Path $repoRoot 'engine'
$lock = Get-Content -Raw (Join-Path $engineRoot 'engine.lock.json') | ConvertFrom-Json
if ($lock.source -ne 'https://github.com/ggml-org/llama.cpp/releases/download') {
    throw "Lock file source is not the official llama.cpp release URL."
}
$marker = '.engine-fetch'
$work = Join-Path ([IO.Path]::GetTempPath()) ("portalaihost-fetch-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
try {
    foreach ($b in $lock.builds) {
        if ($Flavour.Count -gt 0 -and $Flavour -notcontains $b.flavour) { continue }
        $dest = Join-Path $engineRoot $b.flavour
        $markerPath = Join-Path $dest $marker
        if ((Test-Path $markerPath) -and -not $Force) {
            if ((Get-Content -Raw $markerPath).Trim() -eq "$($lock.tag) $($b.sha256)") {
                Write-Host "$($b.flavour): up to date ($($lock.tag))"
                continue
            }
        }
        if ((Test-Path $dest) -and -not (Test-Path $markerPath)) {
            Write-Warning "$($b.flavour): $dest exists but was not created by this script; skipping."
            continue
        }
        $url = "$($lock.source)/$($lock.tag)/$($b.asset)"
        $zip = Join-Path $work $b.asset
        Write-Host "$($b.flavour): downloading $($b.asset)"
        Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing
        $hash = (Get-FileHash -Algorithm SHA256 $zip).Hash.ToLowerInvariant()
        if ($hash -ne $b.sha256.ToLowerInvariant()) {
            throw "$($b.asset): SHA-256 mismatch (got $hash, expected $($b.sha256)). Nothing was installed."
        }
        $stage = Join-Path $work ("stage-" + $b.flavour)
        Expand-Archive -Path $zip -DestinationPath $stage
        if (-not (Test-Path (Join-Path $stage 'llama-server.exe'))) {
            throw "$($b.asset): llama-server.exe not found at the archive root."
        }
        Set-Content -Path (Join-Path $stage $marker) -Value "$($lock.tag) $($b.sha256)"
        # Replace only a folder this script created (it has the marker). Copy first, then swap.
        if (Test-Path $dest) { Remove-Item -Recurse -Force -LiteralPath $dest }
        Move-Item -LiteralPath $stage -Destination $dest
        Write-Host "$($b.flavour): installed to $dest"
    }
}
finally {
    if ((Test-Path $work) -and $work.Contains('portalaihost-fetch-')) {
        Remove-Item -Recurse -Force -LiteralPath $work
    }
}
