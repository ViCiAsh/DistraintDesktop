; Distraint installer (Inno Setup 6.3 or newer). Put this file next to Distraint.csproj and distraint.ico.
; Build the app first:  dotnet publish Distraint.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
; Then open this file in Inno Setup and press Compile (Ctrl+F9). The result is installer-output\Distraint-Setup.exe

#define AppName "Distraint"
#define AppVersion "1.0.0"
#define AppExe "Distraint.exe"

[Setup]
AppId={{935E96A3-8CE8-48B4-9B15-16DDB1840244}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppName}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=installer-output
OutputBaseFilename=Distraint-Setup
SetupIconFile=distraint.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Distraint closes itself through its own check (see [Code]); Inno must not close it behind the protection's back.
CloseApplications=no
RestartApplications=no

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; Flags: unchecked

[Files]
; The watchdog runs from a renamed copy of the same exe, so the installer puts both in place.
Source: "publish\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "publish\{#AppExe}"; DestDir: "{app}"; DestName: "Distraint-Antikill.exe"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "Start {#AppName}"; Flags: nowait postinstall skipifsilent runasoriginaluser

[Code]
// Asks the installed Distraint whether it may be replaced or removed. The app refuses during a strict lock, asks for the
// typed code while protection is on, and then closes itself and its watchdog. Exit code 0 means "go ahead".
function RunPrepare(const AppDir: String): Boolean;
var
  Exe: String;
  Code: Integer;
begin
  Result := True;
  Exe := AppDir + '\{#AppExe}';
  if not FileExists(Exe) then Exit;                       // nothing installed yet
  if Exec(Exe, '--prepare-uninstall', '', SW_SHOWNORMAL, ewWaitUntilTerminated, Code) then
    Result := (Code = 0);
end;

// Runs before files are copied: an upgrade over a running, protected copy must pass the same check.
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if not RunPrepare(ExpandConstant('{app}')) then
    Result := 'Distraint is protected, so the installed copy could not be closed. Setup will not continue.';
end;

function InitializeUninstall(): Boolean;
begin
  Result := RunPrepare(ExpandConstant('{app}'));
  if not Result then
    MsgBox('Distraint was not removed.', mbInformation, MB_OK);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Code: Integer;
begin
  if CurUninstallStep = usUninstall then
  begin
    // Remove "Start with Windows" (scheduled task and the per-user Run entry).
    Exec(ExpandConstant('{sys}\schtasks.exe'), '/Delete /F /TN Distraint', '', SW_HIDE, ewWaitUntilTerminated, Code);
    RegDeleteValue(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Run', 'Distraint');
  end;
  if CurUninstallStep = usPostUninstall then
  begin
    if MsgBox('Also delete your Distraint settings and study history?', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
      DelTree(ExpandConstant('{userappdata}\Distraint'), True, True, True);
  end;
end;
