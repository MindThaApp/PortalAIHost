<#
.SYNOPSIS
  Builds the Portal AI Host MSIX packages (x64, arm64) and a bundle into builds\msix\<version>\.
.DESCRIPTION
  Per architecture: NativeAOT publish of PortalAIHost.Service, self-contained publish of PortalAIHost.App,
  then a flat package layout (both exes at the root, engine\vulkan and engine\cpu beneath it), packed with
  makeappx. PathMap keeps the local source path out of the binaries.

  Default (dev) mode signs the .msix files and the .msixbundle with a throw-away self-signed certificate whose
  subject equals the manifest Publisher. The certificate and its private key exist only in %TEMP% during the run
  (the key is deleted afterwards); the public .cer is written next to the packages so a tester can trust it.
  Nothing secret is ever written into the repository.

  -Store builds an UNSIGNED .msixupload for Partner Center (the Store signs it) and refuses a version whose
  revision (4th) field is not 0.
.PARAMETER Version
  Package version, 4 parts. Default: the version in Package.appxmanifest.
.PARAMETER Arch
  x64, arm64 or both (default).
.PARAMETER Store
  Unsigned .msixupload for the Store. Needs real identity values (see below).
.PARAMETER IdentityName / Publisher / PublisherDisplayName
  Override the placeholder identity in the manifest (the Partner Center values). Required with -Store.
.PARAMETER Arm64Jit
  Developer fallback: build the arm64 host without NativeAOT (the arm64 C++ build tools are needed for AOT).
  Refused with -Store.
.PARAMETER SkipEngineCheck
  Do not require the engine folders (only for layout experiments; never for a release).
#>
[CmdletBinding()]
param(
    [string]$Version,
    [ValidateSet('x64', 'arm64')][string[]]$Arch = @('x64', 'arm64'),
    [switch]$Store,
    [string]$IdentityName,
    [string]$Publisher,
    [string]$PublisherDisplayName,
    [switch]$Arm64Jit,
    [switch]$SkipEngineCheck
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$appDir = Join-Path $repo 'src\PortalAIHost.App'
$svcDir = Join-Path $repo 'src\PortalAIHost.Service'
$manifestSrc = Join-Path $appDir 'Package.appxmanifest'
$sdkBin = 'C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64'
$makeappx = Join-Path $sdkBin 'makeappx.exe'
$signtool = Join-Path $sdkBin 'signtool.exe'
foreach ($t in $makeappx, $signtool) { if (-not (Test-Path $t)) { throw "Windows SDK tool not found: $t" } }
# NativeAOT's linker discovery needs vswhere on PATH.
$vswhereDir = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
if (Test-Path $vswhereDir) { $env:PATH = "$env:PATH;$vswhereDir" }

# --- manifest facts ---------------------------------------------------------------------------------------------
[xml]$m = Get-Content -Raw $manifestSrc
$ns = New-Object Xml.XmlNamespaceManager $m.NameTable
$ns.AddNamespace('f', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10')
if (-not $Version) { $Version = $m.SelectSingleNode('/f:Package/f:Identity', $ns).GetAttribute('Version') }
if ($Version -notmatch '^\d+\.\d+\.\d+\.\d+$') { throw "Version '$Version' is not a 4-part version." }
if ($Store) {
    if ([int]($Version.Split('.')[3]) -ne 0) { throw "Store build: the version revision (4th field) must be 0, got $Version. Bump the 3rd field instead." }
    if ($Arm64Jit) { throw 'Store build: -Arm64Jit is not allowed (the host must be NativeAOT).' }
    if (-not $IdentityName -or -not $Publisher -or -not $PublisherDisplayName) {
        throw 'Store build: pass -IdentityName, -Publisher and -PublisherDisplayName from Partner Center.'
    }
}
$identity = $m.SelectSingleNode('/f:Package/f:Identity', $ns)
$effName = if ($IdentityName) { $IdentityName } else { $identity.GetAttribute('Name') }
$effPublisher = if ($Publisher) { $Publisher } else { $identity.GetAttribute('Publisher') }
if ($Store -and ($effName -match 'PLACEHOLDER' -or $effPublisher -match 'PLACEHOLDER')) { throw 'Store build: identity still holds placeholders.' }

# --- engine check -------------------------------------------------------------------------------------------------
$lock = Get-Content -Raw (Join-Path $repo 'engine\engine.lock.json') | ConvertFrom-Json
function Test-EngineFolder([string]$flavour) {
    $b = $lock.builds | Where-Object { $_.flavour -eq $flavour }
    $dir = Join-Path $repo "engine\$flavour"
    $marker = Join-Path $dir '.engine-fetch'
    if (-not (Test-Path $marker)) { throw "engine\$flavour is missing; run scripts\fetch-engine.ps1." }
    if ((Get-Content -Raw $marker).Trim() -ne "$($lock.tag) $($b.sha256)") { throw "engine\$flavour does not match engine.lock.json (tag/SHA-256); run scripts\fetch-engine.ps1 -Force." }
    if (-not (Test-Path (Join-Path $dir 'llama-server.exe'))) { throw "engine\$flavour has no llama-server.exe." }
    $dir
}

$out = Join-Path $repo "builds\msix\$Version"
New-Item -ItemType Directory -Force $out | Out-Null
$work = Join-Path ([IO.Path]::GetTempPath()) ('portalaihost-msix-' + [guid]::NewGuid().ToString('N'))
$pathMap = "$repo=C:\src\PortalAIHost"
$summary = @()

function Invoke-Publish([string]$project, [string[]]$extra) {
    $log = & dotnet publish $project -c Release @extra -p:PathMap="$pathMap" -nologo -v q 2>&1
    if ($LASTEXITCODE -ne 0) {
        $log | Select-String 'error' | Select-Object -First 5 | ForEach-Object { Write-Host $_.Line.Trim() }
        throw "dotnet publish failed: $project"
    }
    $w = $log | Select-String 'warning' | Select-Object -First 5
    if ($w) { $w | ForEach-Object { Write-Warning $_.Line.Trim() } }
}

function New-PlaceholderLogo([string]$path, [int]$size) {
    Add-Type -AssemblyName System.Drawing
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::FromArgb(255, 32, 96, 160))
    $g.Dispose(); $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
}

try {
    New-Item -ItemType Directory -Force $work | Out-Null
    foreach ($a in $Arch) {
        Write-Host "== $a =="
        $rid = "win-$a"
        $plat = if ($a -eq 'x64') { 'x64' } else { 'ARM64' }
        $flavours = if ($a -eq 'x64') { @('vulkan', 'cpu') } else { @('cpu-arm64') }
        $engine = @{}
        if (-not $SkipEngineCheck) { foreach ($f in $flavours) { $engine[$f] = Test-EngineFolder $f } }

        $svcOut = Join-Path $work "svc-$a"
        $appOut = Join-Path $work "app-$a"
        $aot = -not ($a -eq 'arm64' -and $Arm64Jit)
        if ($aot) { Invoke-Publish $svcDir @('-r', $rid, '-o', $svcOut) }
        else { Invoke-Publish $svcDir @('-r', $rid, '-o', $svcOut, '-p:PublishAot=false', '--self-contained') }
        Invoke-Publish $appDir @('-r', $rid, "-p:Platform=$plat", '-o', $appOut)

        $layout = Join-Path $work "layout-$a"
        New-Item -ItemType Directory -Force $layout | Out-Null
        Copy-Item (Join-Path $appOut '*') $layout -Recurse -Force
        if ($aot) { Copy-Item (Join-Path $svcOut 'PortalAIHost.Service.exe') $layout }
        else { Copy-Item (Join-Path $svcOut '*') $layout -Recurse -Force }
        Get-ChildItem $layout -Recurse -Include *.pdb | Remove-Item -Force
        Get-ChildItem $layout -Filter 'aspnetcorev2_inprocess.dll' | Remove-Item -Force
        if (-not (Test-Path (Join-Path $layout 'PortalAIHost.App.exe'))) { throw 'PortalAIHost.App.exe missing from the App publish.' }
        $pri = Join-Path $layout 'PortalAIHost.App.pri'
        if (Test-Path $pri) { Move-Item $pri (Join-Path $layout 'resources.pri') -Force }

        # The host expects engine\<flavour>\llama-server.exe beside its exe; the arm64 CPU build is the "cpu" flavour.
        foreach ($f in $flavours) {
            $name = if ($f -eq 'cpu-arm64') { 'cpu' } else { $f }
            if ($engine.ContainsKey($f)) {
                $dest = Join-Path $layout "engine\$name"
                New-Item -ItemType Directory -Force $dest | Out-Null
                Copy-Item (Join-Path $engine[$f] '*') $dest -Recurse -Force
                Remove-Item (Join-Path $dest '.engine-fetch') -Force -ErrorAction SilentlyContinue
            }
        }

        # Assets: the project's own, or generated placeholders (replace before a Store submission).
        $assets = Join-Path $layout 'Assets'
        New-Item -ItemType Directory -Force $assets | Out-Null
        $projAssets = Join-Path $appDir 'Assets'
        foreach ($l in @(@('StoreLogo.png', 50), @('Square44x44Logo.png', 44), @('Square150x150Logo.png', 150))) {
            if (Test-Path (Join-Path $projAssets $l[0])) { Copy-Item (Join-Path $projAssets $l[0]) $assets }
            else { New-PlaceholderLogo (Join-Path $assets $l[0]) $l[1] }
        }

        # Manifest: architecture + identity overrides + version.
        [xml]$lm = Get-Content -Raw $manifestSrc
        $lid = $lm.SelectSingleNode('/f:Package/f:Identity', $ns)
        $lid.SetAttribute('ProcessorArchitecture', $a)
        $lid.SetAttribute('Version', $Version)
        if ($IdentityName) { $lid.SetAttribute('Name', $IdentityName) }
        if ($Publisher) { $lid.SetAttribute('Publisher', $Publisher) }
        if ($PublisherDisplayName) { $lm.SelectSingleNode('/f:Package/f:Properties/f:PublisherDisplayName', $ns).InnerText = $PublisherDisplayName }
        $lm.Save((Join-Path $layout 'AppxManifest.xml'))

        $msix = Join-Path $out "PortalAIHost_${Version}_$a.msix"
        & $makeappx pack /d $layout /p $msix /o 2>&1 | Select-String 'error|fail' | ForEach-Object { Write-Host $_.Line }
        if (-not (Test-Path $msix)) { throw "makeappx pack failed for $a." }
        $svcSize = (Get-Item (Join-Path $layout 'PortalAIHost.Service.exe')).Length
        $summary += "{0}: msix {1:N1} MB, Service exe {2:N1} MB ({3})" -f $a, ((Get-Item $msix).Length / 1MB), ($svcSize / 1MB), $(if ($aot) { 'NativeAOT' } else { 'JIT fallback' })
    }

    # --- bundle ---------------------------------------------------------------------------------------------------
    $stage = Join-Path $work 'bundle-in'
    New-Item -ItemType Directory -Force $stage | Out-Null
    foreach ($a in $Arch) { Copy-Item (Join-Path $out "PortalAIHost_${Version}_$a.msix") $stage }
    $bundle = Join-Path $out "PortalAIHost_$Version.msixbundle"
    & $makeappx bundle /d $stage /p $bundle /o /bv $Version 2>&1 | Select-String 'error|fail' | ForEach-Object { Write-Host $_.Line }
    if (-not (Test-Path $bundle)) { throw 'makeappx bundle failed.' }

    if ($Store) {
        $upload = Join-Path $out "PortalAIHost_$Version.msixupload"
        Remove-Item $upload -ErrorAction SilentlyContinue
        $zip = Join-Path $work 'upload.zip'
        Compress-Archive -Path $bundle -DestinationPath $zip
        Move-Item $zip $upload
        $summary += "Store upload (unsigned): $upload"
    }
    else {
        # Throw-away certificate: subject == manifest Publisher. Key lives in the CurrentUser store only for
        # the few seconds it takes to export it to %TEMP%, then it is removed from the store; the pfx is deleted below.
        $pfxPass = [guid]::NewGuid().ToString('N')
        $secure = ConvertTo-SecureString $pfxPass -AsPlainText -Force
        $cert = New-SelfSignedCertificate -Type Custom -Subject $effPublisher -KeyUsage DigitalSignature `
            -FriendlyName 'Portal AI Host dev signing (test only)' -CertStoreLocation 'Cert:\CurrentUser\My' `
            -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}') -NotAfter (Get-Date).AddDays(30)
        $pfx = Join-Path $work 'dev.pfx'
        try {
            Export-PfxCertificate -Cert $cert -FilePath $pfx -Password $secure | Out-Null
            Export-Certificate -Cert $cert -FilePath (Join-Path $out 'PortalAIHost-dev-test.cer') | Out-Null
        }
        finally { Remove-Item "Cert:\CurrentUser\My\$($cert.Thumbprint)" -Force -ErrorAction SilentlyContinue }
        $targets = @($bundle) + @($Arch | ForEach-Object { Join-Path $out "PortalAIHost_${Version}_$_.msix" })
        foreach ($t in $targets) {
            $r = & $signtool sign /fd SHA256 /f $pfx /p $pfxPass $t 2>&1
            if ($LASTEXITCODE -ne 0) { throw "signtool failed on $(Split-Path $t -Leaf): $($r | Select-Object -Last 2)" }
        }
        $summary += 'Signed with a throw-away test certificate (public part: PortalAIHost-dev-test.cer). Not for the Store.'
    }
    $summary += "Bundle: $bundle ({0:N1} MB)" -f ((Get-Item $bundle).Length / 1MB)
    $summary | ForEach-Object { Write-Host $_ }
    Write-Host "OK: $out"
}
finally {
    # $work is a folder this run created under %TEMP% (holds the pfx).
    if ((Test-Path $work) -and $work.Contains('portalaihost-msix-')) { Remove-Item -Recurse -Force -LiteralPath $work }
}
