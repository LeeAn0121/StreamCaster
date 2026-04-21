#define MyAppName "StreamCaster"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "LeeAn"
#define MyAppExeName "StreamCaster.exe"
#define MyAppAssocName MyAppName + " Stream Utility"
#define MyAppAssocExt ".scast"
#define MyAppAssocKey StringChange(MyAppAssocName, " ", "") + MyAppAssocExt
#define SourceDir "..\bin\Release\net9.0-windows\win-x64\publish"

[Setup]
AppId={{E5F44FC4-935C-432A-BBD3-58454BD5B2A1}
AppName={#MyAppName}
AppVerName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
UninstallDisplayName={#MyAppName}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
OutputDir=.
OutputBaseFilename=StreamCasterSetup-x64
Compression=lzma
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\assets\StreamCaster.ico
UninstallDisplayIcon={app}\{#MyAppExeName}

[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"

[Tasks]
Name: "desktopicon"; Description: "바탕 화면 바로가기 생성"; GroupDescription: "추가 작업:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{#MyAppName} 실행"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{userdocs}\StreamCaster\logs"
Type: dirifempty; Name: "{userdocs}\StreamCaster"
