; Version defaults preserve the legacy v0.1 packaging entrypoint.
#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef PayloadDir
  #error PayloadDir is required
#endif
#ifndef OutputDir
  #error OutputDir is required
#endif
#ifndef AppIcon
  #error AppIcon is required
#endif

[Setup]
AppId={{A289B309-A01F-4723-BA27-DAE38199CB9E}
AppName=MixDeck
AppVersion={#AppVersion}
AppVerName=MixDeck v{#AppVersion}
VersionInfoVersion={#AppVersion}.0
VersionInfoProductName=MixDeck
DefaultDirName={localappdata}\Programs\MixDeck
DefaultGroupName=MixDeck
DisableProgramGroupPage=yes
DisableDirPage=no
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.19045
OutputDir={#OutputDir}
OutputBaseFilename=AudioManager-Setup
SetupIconFile={#AppIcon}
UninstallDisplayIcon={app}\AudioManager.Desktop.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
SetupLogging=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\MixDeck"; Filename: "{app}\AudioManager.Desktop.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\MixDeck"; Filename: "{app}\AudioManager.Desktop.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\AudioManager.Desktop.exe"; Description: "Launch MixDeck"; Flags: nowait postinstall skipifsilent unchecked

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Command: String;
  Expected: String;
#ifdef StartupAware
  ResultCode: Integer;
#endif
begin
  if CurUninstallStep = usUninstall then
  begin
#ifdef StartupAware
    // Only enable this define for builds supporting the audio-free registration command.
    if not Exec(ExpandConstant('{app}\AudioManager.Desktop.exe'), '--startup-register-uninstall',
      ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, ResultCode) then
      Log('Unable to start owned-startup cleanup')
    else if ResultCode <> 0 then Log('Owned-startup cleanup failed; inspect startup diagnostics');
#endif
    // Never delete another portable/install location's startup registration.
    Expected := '"' + ExpandConstant('{app}\AudioManager.Desktop.exe') + '" --tray';
    if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'MixDeck', Command) then
      if CompareText(Trim(Command), Expected) = 0 then
        RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'MixDeck');
  end;
end;
