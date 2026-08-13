#define AppName "DeepSeek"
#define AppVersion "2.0.0"
#define AppPublisher "hujicheng-666"
#define AppExeName "DeepSeekMonitor.exe"

[Setup]
AppId={{C593B8E5-02D3-4A16-98BA-2E777B8B1D9B}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
; 默认安装目录名固定为 DeepSeekMonitor(不要用 {#AppName},它叫 DeepSeek)
DefaultDirName={localappdata}\Programs\DeepSeekMonitor
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

[Code]
// 把 Inno 默认的 unins000.exe 卸载程序重命名为 uninstall.exe(数据文件需同名跟随),
// 并同步更新「设置/应用」里的卸载入口,使其指向新文件名。
procedure CurStepChanged(CurStep: TSetupStep);
var
  AppDir: String;
begin
  if CurStep = ssPostInstall then
  begin
    AppDir := ExpandConstant('{app}');
    if FileExists(AppDir + '\unins000.exe') then
    begin
      RenameFile(AppDir + '\unins000.exe', AppDir + '\uninstall.exe');
      if FileExists(AppDir + '\unins000.dat') then
        RenameFile(AppDir + '\unins000.dat', AppDir + '\uninstall.dat');
    end;
    RegWriteStringValue(HKCU,
      'Software\Microsoft\Windows\CurrentVersion\Uninstall\{C593B8E5-02D3-4A16-98BA-2E777B8B1D9B}_is1',
      'UninstallString', '"' + AppDir + '\uninstall.exe"');
    RegWriteStringValue(HKCU,
      'Software\Microsoft\Windows\CurrentVersion\Uninstall\{C593B8E5-02D3-4A16-98BA-2E777B8B1D9B}_is1',
      'QuietUninstallString', '"' + AppDir + '\uninstall.exe" /SILENT');
  end;
end;
