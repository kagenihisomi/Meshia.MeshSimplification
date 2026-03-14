[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$packageRoot = Resolve-Path (Join-Path $scriptDir '..')
$hooksPath = Join-Path $packageRoot '.githooks'

if (-not (Test-Path $hooksPath)) {
    throw "Hooks directory not found: $hooksPath"
}

Write-Host "[hooks] Setting core.hooksPath to $hooksPath"
git -C $packageRoot config core.hooksPath .githooks

if ($LASTEXITCODE -ne 0) {
    throw '[hooks] Failed to configure git hooks path.'
}

Write-Host '[hooks] Installed. Pre-commit checks will run on commit in this repository.'
