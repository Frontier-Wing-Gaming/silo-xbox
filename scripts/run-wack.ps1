# Runs the Windows App Certification Kit on a Store package before you submit it.
# Usage, in an administrator PowerShell (the kit installs the package to test it):
#   powershell -ExecutionPolicy Bypass -File scripts\run-wack.ps1 -Package <path to .msixupload, .msixbundle or .msix>
#
# Store packages are unsigned (the Store signs them), and the kit can only install a signed
# package. So this signs a copy with a throwaway certificate whose subject matches the
# package's Publisher, trusts it for the test, and removes it again afterwards.
param(
  [Parameter(Mandatory = $true)][string]$Package,
  [string]$Report = (Join-Path (Get-Location) "wack-report.xml")
)
$ErrorActionPreference = "Stop"

$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
  [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $admin) { throw "Run this from an administrator PowerShell: the certification kit installs the package to test it." }

$kit = "${env:ProgramFiles(x86)}\Windows Kits\10\App Certification Kit"
if (-not (Test-Path "$kit\appcert.exe")) { throw "Windows App Certification Kit not found. Install the Windows SDK." }

$work = Join-Path ([IO.Path]::GetTempPath()) ("silolux-wack-" + [guid]::NewGuid())
New-Item -ItemType Directory $work | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
$thumb = $null
try {
  # A .msixupload is a zip around the bundle and its symbols: take the bundle out.
  $source = (Resolve-Path $Package).Path
  if ($source -like "*.msixupload" -or $source -like "*.appxupload") {
    [IO.Compression.ZipFile]::ExtractToDirectory($source, "$work\upload")
    $source = (Get-ChildItem "$work\upload" -Include *.msixbundle, *.appxbundle, *.msix, *.appx -Recurse | Select-Object -First 1).FullName
    if (-not $source) { throw "No package inside $Package" }
  }
  $target = Join-Path $work (Split-Path $source -Leaf)
  Copy-Item $source $target

  # The signing certificate's subject must equal the manifest Publisher.
  $zip = [IO.Compression.ZipFile]::OpenRead($target)
  try {
    $entry = $zip.Entries | Where-Object { $_.FullName -in "AppxMetadata/AppxBundleManifest.xml", "AppxManifest.xml" } | Select-Object -First 1
    $reader = New-Object IO.StreamReader($entry.Open())
    [xml]$manifest = $reader.ReadToEnd()
    $reader.Dispose()
  } finally { $zip.Dispose() }
  $publisher = $manifest.DocumentElement.Identity.Publisher
  $name = $manifest.DocumentElement.Identity.Name
  Write-Host "Package: $name  Publisher: $publisher"

  $cert = New-SelfSignedCertificate -Type Custom -Subject $publisher -KeyUsage DigitalSignature `
    -FriendlyName "Silolux WACK test (temporary)" -CertStoreLocation "Cert:\CurrentUser\My" `
    -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}") -NotAfter (Get-Date).AddDays(1)
  $thumb = $cert.Thumbprint
  Export-Certificate -Cert $cert -FilePath "$work\test.cer" | Out-Null
  Import-Certificate -FilePath "$work\test.cer" -CertStoreLocation "Cert:\LocalMachine\TrustedPeople" | Out-Null

  & "$kit\signtool.exe" sign /fd SHA256 /sha1 $thumb /s My $target | Out-Host
  if ($LASTEXITCODE -ne 0) { throw "signtool could not sign the test copy" }

  Write-Host "Running the certification kit (this takes several minutes)..."
  & "$kit\appcert.exe" reset | Out-Null
  if (Test-Path $Report) { Remove-Item $Report }
  & "$kit\appcert.exe" test -appxpackagepath $target -reportoutputpath $Report | Out-Host

  if (-not (Test-Path $Report)) { throw "The kit wrote no report." }
  [xml]$result = Get-Content $Report
  $overall = $result.REPORT.OVERALL_RESULT
  Write-Host ""
  Write-Host "Overall result: $overall"
  $failed = $result.SelectNodes("//TEST[@RESULT='FAIL']")
  foreach ($t in $failed) {
    Write-Host ("  FAIL  " + $t.NAME)
    foreach ($m in $t.SelectNodes(".//MESSAGE")) { Write-Host ("        " + $m.TEXT) }
  }
  $warned = $result.SelectNodes("//TEST[@RESULT='WARNING']")
  foreach ($t in $warned) { Write-Host ("  WARN  " + $t.NAME) }
  Write-Host "Full report: $Report"
} finally {
  if ($thumb) {
    Remove-Item "Cert:\LocalMachine\TrustedPeople\$thumb" -ErrorAction SilentlyContinue
    Remove-Item "Cert:\CurrentUser\My\$thumb" -ErrorAction SilentlyContinue
  }
  Get-AppxPackage -Name $name -ErrorAction SilentlyContinue | Where-Object { $_.Publisher -eq $publisher } |
    Remove-AppxPackage -ErrorAction SilentlyContinue
  Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}
