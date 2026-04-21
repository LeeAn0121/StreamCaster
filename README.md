# StreamCaster

Windows x86/x64용 라이브 스트리밍 유틸리티입니다. WPF UI에서 입력 파일, 자막, UDP/RTSP/HTTP 송출, 랜카드 선택, Packet Size, TTL, Loop, Start/End 제어를 제공합니다.

## 지원 항목

- Input: `TS`, `MP4`, `MKV`, `MOV`, `M2TS`
- Subtitle: `SRT`, `ASS`, `SSA`, `VTT`
- Output Protocol: `UDP`, `RTSP`, `HTTP`
- Network Interface 선택
- 실시간 표시: `STATUS`, `pcr_buffered`, `Bytes`, `Bitrate`, `Speed`, `Position`

## 요구사항

- `.NET 9 SDK`
- `ffmpeg` 실행 가능 환경
  기본값은 `ffmpeg`이며, UI에서 실행 파일 경로를 직접 지정할 수 있습니다.

## 실행

```powershell
dotnet run
```

개발 빌드 실행 파일:

```text
bin\Debug\net9.0-windows\StreamCaster.exe
```

## 빌드

```powershell
dotnet build /p:RestoreIgnoreFailedSources=true /p:NuGetAudit=false
```

## 실행 파일 만들기

배포용 실행 파일 세트는 아래 스크립트로 생성합니다.

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\publish.ps1 -Runtime win-x64
powershell -ExecutionPolicy Bypass -File .\scripts\publish.ps1 -Runtime win-x86
```

생성 위치:

```text
bin\Release\net9.0-windows\win-x64\publish\
bin\Release\net9.0-windows\win-x86\publish\
```

중요:

- 이 프로젝트는 `publish` 폴더 안의 `StreamCaster.exe`를 실행하면 됩니다.
- 현재 환경에서는 오프라인 제약 때문에 `PublishSingleFile=true` 단일 파일 배포는 실패했습니다.
- 따라서 배포 시 `publish` 폴더 전체를 함께 전달해야 합니다.

## 단일 실행 파일 만들기

설치 없이 실행 가능한 단일 파일 배포는 아래 스크립트로 생성합니다. 이 모드에서는 `ffmpeg.exe`를 앱 내부 리소스로 포함하고, 실행 시 사용자 로컬 폴더로 자동 추출합니다.

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\publish-singlefile.ps1 -Runtime win-x64
```

생성 위치:

```text
bin\Release\net9.0-windows\win-x64\singlefile\StreamCaster.exe
```

주의:

- 단일 파일 publish는 `self-contained` 런타임 패키지가 필요합니다.
- 현재처럼 오프라인 제약이 있는 환경에서는 `dotnet publish`가 실패할 수 있습니다.
- 이 경우 인터넷이 가능한 빌드 환경에서 위 스크립트를 실행해야 합니다.

## 설치형 + 포터블형 한 번에 만들기

설치 프로그램과 포터블 단일 실행 파일을 한 번에 생성하려면 아래 스크립트를 사용합니다.

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build-release.ps1 -Runtime win-x64
```

부분 빌드도 가능합니다.

```powershell
# 설치형만
powershell -ExecutionPolicy Bypass -File .\scripts\build-release.ps1 -InstallerOnly

# 포터블형만
powershell -ExecutionPolicy Bypass -File .\scripts\build-release.ps1 -Runtime win-x64 -PortableOnly
```

산출물:

```text
installer\StreamCasterSetup-x64.exe
bin\Release\net9.0-windows\win-x64\singlefile\StreamCaster.exe
```

포터블 단일 실행 파일 메모:

- 다른 PC로 `StreamCaster.exe` 하나만 복사해서 실행할 수 있습니다.
- 첫 실행 시 내장된 `ffmpeg.exe`를 `%LocalAppData%\StreamCaster\runtime\ffmpeg.exe` 로 자동 추출합니다.
- Windows Defender/SmartScreen 경고가 뜰 수 있으며, 허용 후 실행하면 됩니다.

## 설치 프로그램 만들기

저장소에 Inno Setup 스크립트 [installer/StreamCaster.iss](</E:/Steram Project/StreamCaster/installer/StreamCaster.iss>)를 추가해 두었습니다.

1. Inno Setup 6 설치
2. `scripts\publish.ps1 -Runtime win-x64` 실행
3. Inno Setup Compiler에서 `installer\StreamCaster.iss` 열기
4. Compile 실행

그러면 아래 설치 파일이 생성됩니다.

```text
installer\StreamCasterSetup-x64.exe
```

표시명/언인스톨 메모:

- 설치 앱 목록에는 `StreamCaster` 이름만 표시되도록 설정되어 있습니다.
- 언인스톨 시 현재 사용자 문서 폴더의 `StreamCaster\logs` 폴더도 함께 삭제되도록 설정되어 있습니다.

## self-contained 배포 예시

인터넷/패키지 접근이 가능한 일반 환경에서는 다음도 시도할 수 있습니다.

```powershell
dotnet publish -c Release -r win-x64 --self-contained true
dotnet publish -c Release -r win-x86 --self-contained true
```

## 동작 메모

- UDP는 `localaddr`, `pkt_size`, `ttl`을 출력 URL에 반영합니다.
- RTSP는 `rtsp://host:port/live/stream` 형식 기본값을 사용합니다.
- HTTP는 `http://host:port/live.ts` 형식 기본값을 사용합니다.
- 자막 파일이 지정되면 `ffmpeg subtitles` 필터로 영상에 burn-in 처리합니다.
- `pcr_buffered`는 ffmpeg 로그에 해당 값이 있을 때만 실시간 반영됩니다.
