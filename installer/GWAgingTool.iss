#ifndef AppVersion
  #define AppVersion "0.2.0"
#endif
#ifndef SourceDir
  #error SourceDir is required
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts\installer"
#endif

[Setup]
AppId={{E8AC1C37-B197-4F44-91F5-3F2CE5941382}
AppName=GW 老化测试工具
AppVersion={#AppVersion}
AppVerName=GW 老化测试工具 {#AppVersion}
AppPublisher=GW Aging Tool Project
DefaultDirName={autopf}\GW Aging Tool
DefaultGroupName=GW 老化测试工具
DisableProgramGroupPage=yes
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.19045
AppMutex=Local\GW_AgingTool_Running
SetupMutex=GW_AgingTool_Setup
CloseApplications=no
RestartApplications=no
UsePreviousAppDir=yes
UninstallDisplayIcon={app}\Test.exe
UninstallDisplayName=GW 老化测试工具 {#AppVersion}
OutputDir={#OutputDir}
OutputBaseFilename=GW_AgingTool_Setup_{#AppVersion}_win-x64
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupLogging=yes
VersionInfoVersion={#AppVersion}.0

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "快捷方式："

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "installed.json"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\GW 老化测试工具\老化测试工具"; Filename: "{app}\Test.exe"; WorkingDir: "{app}"
Name: "{autoprograms}\GW 老化测试工具\本机兼容性检测"; Filename: "{app}\Test.exe"; Parameters: "--compatibility"; WorkingDir: "{app}"
Name: "{autoprograms}\GW 老化测试工具\使用说明"; Filename: "{app}\安装与多机型部署.md"
Name: "{autodesktop}\GW 老化测试工具"; Filename: "{app}\Test.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\Test.exe"; Description: "启动老化测试工具（真实模式，启动测试前需配置并确认）"; Flags: nowait postinstall skipifsilent runasoriginaluser

[Code]
function InitializeSetup(): Boolean;
var
  Version: TWindowsVersion;
begin
  GetWindowsVersionEx(Version);
  Result := (Version.Major = 10) and (Version.Build >= 19045) and
            (Version.ProductType = VER_NT_WORKSTATION);
  if not Result then
    SuppressibleMsgBox('此安装包支持 Windows 10 22H2 / Windows 11 原生 x64 客户端。Windows Server、ARM64、32 位和旧版 Windows 不在本版支持范围。', mbCriticalError, MB_OK, IDOK);
end;

function InitializeUninstall(): Boolean;
var
  ExitCode: Integer;
  Exe: String;
begin
  Result := False;
  if CheckForMutexes('Local\GW_AgingTool_Running') then begin
    SuppressibleMsgBox('老化工具正在运行，请先在工具中结束任务并退出，再卸载。', mbError, MB_OK, IDOK);
    Exit;
  end;
  Exe := ExpandConstant('{app}\Test.exe');
  if FileExists(Exe) then begin
    if not Exec(Exe, '--uninstall-cleanup', ExpandConstant('{app}'), SW_HIDE,
                ewWaitUntilTerminated, ExitCode) then begin
      SuppressibleMsgBox('无法清理本安装目录对应的登录续测任务。请修复安装后重试。', mbError, MB_OK, IDOK);
      Exit;
    end;
    if ExitCode <> 0 then begin
      SuppressibleMsgBox('登录续测任务清理失败，已保留程序。请以管理员身份重新卸载。', mbError, MB_OK, IDOK);
      Exit;
    end;
  end;
  { User configuration, checkpoints and logs are deliberately outside the application directory. }
  Result := True;
end;
