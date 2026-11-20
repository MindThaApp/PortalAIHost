$ErrorActionPreference = 'Stop'
$r = $PSScriptRoot
$L = Join-Path $r 'layout'
if (Test-Path $L) { Remove-Item $L -Recurse -Force }   # layout is a folder this script created
foreach ($p in 'Host','Client','Child','Visible') { dotnet build (Join-Path $r "src\$p") -c Release -v q -o (Join-Path $r "out\$p") | Select-String 'error' | Select-Object -First 5 }
Add-Type -AssemblyName System.Drawing
function Logo($path) { $b = New-Object System.Drawing.Bitmap 150,150; $b.Save($path); $b.Dispose() }
foreach ($n in 'host','client') {
  New-Item -ItemType Directory -Force "$L\$n\Assets" | Out-Null
  Logo "$L\$n\Assets\logo.png"
}
Copy-Item "$r\manifests\Host.appxmanifest" "$L\host\AppxManifest.xml"
Copy-Item "$r\manifests\Client.appxmanifest" "$L\client\AppxManifest.xml"
Copy-Item "$r\out\Host" "$L\host\Service" -Recurse
Copy-Item "$r\out\Visible" "$L\host\Visible" -Recurse
Copy-Item "$r\out\Child" "$L\host\Service\child" -Recurse
Copy-Item "$r\out\Client\*" "$L\client" -Recurse
