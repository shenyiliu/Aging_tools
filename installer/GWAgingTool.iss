#ifndef AppVersion
  #define AppVersion "0.3.4"
#endif
#ifndef SourceDir
  #error SourceDir is required
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts\installer"
#endif

[Setup]
#ifdef QAAppId
AppId={#QAAppId}
#else
AppId={{E8AC1C37-B197-4F44-91F5-3F2CE5941382}
#endif
AppName=GMK 老化测试工具
AppVersion={#AppVersion}
AppVerName=GMK 老化测试工具 {#AppVersion}
AppPublisher=GMK
DefaultDirName={autopf}\GMK Aging Tool
DefaultGroupName=GMK 老化测试工具
DisableProgramGroupPage=yes
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.19045
AppMutex=Local\GW_AgingTool_Running
#ifdef QAAppId
SetupMutex=GW_AgingTool_Setup_QA
#else
SetupMutex=GW_AgingTool_Setup
#endif
CloseApplications=no
RestartApplications=no
UsePreviousAppDir=yes
DisableDirPage=no
SetupIconFile=..\src\Aging.WinForms\Branding\gmktec.ico
UninstallDisplayIcon={app}\Test.exe
UninstallDisplayName=GMK 老化测试工具 {#AppVersion}
OutputDir={#OutputDir}
OutputBaseFilename=GMK_AgingTool_Setup_{#AppVersion}_win-x64
#ifdef QAAppId
Compression=zip
SolidCompression=no
#else
Compression=lzma2
SolidCompression=yes
#endif
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

#ifndef QAAppId
[Icons]
Name: "{autoprograms}\GMK 老化测试工具\GMK 老化测试工具"; Filename: "{app}\Test.exe"; WorkingDir: "{app}"
Name: "{autoprograms}\GMK 老化测试工具\本机兼容性检测"; Filename: "{app}\Test.exe"; Parameters: "--compatibility"; WorkingDir: "{app}"
Name: "{autoprograms}\GMK 老化测试工具\使用说明"; Filename: "{app}\安装与多机型部署.md"
Name: "{autodesktop}\GMK 老化测试工具"; Filename: "{app}\Test.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[InstallDelete]
Type: files; Name: "{autodesktop}\GW 老化测试工具.lnk"
Type: files; Name: "{autoprograms}\GW 老化测试工具\老化测试工具.lnk"
Type: files; Name: "{autoprograms}\GW 老化测试工具\本机兼容性检测.lnk"
Type: files; Name: "{autoprograms}\GW 老化测试工具\使用说明.lnk"
Type: dirifempty; Name: "{autoprograms}\GW 老化测试工具"
#endif

[InstallDelete]
Type: files; Name: "{app}\Tools\SFT\SFTClassic.exe"
Type: files; Name: "{app}\Tools\3DMark\3DMarkTest.ini"

[Run]
Filename: "{app}\Test.exe"; Description: "启动老化测试工具（管理员权限，启动测试前需配置并确认）"; Flags: nowait postinstall skipifsilent shellexec runasoriginaluser

[Code]
function OpenReplaceProbe(FileName: String; Access, Share: Cardinal;
  Security: Integer; Creation, Attributes: Cardinal; Template: Integer): Integer;
  external 'CreateFileW@kernel32.dll stdcall';
function CloseReplaceProbe(Handle: Integer): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';
function ReplaceProbeError(): Cardinal;
  external 'GetLastError@kernel32.dll stdcall';

function FindBlockedFile(const Directory: String; var BlockedPath: String;
  var ErrorCode: Cardinal): Boolean;
var
  Entry: TFindRec;
  Path: String;
  Handle: Integer;
begin
  Result := False;
  if not FindFirst(AddBackslash(Directory) + '*', Entry) then Exit;
  try
    repeat
      if (Entry.Name <> '.') and (Entry.Name <> '..') then begin
        Path := AddBackslash(Directory) + Entry.Name;
        if (Entry.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then begin
          if (Entry.Attributes and $400) = 0 then
            Result := FindBlockedFile(Path, BlockedPath, ErrorCode);
        end else if Pos('unins', Lowercase(Entry.Name)) <> 1 then begin
          { Open for overwrite/delete access without changing any bytes. A readable
            file can still deny replacement, as with the reported error 32. }
          Handle := OpenReplaceProbe(Path, $40010000, 7, 0, 3, 0, 0);
          if Handle = -1 then begin
            ErrorCode := ReplaceProbeError();
            BlockedPath := Path;
            Result := True;
          end else
            CloseReplaceProbe(Handle);
        end;
        if Result then Exit;
      end;
    until not FindNext(Entry);
  finally
    FindClose(Entry);
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  BlockedPath: String;
  ErrorCode: Cardinal;
begin
  Result := '';
  if FindBlockedFile(ExpandConstant('{app}'), BlockedPath, ErrorCode) then begin
    Log('GMK_INSTALL_LOCK_BLOCKED: ' + BlockedPath + '; Windows error ' + IntToStr(ErrorCode));
    Result := '安装尚未开始，未替换任何程序文件。' + #13#10 + #13#10 +
      '以下文件被其他程序或系统服务占用，或当前无替换权限：' + #13#10 +
      BlockedPath + #13#10 + 'Windows 错误：' + IntToStr(ErrorCode) + #13#10 + #13#10 +
      '请先结束老化测试并退出工具，再释放相关占用后重试。' + #13#10 +
      '如果占用来自系统服务，请联系管理员处理，或重启 Windows 后再安装。' + #13#10 +
      '请勿跳过文件，否则可能留下新旧文件混装、无法启动的程序。';
  end;
end;

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
    { Current-user uninstall may be unelevated; the cleanup entry now requires admin too. }
    if not ShellExec('runas', Exe, '--uninstall-cleanup', ExpandConstant('{app}'), SW_HIDE,
                ewWaitUntilTerminated, ExitCode) then begin
      SuppressibleMsgBox('无法以管理员权限清理本安装目录对应的登录续测任务。请允许 UAC 授权或以管理员身份重试卸载。', mbError, MB_OK, IDOK);
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
