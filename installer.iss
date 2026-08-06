#define AppName "DeepSeek"
#define AppVersion "2.0.0"
#define AppPublisher "hujicheng-666"
#define AppExeName "DeepSeekMonitor.exe"

[Setup]
AppId={{C593B8E5-02D3-4A16-98BA-2E777B8B1D9B}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=release
OutputBaseFilename=DeepSeek-Setup-2.0.0
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\{#AppExeName}
SetupIconFile=assets\whale.ico

[Languages]
Name: "chinesesimp"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务："; Flags: unchecked
Name: "autostart"; Description: "开机自动启动 DeepSeek"; GroupDescription: "附加任务："; Flags: unchecked

[Files]
Source: "dist\DeepSeek\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "DeepSeek"; ValueData: """{app}\{#AppExeName}"""; Tasks: autostart; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#AppExeName}"; Description: "启动 {#AppName}"; Flags: nowait postinstall skipifsilent
