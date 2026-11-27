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
.PARAMETER PlaceholderIdentity
  With -Store: a dry run that keeps the placeholder identity (and allows a missing catalogue signature) to check that
  the package and .msixupload build. The file is named *.PLACEHOLDER.msixupload and must never be uploaded.
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
    [switch]$PlaceholderIdentity,
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
    if (-not $PlaceholderIdentity -and (-not $IdentityName -or -not $Publisher -or -not $PublisherDisplayName)) {
        throw 'Store build: pass -IdentityName, -Publisher and -PublisherDisplayName from Partner Center.'
    }
    # Pseudo-localised strings (Strings\qps-ploc, made by -p:PseudoLoc=true) would ship as an extra language.
    # Release hosts trust only the compiled-in catalogue keys; with none, the packaged app has no model list at all.
    if (-not $PlaceholderIdentity -and (Select-String -Path (Join-Path $repo 'src\Portal.AiHost.Models\Catalogue\CatalogueVerifier.cs') -Pattern 'PublicKeysSpkiBase64 \{ get; \} = Array\.Empty' -Quiet)) {
        throw 'Store build: no catalogue public key is compiled in (CatalogueTrust.PublicKeysSpkiBase64 is empty).'
    }
    if (Test-Path (Join-Path $appDir 'Strings\qps-ploc')) { throw 'Store build: src\PortalAIHost.App\Strings\qps-ploc exists (pseudo-localisation output); remove it first.' }
}
elseif ($PlaceholderIdentity) { throw '-PlaceholderIdentity is only for -Store dry runs.' }
$identity = $m.SelectSingleNode('/f:Package/f:Identity', $ns)
$effName = if ($IdentityName) { $IdentityName } else { $identity.GetAttribute('Name') }
$effPublisher = if ($Publisher) { $Publisher } else { $identity.GetAttribute('Publisher') }
$effDisplay = if ($PublisherDisplayName) { $PublisherDisplayName } else { $m.SelectSingleNode('/f:Package/f:Properties/f:PublisherDisplayName', $ns).InnerText }
$hasPlaceholder = ($effName + $effPublisher + $effDisplay) -match 'PLACEHOLDER'
if ($Store -and $hasPlaceholder -and -not $PlaceholderIdentity) { throw 'Store build: identity still holds placeholders.' }
if ($Store -and $PlaceholderIdentity) { Write-Warning 'Dry run with the placeholder identity: the .msixupload is for checks only, never upload it.' }

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

# The image and video engine (stable-diffusion.cpp) is optional: packed only when scripts\fetch-sdcpp.ps1 installed
# a build that matches engine\sdcpp.lock.json. Returns the folder, or $null (with a warning when it does not match).
$sdLockPath = Join-Path $repo 'engine\sdcpp.lock.json'
$sdLock = if (Test-Path $sdLockPath) { Get-Content -Raw $sdLockPath | ConvertFrom-Json } else { $null }
$ffLockPath = Join-Path $repo 'engine\ffmpeg.lock.json'
$ffLock = if (Test-Path $ffLockPath) { Get-Content -Raw $ffLockPath | ConvertFrom-Json } else { $null }
function Get-SdCppFolder([string]$flavour) {
    $dir = Join-Path $repo "engine\sdcpp\$flavour"
    if (-not $sdLock -or -not (Test-Path $dir)) { return $null }
    $b = $sdLock.builds | Where-Object { $_.flavour -eq $flavour }
    $marker = Join-Path $dir '.engine-fetch'
    if (-not $b -or -not (Test-Path $marker) -or (Get-Content -Raw $marker).Trim() -ne "$($sdLock.tag) $($b.sha256)") {
        Write-Warning "engine\sdcpp\$flavour does not match engine\sdcpp.lock.json; the image and video engine is left out."
        return $null
    }
    if (-not ((Test-Path (Join-Path $dir 'sd-cli.exe')) -or (Test-Path (Join-Path $dir 'sd.exe')))) {
        Write-Warning "engine\sdcpp\$flavour has no sd-cli.exe or sd.exe; the image and video engine is left out."
        return $null
    }
    $dir
}

$out = Join-Path $repo "builds\msix\$Version"
New-Item -ItemType Directory -Force $out | Out-Null
$work = Join-Path ([IO.Path]::GetTempPath()) ('portalaihost-msix-' + [guid]::NewGuid().ToString('N'))
$pathMap = "$repo=C:\src\PortalAIHost"
$summary = @()

# Crash reports: the project reads the address at build time from PORTALAIHOST_SENTRY_DSN or the key file
# (%LOCALAPPDATA%\PortalAIHost-signing-key\sentry-dsn.txt). It is never printed or committed. Store builds are tagged 'store'.
$crashDsnFile = Join-Path $env:LOCALAPPDATA 'PortalAIHost-signing-key\sentry-dsn.txt'
$hasCrashDsn = [bool]$env:PORTALAIHOST_SENTRY_DSN -or (Test-Path $crashDsnFile)
$crashEnv = if ($Store) { 'store' } else { 'dev' }
if ($Store -and -not $hasCrashDsn) { Write-Warning 'No crash-report address found: this build will not send crash reports.' }

function Invoke-Publish([string]$project, [string[]]$extra) {
    $log = & dotnet publish $project -c Release @extra "-p:CrashEnvironment=$crashEnv" -p:PathMap="$pathMap" -nologo -v q 2>&1
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
        # Layout: PortalAIHost.App.exe and PortalAIHost.Service.exe side by side at the package root (the window's
        # launcher and the host's window lookup both search there), engine\<flavour>\ and catalogue\ beside them.
        # The host only trusts a signed model list: without catalogue.json.sig it starts with downloads off.
        $catSrc = Join-Path $repo 'catalogue'
        if ((Test-Path (Join-Path $catSrc 'catalogue.json')) -and (Test-Path (Join-Path $catSrc 'catalogue.json.sig'))) {
            $catDest = Join-Path $layout 'catalogue'
            New-Item -ItemType Directory -Force $catDest | Out-Null
            Copy-Item (Join-Path $catSrc 'catalogue.json'), (Join-Path $catSrc 'catalogue.json.sig') $catDest
            # The image and video list is optional and signed on its own; a host without it just has no such models.
            if ((Test-Path (Join-Path $catSrc 'catalogue-gen.json')) -and (Test-Path (Join-Path $catSrc 'catalogue-gen.json.sig'))) {
                Copy-Item (Join-Path $catSrc 'catalogue-gen.json'), (Join-Path $catSrc 'catalogue-gen.json.sig') $catDest
            }
        }
        elseif ($Store -and -not $PlaceholderIdentity) { throw 'Store build: catalogue\catalogue.json.sig is missing (sign the model list first).' }
        else { Write-Warning 'catalogue\catalogue.json.sig is missing: this package has no model list (downloads off).' }

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

        # The voice helper (built by scripts\build-voice.ps1) is x64 only; without it the voice feature reports itself off.
        $voiceSrc = Join-Path $repo 'engine\voice'
        if ($a -eq 'x64' -and (Test-Path (Join-Path $voiceSrc 'portal-voice-server.exe'))) {
            $voiceDest = Join-Path $layout 'engine\voice'
            New-Item -ItemType Directory -Force $voiceDest | Out-Null
            Copy-Item (Join-Path $voiceSrc '*') $voiceDest -Recurse -Force
            Remove-Item (Join-Path $voiceDest '.engine-fetch') -Force -ErrorAction SilentlyContinue
        }
        elseif ($a -eq 'x64') { Write-Warning 'engine\voice\portal-voice-server.exe is missing: this package has no voice feature (run scripts\build-voice.ps1).' }

        # The upscale helper (built by scripts\build-upscale.ps1) is x64 only; without it picture upscaling reports itself off.
        $upscaleSrc = Join-Path $repo 'engine\upscale'
        if ($a -eq 'x64' -and (Test-Path (Join-Path $upscaleSrc 'portal-upscale.exe'))) {
            $upscaleDest = Join-Path $layout 'engine\upscale'
            New-Item -ItemType Directory -Force $upscaleDest | Out-Null
            Copy-Item (Join-Path $upscaleSrc '*') $upscaleDest -Recurse -Force
            Remove-Item (Join-Path $upscaleDest '.engine-fetch') -Force -ErrorAction SilentlyContinue
        }
        elseif ($a -eq 'x64') { Write-Warning 'engine\upscale\portal-upscale.exe is missing: this package has no upscaling (run scripts\build-upscale.ps1).' }

        # engine\ffmpeg\ (fetched by scripts\fetch-ffmpeg.ps1; x64 only): MP4 conversion, last-frame pictures, joins, MP3 samples.
        # Without it those features report themselves off.
        $ffDir = Join-Path $repo 'engine\ffmpeg'
        $ffMarker = Join-Path $ffDir '.engine-fetch'
        $ffBuild = if ($ffLock) { $ffLock.builds | Where-Object { $_.flavour -eq 'x64' } } else { $null }
        if ($a -eq 'x64' -and $ffBuild -and (Test-Path $ffMarker) -and (Get-Content -Raw $ffMarker).Trim() -eq "$($ffLock.tag) $($ffBuild.sha256)" -and (Test-Path (Join-Path $ffDir 'ffmpeg.exe'))) {
            $ffDest = Join-Path $layout 'engine\ffmpeg'
            New-Item -ItemType Directory -Force $ffDest | Out-Null
            Copy-Item (Join-Path $ffDir '*') $ffDest -Recurse -Force
            Remove-Item (Join-Path $ffDest '.engine-fetch') -Force -ErrorAction SilentlyContinue
            $summary += 'ffmpeg included'
        }
        elseif ($a -eq 'x64') { Write-Warning 'engine\ffmpeg is missing or does not match engine\ffmpeg.lock.json: this package has no MP4, join or MP3 features (run scripts\fetch-ffmpeg.ps1).' }

        # engine\sdcpp\<flavour>\ (x64 only), only when present; without it the job routes answer engine_missing.
        if ($a -eq 'x64') {
            foreach ($sf in @('vulkan', 'cpu')) {
                $sdDir = Get-SdCppFolder $sf
                if ($sdDir) {
                    $dest = Join-Path $layout "engine\sdcpp\$sf"
                    New-Item -ItemType Directory -Force $dest | Out-Null
                    Copy-Item (Join-Path $sdDir '*') $dest -Recurse -Force
                    Remove-Item (Join-Path $dest '.engine-fetch') -Force -ErrorAction SilentlyContinue
                    $summary += "image and video engine ($sf) included"
                }
            }
        }

        # Assets: the project's scale/targetsize-qualified logos (rendered from docs\store\logo). The App publish
        # indexes them into its PRI (resources.pri), which resolves the manifest's unqualified names.
        $assets = Join-Path $layout 'Assets'
        $projAssets = Join-Path $appDir 'Assets'
        if (Test-Path (Join-Path $projAssets 'Square44x44Logo.scale-100.png')) {
            New-Item -ItemType Directory -Force $assets | Out-Null
            Copy-Item (Join-Path $projAssets '*.png') $assets
        }
        elseif ($Store) { throw 'Store build: src\PortalAIHost.App\Assets has no logos.' }
        else {
            New-Item -ItemType Directory -Force $assets | Out-Null
            foreach ($l in @(@('StoreLogo.png', 50), @('Square44x44Logo.png', 44), @('Square150x150Logo.png', 150))) {
                New-PlaceholderLogo (Join-Path $assets $l[0]) $l[1]
            }
        }

        # Manifest: architecture + identity overrides + version.
        $lm = $m.Clone()   # the manifest as read at the start, so every architecture gets the same one
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
        $upload = Join-Path $out $(if ($PlaceholderIdentity) { "PortalAIHost_$Version.PLACEHOLDER.msixupload" } else { "PortalAIHost_$Version.msixupload" })
        Remove-Item $upload -ErrorAction SilentlyContinue
        $zip = Join-Path $work 'upload.zip'
        Compress-Archive -Path $bundle -DestinationPath $zip
        Move-Item $zip $upload
        $summary += "Store upload (unsigned): $upload ({0:N1} MB)" -f ((Get-Item $upload).Length / 1MB)
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
