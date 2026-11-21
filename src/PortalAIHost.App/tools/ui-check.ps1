# Walks every page of the window against the fake host, using UI Automation only (no injected mouse or keyboard).
# Runs a copy of the build from the temp folder, off-screen, one window at a time, and always closes it.
# Usage (Windows PowerShell): .\ui-check.ps1 -BuildDir <bin\x64\Debug\...\win-x64> -OutDir <screens folder>
param(
    [Parameter(Mandatory)][string]$BuildDir,
    [Parameter(Mandatory)][string]$OutDir
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class Cap {
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
}
"@

if (-not (Test-Path -LiteralPath $BuildDir) -or -not (Get-ChildItem -LiteralPath $BuildDir -File)) { throw "Build folder missing or empty: $BuildDir" }
$work = Join-Path $env:TEMP ("portalaihost-uicheck-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
Copy-Item -Path (Join-Path $BuildDir '*') -Destination $work -Recurse
$exe = Join-Path $work 'PortalAIHost.App.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw "No exe in copy: $exe" }

function Get-Win([int]$procId, [int]$timeoutSec = 25) {
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $procId)
    while ((Get-Date) -lt $deadline) {
        $w = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
        if ($w -ne $null -and $w.Current.NativeWindowHandle -ne 0) { return $w }
        Start-Sleep -Milliseconds 300
    }
    throw "No window for process $procId"
}

function Save-Shot([string]$shotDir, [string]$name) {
    # The app renders its own window (or the open dialog) when a take-NAME.txt file appears in its shot folder.
    New-Item -ItemType Directory -Force -Path $shotDir | Out-Null
    Set-Content -LiteralPath (Join-Path $shotDir ("take-" + $name + ".txt")) -Value $name
    $png = Join-Path $shotDir ($name + ".png")
    $deadline = (Get-Date).AddSeconds(10)
    while (-not (Test-Path -LiteralPath $png) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 300 }
    Start-Sleep -Milliseconds 300
    if (-not (Test-Path -LiteralPath $png)) { throw "No picture made for $name" }
    $dest = Join-Path $OutDir ($name + ".png")
    Copy-Item -LiteralPath $png -Destination $dest -Force
    return $dest
}

function Find-Named($win, [string]$name) {
    $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)
    return $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Invoke-Named($win, [string]$name) {
    $el = Find-Named $win $name
    if ($el -eq $null) { return $false }
    $p = $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $p.Invoke()
    return $true
}

function Unnamed-Controls($win) {
    $types = @('Button', 'CheckBox', 'ComboBox', 'Edit', 'Hyperlink', 'ListItem', 'Slider', 'TabItem', 'MenuItem')
    $all = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition)
    $bad = @()
    foreach ($e in $all) {
        $t = $e.Current.ControlType.ProgrammaticName -replace '^ControlType\.', ''
        if ($types -contains $t -and [string]::IsNullOrWhiteSpace($e.Current.Name) -and -not $e.Current.IsOffscreen) { $bad += $t }
    }
    return $bad
}

function Close-Window($proc, $win) {
    try { $win.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close() } catch { }
    if (-not $proc.WaitForExit(6000)) {
        $cmd = (Get-CimInstance Win32_Process -Filter "ProcessId=$($proc.Id)").CommandLine
        if ($cmd -and (($cmd -replace '/', '\') -like ("*" + $work + "*"))) { Stop-Process -Id $proc.Id -Force }
    }
}

$results = @()
function Run-Page([string]$name, [string[]]$appArgs, [scriptblock]$steps = $null) {
    $shotDir = Join-Path $work ('shots-' + $name)
    $appArgs = $appArgs + ('--shot-dir=' + $shotDir)
    $proc = Start-Process -FilePath $exe -ArgumentList $appArgs -PassThru -WorkingDirectory $work
    $win = $null
    try {
        $win = Get-Win $proc.Id
        Start-Sleep -Seconds 3
        if ($steps) { & $steps $win }
        $shot = Save-Shot $shotDir $name
        $bad = Unnamed-Controls $win
        $script:results += "{0}: shot={1} unnamedControls={2}" -f $name, (Split-Path $shot -Leaf), $bad.Count
    } finally {
        if ($win) { Close-Window $proc $win } elseif (-not $proc.HasExited) {
            $cmd = (Get-CimInstance Win32_Process -Filter "ProcessId=$($proc.Id)").CommandLine
            if ($cmd -and (($cmd -replace '/', '\') -like ("*" + $work + "*"))) { Stop-Process -Id $proc.Id -Force }
        }
    }
}

try {
    $common = @('--offscreen', '--skip-first-run')
    Run-Page '01-home-loaded' ($common + '--fake=loaded')
    Run-Page '02-home-no-host' ($common + '--fake=nohost')
    Run-Page '03-home-safe-mode' ($common + '--fake=safemode')
    Run-Page '04-models-fresh' ($common + '--fake=fresh' + '--page=models')
    Run-Page '05-models-downloading' ($common + '--fake=downloading' + '--page=models')
    Run-Page '06-apps' ($common + '--fake=loaded' + '--page=apps')
    Run-Page '07-settings' ($common + '--fake=loaded' + '--page=settings')
    Run-Page '08-try-it' ($common + '--fake=loaded' + '--page=tryit') {
        param($win)
        $edit = Find-Named $win 'Your question'
        if ($edit -ne $null) { $edit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('Write one sentence about the sea.') }
        Start-Sleep -Milliseconds 500
        [void](Invoke-Named $win 'Ask')
        Start-Sleep -Seconds 3
    }
    Run-Page '09-approval-prompt' ('--offscreen', '--fake=approvalpending')
    Run-Page '10-first-run-check' ('--offscreen', '--fake=fresh')
    Run-Page '11-first-run-model' ('--offscreen', '--fake=fresh') { param($win) [void](Invoke-Named $win 'Continue'); Start-Sleep -Seconds 1 }
    Run-Page '12-first-run-download' ('--offscreen', '--fake=fresh') {
        param($win)
        [void](Invoke-Named $win 'Continue'); Start-Sleep -Seconds 1
        [void](Invoke-Named $win 'Download this model'); Start-Sleep -Seconds 4
    }
    Run-Page '13-first-run-startup' ('--offscreen', '--fake=fresh') {
        param($win)
        [void](Invoke-Named $win 'Continue'); Start-Sleep -Seconds 1
        [void](Invoke-Named $win 'Download this model'); Start-Sleep -Seconds 18
    }
    Run-Page '14-pseudo-loc-models' ($common + '--fake=loaded' + '--page=models' + '--pseudo-loc')
} finally {
    $left = Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -and (($_.CommandLine -replace '/', '\') -like ("*" + $work + "*")) }
    $results += "instances left: $(@($left).Count)"
    $results | ForEach-Object { Write-Output $_ }
}
