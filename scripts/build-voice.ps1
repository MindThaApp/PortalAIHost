<#
.SYNOPSIS
  Builds the voice helper (portal-voice-server.exe) from source into engine\voice\ for the host to start.
.DESCRIPTION
  Publishes src\Portal.AiHost.VoiceServer self-contained for win-x64 (the speech runtime, org.k2fsa.sherpa.onnx, comes from
  its NuGet package: Apache-2.0). The output folder is replaced only when it carries the .engine-fetch marker this script
  writes (or does not exist yet); any other folder is left alone and reported. Models are not part of the helper: the host
  keeps them in its own voice-models folder.
#>
[CmdletBinding()]
param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$dest = Join-Path $repo 'engine\voice'
$marker = Join-Path $dest '.engine-fetch'
if ((Test-Path $dest) -and (Get-ChildItem $dest -Force | Select-Object -First 1) -and -not (Test-Path $marker)) {
    throw "engine\voice exists and was not made by a script (no .engine-fetch marker); leaving it alone."
}

$work = Join-Path ([IO.Path]::GetTempPath()) ("portalaihost-voicebuild-" + [guid]::NewGuid().ToString('N'))
try {
    $proj = Join-Path $repo 'src\Portal.AiHost.VoiceServer\Portal.AiHost.VoiceServer.csproj'
    & dotnet publish $proj -c $Configuration -r win-x64 --self-contained true -p:PublishSingleFile=false -p:DebugType=none -o $work -v q
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }
    if (-not (Test-Path (Join-Path $work 'portal-voice-server.exe'))) { throw 'The publish has no portal-voice-server.exe.' }

    # Replace only a folder this script made (marker present) and only its contents.
    if (Test-Path $marker) { Get-ChildItem $dest -Force | Remove-Item -Recurse -Force }
    New-Item -ItemType Directory -Force $dest | Out-Null
    Copy-Item (Join-Path $work '*') $dest -Recurse -Force
    Copy-Item (Join-Path $repo 'docs\THIRD-PARTY-NOTICES.md') (Join-Path $dest 'THIRD-PARTY-NOTICES.md') -Force
    Set-Content -Path $marker -Value 'built-from-source' -NoNewline
    Write-Host "Voice helper written to $dest"
}
finally {
    if (Test-Path $work) { Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue }
}
