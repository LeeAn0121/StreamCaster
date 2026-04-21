param(
    [ValidateSet("win-x64", "win-x86")]
    [string]$Runtime = "win-x64",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $PSScriptRoot
$publishDir = Join-Path $projectRoot "bin\$Configuration\net9.0-windows\$Runtime\publish"

Write-Host "Publishing StreamCaster for $Runtime..."
dotnet publish $projectRoot `
    -c $Configuration `
    -r $Runtime `
    --self-contained false `
    /p:RestoreIgnoreFailedSources=true `
    /p:NuGetAudit=false

Write-Host ""
Write-Host "Publish completed:"
Write-Host $publishDir
