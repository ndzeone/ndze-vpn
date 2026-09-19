; Ndze VPN installer (Inno Setup 6). Built by build\build.ps1, which passes /DAppVersion and
; /DPublishDir. Per-user install by default (no UAC prompt); the elevation dialog lets the user
; choose an all-users install instead.

#ifndef AppVersion
  #define AppVersion "1.3.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\artifacts\publish"
#endif

#define AppName "Ndze VPN"
#define AppExe "NdzeVpn.exe"
#define AppPublisher "ndze"

[Setup]
AppId={{6C3E1B7A-4F2D-4E8B-9A51-0D7C2B9E4F13}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\Ndze VPN
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=..\artifacts
OutputBaseFilename=NdzeVPN-Setup-{#AppVersion}
SetupIconFile=..\src\NdzeVpn\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
WizardSizePercent=110
; ultra64 + block threads runs ISCC out of memory on ~260 MB of cores; max in a separate
; process compresses nearly as well.
Compression=lzma2/max
SolidCompression=yes
LZMAUseSeparateProcess=yes
CloseApplications=force
RestartApplications=no
SetupMutex=NdzeVpnSetupMutex

[Languages]
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
ru.AutoStart=Запускать {#AppName} вместе с Windows
en.AutoStart=Start {#AppName} with Windows
ru.LaunchNow=Запустить {#AppName}
en.LaunchNow=Launch {#AppName}
ru.RemoveData=Удалить также настройки, ключи и подписки?
en.RemoveData=Also delete settings, keys and subscriptions?

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "autostart"; Description: "{cm:AutoStart}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "NdzeVpn"; \
    ValueData: """{app}\{#AppExe}"" --autostart"; Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchNow}"; Flags: nowait postinstall skipifsilent
; In-app updater runs us with /SILENT /relaunch=1: start the new version once files are in place.
Filename: "{app}\{#AppExe}"; Flags: nowait runasoriginaluser; Check: IsRelaunch

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/F /T /IM {#AppExe}"; Flags: runhidden; RunOnceId: "KillApp"
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM xray.exe"; Flags: runhidden; RunOnceId: "KillXray"
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM sing-box.exe"; Flags: runhidden; RunOnceId: "KillSingBox"

[UninstallDelete]
Type: filesandordirs; Name: "{app}\core"

[Code]
const
  InternetSettings = 'Software\Microsoft\Windows\CurrentVersion\Internet Settings';

{ If the app was killed while connected, Windows is still pointed at its dead local listener and
  every browser is broken. Undo that on uninstall, but only when the proxy is really ours. }
procedure ClearStaleProxy();
var
  Server: String;
begin
  if RegQueryStringValue(HKCU, InternetSettings, 'ProxyServer', Server) then
  begin
    if Pos('127.0.0.1:108', Server) = 1 then
    begin
      RegWriteDWordValue(HKCU, InternetSettings, 'ProxyEnable', 0);
      RegDeleteValue(HKCU, InternetSettings, 'ProxyServer');
    end;
  end;
end;

function IsRelaunch(): Boolean;
begin
  Result := ExpandConstant('{param:relaunch|0}') = '1';
end;

function InitializeSetup(): Boolean;
var
  Code: Integer;
begin
  { Upgrade in place: the running app and its sidecars hold file locks. }
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /T /IM {#AppExe}', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM xray.exe', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM sing-box.exe', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    ClearStaleProxy();
    if (not UninstallSilent) and
       (MsgBox(CustomMessage('RemoveData'), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES) then
      DelTree(ExpandConstant('{userappdata}\NdzeVpn'), True, True, True);
  end;
end;
