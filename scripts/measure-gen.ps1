<#
.SYNOPSIS
  Times image and video generation with an installed stable-diffusion.cpp build, to calibrate the host's estimates.
.DESCRIPTION
  Runs up to three standard cases with the sd.cpp command-line program and records wall time, sampling time,
  exit code and peak process memory (RAM; graphics memory when the GPU performance counters are readable):
    sd15   SD 1.5 at 512x512, 20 steps
    sdxl   SDXL at 1024x1024, 25 steps
    wan    Wan 2.1 1.3B at 832x480, 33 frames, 20 steps
  A case runs only when its model files are given. Everything this script writes goes to a new folder under
  %TEMP% (outputs, per-run logs, results.json and results.txt); model files are only read. The flags match the
  host's table in src\Portal.AiHost.Engine\SdCppArgs.cs.
.PARAMETER SdExe
  The sd.cpp program. Default: engine\sdcpp\vulkan\sd-cli.exe (or sd.exe) in this repository.
.PARAMETER Runs
  Runs per case (the first one includes the cold disk read of the weights).
.EXAMPLE
  .\scripts\measure-gen.ps1 -Sd15Model D:\models\sd15-q8.gguf -WanDiffusion D:\models\wan2.1-t2v-1.3b-q8.gguf `
      -WanT5 D:\models\umt5-xxl-q8.gguf -WanVae D:\models\wan_2.1_vae.safetensors
#>
[CmdletBinding()]
param(
    [string]$SdExe,
    [string]$Sd15Model,
    [string]$SdxlModel,
    [string]$SdxlVae,
    [string]$WanDiffusion,
    [string]$WanT5,
    [string]$WanVae,
    [int]$Runs = 1,
    [int]$Threads = 0,
    [switch]$ClipOnCpu,
    [switch]$VaeOnCpu,
    [switch]$OffloadToCpu,
    [string[]]$ExtraArgs = @()
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

if (-not $SdExe) {
    foreach ($name in 'sd-cli.exe', 'sd.exe') {
        $candidate = Join-Path $repoRoot "engine\sdcpp\vulkan\$name"
        if (Test-Path $candidate) { $SdExe = $candidate; break }
    }
}
if (-not $SdExe -or -not (Test-Path $SdExe)) { throw 'No sd.cpp program found. Run scripts\fetch-sdcpp.ps1 or pass -SdExe.' }

function Assert-File([string]$path, [string]$what) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "$what not found: $path" }
    (Resolve-Path -LiteralPath $path).Path
}

$cases = @()
if ($Sd15Model) {
    $cases += @{ Name = 'sd15'; Mode = 'img_gen'; Ext = '.png'; Width = 512; Height = 512; Steps = 20; Frames = 1
        Files = @('-m', (Assert-File $Sd15Model 'SD 1.5 model')); Extra = @('--cfg-scale', '7', '--sampling-method', 'euler_a') }
}
if ($SdxlModel) {
    $files = @('-m', (Assert-File $SdxlModel 'SDXL model'))
    if ($SdxlVae) { $files += @('--vae', (Assert-File $SdxlVae 'SDXL VAE')) }
    $cases += @{ Name = 'sdxl'; Mode = 'img_gen'; Ext = '.png'; Width = 1024; Height = 1024; Steps = 25; Frames = 1
        Files = $files; Extra = @('--cfg-scale', '7', '--sampling-method', 'euler_a', '--vae-tiling') }
}
if ($WanDiffusion) {
    if (-not $WanT5 -or -not $WanVae) { throw 'The Wan case needs -WanDiffusion, -WanT5 and -WanVae.' }
    $cases += @{ Name = 'wan'; Mode = 'vid_gen'; Ext = '.avi'; Width = 832; Height = 480; Steps = 20; Frames = 33
        Files = @('--diffusion-model', (Assert-File $WanDiffusion 'Wan diffusion model'), '--t5xxl', (Assert-File $WanT5 'UMT5 text encoder'),
            '--vae', (Assert-File $WanVae 'Wan VAE'))
        Extra = @('--cfg-scale', '6', '--sampling-method', 'euler', '--flow-shift', '3', '--video-frames', '33', '--fps', '16',
            '--flash-attn', 'auto,diffusion=on', '--vae-tiling') }
}
if ($cases.Count -eq 0) { throw 'Give at least one model: -Sd15Model, -SdxlModel or -WanDiffusion/-WanT5/-WanVae.' }

# A new folder for this measurement: nothing that exists is ever written to.
$out = Join-Path ([IO.Path]::GetTempPath()) ('portalaihost-measure-gen-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 6))
New-Item -ItemType Directory -Path $out | Out-Null

function Get-GpuDedicatedBytes([int]$processId) {
    try {
        $samples = (Get-Counter -Counter "\GPU Process Memory(pid_$($processId)_*)\Dedicated Usage" -ErrorAction Stop).CounterSamples
        [long](($samples | Measure-Object -Property CookedValue -Sum).Sum)
    }
    catch { -1 }
}

$results = @()
foreach ($case in $cases) {
    for ($run = 1; $run -le $Runs; $run++) {
        $output = Join-Path $out ("{0}-run{1}{2}" -f $case.Name, $run, $case.Ext)
        $log = Join-Path $out ("{0}-run{1}.log" -f $case.Name, $run)
        $cmd = @('-M', $case.Mode) + $case.Files + @('-p', 'a lighthouse on a cliff at dusk, waves, cinematic light',
            '-W', $case.Width, '-H', $case.Height, '--steps', $case.Steps, '-s', '42', '-o', $output) + $case.Extra
        if ($Threads -gt 0) { $cmd += @('-t', $Threads) }
        $onCpu = @()
        if ($ClipOnCpu) { $onCpu += 'te=cpu' }
        if ($VaeOnCpu) { $onCpu += 'vae=cpu' }
        if ($onCpu.Count -gt 0) { $cmd += @('--backend', ($onCpu -join ',')) }
        if ($OffloadToCpu) { $cmd += '--offload-to-cpu' }
        $cmd += $ExtraArgs

        Write-Host ("{0} run {1}: {2}x{3}, {4} steps, {5} frame(s)" -f $case.Name, $run, $case.Width, $case.Height, $case.Steps, $case.Frames)
        $psi = [Diagnostics.ProcessStartInfo]::new($SdExe)
        foreach ($a in $cmd) { $psi.ArgumentList.Add([string]$a) }
        $psi.UseShellExecute = $false
        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError = $true
        $psi.CreateNoWindow = $true
        $psi.WorkingDirectory = $out
        $watch = [Diagnostics.Stopwatch]::StartNew()
        $proc = [Diagnostics.Process]::Start($psi)
        $stdout = $proc.StandardOutput.ReadToEndAsync()
        $stderr = $proc.StandardError.ReadToEndAsync()
        $peakRam = 0L; $peakGpu = -1L
        while (-not $proc.WaitForExit(500)) {
            try { $proc.Refresh(); $peakRam = [Math]::Max($peakRam, $proc.PeakWorkingSet64) } catch { }
            $gpu = Get-GpuDedicatedBytes $proc.Id
            if ($gpu -gt $peakGpu) { $peakGpu = $gpu }
        }
        $watch.Stop()
        $text = $stdout.Result + "`n" + $stderr.Result
        Set-Content -LiteralPath $log -Value $text
        $sampling = [regex]::Matches($text, 'sampling completed, taking ([\d.]+)s') | ForEach-Object { [double]$_.Groups[1].Value }
        $results += [pscustomobject]@{
            case = $case.Name; run = $run; width = $case.Width; height = $case.Height; steps = $case.Steps; frames = $case.Frames
            exit_code = $proc.ExitCode; wall_seconds = [Math]::Round($watch.Elapsed.TotalSeconds, 1)
            sampling_seconds = if ($sampling) { [Math]::Round(($sampling | Measure-Object -Sum).Sum, 1) } else { $null }
            seconds_per_step = if ($sampling) { [Math]::Round(($sampling | Measure-Object -Sum).Sum / $case.Steps, 2) } else { $null }
            peak_ram_bytes = $peakRam; peak_gpu_bytes = $peakGpu
            output = if (Test-Path -LiteralPath $output) { $output } else { $null }; log = $log
        }
        $proc.Dispose()
    }
}

$info = [pscustomobject]@{
    measured_at = (Get-Date).ToString('o'); sd_exe = $SdExe; computer = @{
        cpu = (Get-CimInstance Win32_Processor | Select-Object -First 1).Name
        ram_bytes = (Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory
        gpu = (Get-CimInstance Win32_VideoController | Select-Object -ExpandProperty Name) -join '; ' }
    flags = @{ clip_on_cpu = [bool]$ClipOnCpu; vae_on_cpu = [bool]$VaeOnCpu; offload_to_cpu = [bool]$OffloadToCpu; threads = $Threads }
    results = $results
}
$info | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $out 'results.json')
$results | Format-Table case, run, exit_code, wall_seconds, sampling_seconds, seconds_per_step, peak_ram_bytes, peak_gpu_bytes -AutoSize |
    Out-String -Width 200 | Set-Content -LiteralPath (Join-Path $out 'results.txt')
Get-Content -LiteralPath (Join-Path $out 'results.txt')
Write-Host "Results: $(Join-Path $out 'results.json')"
