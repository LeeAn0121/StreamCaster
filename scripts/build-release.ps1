param(
    [ValidateSet("win-x64", "win-x86")]
    [string]$Runtime = "win-x64",
    [string]$Configuration = "Release",
    [switch]$InstallerOnly,
    [switch]$PortableOnly
)

$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $PSScriptRoot
$installerScript = Join-Path $PSScriptRoot "build-installer.ps1"
$portableScript = Join-Path $PSScriptRoot "publish-singlefile.ps1"
$installerOutput = Join-Path $projectRoot "installer\StreamCasterSetup-x64.exe"
$portableOutput = Join-Path $projectRoot "bin\$Configuration\net9.0-windows\$Runtime\singlefile\StreamCaster.exe"

if ($InstallerOnly -and $PortableOnly) {
    throw "InstallerOnly와 PortableOnly는 동시에 사용할 수 없습니다."
}

$buildInstaller = -not $PortableOnly
$buildPortable = -not $InstallerOnly
$step = 0
$total = @($buildInstaller, $buildPortable).Where({ $_ }).Count

Write-Host ""
Write-Host "StreamCaster release build" -ForegroundColor Cyan
Write-Host "  Runtime       : $Runtime"
Write-Host "  Configuration : $Configuration"
Write-Host "  Installer     : $buildInstaller"
Write-Host "  Portable      : $buildPortable"

if ($buildInstaller) {
    $step++
    Write-Host ""
    Write-Host "[$step/$total] Building installer..." -ForegroundColor Cyan
    & $installerScript
    if ($LASTEXITCODE -ne 0) { throw "설치형 빌드 실패" }
}

if ($buildPortable) {
    $step++
    Write-Host ""
    Write-Host "[$step/$total] Building portable single-file exe..." -ForegroundColor Cyan
    & $portableScript -Runtime $Runtime -Configuration $Configuration
    if ($LASTEXITCODE -ne 0) { throw "포터블 빌드 실패" }
}

Write-Host ""
Write-Host "Build completed." -ForegroundColor Green
if ($buildInstaller) {
    Write-Host "  Installer : $installerOutput"
}
if ($buildPortable) {
    Write-Host "  Portable  : $portableOutput"
}
