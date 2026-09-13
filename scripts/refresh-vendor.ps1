# Refresh vendor/RecycleBin from a local build of the sibling project.
# Usage: .\scripts\refresh-vendor.ps1 [-Source <path to Modern-Recycle-Bin>]

param([string]$Source)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$ven  = Join-Path $root 'vendor\RecycleBin'

# Default to the sibling checkout: clone the two repos side by side and this
# works on any machine. Pass -Source for anything else.
if (-not $Source) { $Source = Join-Path (Split-Path -Parent $root) 'Modern-Recycle-Bin' }

if (-not (Test-Path (Join-Path $Source 'dist\RecycleBin.exe'))) {
    throw "Build the source project first (its dist\RecycleBin.exe is missing): $Source"
}

New-Item -ItemType Directory -Path (Join-Path $ven 'ui') -Force | Out-Null
Copy-Item (Join-Path $Source 'dist\RecycleBin.exe')                  $ven -Force
Copy-Item (Join-Path $Source 'lib\Microsoft.Web.WebView2.Core.dll')  $ven -Force
Copy-Item (Join-Path $Source 'lib\WebView2Loader.dll')               $ven -Force
Copy-Item (Join-Path $Source 'lib\WebView2-LICENSE.txt')             $ven -Force
Copy-Item (Join-Path $Source 'src\ui\index.html') (Join-Path $ven 'ui') -Force

Write-Host 'vendor/RecycleBin refreshed.'