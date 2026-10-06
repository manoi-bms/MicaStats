; MicaStats - Inno Setup Script
; Compile with: ISCC.exe /DAppVersion=x.y.z installer.iss

#ifndef PublishDir
  #define PublishDir "release-output"
#endif

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
Source: "{#PublishDir}\MicaStats.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; Include the icon for the installer itself
Source: "icon.ico"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\MicaStats"; Filename: "{app}\MicaStats.exe"
Name: "{autoprograms}\MicaPad"; Filename: "{app}\MicaStats.exe"; Parameters: "--pad"; IconFilename: "{app}\micapad.ico"; AppUserModelID: "Kil0bit.SystemMonitor.MicaPad"
Name: "{autodesktop}\MicaStats"; Filename: "{app}\MicaStats.exe"; Tasks: desktopicon

[Registry]
; "Open with > MicaPad" in Explorer for common text files. MicaPad is offered, never made the default.
Root: HKA; Subkey: "Software\Classes\Applications\MicaStats.exe"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "MicaPad"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\Applications\MicaStats.exe\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\micapad.ico"
Root: HKA; Subkey: "Software\Classes\Applications\MicaStats.exe\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\MicaStats.exe"" --pad ""%1"""
Root: HKA; Subkey: "Software\Classes\Applications\MicaStats.exe\SupportedTypes"; ValueType: string; ValueName: ".txt"; ValueData: ""
Root: HKA; Subkey: "Software\Classes\Applications\MicaStats.exe\SupportedTypes"; ValueType: string; ValueName: ".log"; ValueData: ""
Root: HKA; Subkey: "Software\Classes\Applications\MicaStats.exe\SupportedTypes"; ValueType: string; ValueName: ".ini"; ValueData: ""
Root: HKA; Subkey: "Software\Classes\Applications\MicaStats.exe\SupportedTypes"; ValueType: string; ValueName: ".md"; ValueData: ""
Root: HKA; Subkey: "Software\Classes\Applications\MicaStats.exe\SupportedTypes"; ValueType: string; ValueName: ".json"; ValueData: ""
Root: HKA; Subkey: "Software\Classes\Applications\MicaStats.exe\SupportedTypes"; ValueType: string; ValueName: ".xml"; ValueData: ""
Root: HKA; Subkey: "Software\Classes\Applications\MicaStats.exe\SupportedTypes"; ValueType: string; ValueName: ".csv"; ValueData: ""
Root: HKA; Subkey: "Software\Classes\Applications\MicaStats.exe\SupportedTypes"; ValueType: string; ValueName: ".cfg"; ValueData: ""
Root: HKA; Subkey: "Software\Classes\Applications\MicaStats.exe\SupportedTypes"; ValueType: string; ValueName: ".conf"; ValueData: ""
Root: HKA; Subkey: "Software\Classes\Applications\MicaStats.exe\SupportedTypes"; ValueType: string; ValueName: ".yaml"; ValueData: ""
Root: HKA; Subkey: "Software\Classes\Applications\MicaStats.exe\SupportedTypes"; ValueType: string; ValueName: ".yml"; ValueData: ""
; Listed directly in each type's Open with submenu.
Root: HKA; Subkey: "Software\Classes\.txt\OpenWithList\MicaStats.exe"; ValueType: none; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\.log\OpenWithList\MicaStats.exe"; ValueType: none; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\.ini\OpenWithList\MicaStats.exe"; ValueType: none; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\.md\OpenWithList\MicaStats.exe"; ValueType: none; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\.json\OpenWithList\MicaStats.exe"; ValueType: none; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\.xml\OpenWithList\MicaStats.exe"; ValueType: none; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\.csv\OpenWithList\MicaStats.exe"; ValueType: none; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\.cfg\OpenWithList\MicaStats.exe"; ValueType: none; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\.conf\OpenWithList\MicaStats.exe"; ValueType: none; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\.yaml\OpenWithList\MicaStats.exe"; ValueType: none; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\.yml\OpenWithList\MicaStats.exe"; ValueType: none; Flags: uninsdeletekey

[Run]
Filename: "{app}\MicaStats.exe"; Description: "{cm:LaunchProgram,MicaStats}"; Flags: nowait postinstall skipifsilent
