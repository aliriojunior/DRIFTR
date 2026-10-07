#define MyAppName "DRIFTR"
#define MyAppVersion "1.1.0"
#define MyAppPublisher "DRIFTR"
#define MyAppExeName "DRIFTR.exe"
#define MyPayloadDir "..\artifacts\DRIFTR-1.1.0-win-x64"

[Setup]
; This identity is permanent. Every future DRIFTR installer must reuse it.
AppId={{7B167009-F34A-4CAD-829E-47310861AA48}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\DRIFTR
DefaultGroupName=DRIFTR
DisableWelcomePage=no
DisableDirPage=no
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0
OutputDir=..\artifacts\installer
OutputBaseFilename=DRIFTR-Setup-1.1.0
SetupIconFile=..\Assets\driftr.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
CloseApplicationsFilter={#MyAppExeName}
RestartApplications=no
SetupMutex=DRIFTR-Installer-7B167009-F34A-4CAD-829E-47310861AA48
UsePreviousAppDir=yes
UsePreviousGroup=yes
UsePreviousTasks=yes
VersionInfoVersion=1.1.0.0
VersionInfoProductVersion=1.1.0.0
VersionInfoProductName={#MyAppName}
VersionInfoDescription=DRIFTR Setup
VersionInfoCompany={#MyAppPublisher}
VersionInfoCopyright=DRIFTR
SetupLogging=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#MyPayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\DRIFTR"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\{#MyAppExeName}"; AppUserModelID: "DRIFTR.Desktop"
Name: "{autodesktop}\DRIFTR"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\{#MyAppExeName}"; AppUserModelID: "DRIFTR.Desktop"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,DRIFTR}"; Flags: nowait postinstall skipifsilent
