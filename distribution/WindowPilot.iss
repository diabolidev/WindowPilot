#define AppName "WindowPilot"
#ifndef AppVersion
#define AppVersion "0.1.0"
#endif
#define AppExe "WindowPilot.App.exe"

#ifndef SourceDir
  #error SourceDir must be defined on the ISCC command line.
#endif

[Setup]
AppId={{D642633B-1CEB-4145-B53F-C0AAE68C469F}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
DefaultDirName={localappdata}\Programs\WindowPilot
DefaultGroupName=WindowPilot
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
MinVersion=10.0.22000
DisableProgramGroupPage=yes
WizardStyle=modern
Compression=lzma2
SolidCompression=yes
OutputBaseFilename=WindowPilot-Setup-win-x64
UninstallDisplayIcon={app}\{#AppExe}
SetupIconFile=..\src\WindowPilot.App\Assets\windowpilot.ico
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional options:"; Flags: unchecked

[Icons]
Name: "{autoprograms}\WindowPilot"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\WindowPilot"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "Launch WindowPilot now"; Flags: nowait postinstall skipifsilent
Filename: "{app}\{#AppExe}"; Parameters: "--background"; Flags: nowait; Check: WizardSilent

[Code]
// Upgrade installations should repair a previously configured Run entry.
// Respect the user's on/off decision: never create a new entry here.
procedure CurStepChanged(CurStep: TSetupStep);
var
  ExistingCommand: String;
begin
  if CurStep = ssPostInstall then
  begin
    if RegQueryStringValue(HKEY_CURRENT_USER,
         'Software\Microsoft\Windows\CurrentVersion\Run',
         'WindowPilot', ExistingCommand) then
      RegWriteStringValue(HKEY_CURRENT_USER,
        'Software\Microsoft\Windows\CurrentVersion\Run',
        'WindowPilot', '"' + ExpandConstant('{app}\{#AppExe}') + '" --background');
  end;
end;

// Beim Deinstallieren nur unseren eigenen Autostart-Eintrag entfernen.
// Layouts und Konfigurationen bleiben erhalten (fuer Neuinstallationen).
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  StartupValue: String;
  InstalledExe: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    if RegQueryStringValue(HKEY_CURRENT_USER,
         'Software\Microsoft\Windows\CurrentVersion\Run',
         'WindowPilot', StartupValue) then
    begin
      InstalledExe := ExpandConstant('{app}\{#AppExe}');
      if Pos(Lowercase(InstalledExe), Lowercase(StartupValue)) > 0 then
        RegDeleteValue(HKEY_CURRENT_USER,
          'Software\Microsoft\Windows\CurrentVersion\Run',
          'WindowPilot');
    end;
  end;
end;
