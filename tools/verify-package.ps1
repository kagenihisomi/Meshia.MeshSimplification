[CmdletBinding()]
param(
    [switch]$VerboseBuild
)

$ErrorActionPreference = 'Stop'

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$packageRoot = Resolve-Path (Join-Path $scriptDir '..')
$workspaceRoot = Resolve-Path (Join-Path $packageRoot '..\..')

$projects = @(
    'Meshia.MeshSimplification.Runtime.csproj',
    'Meshia.MeshSimplification.Editor.csproj',
    'Meshia.MeshSimplification.Ndmf.Runtime.csproj',
    'Meshia.MeshSimplification.Ndmf.Editor.csproj'
)

Write-Host "[verify] package root: $packageRoot"
Write-Host "[verify] workspace root: $workspaceRoot"

$buildVerbosity = if ($VerboseBuild) { 'normal' } else { 'minimal' }

foreach ($project in $projects) {
    $projectPath = Join-Path $workspaceRoot $project
    if (-not (Test-Path $projectPath)) {
        throw "[verify] Required project not found: $projectPath"
    }

    Write-Host "[verify] Building $project"
    dotnet build $projectPath -v $buildVerbosity --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "[verify] Build failed: $project"
    }
}

Write-Host '[verify] All package builds succeeded.'
