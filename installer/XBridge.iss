; Driver payloads: ONLY ViGEmBus and HidHide - the gamepad needs no vendor driver, rumble goes
; through the inbox hidusb.sys stack. .NET 8 Desktop Runtime is NOT bundled (downloaded if missing).

#define AppName       "XBridge"
#define AppShortName  "XBridge"
#define AppVersion    "2.2.0"
#define AppPublisher  "XBridge"
#define AppExe        "XBridge.exe"
#define PublishDir    "..\publish"
#define ViGEmSetup    "ViGEmBusSetup_x64.exe"
#define HidHideSetup  "HidHideSetup_x64.exe"

#if !FileExists(AddBackslash(SourcePath) + "redist\" + ViGEmSetup)
  #error Missing installer\redist\ViGEmBusSetup_x64.exe -- download it from the ViGEmBus GitHub releases and put it there.
#endif
#if !FileExists(AddBackslash(SourcePath) + "redist\" + HidHideSetup)
  #error Missing installer\redist\HidHideSetup_x64.exe -- download it from the HidHide GitHub releases and put it there.
#endif

[Setup]
AppId={{6F3A9E2B-7C41-4B8E-9D2A-1E5C0A7B4D33}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\{#AppShortName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#AppExe}
SetupIconFile=..\src\Assets\XBridge.ico
OutputBaseFilename={#AppShortName}-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; admin is required ONLY for: writing to Program Files, running the two bundled
; driver installers, and (optional) creating a scheduled task.
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion
Source: "redist\{#ViGEmSetup}";   Flags: dontcopy
Source: "redist\{#HidHideSetup}"; Flags: dontcopy

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; \
  ValueName: "{#AppShortName}"; ValueData: """{app}\{#AppExe}"""; Flags: uninsdeletevalue

[Icons]
Name: "{group}\{#AppName}";                 Filename: "{app}\{#AppExe}"
Name: "{group}\Calibrate ({#AppName})";     Filename: "{app}\{#AppExe}"; Parameters: "--calibrate"
Name: "{group}\Uninstall {#AppName}";       Filename: "{uninstallexe}"

[Run]
Filename: "{app}\{#AppExe}"; Description: "Start {#AppName} now"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{localappdata}\{#AppShortName}\logs"

[Code]
const
  SERVICE_VIGEM   = 'ViGEmBus';
  SERVICE_HIDHIDE = 'HidHide';
  { Evergreen link - always the latest .NET 8.0.x Windows Desktop Runtime, x64. }
  DOTNET_URL      = 'https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe';

var
  { A freshly (re)installed HidHide/ViGEmBus filter only attaches to devices that are
    re-enumerated afterward - already-plugged-in gamepads need a reboot or replug. }
  DriversJustInstalled: Boolean;

function NetDesktop8Installed: Boolean;
var
  fr: TFindRec;
begin
  { The app is framework-dependent WinForms: it needs Microsoft.WindowsDesktop.App 8.x. }
  Result := False;
  if FindFirst(ExpandConstant('{commonpf}\dotnet\shared\Microsoft.WindowsDesktop.App\8.*'), fr) then
  try
    repeat
      if (fr.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
      begin
        Result := True;
        Break;
      end;
    until not FindNext(fr);
  finally
    FindClose(fr);
  end;
end;

function OnDotNetDownloadProgress(const Url, FileName: String; const Progress, ProgressMax: Int64): Boolean;
begin
  Result := True;
end;

procedure EnsureDotNetDesktopRuntime;
var
  exe: String;
  rc: Integer;
begin
  if NetDesktop8Installed then
  begin
    Log('.NET 8 Desktop Runtime (x64) present.');
    Exit;
  end;

  Log('.NET 8 Desktop Runtime (x64) NOT found - downloading ' + DOTNET_URL);
  try
    DownloadTemporaryFile(DOTNET_URL, 'windowsdesktop-runtime-8-x64.exe', '', @OnDotNetDownloadProgress);
  except
    MsgBox('Could not download the .NET 8 Desktop Runtime (no internet?).' + #13#10#13#10 +
           'Install it manually from:' + #13#10 + DOTNET_URL + #13#10#13#10 +
           'then run this setup again.', mbCriticalError, MB_OK);
    Abort;
  end;

  exe := ExpandConstant('{tmp}\windowsdesktop-runtime-8-x64.exe');
  Log('Installing .NET 8 Desktop Runtime silently: ' + exe);
  if not Exec(exe, '/install /quiet /norestart', '', SW_SHOW, ewWaitUntilTerminated, rc) then
    rc := -1;

  if (rc = 0) or (rc = 3010) then          { 3010 = success, reboot advised }
    Log('.NET 8 Desktop Runtime installed (exit ' + IntToStr(rc) + ').')
  else if (rc = 1638) or NetDesktop8Installed then
    Log('.NET 8 Desktop Runtime already present / newer (exit ' + IntToStr(rc) + ').')
  else
  begin
    MsgBox('The .NET 8 Desktop Runtime installer returned exit code ' + IntToStr(rc) + '.' + #13#10 +
           '{#AppName} will not start until it is installed - get it from:' + #13#10 + DOTNET_URL,
           mbError, MB_OK);
    Abort;
  end;
end;

function ServiceExists(const ServiceName: string): Boolean;
var
  rc: Integer;
begin
  { "sc query" returns 0 when the service exists (even if stopped), 1060 otherwise. }
  Result := Exec(ExpandConstant('{sys}\sc.exe'), 'query "' + ServiceName + '"', '',
                 SW_HIDE, ewWaitUntilTerminated, rc) and (rc = 0);
end;

procedure InstallDriverIfMissing(const SetupFile, ServiceName, Nice: string);
var
  rc: Integer;
  tmp: string;
begin
  if ServiceExists(ServiceName) then
  begin
    Log(Nice + ': service present, skipping bundled installer.');
    Exit;
  end;

  ExtractTemporaryFile(SetupFile);
  tmp := ExpandConstant('{tmp}\' + SetupFile);
  Log('Installing ' + Nice + ' silently from ' + tmp);

  { Advanced Installer EXE bootstrapper: /exenoui runs with no UI, trailing args go to msiexec. }
  if not Exec(tmp, '/exenoui /qn /norestart', '', SW_HIDE, ewWaitUntilTerminated, rc) then
    rc := -1;

  if (rc = 0) or (rc = 3010) then          { 3010 = success, reboot advised }
  begin
    Log(Nice + ' installed (exit ' + IntToStr(rc) + ').');
    DriversJustInstalled := True;
  end
  else
    MsgBox(Nice + ' installer returned exit code ' + IntToStr(rc) + '.' + #13#10 +
           'You may have to install it manually before ' + '{#AppName}' + ' will work.',
           mbError, MB_OK);
end;

procedure KillRunningApp();
var
  rc: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM {#AppExe}',    '', SW_HIDE, ewWaitUntilTerminated, rc);
  Sleep(800);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM {#AppExe}', '', SW_HIDE, ewWaitUntilTerminated, rc);
  Sleep(400);
end;

{ Let the app release its HidHide entries (device un-hide + app un-reg + cloak off).
  Run only after the app is stopped, or its periodic re-hide could undo it. }
function ReleaseHidHide(): Boolean;
var
  rc: Integer;
  exe: string;
begin
  Result := True;
  exe := ExpandConstant('{app}\{#AppExe}');
  if FileExists(exe) then
  begin
    Result := Exec(exe, '--hidhide-cleanup', '', SW_HIDE, ewWaitUntilTerminated, rc) and (rc = 0);
    Log('XBridge --hidhide-cleanup exit ' + IntToStr(rc));
  end;
end;

{ Pre-seed HidHide's allow-list with our exe (via the managed HidHide API, elevated here)
  so the app can open the driver as a standard user on first run. }
procedure RegisterWithHidHide;
var
  rc: Integer;
begin
  Exec(ExpandConstant('{app}\{#AppExe}'), '--hidhide-register', '', SW_HIDE, ewWaitUntilTerminated, rc);
  Log('XBridge --hidhide-register exit ' + IntToStr(rc));
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  rc: Integer;
begin
  if CurStep = ssInstall then
  begin
    { On update / reinstall: stop the old instance so its exe is not locked. Pads stay hidden on
      purpose - the new version inherits hidden-devices.txt, and un-hiding them here would make
      them visible to browsers again until they are reconnected. }
    KillRunningApp();
    { .NET is NOT bundled - fetch + silently install the runtime if it is missing. }
    EnsureDotNetDesktopRuntime();
    InstallDriverIfMissing('{#ViGEmSetup}',   SERVICE_VIGEM,   'ViGEmBus');
    InstallDriverIfMissing('{#HidHideSetup}', SERVICE_HIDHIDE, 'HidHide');
  end;

  if CurStep = ssPostInstall then
  begin
    RegisterWithHidHide();
    { Drop a leftover elevated-startup task from an older install - it double-starts the app. }
    Exec(ExpandConstant('{sys}\schtasks.exe'), '/Delete /F /TN "{#AppShortName}"',
         '', SW_HIDE, ewWaitUntilTerminated, rc);
  end;
end;

function NeedRestart(): Boolean;
begin
  Result := DriversJustInstalled;
end;

{ Uninstalling while devices are still blocked would leave them hidden with no XBridge left to
  un-hide them, so a failed cleanup stops the uninstall instead. }
function InitializeUninstall(): Boolean;
begin
  Result := True;
  KillRunningApp();

  { hidden-devices.txt is deleted by every successful cleanup, so its presence means pads are still hidden. }
  if not FileExists(ExpandConstant('{app}\{#AppExe}')) then
  begin
    if FileExists(ExpandConstant('{localappdata}\XBridge\hidden-devices.txt')) then
      Result := SuppressibleMsgBox('{#AppExe} is missing, so the controllers XBridge hid cannot be un-hidden automatically.' + #13#10#13#10 +
           'Cancel, run the XBridge installer again to repair it, then uninstall. Or click OK to uninstall anyway and un-hide them yourself in the HidHide Configuration Client.',
           mbError, MB_OKCANCEL, IDCANCEL) = IDOK;
    Exit;
  end;

  while not ReleaseHidHide() do
    if SuppressibleMsgBox('XBridge could not un-hide your controllers in HidHide, so they would stay invisible to games after uninstalling.' + #13#10#13#10 +
         'Close the HidHide Configuration Client if it is open, then click Retry. Cancel stops the uninstall.',
         mbError, MB_RETRYCANCEL, IDCANCEL) <> IDRETRY then
    begin
      Result := False;
      Exit;
    end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  rc: Integer;
begin
  if CurUninstallStep = usUninstall then
  begin
    KillRunningApp();
    Exec(ExpandConstant('{sys}\schtasks.exe'), '/Delete /F /TN "{#AppShortName}"',
         '', SW_HIDE, ewWaitUntilTerminated, rc);
  end;
end;
