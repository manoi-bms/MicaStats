; MicaStats - Inno Setup Script
; Compile with: ISCC.exe /DAppVersion=x.y.z installer.iss

[Setup]
; New AppId: MicaStats installs and uninstalls independently of the upstream
; kil0bit System Monitor it forked from.
AppId={{F41A43F7-32FD-41B5-A95A-67E849912038}
AppName=MicaStats
AppVersion={#AppVersion}
AppPublisher=Chaiyaporn Suratemeekul (manoi-bms)
AppPublisherURL=https://github.com/manoi-bms/MicaStats
AppSupportURL=https://github.com/manoi-bms/MicaStats/issues
AppUpdatesURL=https://github.com/manoi-bms/MicaStats/releases
DefaultDirName={autopf}\MicaStats
DisableProgramGroupPage=yes
; Required for trusted path installation
PrivilegesRequired=admin
; Optional: Let user choose install location
DisableDirPage=no
OutputBaseFilename=MicaStats-v{#AppVersion}-Setup
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
SetupIconFile=icon.ico
UninstallDisplayIcon={app}\MicaStats.exe

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; The source path will be where dotnet publish outputs the files
Source: "release-output\MicaStats.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "release-output\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; Include the icon for the installer itself
Source: "icon.ico"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\MicaStats"; Filename: "{app}\MicaStats.exe"
Name: "{autodesktop}\MicaStats"; Filename: "{app}\MicaStats.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\MicaStats.exe"; Description: "{cm:LaunchProgram,MicaStats}"; Flags: nowait postinstall skipifsilent
