# Publishes a Files Companion release (run by the assistant after sign-in).
#
#   .\scripts\publish-release.ps1 -Version 1.2.3
#
# Requires: build.cmd has produced dist\*, docs\RELEASE-NOTES-v<version>.md
# exists, and gh is signed in (Login-GitHub.cmd). Uploads go through
# uploads.github.com, which stays reachable on this network; every call is
# retried because api.github.com is intermittent here.

param([Parameter(Mandatory = $true)][string]$Version)

$ErrorActionPreference = 'Stop'

$root  = Split-Path -Parent $PSScriptRoot
$gh    = Join-Path $root 'tools\gh\bin\gh.exe'
$repo  = 'Dannyzzy/Files-Companion'
$tag   = "v$Version"
$notes = Join-Path $root "docs\RELEASE-NOTES-$tag.md"
$setup = Join-Path $root 'dist\FilesCompanionSetup.exe'
$shim  = Join-Path $root 'dist\FilesOpen.exe'

if (-not (Test-Path $gh))    { throw "gh.exe not found at $gh" }
if (-not (Test-Path $notes)) { throw "release notes not found: $notes" }
foreach ($f in @($setup, $shim)) {
    if (-not (Test-Path $f)) { throw "missing build output: $f - run build.cmd first" }
}

& $gh auth status 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'gh is not signed in - run Login-GitHub.cmd first' }

function Invoke-Gh([string[]]$GhArgs) {
    for ($i = 1; $i -le 5; $i++) {
        & $gh @GhArgs 2>&1 | Write-Host
        if ($LASTEXITCODE -eq 0) { return }
        Write-Host "   attempt $i failed, retrying in 5s..."
        Start-Sleep -Seconds 5
    }
    throw "gh failed after 5 attempts: $($GhArgs -join ' ')"
}

Write-Host "== release $tag =="
Invoke-Gh @('release', 'create', $tag, $setup, $shim,
            '--repo', $repo,
            '--title', "Files Companion $Version",
            '--notes-file', $notes)

Write-Host '== published =='
& $gh release view $tag --repo $repo
