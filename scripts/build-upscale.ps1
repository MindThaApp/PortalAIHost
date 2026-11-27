<#
.SYNOPSIS
  Builds the upscale helper (portal-upscale.exe) from source into engine\upscale\ for the host to start.
.DESCRIPTION
  Publishes src\Portal.AiHost.UpscaleHelper self-contained for win-x64 (the model runtime, Microsoft.ML.OnnxRuntime.DirectML: MIT licence, comes from
  its NuGet package) into a temp folder, copies it to engine\upscale.new, checks the exe is there, then swaps:
  engine\upscale becomes engine\upscale.old, engine\upscale.new becomes engine\upscale, and engine\upscale.old is removed only when it
  carries the .engine-fetch marker this script writes. Any other existing engine\upscale, and any junction or other reparse
  point, is left alone and reported. Models are not part of the helper: the host keeps them in its own model folders.
#>
[CmdletBinding()]
param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$engineRoot = Join-Path $repo 'engine'
$dest = Join-Path $engineRoot 'upscale'
$new = Join-Path $engineRoot 'upscale.new'
$old = Join-Path $engineRoot 'upscale.old'
$marker = '.engine-fetch'

function Test-Reparse([string]$path) {
    return (Test-Path -LiteralPath $path) -and ((Get-Item -LiteralPath $path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)
}

foreach ($p in @($engineRoot, $dest, $new, $old)) {
    if (Test-Reparse $p) { throw "$p is a junction or link; refusing to continue." }
}
if ((Test-Path -LiteralPath $new) -or (Test-Path -LiteralPath $old)) {
    throw "engine\upscale.new or engine\upscale.old is left over from an earlier run; check and remove it by hand."
}
if ((Test-Path -LiteralPath $dest) -and (Get-ChildItem -LiteralPath $dest -Force | Select-Object -First 1) -and -not (Test-Path -LiteralPath (Join-Path $dest $marker))) {
    throw "engine\upscale exists and was not made by a script (no $marker marker); leaving it alone."
}

$work = Join-Path ([IO.Path]::GetTempPath()) ("portalaihost-upscalebuild-" + [guid]::NewGuid().ToString('N'))
$swapped = $false
try {
    $proj = Join-Path $repo 'src\Portal.AiHost.UpscaleHelper\Portal.AiHost.UpscaleHelper.csproj'
    & dotnet publish $proj -c $Configuration -r win-x64 --self-contained true -p:PublishSingleFile=false -p:DebugType=none -o $work -v q
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }
    if (-not (Test-Path (Join-Path $work 'portal-upscale.exe'))) { throw 'The publish has no portal-upscale.exe.' }

    New-Item -ItemType Directory -Path $new | Out-Null
    Copy-Item (Join-Path $work '*') $new -Recurse -Force
    Copy-Item (Join-Path $repo 'docs\THIRD-PARTY-NOTICES.md') (Join-Path $new 'THIRD-PARTY-NOTICES.md') -Force
    Set-Content -Path (Join-Path $new $marker) -Value 'built-from-source' -NoNewline
    if (-not (Test-Path (Join-Path $new 'portal-upscale.exe'))) { throw 'engine\upscale.new has no portal-upscale.exe.' }

    # Swap: the old folder is renamed first and removed only after the new one is in place.
    if (Test-Path -LiteralPath $dest) { Rename-Item -LiteralPath $dest -NewName 'upscale.old' }
    try { Rename-Item -LiteralPath $new -NewName 'upscale'; $swapped = $true }
    catch {
        if (Test-Path -LiteralPath $old) { Rename-Item -LiteralPath $old -NewName 'upscale' }
        throw
    }
    if ((Test-Path -LiteralPath $old) -and (Test-Path -LiteralPath (Join-Path $old $marker))) {
        Remove-Item -LiteralPath $old -Recurse -Force
    }
    elseif (Test-Path -LiteralPath $old) { Write-Warning 'engine\upscale.old has no marker and was kept.' }
    Write-Host "Upscale helper written to $dest"
}
finally {
    if (-not $swapped -and (Test-Path -LiteralPath $new)) { Remove-Item -LiteralPath $new -Recurse -Force -ErrorAction SilentlyContinue }
    if (Test-Path $work) { Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue }
}
