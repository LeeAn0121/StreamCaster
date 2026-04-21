#Requires -Version 7
$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $PSScriptRoot
$publishDir  = Join-Path $projectRoot "bin\Release\net9.0-windows\win-x64\publish"
$issFile     = Join-Path $projectRoot "installer\StreamCaster.iss"
$outputExe   = Join-Path $projectRoot "installer\StreamCasterSetup-x64.exe"

# ── 1. Publish ──────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "[1/3] dotnet publish..." -ForegroundColor Cyan

dotnet publish $projectRoot `
    -c Release `
    -r win-x64 `
    --self-contained true `
    /p:PublishSingleFile=false `
    /p:RestoreIgnoreFailedSources=true `
    /p:NuGetAudit=false

if ($LASTEXITCODE -ne 0) { Write-Error "publish 실패"; exit 1 }

# ── 2. FFmpeg 복사 ──────────────────────────────────────────────────────────
Write-Host ""
Write-Host "[2/3] ffmpeg.exe 탐색 및 복사..." -ForegroundColor Cyan

$ffmpegDst = Join-Path $publishDir "ffmpeg.exe"

# 이미 publish 폴더에 있으면 스킵
if (Test-Path $ffmpegDst) {
    Write-Host "  ffmpeg.exe 이미 존재: $ffmpegDst" -ForegroundColor Green
} else {
    # 탐색 우선순위
    $candidates = @(
        (Get-Command ffmpeg -ErrorAction SilentlyContinue)?.Source,
        "$env:LOCALAPPDATA\Microsoft\WinGet\Links\ffmpeg.exe",
        "$env:ProgramFiles\ffmpeg\bin\ffmpeg.exe",
        "${env:ProgramFiles(x86)}\ffmpeg\bin\ffmpeg.exe"
    ) | Where-Object { $_ -and (Test-Path $_) }

    if (-not $candidates) {
        Write-Host ""
        Write-Host "  [오류] ffmpeg.exe를 찾을 수 없습니다." -ForegroundColor Red
        Write-Host "  ffmpeg.exe를 다음 경로에 직접 복사한 뒤 다시 실행하세요:" -ForegroundColor Yellow
        Write-Host "  $publishDir" -ForegroundColor Yellow
        exit 1
    }

    $ffmpegSrc = $candidates[0]
    Write-Host "  복사: $ffmpegSrc -> $ffmpegDst" -ForegroundColor Green
    Copy-Item $ffmpegSrc $ffmpegDst -Force
}

# ── 3. Inno Setup 컴파일 ────────────────────────────────────────────────────
Write-Host ""
Write-Host "[3/3] Inno Setup 컴파일..." -ForegroundColor Cyan

$iscc = @(
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
    "C:\Program Files\Inno Setup 6\ISCC.exe",
    "C:\Program Files (x86)\Inno Setup 5\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $iscc) {
    Write-Host ""
    Write-Host "  [오류] Inno Setup을 찾을 수 없습니다." -ForegroundColor Red
    Write-Host "  https://jrsoftware.org/isdl.php 에서 설치 후 재실행하세요." -ForegroundColor Yellow
    exit 1
}

& $iscc $issFile
if ($LASTEXITCODE -ne 0) { Write-Error "Inno Setup 컴파일 실패"; exit 1 }

# ── 완료 ────────────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "✅ 인스톨러 생성 완료:" -ForegroundColor Green
Write-Host "   $outputExe" -ForegroundColor White
