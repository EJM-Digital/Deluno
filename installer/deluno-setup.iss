; Deluno Windows Installer — Inno Setup 6
; Compiled by CI: ISCC.exe deluno-setup.iss /DAppVersion=x.y.z /DBinDir=..\..\artifacts\windows\bin

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef BinDir
  #define BinDir "..\..\artifacts\windows\bin"
#endif

#define AppName      "Deluno"
#define AppPublisher "Deluno"
#define AppURL       "https://github.com/jampat000/Deluno"
#define AppExeName   "Deluno.exe"
#define ServiceName  "Deluno"
#define DataDir      "{commonappdata}\Deluno\data"
#define BinInstDir   "{commonappdata}\Deluno\bin"

[Setup]
AppId                    = {{A7F3C2D1-8B4E-4F9A-BC23-5D1E7F8A9C0B}
AppName                  = {#AppName}
AppVersion               = {#AppVersion}
AppPublisher             = {#AppPublisher}
AppPublisherURL          = {#AppURL}
AppSupportURL            = {#AppURL}/issues
AppUpdatesURL            = {#AppURL}/releases
DefaultDirName           = {commonappdata}\Deluno
DefaultGroupName         = {#AppName}
DisableDirPage           = yes
DisableProgramGroupPage  = yes
OutputDir                = ..\artifacts\windows\installer
OutputBaseFilename       = Deluno-Setup-{#AppVersion}
Compression              = lzma2/ultra64
SolidCompression         = yes
WizardStyle              = modern
WizardSizePercent        = 110
SetupIconFile            =
UninstallDisplayName     = {#AppName} {#AppVersion}
CloseApplications        = yes
RestartApplications      = yes
PrivilegesRequired       = admin
PrivilegesRequiredOverridesAllowed = commandline
MinVersion               = 10.0
ArchitecturesInstallIn64BitMode = x64compatible

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

; ── Custom pages ──────────────────────────────────────────────────────────────

[Code]
var
  StartupPage:    TWizardPage;
  RbTray:         TRadioButton;
  RbLocalSystem:  TRadioButton;
  RbRunAsUser:    TRadioButton;
  LblUsername:    TLabel;
  TbUsername:     TEdit;
  LblPassword:    TLabel;
  TbPassword:     TEdit;
  LblNasWarning:  TLabel;

  PortPage:       TWizardPage;
  TbPort:         TEdit;
  LblPortStatus:  TLabel;

procedure UpdateCredentialVisibility(Sender: TObject);
begin
  LblUsername.Visible   := RbRunAsUser.Checked;
  TbUsername.Visible    := RbRunAsUser.Checked;
  LblPassword.Visible   := RbRunAsUser.Checked;
  TbPassword.Visible    := RbRunAsUser.Checked;
  LblNasWarning.Visible := RbLocalSystem.Checked;
end;

procedure ValidatePort(Sender: TObject);
var
  Port: Integer;
begin
  Port := StrToIntDef(TbPort.Text, 0);
  if (Port < 1024) or (Port > 65535) then
    LblPortStatus.Caption := '✗  Enter a port between 1024 and 65535'
  else
    LblPortStatus.Caption := '✓  Port ' + TbPort.Text + ' looks good';
end;

procedure InitializeWizard;
begin
  { ── Startup mode page ── }
  StartupPage := CreateCustomPage(wpWelcome, 'Startup Mode',
    'Choose how Deluno starts with Windows.');

  RbTray := TRadioButton.Create(StartupPage);
  RbTray.Parent  := StartupPage.Surface;
  RbTray.Caption := 'Start at login (recommended)';
  RbTray.Left    := 0; RbTray.Top := 0;
  RbTray.Width   := 400; RbTray.Checked := True;
  RbTray.OnClick := @UpdateCredentialVisibility;

  with TLabel.Create(StartupPage) do begin
    Parent  := StartupPage.Surface;
    Caption := 'Runs when you log in. Full access to network drives and NAS shares.';
    Left := 20; Top := 22; Width := 380;
    Font.Color := clGray;
  end;

  RbLocalSystem := TRadioButton.Create(StartupPage);
  RbLocalSystem.Parent  := StartupPage.Surface;
  RbLocalSystem.Caption := 'Windows Service — Local System account';
  RbLocalSystem.Left := 0; RbLocalSystem.Top := 56;
  RbLocalSystem.Width := 400;
  RbLocalSystem.OnClick := @UpdateCredentialVisibility;

  LblNasWarning := TLabel.Create(StartupPage);
  LblNasWarning.Parent  := StartupPage.Surface;
  LblNasWarning.Caption :=
    'Starts at boot. Cannot access mapped drives or NAS shares requiring credentials.';
  LblNasWarning.Left := 20; LblNasWarning.Top := 78; LblNasWarning.Width := 380;
  LblNasWarning.Font.Color := $004080C0;
  LblNasWarning.Visible := False;

  RbRunAsUser := TRadioButton.Create(StartupPage);
  RbRunAsUser.Parent  := StartupPage.Surface;
  RbRunAsUser.Caption := 'Windows Service — Run as specific user (NAS / network drives at boot)';
  RbRunAsUser.Left := 0; RbRunAsUser.Top := 116;
  RbRunAsUser.Width := 400;
  RbRunAsUser.OnClick := @UpdateCredentialVisibility;

  LblUsername := TLabel.Create(StartupPage);
  LblUsername.Parent  := StartupPage.Surface;
  LblUsername.Caption := 'Windows username (e.g. .\John or DOMAIN\John):';
  LblUsername.Left := 20; LblUsername.Top := 142; LblUsername.Width := 380;
  LblUsername.Visible := False;

  TbUsername := TEdit.Create(StartupPage);
  TbUsername.Parent  := StartupPage.Surface;
  TbUsername.Left := 20; TbUsername.Top := 160; TbUsername.Width := 360;
  TbUsername.Visible := False;

  LblPassword := TLabel.Create(StartupPage);
  LblPassword.Parent  := StartupPage.Surface;
  LblPassword.Caption := 'Password:';
  LblPassword.Left := 20; LblPassword.Top := 192; LblPassword.Width := 380;
  LblPassword.Visible := False;

  TbPassword := TEdit.Create(StartupPage);
  TbPassword.Parent     := StartupPage.Surface;
  TbPassword.Left := 20; TbPassword.Top := 210; TbPassword.Width := 360;
  TbPassword.PasswordChar := '*';
  TbPassword.Visible := False;

  { ── Port page ── }
  PortPage := CreateCustomPage(StartupPage.ID, 'Network Port',
    'Choose the port Deluno listens on.');

  with TLabel.Create(PortPage) do begin
    Parent  := PortPage.Surface;
    Caption :=
      'Deluno will be accessible at http://localhost:[port]' + #13#10 +
      'The default port 7879 works for most installations.';
    Left := 0; Top := 0; Width := 400;
  end;

  with TLabel.Create(PortPage) do begin
    Parent  := PortPage.Surface;
    Caption := 'Port number:';
    Left := 0; Top := 52;
  end;

  TbPort := TEdit.Create(PortPage);
  TbPort.Parent  := PortPage.Surface;
  TbPort.Text    := '7879';
  TbPort.Left := 0; TbPort.Top := 70; TbPort.Width := 100;
  TbPort.OnChange := @ValidatePort;

  LblPortStatus := TLabel.Create(PortPage);
  LblPortStatus.Parent  := PortPage.Surface;
  LblPortStatus.Caption := '✓  Port 7879 looks good';
  LblPortStatus.Left := 110; LblPortStatus.Top := 74;
  LblPortStatus.Font.Color := clGreen;
  LblPortStatus.Width := 280;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;

  if CurPageID = StartupPage.ID then begin
    if RbRunAsUser.Checked and (Trim(TbUsername.Text) = '') then begin
      MsgBox('Please enter a Windows username for the service account.', mbError, MB_OK);
      Result := False;
    end;
  end;

  if CurPageID = PortPage.ID then begin
    if (StrToIntDef(TbPort.Text, 0) < 1024) or (StrToIntDef(TbPort.Text, 0) > 65535) then begin
      MsgBox('Please enter a valid port number (1024–65535).', mbError, MB_OK);
      Result := False;
    end;
  end;
end;

procedure WriteAppConfig;
var
  ConfigPath: String;
  Json:       String;
  DataPath:   String;
begin
  DataPath   := ExpandConstant('{commonappdata}\Deluno\data');
  ConfigPath := DataPath + '\deluno.json';

  ForceDirectories(DataPath);

  Json := '{"port":' + TbPort.Text + ',' +
          '"dataRoot":"' + StringReplace(DataPath, '\', '\\', [rfReplaceAll]) + '"}';

  SaveStringToFile(ConfigPath, Json, False);
end;

procedure ApplyStartupMode;
var
  ExePath:    String;
  ResultCode: Integer;
begin
  ExePath := ExpandConstant('{commonappdata}\Deluno\bin\Deluno.exe');

  if RbTray.Checked then
  begin
    RegWriteStringValue(HKCU,
      'Software\Microsoft\Windows\CurrentVersion\Run',
      'Deluno', '"' + ExePath + '"');
    RegDeleteValue(HKLM, 'SYSTEM\CurrentControlSet\Services\Deluno', 'ImagePath');
    Exec('sc.exe', 'delete Deluno', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end
  else if RbLocalSystem.Checked then
  begin
    RegDeleteValue(HKCU,
      'Software\Microsoft\Windows\CurrentVersion\Run', 'Deluno');
    Exec(ExePath, '--install-service', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end
  else if RbRunAsUser.Checked then
  begin
    RegDeleteValue(HKCU,
      'Software\Microsoft\Windows\CurrentVersion\Run', 'Deluno');
    Exec(ExePath,
      '--install-service --username "' + TbUsername.Text +
      '" --password "' + TbPassword.Text + '"',
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    WriteAppConfig;
    ApplyStartupMode;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir:    String;
  ResultCode: Integer;
begin
  if CurUninstallStep = usUninstall then
  begin
    { Stop and remove service if present }
    Exec('sc.exe', 'stop Deluno',   '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Exec('sc.exe', 'delete Deluno', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    RegDeleteValue(HKCU,
      'Software\Microsoft\Windows\CurrentVersion\Run', 'Deluno');
  end;

  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{commonappdata}\Deluno\data');
    if MsgBox(
      'Do you want to delete all Deluno data?' + #13#10 +
      '(database, configuration, logs)' + #13#10#13#10 +
      DataDir + #13#10#13#10 +
      'Leave unticked to keep your settings for a future reinstall.',
      mbConfirmation, MB_YESNO) = IDYES then
    begin
      DelTree(DataDir, True, True, True);
    end;
  end;
end;

[Files]
; All published tray app files
Source: "{#BinDir}\*"; DestDir: "{#BinInstDir}"; \
  Flags: ignoreversion recursesubdirs createallsubdirs; \
  Excludes: "*.pdb"

[Icons]
Name: "{group}\Deluno";           Filename: "{#BinInstDir}\{#AppExeName}"
Name: "{group}\Uninstall Deluno"; Filename: "{uninstallexe}"
Name: "{commonstartup}\Deluno";   Filename: "{#BinInstDir}\{#AppExeName}"; \
  Check: not RbTray.Checked  ; Tasks: not startuptask

[Tasks]
Name: startuptask; \
  Description: "Start Deluno automatically when Windows starts"; \
  Check: RbTray.Checked

[Run]
Filename: "{#BinInstDir}\{#AppExeName}"; \
  Description: "Start Deluno now"; \
  Flags: nowait postinstall skipifsilent; \
  Check: RbTray.Checked

Filename: "http://localhost:{code:GetPort}"; \
  Description: "Open Deluno in browser"; \
  Flags: nowait postinstall skipifsilent shellexec; \
  Check: RbTray.Checked

[Code]
function GetPort(Param: String): String;
begin
  Result := TbPort.Text;
end;
