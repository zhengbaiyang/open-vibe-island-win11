; ============================================================================
;  Open Island — Windows 11 installer (Inno Setup 6.7+)
;
;  Build with:  .\scripts\package.ps1
;  Command line defines (optional):
;    /DAppVersion=0.1.8  /DAppDisplayVersion=0.1.8
;    /DSourceDir=...       /DOutputDir=...
; ============================================================================

#ifndef AppVersion
  #define AppVersion "0.1.8"
#endif
#ifndef AppDisplayVersion
  #define AppDisplayVersion "0.1.8"
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\publish\win-x64"
#endif
#ifndef OutputDir
  #define OutputDir "..\dist"
#endif

#define AppName "Open Island"
#define AppShortName "OpenIsland"
#define AppPublisher "Open Island"
#define AppURL "https://github.com/zhengbaiyang/open-vibe-island-win11"
#define AppExeName "OpenIsland.exe"

[Setup]
; Stable AppId — keep this GUID forever so upgrades replace instead of stacking.
AppId={{7C1E9D42-5B3A-4F0E-9B6D-2F8A41C63E17}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppDisplayVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}
AppUpdatesURL={#AppURL}/releases
VersionInfoVersion={#AppVersion}
VersionInfoDescription={#AppName} Setup
VersionInfoProductName={#AppName}
VersionInfoCompany={#AppPublisher}
VersionInfoCopyright=GNU GPL-3.0 · upstream Octane0411/open-vibe-island

; Per-user install by default (no UAC); users may choose "install for all users"
; on the privileges page or with /ALLUSERS.
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog commandline
UsePreviousAppDir=yes
AllowNoIcons=yes

OutputDir={#OutputDir}
#ifdef TestBuild
; Verification build for restricted CI/sandbox environments: no shell integration.
OutputBaseFilename=OpenIsland-Setup-TEST-win-x64
CreateUninstallRegKey=no
#else
OutputBaseFilename={#AppShortName}-Setup-{#AppVersion}-win-x64
CreateUninstallRegKey=yes
#endif
SetupIconFile=OpenIsland.ico
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}

Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern dynamic
WizardSizePercent=110
LicenseFile=..\LICENSE
InfoBeforeFile=before-install.txt

ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.22000

; The app is a background tray utility; the setup force-closes it in [Code] before
; copying files, so Restart Manager prompts are not needed.
CloseApplications=no
RestartApplications=no
SetupLogging=yes

[Languages]
Name: "chinesesimplified"; MessagesFile: "ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; All self-contained binaries + plugins + docs are staged by scripts\package.ps1.
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Comment: "Open Island — AI coding agent control center"; Check: not SetupTestMode
Name: "{autoprograms}\卸载 {#AppName}"; Filename: "{uninstallexe}"; Comment: "Uninstall {#AppName}"; Check: not SetupTestMode
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon; Check: not SetupTestMode

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(AppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent; Check: not IsAutoUpdate and not SetupTestMode
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(AppName, '&', '&&')}}"; Flags: nowait postinstall; Check: IsAutoUpdate and not SetupTestMode

[UninstallRun]
; Make sure the floating island is not running while its files are removed.
Filename: "{cmd}"; Parameters: "/C taskkill /IM {#AppExeName} /F >nul 2>&1"; Flags: runhidden; RunOnceId: "StopIsland"
; Clean up the app's launch-at-startup entry (managed by the app's settings).
Filename: "{cmd}"; Parameters: "/C reg delete ""HKCU\Software\Microsoft\Windows\CurrentVersion\Run"" /v OpenIsland /f >nul 2>&1"; Flags: runhidden; RunOnceId: "RemoveStartupEntry"

[Code]
{ Test hook (used by scripts/package.ps1 verification): when the environment variable
  OPEN_ISLAND_SETUP_TEST=1, shell integration (shortcuts) is skipped so the payload
  and uninstaller can be validated in restricted CI environments. }
function SetupTestMode(): Boolean;
begin
  Result := CompareText(GetEnv('OPEN_ISLAND_SETUP_TEST'), '1') = 0;
end;

{ Returns True when invoked with /AUTOUPDATE (for unattended in-app updates that want
  the installer itself to relaunch Open Island after a silent upgrade). }
function IsAutoUpdate(): Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
  begin
    if CompareText(ParamStr(I), '/AUTOUPDATE') = 0 then
    begin
      Result := True;
      Exit;
    end;
  end;
end;

function IsAppMutexPresent(): Boolean;
begin
  Result := CheckForMutexes('Local\OpenIslandWin11SingleInstance');
end;

function IsInstalledExeLocked(): Boolean;
var
  ExePath: String;
  Stream: TFileStream;
begin
  Result := False;
  ExePath := ExpandConstant('{app}\{#AppExeName}');
  if not FileExists(ExePath) then
    Exit;
  try
    Stream := TFileStream.Create(ExePath, fmOpenReadWrite);
    Stream.Free;
  except
    Result := True;
  end;
end;

function IsAppBusy(): Boolean;
begin
  Result := IsAppMutexPresent() or IsInstalledExeLocked();
end;

{ Wait for a running island to finish exiting (or force-close it) before overwriting files. }
procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
  Attempt: Integer;
begin
  if CurStep = ssInstall then
  begin
    { During auto-update, give the app a brief grace window to finish exiting cleanly
      and release file handles before force-killing. }
    if IsAutoUpdate() then
    begin
      Attempt := 0;
      while (Attempt < 16) and IsAppBusy() do
      begin
        Sleep(250);
        Attempt := Attempt + 1;
      end;
    end;

    { Force-close if still running or locked, then wait until both the process mutex
      and executable file lock have cleared before Inno Setup overwrites files. }
    if IsAppBusy() then
    begin
      Exec(ExpandConstant('{cmd}'), '/C taskkill /IM {#AppExeName} /F >nul 2>&1',
        '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

      Attempt := 0;
      while (Attempt < 20) and IsAppBusy() do
      begin
        Sleep(250);
        Attempt := Attempt + 1;
      end;
      Sleep(250);
    end;
  end;
end;

{ The app stores session/settings data in %LOCALAPPDATA%\OpenIsland. We keep it on
  uninstall so users do not lose their hook configuration and preferences. }
