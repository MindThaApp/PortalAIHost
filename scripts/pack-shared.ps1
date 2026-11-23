<#
Packs the shared libraries (Contracts, Models, Models.Windows, Engine) into builds\packages as .nupkg files that
other Portal apps can use through a local-folder NuGet feed.
Usage: pwsh scripts\pack-shared.ps1 [-Configuration Release]
#>
param([string]$Configuration = 'Release')

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'builds\packages'
New-Item -ItemType Directory -Force $out | Out-Null

$free = (Get-PSDrive -Name ((Get-Item $root).PSDrive.Name)).Free
if ($free -lt 1.5GB) { throw "Less than 1.5 GB free on the drive; stopping." }

$projects = 'Portal.AiHost.Contracts', 'Portal.AiHost.Models', 'Portal.AiHost.Models.Windows', 'Portal.AiHost.Engine'
foreach ($p in $projects) {
    dotnet pack (Join-Path $root "src\$p\$p.csproj") -c $Configuration -o $out --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "pack failed for $p" }
}

Get-ChildItem $out -Filter *.nupkg | Sort-Object Name |
    ForEach-Object { '{0}  {1:N0} KB' -f $_.Name, ($_.Length / 1KB) }
