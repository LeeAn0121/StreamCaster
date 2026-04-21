param(
    [ValidateSet("win-x64", "win-x86")]
    [string]$Runtime = "win-x64",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $PSScriptRoot
$toolsDir = Join-Path $projectRoot "tools"
$embeddedFfmpeg = Join-Path $toolsDir "ffmpeg.exe"
$publishDir = Join-Path $projectRoot "bin\$Configuration\net9.0-windows\$Runtime\singlefile"

if (-not (Test-Path $toolsDir)) {
    New-Item -ItemType Directory -Path $toolsDir | Out-Null
}

$candidates = @(
    (Get-Command ffmpeg -ErrorAction SilentlyContinue)?.Source,
    (Join-Path $projectRoot "bin\Release\net9.0-windows\win-x64\publish\ffmpeg.exe"),
    "$env:LOCALAPPDATA\Microsoft\WinGet\Links\ffmpeg.exe",
    "$env:ProgramFiles\ffmpeg\bin\ffmpeg.exe",
    "${env:ProgramFiles(x86)}\ffmpeg\bin\ffmpeg.exe"
) | Where-Object { $_ -and (Test-Path $_) }

if (-not $candidates) {
    Write-Error "ffmpeg.exe를 찾을 수 없습니다. 단일 실행 파일 배포에는 ffmpeg 내장이 필요합니다."
}

Copy-Item $candidates[0] $embeddedFfmpeg -Force

Write-Host "Publishing StreamCaster single-file for $Runtime..."
dotnet publish $projectRoot `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -o $publishDir `
    /p:PublishSingleFile=true `
    /p:IncludeNativeLibrariesForSelfExtract=true `
    /p:EnableCompressionInSingleFile=true `
    /p:RestoreIgnoreFailedSources=true `
    /p:NuGetAudit=false

Write-Host ""
Write-Host "Single-file publish completed:"
Write-Host (Join-Path $publishDir "StreamCaster.exe")
