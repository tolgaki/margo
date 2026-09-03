; Margo — Windows installer (Inno Setup 6)
;
; Build with:  packaging\windows\build-exe.ps1
; or manually: ISCC.exe /DMyAppVersion=1.0.0 /DPayloadDir=..\..\build\windows\payload margo.iss
;
; Installs per-user, so no UAC prompt. Files are staged in %LOCALAPPDATA%\Margo
; and the real install is delegated to install.ps1 — one implementation of the
; merge-and-preserve logic, shared with the command line and the macOS package.

#define MyAppName      "Margo"
#define MyAppPublisher "Margo contributors"
#define MyAppURL       "https://github.com/tolgaki/margo"

#ifndef MyAppVersion
  #define MyAppVersion "0.1.0"
#endif

#ifndef PayloadDir
  #define PayloadDir "..\..\build\windows\payload"
#endif

[Setup]
AppId={{B7E4B0A1-3C5D-4E2A-9F17-6D8C2A5E9B34}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases
VersionInfoVersion={#MyAppVersion}
VersionInfoDescription=AI chief of staff for Microsoft 365

; Per-user install: no admin rights, no UAC prompt.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DefaultDirName={localappdata}\Margo
DisableProgramGroupPage=yes
DisableDirPage=yes
UsePreviousAppDir=yes

LicenseFile={#PayloadDir}\LICENSE
InfoBeforeFile=welcome.txt
OutputDir=..\..\dist
OutputBaseFilename=Margo-{#MyAppVersion}-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName={#MyAppName} {#MyAppVersion}
SetupLogging=yes

; Set by build-exe.ps1 when signing credentials are present. The "signtool"
; command itself is supplied on the ISCC command line via /Ssigntool=...
#ifdef SignOutput
SignTool=signtool
#endif

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Types]
Name: "recommended"; Description: "Margo and the chief-of-staff playbook"
Name: "full";        Description: "Everything, including the optional skills"
Name: "custom";      Description: "Choose what to install"; Flags: iscustom

[Components]
Name: "core"; \
  Description: "Margo agent + chief-of-staff playbook"; \
  Types: recommended full custom; Flags: fixed

Name: "decisionlog"; \
  Description: "decision-log skill (append-only record of what the team decided, and why)"; \
  Types: full


[Files]
; Everything except the optional skills.
Source: "{#PayloadDir}\*"; DestDir: "{app}"; \
  Excludes: "skills\decision-log,skills\decision-log\*"; \
  Flags: ignoreversion recursesubdirs createallsubdirs; Components: core

Source: "{#PayloadDir}\skills\decision-log\*"; DestDir: "{app}\skills\decision-log"; \
  Flags: ignoreversion recursesubdirs createallsubdirs; Components: decisionlog


[Icons]
Name: "{userprograms}\Margo\Margo documentation"; Filename: "{#MyAppURL}"
Name: "{userprograms}\Margo\Edit preferences.md"; \
  Filename: "notepad.exe"; \
  Parameters: """{%USERPROFILE}\.copilot\skills\chief-of-staff\preferences.md"""
Name: "{userprograms}\Margo\Uninstall Margo"; Filename: "{uninstallexe}"

[Run]
Filename: "notepad.exe"; \
  Parameters: """{%USERPROFILE}\.copilot\skills\chief-of-staff\preferences.md"""; \
  Description: "Tell Margo who you are (edit preferences.md)"; \
  Flags: postinstall nowait skipifsilent

Filename: "{#MyAppURL}/blob/main/docs/getting-started.md"; \
  Description: "Open the getting-started guide"; \
  Flags: postinstall shellexec nowait skipifsilent unchecked

[Code]

function CopilotDir(): string;
begin
  Result := ExpandConstant('{%USERPROFILE}') + '\.copilot';
end;

function RunEngine(const Args: string; var ExitCode: Integer): Boolean;
var
  Params: string;
begin
  Params := '-NoProfile -ExecutionPolicy Bypass -File "'
          + ExpandConstant('{app}\install.ps1') + '" '
          + Args + ' -Dest "' + CopilotDir() + '" -Yes';
  Result := Exec('powershell.exe', Params, ExpandConstant('{app}'),
                 SW_HIDE, ewWaitUntilTerminated, ExitCode);
end;

// Install one skill, returning a description of the failure or '' on success.
function InstallSkill(const Name: string): string;
var
  ExitCode: Integer;
begin
  Result := '';
  if not RunEngine('install -Skills ' + Name, ExitCode) then
    Result := #13#10 + '  - ' + Name + ' (could not start PowerShell)'
  else if ExitCode <> 0 then
    Result := #13#10 + '  - ' + Name + ' (exit code ' + IntToStr(ExitCode) + ')';
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Failed: string;
begin
  if CurStep <> ssPostInstall then
    Exit;

  Failed := InstallSkill('chief-of-staff');

  if WizardIsComponentSelected('decisionlog') then
    Failed := Failed + InstallSkill('decision-log');


  if Failed <> '' then
    MsgBox('Margo was copied to' + #13#10 + ExpandConstant('{app}') + #13#10 + #13#10 +
           'but these skills could not be installed into ' + CopilotDir() + ':' + Failed + #13#10 + #13#10 +
           'You can finish manually by running:' + #13#10 +
           '  powershell -ExecutionPolicy Bypass -File "' + ExpandConstant('{app}\install.ps1') + '"',
           mbError, MB_OK);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ExitCode: Integer;
  Started: Boolean;
  Reason: string;
begin
  // Remove the installed copies before the staged files disappear. The engine
  // archives preferences.md, commitments.md, config.md and the whole state/
  // subtree rather than deleting them.
  if CurUninstallStep <> usUninstall then
    Exit;

  if not FileExists(ExpandConstant('{app}\install.ps1')) then
  begin
    MsgBox('The Margo uninstaller could not find its own engine at' + #13#10 +
           ExpandConstant('{app}\install.ps1') + #13#10 + #13#10 +
           'Margo may still be installed in ' + CopilotDir() + '.' + #13#10 +
           'Remove these by hand if so:' + #13#10 +
           '  ' + CopilotDir() + '\agents\margo.agent.md' + #13#10 +
           '  ' + CopilotDir() + '\skills\chief-of-staff' + #13#10 +
           '  ' + CopilotDir() + '\skills\decision-log' + #13#10 +
           '  ' + CopilotDir() + '\automations' + #13#10 +
           '  ' + CopilotDir() + '\tools',
           mbError, MB_OK);
    Exit;
  end;

  Started := RunEngine('uninstall', ExitCode);

  // Both halves matter. Ignoring them lets setup report a clean uninstall while
  // ~/.copilot is untouched — and {app} is about to be deleted, taking the only
  // copy of the engine that could finish the job.
  if (not Started) or (ExitCode <> 0) then
  begin
    if Started then
      Reason := 'The uninstall helper exited with code ' + IntToStr(ExitCode) + '.'
    else
      Reason := 'The uninstall helper could not be started.';

    MsgBox('Margo was not fully removed from ' + CopilotDir() + '.' + #13#10 + #13#10 +
           Reason + #13#10 + #13#10 +
           'Your personal files were NOT deleted. Remove the rest by hand:' + #13#10 +
           '  ' + CopilotDir() + '\agents\margo.agent.md' + #13#10 +
           '  ' + CopilotDir() + '\skills\chief-of-staff' + #13#10 +
           '  ' + CopilotDir() + '\skills\decision-log' + #13#10 +
           '  ' + CopilotDir() + '\automations' + #13#10 +
           '  ' + CopilotDir() + '\tools',
           mbError, MB_OK);
  end;
end;
