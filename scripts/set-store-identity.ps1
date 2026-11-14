# Rewrites Package.appxmanifest with the identity Partner Center reserved for the Store
# listing. Store builds need it; sideload builds keep CN=FrontierWingGaming.
# Usage: powershell -File scripts/set-store-identity.ps1 -Name <..> -Publisher <CN=..> -PublisherDisplayName <..>
# Commands assume the repository root is the cwd. Don't commit the result.
param(
  [Parameter(Mandatory = $true)][string]$Name,
  [Parameter(Mandatory = $true)][string]$Publisher,
  [Parameter(Mandatory = $true)][string]$PublisherDisplayName,
  [string]$Manifest = "Silolux/Package.appxmanifest"
)
$ErrorActionPreference = "Stop"
if ($Publisher -notmatch '^CN=') { throw "Publisher must be the full value from Partner Center, starting with CN=" }

$path = (Resolve-Path $Manifest).Path
$xml = New-Object System.Xml.XmlDocument
$xml.PreserveWhitespace = $true
$xml.Load($path)
$ns = New-Object System.Xml.XmlNamespaceManager $xml.NameTable
$ns.AddNamespace("m", "http://schemas.microsoft.com/appx/manifest/foundation/windows10")

$identity = $xml.SelectSingleNode("/m:Package/m:Identity", $ns)
$identity.SetAttribute("Name", $Name)
$identity.SetAttribute("Publisher", $Publisher)
$xml.SelectSingleNode("/m:Package/m:Properties/m:PublisherDisplayName", $ns).InnerText = $PublisherDisplayName

$xml.Save($path)
Write-Host "Store identity applied: $Name / $Publisher / $PublisherDisplayName"
