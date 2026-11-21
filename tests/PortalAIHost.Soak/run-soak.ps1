<#
.SYNOPSIS
  Builds what the soak needs and starts the soak driver DETACHED; the result goes to a file.
.DESCRIPTION
  Fake variant (default, about 5 minutes): the host runs against a stub llama-server (StubChild published as
  llama-server.exe). Real variant (-Real, 60 minutes): pass -EngineDir (the fetched engine\cpu or engine\vulkan
  folder) and -Model (a small chat .gguf). The host copy, data root and models live in a fresh folder under %TEMP%;
  nothing of the owner's data or builds is used.
  Check the result with:  Get-Content <ResultFile> -Tail 1
.PARAMETER Minutes   Override the duration.
.PARAMETER Service   A published NativeAOT PortalAIHost.Service.exe; default: publish one for win-x64.
#>
[CmdletBinding()]
param(
    [switch]$Real,
    [string]$EngineDir,
    [string]$Model,
    [string]$EmbedModel,
    [double]$Minutes,
    [string]$Service,
    [string]$ResultFile
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$tmp = Join-Path ([IO.Path]::GetTempPath()) ('portalaihost-soakrun-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $tmp | Out-Null
$vswhereDir = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
if (Test-Path $vswhereDir) { $env:PATH = "$env:PATH;$vswhereDir" }
if (-not $ResultFile) { $ResultFile = Join-Path $tmp 'soak-result.txt' }

$driverOut = Join-Path $tmp 'driver'
dotnet publish (Join-Path $repo 'tests\PortalAIHost.Soak') -c Release -o $driverOut -nologo -v q | Select-String 'error' | Select-Object -First 5
if (-not $Service) {
    $svc = Join-Path $tmp 'service'
    dotnet publish (Join-Path $repo 'src\PortalAIHost.Service') -c Release -r win-x64 -o $svc -nologo -v q | Select-String 'error' | Select-Object -First 5
    $Service = Join-Path $svc 'PortalAIHost.Service.exe'
}
$driverArgs = @('--service', $Service, '--out', $ResultFile)
if ($Real) {
    if (-not $EngineDir -or -not $Model) { throw '-Real needs -EngineDir and -Model.' }
    $driverArgs += @('--mode', 'real', '--engine-dir', $EngineDir, '--model', $Model)
    if ($EmbedModel) { $driverArgs += @('--embed-model', $EmbedModel) }
}
else {
    $stub = Join-Path $tmp 'stub'
    dotnet publish (Join-Path $repo 'tests\Portal.AiHost.Engine.Tests\StubChild') -c Release -p:AssemblyName=llama-server -o $stub -nologo -v q | Select-String 'error' | Select-Object -First 5
    $driverArgs += @('--mode', 'fake', '--engine-stub', $stub)
}
if ($Minutes) { $driverArgs += @('--minutes', "$Minutes") }
$p = Start-Process -FilePath (Join-Path $driverOut 'PortalAIHost.Soak.exe') -ArgumentList $driverArgs -WindowStyle Hidden `
    -RedirectStandardOutput (Join-Path $tmp 'stdout.txt') -RedirectStandardError (Join-Path $tmp 'stderr.txt') -PassThru
Write-Host "started soak pid $($p.Id); result file: $ResultFile (last line is PASS or FAIL)"
