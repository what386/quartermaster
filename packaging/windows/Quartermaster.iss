#ifndef AppVersion
  #error AppVersion must be supplied by build-installer.ps1.
#endif
#ifndef PublishDir
  #error PublishDir must be supplied by build-installer.ps1.
#endif
#ifndef OutputDir
  #error OutputDir must be supplied by build-installer.ps1.
#endif

[Setup]
; Keep this ID stable across versions so upgrades reuse the installation and uninstall log.
AppId=Quartermaster
AppName=Quartermaster
AppVersion={#AppVersion}
AppPublisher=what386
AppPublisherURL=https://github.com/what386/quartermaster
AppSupportURL=https://github.com/what386/quartermaster/issues
AppUpdatesURL=https://github.com/what386/quartermaster/releases
DefaultDirName={localappdata}\Programs\Quartermaster
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#OutputDir}
OutputBaseFilename=Quartermaster-win-x64-setup
SetupIconFile=..\..\src\Quartermaster.Gui\Assets\icon.ico
UninstallDisplayIcon={app}\Quartermaster.Gui.exe
UninstallDisplayName=Quartermaster
LicenseFile=..\..\LICENSE
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\Quartermaster"; Filename: "{app}\Quartermaster.Gui.exe"; WorkingDir: "{app}"; IconFilename: "{app}\Quartermaster.Gui.exe"
Name: "{userdesktop}\Quartermaster"; Filename: "{app}\Quartermaster.Gui.exe"; WorkingDir: "{app}"; IconFilename: "{app}\Quartermaster.Gui.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Quartermaster.Gui.exe"; Description: "Launch Quartermaster"; Flags: nowait postinstall skipifsilent

; User data lives outside {app}. Do not delete the library or settings on uninstall.
